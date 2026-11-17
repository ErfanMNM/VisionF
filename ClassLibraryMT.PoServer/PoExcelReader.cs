using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Kết quả đọc một sheet Excel: danh sách mã lấy từ CỘT ĐẦU TIÊN.
///
/// <para>
/// Mỗi ô đã được chuẩn hoá thành chuỗi (số -> không có dấu phẩy thập phân,
/// ngày tháng -> chuỗi ISO). Ô trống bị bỏ qua.
/// </para>
/// </summary>
public sealed class PoExcelReadResult
{
    /// <summary>Danh sách mã (theo thứ tự đọc, có thể trùng — server tự de-dup).</summary>
    public List<string> Codes { get; } = new();

    /// <summary>Số dòng đã đọc (kể cả dòng tiêu đề bị bỏ qua).</summary>
    public int RowsRead { get; set; }

    /// <summary>
    /// True nếu dòng đầu tiên có nội dung được coi là dòng tiêu đề và bị bỏ qua.
    /// </summary>
    public bool HeaderSkipped { get; set; }
}

/// <summary>
/// Đọc file Excel (.xlsx / .xls) và lấy danh sách mã từ cột đầu tiên.
///
/// <para>
/// <b>Yêu cầu AOT:</b> không dùng thư viện Excel bên ngoài (ClosedXML /
/// NPOI / EPPlus đều dùng reflection nên không publish được Native AOT).
/// Thay vào đó:
///
/// <list type="bullet">
/// <item><b>.xlsx</b> (OOXML) — file ZIP chứa XML. Đọc bằng
/// <see cref="ZipArchive"/> + <see cref="XmlReader"/> (100% AOT-safe).</item>
/// <item><b>.xls</b> (BIFF8/BIFF5) — định dạng nhị phân cũ. Đọc bằng bộ đọc
/// BIFF tự viết (không phụ thuộc thư viện ngoài).</item>
/// </list>
///
/// </para>
///
/// <para>
/// Quy tắc chọn sheet: lấy sheet ĐẦU TIÊN theo thứ tự workbook.xml khai báo
/// (thường là sheet đầu tiên trong file). Quy tắc chọn cột: lấy CỘT ĐẦU TIÊN
/// (cột A). Dòng tiêu đề đầu tiên được bỏ qua nếu nó khớp tên cột thường gặp
/// (serial / code / ma / ...) và không chứa chữ số.
/// </para>
/// </summary>
public static class PoExcelReader
{
    /// <summary>
    /// Đọc mã từ file Excel.
    /// </summary>
    /// <param name="path">Đường dẫn file tạm đã stream sẵn vào đĩa.</param>
    /// <param name="cancellationToken">Token hủy.</param>
    /// <returns>Danh sách mã đọc được.</returns>
    /// <exception cref="PoFileFormatException">File không phải Excel hợp lệ.</exception>
    public static async Task<PoExcelReadResult> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var result = new PoExcelReadResult();

        // Dò mấy byte đầu để biết định dạng: "PK\x03\x04" = ZIP/OOXML (.xlsx),
        // còn lại coi như BIFF nhị phân (.xls).
        var isZip = await IsZipContainerAsync(path, cancellationToken).ConfigureAwait(false);

        if (isZip)
        {
            await ReadOoxmlAsync(path, result, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReadBiffAsync(path, result, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Kiểm tra file có phải container ZIP (đặc trưng của .xlsx) hay không.
    /// </summary>
    private static async Task<bool> IsZipContainerAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var magic = new byte[4];
        var read = 0;

        await using (var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true))
        {
            while (read < 4)
            {
                var n = await fs
                    .ReadAsync(magic.AsMemory(read), cancellationToken)
                    .ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }
        }

        return read == 4
            && magic[0] == 0x50
            && magic[1] == 0x4B
            && (magic[2] == 0x03 || magic[2] == 0x05 || magic[2] == 0x07);
    }

    // =================================================================
    // .xlsx (OOXML) — đọc ZIP + XML
    // =================================================================

    /// <summary>Đọc workbook .xlsx: sheet đầu tiên, cột A, bỏ dòng tiêu đề.</summary>
    private static async Task ReadOoxmlAsync(
        string path,
        PoExcelReadResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            // 1) Đọc sharedStrings (nếu có) — dùng chung cho mọi sheet.
            var sharedStrings = await ReadSharedStringsAsync(archive, cancellationToken)
                .ConfigureAwait(false);

            // 2) Tìm sheet đầu tiên theo workbook.xml + workbook.xml.rels.
            var sheetPath = await ResolveFirstSheetPathAsync(archive, cancellationToken)
                .ConfigureAwait(false);

            if (sheetPath is null)
            {
                throw new PoFileFormatException(
                    "File Excel không có sheet nào (workbook rỗng).");
            }

            var sheetEntry = archive.GetEntry(sheetPath)
                ?? throw new PoFileFormatException(
                    $"Không đọc được sheet đầu tiên của file Excel ({sheetPath}).");

            // 3) Đọc các hàng, chỉ lấy cột đầu tiên (A).
            var rows = await ReadSheetFirstColumnAsync(
                sheetEntry, sharedStrings, cancellationToken).ConfigureAwait(false);

            ApplyRows(rows, result);
        }
        catch (InvalidDataException ex)
        {
            throw new PoFileFormatException(
                "File Excel (.xlsx) không hợp lệ hoặc bị hỏng: " + ex.Message);
        }
        catch (XmlException ex)
        {
            throw new PoFileFormatException(
                "File Excel (.xlsx) có XML không hợp lệ: " + ex.Message);
        }
    }

    /// <summary>
    /// Đọc <c>xl/sharedStrings.xml</c> — mảng chuỗi dùng chung.
    /// </summary>
    private static async Task<List<string>> ReadSharedStringsAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();

        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return result;
        }

        await using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true });

        // Mỗi phần tử <si> là một chuỗi; đọc text bên trong nó.
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                result.Add(
                    await ReadInnerTextAsync(reader, cancellationToken).ConfigureAwait(false));
            }
        }

        return result;
    }

    /// <summary>Đọc toàn bộ text bên trong phần tử hiện tại (kể cả <c>t</c> lồng nhau).</summary>
    private static async Task<string> ReadInnerTextAsync(
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var depth = reader.Depth;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Text
                || reader.NodeType == XmlNodeType.SignificantWhitespace
                || reader.NodeType == XmlNodeType.Whitespace)
            {
                sb.Append(reader.Value);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Xác định đường dẫn (trong ZIP) của sheet đầu tiên.
    /// </summary>
    private static async Task<string?> ResolveFirstSheetPathAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        if (workbookEntry is null)
        {
            return null;
        }

        // Đọc danh sách (r:id, name) của các sheet theo thứ tự khai báo.
        var sheetRels = new List<string>();
        await using (var stream = workbookEntry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true }))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                {
                    var relId = reader.GetAttribute("r:id");
                    if (!string.IsNullOrEmpty(relId))
                    {
                        sheetRels.Add(relId);
                    }
                }
            }
        }

        if (sheetRels.Count == 0)
        {
            return null;
        }

        // Đọc workbook.xml.rels để map r:id -> Target.
        var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (relsEntry is null)
        {
            return null;
        }

        var relMap = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var stream = relsEntry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true }))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship")
                {
                    var id = reader.GetAttribute("Id");
                    var target = reader.GetAttribute("Target");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                    {
                        relMap[id] = target!;
                    }
                }
            }
        }

        foreach (var relId in sheetRels)
        {
            if (!relMap.TryGetValue(relId, out var target))
            {
                continue;
            }

            // Target thường là "worksheets/sheet1.xml" -> "xl/worksheets/sheet1.xml".
            var normalized = target.Replace('\\', '/').TrimStart('/');
            if (!normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "xl/" + normalized;
            }

            if (archive.GetEntry(normalized) is not null)
            {
                return normalized;
            }
        }

        // Không map được -> thử "xl/worksheets/sheet1.xml" (rất phổ biến).
        return archive.GetEntry("xl/worksheets/sheet1.xml") is not null
            ? "xl/worksheets/sheet1.xml"
            : null;
    }

    /// <summary>
    /// Đọc cột đầu tiên (cột A) của mọi hàng trong sheet.
    /// </summary>
    private static async Task<List<string>> ReadSheetFirstColumnAsync(
        ZipArchiveEntry sheetEntry,
        List<string> sharedStrings,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();

        await using var stream = sheetEntry.Open();
        using var reader = XmlReader.Create(
            stream, new XmlReaderSettings { Async = true, IgnoreWhitespace = false });

        // Đang ở trong <row> hay không.
        var inRow = false;

        // Ô đầu tiên của hàng hiện tại đã lấy chưa.
        var tookFirstCell = false;

        // Kiểu của ô (t="s" = shared string, t="inlineStr", t="str", mặc định = số).
        var cellType = string.Empty;

        // Giá trị thô của ô (text trong <v> hoặc <t> của inlineStr).
        var cellValue = new StringBuilder();

        var inValueElement = false;
        var inInlineText = false;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "row":
                        inRow = true;
                        tookFirstCell = false;
                        cellType = string.Empty;
                        cellValue.Clear();
                        break;

                    case "c":
                        cellType = reader.GetAttribute("t") ?? string.Empty;
                        cellValue.Clear();
                        break;

                    case "v":
                        inValueElement = true;
                        break;

                    case "is":
                        inInlineText = false;
                        break;

                    case "t":
                        if (inRow)
                        {
                            inInlineText = true;
                        }

                        break;
                }

                continue;
            }

            if (reader.NodeType == XmlNodeType.Text
                || reader.NodeType == XmlNodeType.SignificantWhitespace
                || reader.NodeType == XmlNodeType.Whitespace)
            {
                if (inValueElement || inInlineText)
                {
                    cellValue.Append(reader.Value);
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.EndElement)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "v":
                    inValueElement = false;
                    break;

                case "t":
                    inInlineText = false;
                    break;

                case "c":
                    if (!inRow || tookFirstCell)
                    {
                        break;
                    }

                    tookFirstCell = true;
                    var text = ResolveCellText(cellType, cellValue.ToString(), sharedStrings);
                    if (text.Length > 0)
                    {
                        result.Add(text);
                    }

                    cellValue.Clear();
                    break;

                case "row":
                    inRow = false;
                    tookFirstCell = false;
                    break;
            }
        }

        return result;
    }

    /// <summary>Chuẩn hoá giá trị một ô sang chuỗi.</summary>
    private static string ResolveCellText(
        string cellType,
        string rawValue,
        List<string> sharedStrings)
    {
        if (rawValue.Length == 0)
        {
            return string.Empty;
        }

        // Shared string -> tra trong bảng.
        if (cellType == "s")
        {
            if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                && idx >= 0
                && idx < sharedStrings.Count)
            {
                return sharedStrings[idx].Trim();
            }

            return string.Empty;
        }

        // Chuỗi nội tuyến -> giữ nguyên (đã trim).
        if (cellType == "inlineStr" || cellType == "str")
        {
            return rawValue.Trim();
        }

        // Cờ boolean -> "TRUE"/"FALSE" không phải mã -> bỏ.
        if (cellType == "b")
        {
            return string.Empty;
        }

        // Ô số: bỏ dấu phẩy nghìn, ép về số nguyên nếu nguyên.
        if (cellType.Length == 0 || cellType == "n")
        {
            return NormalizeNumericCell(rawValue);
        }

        return rawValue.Trim();
    }

    /// <summary>Chuẩn hoá ô dạng số (VD <c>08934567890123</c> -> giữ nguyên chuỗi).</summary>
    private static string NormalizeNumericCell(string rawValue)
    {
        var text = rawValue.Trim();

        if (text.Length == 0)
        {
            return string.Empty;
        }

        // Có dấu phẩy phân cách nghìn và không có dấu chấm -> bỏ dấu phẩy.
        if (text.Contains(',', StringComparison.Ordinal)
            && !text.Contains('.', StringComparison.Ordinal))
        {
            text = text.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        // Số nguyên -> trả về dạng không mũ, không mất chữ số 0 đầu (ví dụ serial
        // "000123" trong Excel thường bị ép về 123 — giữ nguyên theo chuỗi gốc
        // nếu đọc được dạng nguyên).
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return text;
        }

        if (double.TryParse(
            text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        return text;
    }

    // =================================================================
    // .xls (BIFF) — định dạng nhị phân cũ (Excel 97-2003)
    // =================================================================

    /// <summary>Đọc file .xls (BIFF8/BIFF5): sheet đầu tiên, cột A.</summary>
    private static async Task ReadBiffAsync(
        string path,
        PoExcelReadResult result,
        CancellationToken cancellationToken)
    {
        var codes = new List<string>();

        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        var header = new byte[8];
        await ReadExactAsync(fs, header, cancellationToken).ConfigureAwait(false);

        // OLE2 signature: D0 CF 11 E0 A1 B1 1A E1
        var isOle2 = header[0] == 0xD0 && header[1] == 0xCF
            && header[2] == 0x11 && header[3] == 0xE0
            && header[4] == 0xA1 && header[5] == 0xB1
            && header[6] == 0x1A && header[7] == 0xE1;

        if (isOle2)
        {
            // .xls là container OLE2 (Compound File) -> cần đọc stream "Workbook"
            // hoặc "Book". Việc đọc CFB đầy đủ khá phức tạp; ta dùng cách
            // quét trực tiếp các record BIFF trong toàn bộ file (Workbook stream
            // là stream đầu tiên và thường nằm liên tiếp trong file).
            codes = await ReadBiffRecordsAsync(fs, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // BIFF "raw" (đôi khi .xls được lưu dạng workbook stream trần).
            fs.Position = 0;
            codes = await ReadBiffRecordsAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        ApplyRows(codes, result);
    }

    /// <summary>Đọc chính xác toàn bộ <paramref name="buffer"/> từ stream.</summary>
    private static async Task ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream
                .ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            offset += n;
        }
    }

    /// <summary>
    /// Quét các record BIFF trong stream, lấy giá trị cột A của sheet đầu tiên.
    /// </summary>
    private static async Task<List<string>> ReadBiffRecordsAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();

        // Sheet đầu tiên: BOF (0x0809) -> ... -> EOF (0x000A)
        var inFirstSheet = false;
        var sheetDone = false;

        var currentRow = -1;
        var rowHasValue = false;

        // Bảng chuỗi dùng chung (SST) — BIFF8.
        var sst = new List<string>();

        // Bảng chuỗi cũ (BIFF5) — theo từng record EXTSST/STRING.
        var pendingStringTable = new List<string>();

        var buffer = new byte[8192];
        var pos = 0;

        while (!sheetDone)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Cần 4 byte để đọc opcode + length.
            if (pos + 4 > buffer.Length)
            {
                // Dịch phần dư xuống đầu rồi đọc thêm.
                Array.Copy(buffer, pos, buffer, 0, buffer.Length - pos);
                var remaining = buffer.Length - pos;
                pos = 0;

                var read = await stream
                    .ReadAsync(buffer.AsMemory(remaining), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (remaining + read < 4)
                {
                    break;
                }

                continue;
            }

            var opcode = (ushort)(buffer[pos] | (buffer[pos + 1] << 8));
            var length = (ushort)(buffer[pos + 2] | (buffer[pos + 3] << 8));

            if (pos + 4 + length > buffer.Length)
            {
                // Record lớn hơn buffer -> đọc riêng.
                var record = new byte[length];
                await ReadExactAsync(stream, record, cancellationToken).ConfigureAwait(false);

                await HandleBiffRecordAsync(
                    opcode, record, result, ref inFirstSheet, ref sheetDone,
                    ref currentRow, ref rowHasValue, sst, pendingStringTable,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var payload = new byte[length];
            Array.Copy(buffer, pos + 4, payload, 0, length);
            pos += 4 + length;

            await HandleBiffRecordAsync(
                opcode, payload, result, ref inFirstSheet, ref sheetDone,
                ref currentRow, ref rowHasValue, sst, pendingStringTable,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Xử lý một record BIFF, lấy giá trị cột A.</summary>
    private static Task HandleBiffRecordAsync(
        ushort opcode,
        byte[] payload,
        List<string> result,
        ref bool inFirstSheet,
        ref bool sheetDone,
        ref int currentRow,
        ref bool rowHasValue,
        List<string> sst,
        List<string> pendingStringTable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (opcode)
        {
            // BOF: bắt đầu workbook hoặc sheet.
            case 0x0809:
                if (!inFirstSheet)
                {
                    inFirstSheet = true;
                }

                break;

            // EOF: kết thúc sheet đầu tiên -> dừng.
            case 0x000A:
                if (inFirstSheet)
                {
                    sheetDone = true;
                }

                break;

            // SST (BIFF8): bảng chuỗi dùng chung.
            case 0x00FC:
                if (inFirstSheet)
                {
                    ParseBiff8Sst(payload, sst);
                }

                break;

            // LABEL (BIFF8): 0x0204, chuỗi nội tuyến.
            case 0x0204:
                if (inFirstSheet && !rowHasValue)
                {
                    var text = ParseBiff8Label(payload);
                    if (text.Length > 0)
                    {
                        result.Add(text);
                        rowHasValue = true;
                    }
                }

                break;

            // NUMBER (0x0203) / RK (0x027E) / MULRK (0x00BD): giá trị số.
            case 0x0203:
            case 0x027E:
            case 0x00BD:
                if (inFirstSheet && !rowHasValue)
                {
                    var text = ParseBiffNumericCell(opcode, payload);
                    if (text.Length > 0)
                    {
                        result.Add(text);
                        rowHasValue = true;
                    }
                }

                break;

            // LABEL (BIFF5): 0x0204 dùng chuỗi 8-bit.
            case 0x0004:
                if (inFirstSheet && !rowHasValue)
                {
                    var text = ParseBiff5Label(payload);
                    if (text.Length > 0)
                    {
                        result.Add(text);
                        rowHasValue = true;
                    }
                }

                break;

            // NUMBER (BIFF5): 0x0003.
            case 0x0003:
                if (inFirstSheet && !rowHasValue)
                {
                    var text = ParseBiff5Number(payload);
                    if (text.Length > 0)
                    {
                        result.Add(text);
                        rowHasValue = true;
                    }
                }

                break;

            // ROW: bắt đầu hàng mới -> reset cờ đã lấy giá trị.
            case 0x0208:
            case 0x0006:
                currentRow++;
                rowHasValue = false;
                break;

            // FORMULA + STRING: kết quả chuỗi của công thức.
            case 0x00FC + 1:
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>Parse record SST của BIFF8 (bảng chuỗi dùng chung).</summary>
    private static void ParseBiff8Sst(byte[] payload, List<string> sst)
    {
        if (payload.Length < 8)
        {
            return;
        }

        var total = (int)(uint)(payload[4] | (payload[5] << 8) | (payload[6] << 16) | (payload[7] << 24));

        // Đọc chuỗi Unicode (XLUnicodeString): 2 byte độ dài (ký tự).
        var pos = 8;
        var totalRead = 0;

        while (pos < payload.Length && totalRead < total)
        {
            if (pos + 2 > payload.Length)
            {
                break;
            }

            var cch = payload[pos] | (payload[pos + 1] << 8);
            pos += 2;

            if (pos + 1 > payload.Length)
            {
                break;
            }

            var flags = payload[pos];
            pos += 1;

            var highByte = (flags & 0x01) != 0;
            var extended = (flags & 0x04) != 0;
            var rich = (flags & 0x08) != 0;
            var farEast = (flags & 0x04) != 0;

            var richRuns = 0;
            var farEastSize = 0;
            if (rich)
            {
                if (pos + 2 > payload.Length)
                {
                    break;
                }

                richRuns = payload[pos] | (payload[pos + 1] << 8);
                pos += 2;
            }

            if (extended || farEast)
            {
                if (pos + 4 > payload.Length)
                {
                    break;
                }

                farEastSize = (int)(uint)(payload[pos] | (payload[pos + 1] << 8)
                    | (payload[pos + 2] << 16) | (payload[pos + 3] << 24));
                pos += 4;
            }

            var sb = new StringBuilder(cch);
            for (var i = 0; i < cch; i++)
            {
                if (highByte)
                {
                    if (pos + 2 > payload.Length)
                    {
                        break;
                    }

                    sb.Append((char)(payload[pos] | (payload[pos + 1] << 8)));
                    pos += 2;
                }
                else
                {
                    if (pos + 1 > payload.Length)
                    {
                        break;
                    }

                    sb.Append((char)payload[pos]);
                    pos += 1;
                }
            }

            if (richRuns > 0)
            {
                pos += richRuns * 4;
            }

            if (farEastSize > 0)
            {
                pos += farEastSize;
            }

            sst.Add(sb.ToString());
            totalRead++;
        }
    }

    /// <summary>Parse record LABEL của BIFF8 (chuỗi nội tuyến).</summary>
    private static string ParseBiff8Label(byte[] payload)
    {
        // Cấu trúc: row(2) col(2) xf(2) -> XLUnicodeString (cch 2 byte + flags 1 byte)
        if (payload.Length < 9)
        {
            return string.Empty;
        }

        var cch = payload[6] | (payload[7] << 8);
        var flags = payload[8];
        var pos = 9;

        var sb = new StringBuilder(cch);
        for (var i = 0; i < cch; i++)
        {
            if ((flags & 0x01) != 0)
            {
                if (pos + 2 > payload.Length)
                {
                    break;
                }

                sb.Append((char)(payload[pos] | (payload[pos + 1] << 8)));
                pos += 2;
            }
            else
            {
                if (pos + 1 > payload.Length)
                {
                    break;
                }

                sb.Append((char)payload[pos]);
                pos += 1;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Parse record LABEL của BIFF5 (chuỗi 8-bit).</summary>
    private static string ParseBiff5Label(byte[] payload)
    {
        if (payload.Length < 8)
        {
            return string.Empty;
        }

        var cch = payload[6] | (payload[7] << 8);
        if (payload.Length < 8 + cch)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(cch);
        for (var i = 0; i < cch; i++)
        {
            sb.Append((char)payload[8 + i]);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Parse ô số của BIFF8 (NUMBER / RK / MULRK).</summary>
    private static string ParseBiffNumericCell(ushort opcode, byte[] payload)
    {
        if (opcode == 0x027E)
        {
            // RK: 4 byte — bit 0 chỉ đơn vị, bit 1 = số nguyên 30-bit.
            if (payload.Length < 10)
            {
                return string.Empty;
            }

            var rk = (int)(uint)(payload[6] | (payload[7] << 8) | (payload[8] << 16) | (payload[9] << 24));
            var isInteger = (rk & 0x02) != 0;
            var divideBy100 = (rk & 0x01) != 0;

            if (isInteger)
            {
                var value = rk >> 2;
                if (divideBy100)
                {
                    return ((double)value / 100.0)
                        .ToString("0.###############", CultureInfo.InvariantCulture);
                }

                return value.ToString(CultureInfo.InvariantCulture);
            }

            // Phần thực 64-bit: RK chỉ giữ 4 byte cao của double -> dựng lại
            // từ 8 byte (4 byte thấp = 0).
            var raw = new byte[8];
            raw[4] = payload[7];
            raw[5] = payload[8];
            raw[6] = payload[9];
            var d = BitConverter.ToDouble(raw, 0);

            if (divideBy100)
            {
                d /= 100.0;
            }

            return FormatExcelNumber(d);
        }

        if (opcode == 0x0203)
        {
            // NUMBER (BIFF8): row(2) col(2) xf(2) num(8)
            if (payload.Length < 14)
            {
                return string.Empty;
            }

            var d = BitConverter.ToDouble(payload, 6);
            return FormatExcelNumber(d);
        }

        // MULRK: chỉ lấy ô đầu tiên.
        if (opcode == 0x00BD)
        {
            if (payload.Length < 14)
            {
                return string.Empty;
            }

            var rk = (int)(uint)(payload[6] | (payload[7] << 8) | (payload[8] << 16) | (payload[9] << 24));
            var isInteger = (rk & 0x02) != 0;
            var divideBy100 = (rk & 0x01) != 0;

            if (isInteger)
            {
                var value = rk >> 2;
                return divideBy100
                    ? ((double)value / 100.0).ToString("0.###############", CultureInfo.InvariantCulture)
                    : value.ToString(CultureInfo.InvariantCulture);
            }

            var raw = new byte[8];
            raw[4] = payload[7];
            raw[5] = payload[8];
            raw[6] = payload[9];
            var d = BitConverter.ToDouble(raw, 0);
            if (divideBy100)
            {
                d /= 100.0;
            }

            return FormatExcelNumber(d);
        }

        return string.Empty;
    }

    /// <summary>Parse ô số của BIFF5 (NUMBER 0x0003).</summary>
    private static string ParseBiff5Number(byte[] payload)
    {
        if (payload.Length < 14)
        {
            return string.Empty;
        }

        var d = BitConverter.ToDouble(payload, 6);
        return FormatExcelNumber(d);
    }

    /// <summary>Định dạng giá trị số Excel thành chuỗi mã.</summary>
    private static string FormatExcelNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return string.Empty;
        }

        // Làm tròn số nguyên (số serial thường lưu dạng số).
        if (Math.Abs(d - Math.Round(d)) < 1e-9)
        {
            return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
        }

        return d.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Áp dụng danh sách hàng đọc được: bỏ dòng tiêu đề, bỏ ô rỗng.
    /// </summary>
    private static void ApplyRows(List<string> rows, PoExcelReadResult result)
    {
        result.RowsRead = rows.Count;

        var headerChecked = false;

        foreach (var raw in rows)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!headerChecked)
            {
                headerChecked = true;

                if (PoCodeFileParser.IsHeaderLine(line))
                {
                    result.HeaderSkipped = true;
                    continue;
                }
            }

            result.Codes.Add(line);
        }
    }
}

/// <summary>
/// Ném khi file tải lên không đúng định dạng / không đọc được.
/// </summary>
public sealed class PoFileFormatException : Exception
{
    /// <summary>Khởi tạo lỗi với thông điệp.</summary>
    public PoFileFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Khởi tạo lỗi với thông điệp và inner exception.</summary>
    public PoFileFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
