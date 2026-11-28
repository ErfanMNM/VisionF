using System.Text;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Một phần (part) của body <c>multipart/form-data</c>.
/// </summary>
/// <param name="HeaderText">Phần header của part (dạng text).</param>
/// <param name="ContentStart">Vị trí bắt đầu nội dung trong stream.</param>
/// <param name="ContentLength">Độ dài nội dung (byte).</param>
public readonly record struct PoMultipartPart(
    string HeaderText,
    long ContentStart,
    long ContentLength);

/// <summary>
/// Tiện ích đọc header <c>Content-Disposition</c> của multipart.
/// </summary>
public static class PoMultipartHeader
{
    /// <summary>
    /// Lấy giá trị một tham số trong header <c>Content-Disposition</c>
    /// (VD <c>name="poName"</c>, <c>filename="codes.csv"</c>).
    /// </summary>
    /// <param name="headerText">
    /// Nội dung header. Có thể là GIÁ TRỊ thô của header
    /// (<c>attachment; filename="codes.csv"</c>) hoặc cả khối header đầy đủ
    /// (<c>Content-Disposition: ...</c> + các dòng header khác).
    /// </param>
    /// <param name="parameterName">Tên tham số cần lấy.</param>
    /// <returns>Giá trị (null nếu không có).</returns>
    public static string? GetDispositionParameter(string? headerText, string parameterName)
    {
        if (string.IsNullOrEmpty(headerText))
        {
            return null;
        }

        // Trường hợp 1: đầu vào LÀ giá trị thô của Content-Disposition.
        if (!headerText.Contains("Content-Disposition", StringComparison.OrdinalIgnoreCase))
        {
            return ParseParameterFromValue(headerText, parameterName);
        }

        // Trường hợp 2: đầu vào là khối header đầy đủ -> lấy đúng dòng
        // Content-Disposition rồi parse.
        foreach (var line in headerText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            var value = colon >= 0
                ? trimmed[(colon + 1)..]
                : trimmed["Content-Disposition".Length..];

            var parsed = ParseParameterFromValue(value, parameterName);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Lấy một tham số trong giá trị thô của header
    /// (VD <c>attachment; filename="codes.csv"</c>).
    /// </summary>
    private static string? ParseParameterFromValue(string value, string parameterName)
    {
        foreach (var part in value.Split(';'))
        {
            var trimmed = part.Trim();
            if (!trimmed.StartsWith(parameterName + "=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = trimmed[(parameterName.Length + 1)..].Trim();

            // Giá trị có thể được bọc trong dấu nháy kép.
            if (raw.StartsWith('"'))
            {
                var closing = raw.IndexOf('"', 1);
                return closing > 0 ? raw[1..closing] : raw[1..].TrimEnd();
            }

            return raw;
        }

        return null;
    }

    /// <summary>
    /// Lấy boundary từ header <c>Content-Type</c> dạng
    /// <c>multipart/form-data; boundary=----abc</c>.
    /// </summary>
    /// <param name="contentType">Header Content-Type.</param>
    /// <returns>Boundary (không kèm "--") hoặc null nếu không có.</returns>
    public static string? GetBoundary(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }

        foreach (var part in contentType.Split(';'))
        {
            var trimmed = part.Trim();
            if (!trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return trimmed["boundary=".Length..].Trim().Trim('"');
        }

        return null;
    }
}

/// <summary>
/// Đọc và tách các phần của body <c>multipart/form-data</c> theo kiểu streaming
/// (không nạp toàn bộ body vào RAM).
///
/// <para>
/// Cách duyệt: dùng <see cref="Stream.Seek"/> để dò từng separator trong file
/// (random access) — không cần buffer toàn bộ body trong RAM và không có nguy
/// cơ vòng lặp vô hạn.
/// </para>
///
/// <para>
/// Yêu cầu: stream phải hỗ trợ <see cref="Stream.Seek(long, SeekOrigin)"/>
/// (file trên đĩa).
/// </para>
/// </summary>
public static class PoMultipartReader
{
    /// <summary>
    /// Duyệt các phần multipart trong một stream (đã nằm trên đĩa).
    /// </summary>
    /// <param name="stream">Stream chứa body multipart (hỗ trợ seek).</param>
    /// <param name="boundary">Boundary (không kèm "--").</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Các phần tìm được.</returns>
    public static async IAsyncEnumerable<PoMultipartPart> EnumeratePartsAsync(
        Stream stream,
        string boundary,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("Multipart input must be a readable seekable stream.", nameof(stream));
        ValidateBoundary(boundary);
        var separator = Encoding.ASCII.GetBytes("--" + boundary);
        byte[] headerTerminator = [13, 10, 13, 10];
        var length = stream.Length;
        var delimiter = await FindDelimiterAsync(stream, separator, 0, length, ct).ConfigureAwait(false)
            ?? throw new PoFileFormatException("Multipart is missing its opening boundary.");

        while (!delimiter.Closing)
        {
            ct.ThrowIfCancellationRequested();
            var cursor = delimiter.Next;
            // Header scanning and allocation are bounded independently of the body/file limit.
            var headerLimit = Math.Min(length, cursor + MaxHeaderBytes + headerTerminator.Length);
            var headerEnd = await FindSequenceAsync(stream, headerTerminator, cursor, headerLimit, ct)
                .ConfigureAwait(false);
            if (headerEnd < 0 || headerEnd - cursor > MaxHeaderBytes)
                throw new PoFileFormatException("Multipart headers are incomplete or exceed 64 KB.");
            var headerBytes = await ReadRangeAsync(stream, cursor, checked((int)(headerEnd - cursor)), ct)
                .ConfigureAwait(false);
            string headerText;
            try { headerText = new UTF8Encoding(false, true).GetString(headerBytes); }
            catch (DecoderFallbackException error)
            { throw new PoFileFormatException("Multipart headers are not valid UTF-8.", error); }
            ValidateHeaders(headerText);

            var contentStart = headerEnd + headerTerminator.Length;
            var next = await FindDelimiterAsync(stream, separator, contentStart, length, ct).ConfigureAwait(false)
                ?? throw new PoFileFormatException("Multipart is missing its complete closing boundary.");
            // CRLF before a MIME delimiter belongs to the delimiter, not the file.
            var contentEnd = next.Position - 2;
            if (contentEnd < contentStart)
                throw new PoFileFormatException("Multipart part has an invalid content boundary.");
            yield return new PoMultipartPart(headerText, contentStart, contentEnd - contentStart);
            delimiter = next;
        }
    }

    private const int MaxHeaderBytes = 64 * 1024;

    private static void ValidateBoundary(string boundary)
    {
        // MIME bchars: ASCII letters/digits and these punctuation characters; no trailing space.
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 70 || boundary[^1] == ' '
            || boundary.Any(character => !char.IsAsciiLetterOrDigit(character)
                && "'()+_,-./:=? ".IndexOf(character, StringComparison.Ordinal) < 0))
            throw new PoFileFormatException("Multipart boundary must contain 1-70 valid MIME characters without CR/LF.");
    }

    private static void ValidateHeaders(string headerText)
    {
        if (headerText.Length == 0)
            throw new PoFileFormatException("Multipart part is missing its headers.");
        var dispositionSeen = false;
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.None))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(character => !char.IsAsciiLetterOrDigit(character)
                    && "!#$%&'*+-.^_`|~".IndexOf(character, StringComparison.Ordinal) < 0)
                || line[(colon + 1)..].Any(character => character < ' ' && character != '\t' || character == 127))
                throw new PoFileFormatException("Multipart contains a malformed part header.");
            if (!line[..colon].Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)) continue;
            if (dispositionSeen)
                throw new PoFileFormatException("Multipart part has duplicate Content-Disposition headers.");
            dispositionSeen = true;
            var disposition = line[(colon + 1)..].TrimStart();
            if (!disposition.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                && !disposition.StartsWith("form-data;", StringComparison.OrdinalIgnoreCase))
                throw new PoFileFormatException("Multipart part requires Content-Disposition: form-data.");
        }
        if (!dispositionSeen)
            throw new PoFileFormatException("Multipart part is missing Content-Disposition.");
    }

    private readonly record struct MultipartDelimiter(long Position, long Next, bool Closing);

    private static async Task<MultipartDelimiter?> FindDelimiterAsync(
        Stream stream, byte[] separator, long start, long length, CancellationToken ct)
    {
        var search = start;
        while (search < length)
        {
            var position = await FindSequenceAsync(stream, separator, search, length, ct).ConfigureAwait(false);
            if (position < 0) return null;
            var after = position + separator.Length;
            var lineStart = position == 0 || position >= 2
                && await StartsWithAsync(stream, position - 2, length, 13, 10, ct).ConfigureAwait(false);
            if (lineStart)
            {
                if (await StartsWithAsync(stream, after, length, 13, 10, ct).ConfigureAwait(false))
                    return new MultipartDelimiter(position, after + 2, Closing: false);
                if (await StartsWithAsync(stream, after, length, (byte)'-', (byte)'-', ct).ConfigureAwait(false)
                    && (after + 2 == length
                        || await StartsWithAsync(stream, after + 2, length, 13, 10, ct).ConfigureAwait(false)))
                    return new MultipartDelimiter(position, after + 2, Closing: true);
            }
            // Inline boundary text and boundary prefixes are ordinary opaque file data.
            search = position + 1;
        }
        return null;
    }
    /// <summary>Kiểm tra stream có bắt đầu bằng 2 byte cho trước hay không.</summary>
    private static async Task<bool> StartsWithAsync(
        Stream stream,
        long position,
        long length,
        byte first,
        byte second,
        CancellationToken ct)
    {
        if (position + 1 >= length)
        {
            return false;
        }

        stream.Seek(position, SeekOrigin.Begin);
        var probe = new byte[2];
        await ReadExactAsync(stream, probe, ct).ConfigureAwait(false);

        return probe[0] == first && probe[1] == second;
    }

    /// <summary>Đọc chính xác số byte yêu cầu từ stream (tại vị trí hiện tại).</summary>
    private static async Task ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream
                .ReadAsync(buffer.AsMemory(offset), ct)
                .ConfigureAwait(false);
            if (n <= 0)
            {
                throw new PoFileFormatException("Multipart ended before the requested data was read.");
            }

            offset += n;
        }
    }

    /// <summary>Đọc một khoảng byte từ stream (seek trước rồi đọc).</summary>
    private static async Task<byte[]> ReadRangeAsync(
        Stream stream,
        long start,
        int count,
        CancellationToken ct)
    {
        if (count <= 0)
        {
            return [];
        }

        var buffer = new byte[count];
        stream.Seek(start, SeekOrigin.Begin);
        await ReadExactAsync(stream, buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>
    /// Tìm vị trí đầu tiên của một mảng byte con trong file (dùng seek + đọc
    /// theo cửa sổ trượt, không nạp toàn bộ file vào RAM).
    /// </summary>
    private static async Task<long> FindSequenceAsync(
        Stream stream,
        byte[] needle,
        long start,
        long length,
        CancellationToken ct)
    {
        if (needle.Length == 0 || start < 0 || start >= length)
        {
            return -1;
        }

        const int windowSize = 64 * 1024;
        var window = new byte[windowSize + needle.Length - 1];

        var chunk = new byte[windowSize];
        long chunkStart = start;
        var carry = 0;

        while (chunkStart < length)
        {
            ct.ThrowIfCancellationRequested();

            stream.Seek(chunkStart, SeekOrigin.Begin);

            var toRead = (int)Math.Min(chunk.Length, length - chunkStart);
            var read = 0;
            while (read < toRead)
            {
                var n = await stream
                    .ReadAsync(chunk.AsMemory(read, toRead - read), ct)
                    .ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            if (read <= 0)
            {
                throw new PoFileFormatException("Multipart stream is truncated.");
            }

            // Ghép carry + chunk mới.
            Array.Copy(chunk, 0, window, carry, read);
            var windowLen = carry + read;

            var idx = IndexOfSequence(window, windowLen, needle);
            if (idx >= 0)
            {
                return chunkStart - carry + idx;
            }

            // Giữ lại (needle.Length - 1) byte cuối cho lần sau.
            carry = Math.Min(needle.Length - 1, windowLen);
            Array.Copy(window, windowLen - carry, window, 0, carry);

            chunkStart += read;
        }

        return -1;
    }

    /// <summary>Tìm vị trí đầu tiên của một mảng byte con trong mảng byte.</summary>
    private static int IndexOfSequence(byte[] haystack, int haystackLength, byte[] needle)
    {
        if (needle.Length == 0 || haystackLength < needle.Length)
        {
            return -1;
        }

        var last = haystackLength - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Tiện ích đọc nội dung của một phần multipart.
/// </summary>
public static class PoMultipartPartReader
{
    /// <summary>
    /// Chép nội dung của part ra stream đích (streaming).
    /// </summary>
    /// <param name="stream">Stream chứa body multipart (hỗ trợ seek).</param>
    /// <param name="part">Thông tin part.</param>
    /// <param name="output">Stream đích.</param>
    /// <param name="maxBytes">Giới hạn tối đa (byte).</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Số byte đã chép.</returns>
    /// <exception cref="BodyTooLargeException">Vượt quá giới hạn.</exception>
    public static async Task<long> CopyContentToAsync(
        Stream stream,
        PoMultipartPart part,
        Stream output,
        int maxBytes,
        CancellationToken ct = default)
    {
        stream.Seek(part.ContentStart, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        long total = 0;
        var remaining = part.ContentLength;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, want), ct)
                .ConfigureAwait(false);

            if (read <= 0)
            {
                throw new PoFileFormatException("Multipart file part is truncated.");
            }

            total += read;
            remaining -= read;

            if (total > maxBytes)
            {
                throw new BodyTooLargeException();
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        return total;
    }

    /// <summary>
    /// Đọc nội dung part dạng text (dùng cho field metadata nhỏ).
    /// </summary>
    /// <param name="stream">Stream chứa body multipart (hỗ trợ seek).</param>
    /// <param name="part">Thông tin part.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Nội dung text (UTF-8).</returns>
    public static async Task<string> ReadContentAsTextAsync(
        Stream stream,
        PoMultipartPart part,
        CancellationToken ct = default)
    {
        stream.Seek(part.ContentStart, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        long remaining = part.ContentLength;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, want), ct)
                .ConfigureAwait(false);

            if (read <= 0)
            {
                throw new PoFileFormatException("Multipart metadata part is truncated.");
            }

            ms.Write(buffer, 0, read);
            remaining -= read;
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
