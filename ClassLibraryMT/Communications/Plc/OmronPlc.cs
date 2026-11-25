namespace ClassLibraryMT.Communications.Plc;

/// <summary>
/// Client dong bo doc/ghi vung D (vung DM) cua PLC Omron qua FINS/UDP.
///
/// Cach dung:
/// <code>
/// var plc = new OmronPlc("192.168.1.10", 9600);
/// PlcResult rs = plc.WriteInt32("D12", 1);
/// PlcResult&lt;int&gt; rr = plc.ReadInt32("D12");
/// if (rr.IsSuccess) { int v = rr.Value; }
/// Console.WriteLine($"mat {rr.ProcessTime.TotalMilliseconds:F1} ms");
/// </code>
///
/// Khong heartbeat, khong thread nen, khong event trang thai.
/// </summary>
public sealed partial class OmronPlc : IDisposable
{
    /// <summary>Cong UDP mac dinh cua FINS.</summary>
    public const int DefaultPort = 9600;

    /// <summary>Timeout mac dinh cho mot request (ms).</summary>
    public const int DefaultTimeoutMs = 1000;

    // Cùng một khóa bảo vệ request hoàn chỉnh, read-modify-write, đổi cấu
    // hình và Dispose. Mọi caller (kể cả tab Chỉnh PLC) dùng chung client.
    private readonly object _sync = new();
    // Lease non-thread-affine: một recipe có thể gồm nhiều request, trong
    // khi request đơn vẫn dùng _sync để bảo vệ SID/socket.
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private FinsUdpTransport? _transport;
    private byte _remoteNode;
    private byte _localNode;
    private bool _enabled;
    private bool _disposed;

    /// <summary>Dia chi IP cua PLC.</summary>
    public string IpAddress { get; private set; }

    /// <summary>Cong UDP cua PLC.</summary>
    public int Port { get; private set; }

    /// <summary>Node cua PLC (DA1 trong header FINS).</summary>
    public byte RemoteNode => _remoteNode;

    /// <summary>Node cua may PC (SA1 trong header FINS).</summary>
    public byte LocalNode => _localNode;

    /// <summary>Timeout moi request (ms).</summary>
    public int TimeoutMs { get; private set; }

    /// <summary>So lan thu lai khi khong nhan duoc phan hoi.</summary>
    public int Retries { get; private set; }

    /// <summary>False khi PLC bị tắt; mọi đọc/ghi trả DISABLED mà không gửi UDP.</summary>
    public bool Enabled { get { lock (_sync) return _enabled; } }

    /// <summary>
    /// Lấy lease cho toàn bộ thao tác thủ công/recipe mà không chặn luồng UI.
    /// Null nghĩa là một thao tác hoặc lần đổi cấu hình khác đang giữ PLC.
    /// </summary>
    public IDisposable? TryAcquireTransactionLease()
    {
        if (!_transactionGate.Wait(0)) return null;
        if (Volatile.Read(ref _disposed))
        {
            _transactionGate.Release();
            return null;
        }
        return new TransactionLease(_transactionGate);
    }

    /// <summary>Đợi recipe hiện tại hoàn tất trước khi cấu hình client và options runtime.</summary>
    public async Task<IDisposable> AcquireConfigurationLeaseAsync(CancellationToken cancellationToken = default)
    {
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _disposed))
        {
            _transactionGate.Release();
            throw new ObjectDisposedException(nameof(OmronPlc));
        }
        return new TransactionLease(_transactionGate);
    }

    private sealed class TransactionLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    /// <summary>
    /// Tao client.
    /// </summary>
    /// <param name="ip">Dia chi IP cua PLC.</param>
    /// <param name="port">Cong UDP FINS, mac dinh 9600.</param>
    /// <param name="remoteNode">Node PLC (DA1). Node thuc te thuong la octet cuoi cua IP.</param>
    /// <param name="localNode">Node PC (SA1), mac dinh 1.</param>
    /// <param name="timeoutMs">Timeout moi request, mac dinh 1000ms.</param>
    /// <param name="retries">So lan thu lai, mac dinh 0.</param>
    /// <param name="enabled">False để không mở socket hoặc gửi lệnh PLC.</param>
    public OmronPlc(
        string ip,
        int port = DefaultPort,
        byte remoteNode = 0,
        byte localNode = 1,
        int timeoutMs = DefaultTimeoutMs,
        int retries = 0,
        bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);

        IpAddress = ip;
        Port = port;
        _remoteNode = remoteNode;
        _localNode = localNode;
        TimeoutMs = timeoutMs;
        Retries = retries;

        _enabled = enabled;
        _transport = enabled ? CreateTransport(ip, port, timeoutMs, retries) : null;
    }

    /// <summary>Đổi endpoint/node/timeout tại chỗ sau khi request hiện tại hoàn tất.</summary>
    public void Reconfigure(string ip, int port, byte remoteNode, byte localNode,
        int timeoutMs, int retries, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        // Tạo trước khi swap: lỗi DNS/socket không làm mất cấu hình đang chạy.
        var replacement = enabled ? CreateTransport(ip, port, timeoutMs, retries) : null;
        lock (_sync)
        {
            if (_disposed)
            {
                replacement?.Dispose();
                throw new ObjectDisposedException(nameof(OmronPlc));
            }
            var previous = _transport;
            _transport = replacement;
            IpAddress = ip;
            Port = port;
            _remoteNode = remoteNode;
            _localNode = localNode;
            TimeoutMs = timeoutMs;
            Retries = retries;
            _enabled = enabled;
            previous?.Dispose();
        }
    }

    private static FinsUdpTransport CreateTransport(string host, int port, int timeoutMs, int retries)
    {
        var address = System.Net.IPAddress.TryParse(host, out var parsed)
            ? parsed
            : System.Net.Dns.GetHostAddresses(host)
                .FirstOrDefault(candidate => candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?? throw new ArgumentException("Không phân giải được địa chỉ IPv4 PLC.", nameof(host));
        return new FinsUdpTransport(address.ToString(), port, timeoutMs, retries);
    }

    // ------------------------------------------------------------------
    // Read - so nguyen 16 bit
    // ------------------------------------------------------------------

    /// <summary>Doc 1 word (short).</summary>
    public PlcResult<short> ReadInt16(string address) =>
        ReadScalar<short>(address, 1, (w, i) => FinsData.ToInt16(w, i));

    /// <summary>Doc 1 word (ushort).</summary>
    public PlcResult<ushort> ReadUInt16(string address) =>
        ReadScalar<ushort>(address, 1, (w, i) => FinsData.ToUInt16(w, i));

    // ------------------------------------------------------------------
    // Read - so nguyen 32 bit
    // ------------------------------------------------------------------

    /// <summary>Doc 2 word (int).</summary>
    public PlcResult<int> ReadInt32(string address) =>
        ReadScalar<int>(address, 2, (w, i) => FinsData.ToInt32(w, i));

    /// <summary>Doc 2 word (uint).</summary>
    public PlcResult<uint> ReadUInt32(string address) =>
        ReadScalar<uint>(address, 2, (w, i) => FinsData.ToUInt32(w, i));

    // ------------------------------------------------------------------
    // Read - so nguyen 64 bit
    // ------------------------------------------------------------------

    /// <summary>Doc 4 word (long).</summary>
    public PlcResult<long> ReadInt64(string address) =>
        ReadScalar<long>(address, 4, (w, i) => FinsData.ToInt64(w, i));

    /// <summary>Doc 4 word (ulong).</summary>
    public PlcResult<ulong> ReadUInt64(string address) =>
        ReadScalar<ulong>(address, 4, (w, i) => FinsData.ToUInt64(w, i));

    // ------------------------------------------------------------------
    // Read - so thuc
    // ------------------------------------------------------------------

    /// <summary>Doc 2 word (float).</summary>
    public PlcResult<float> ReadFloat(string address) =>
        ReadScalar<float>(address, 2, (w, i) => FinsData.ToSingle(w, i));

    /// <summary>Doc 4 word (double).</summary>
    public PlcResult<double> ReadDouble(string address) =>
        ReadScalar<double>(address, 4, (w, i) => FinsData.ToDouble(w, i));

    // ------------------------------------------------------------------
    // Read - bool
    // ------------------------------------------------------------------

    /// <summary>
    /// Doc 1 bit trong 1 word. Dia chi co dang "D12.05".
    /// </summary>
    public PlcResult<bool> ReadBool(string address)
    {
        if (!FinsAddress.TryParse(address, out var parsed) || !parsed.IsBit)
            return PlcResult<bool>.Fail("ARG", $"Địa chỉ bit không hợp lệ: '{address}'.", default);

        var read = ReadWordsAt(parsed.WordAddress, 1);
        if (!read.IsSuccess)
            return PlcResult<bool>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var value = FinsData.GetBit(read.Value![0], parsed.BitAddress!.Value);
        return PlcResult<bool>.Ok(value, "OK", read.ProcessTime);
    }

    // ------------------------------------------------------------------
    // Read - chuoi
    // ------------------------------------------------------------------

    /// <summary>
    /// Doc chuoi ASCII, 2 ky tu / word. <paramref name="length"/> la so ky tu.
    /// </summary>
    public PlcResult<string> ReadString(string address, int length)
    {
        if (length <= 0)
            return PlcResult<string>.Fail("ARG", $"Độ dài chuỗi phải > 0 (nhận {length}).", default);

        var wordCount = (length + 1) / 2;
        var read = ReadWords(address, wordCount);
        if (!read.IsSuccess)
            return PlcResult<string>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var value = FinsCodec.DecodeStringFromWords(read.Value, length);
        return PlcResult<string>.Ok(value, "OK", read.ProcessTime);
    }

    // ------------------------------------------------------------------
    // Read - mang
    // ------------------------------------------------------------------

    /// <summary>Doc <paramref name="length"/> word (short).</summary>
    public PlcResult<short[]> ReadInt16Array(string address, int length)
    {
        var read = ReadWords(address, length);
        if (!read.IsSuccess) return PlcResult<short[]>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var words = read.Value!;
        var values = new short[words.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = unchecked((short)words[i]);

        return PlcResult<short[]>.Ok(values, "OK", read.ProcessTime);
    }

    /// <summary>Doc <paramref name="length"/> word (ushort).</summary>
    public PlcResult<ushort[]> ReadUInt16Array(string address, int length)
    {
        var read = ReadWords(address, length);
        if (!read.IsSuccess) return PlcResult<ushort[]>.Fail(read.ErrorCode, read.Message, read.ProcessTime);
        return PlcResult<ushort[]>.Ok(read.Value, "OK", read.ProcessTime);
    }

    /// <summary>Doc <paramref name="length"/> phan tu (int), moi phan tu 2 word.</summary>
    public PlcResult<int[]> ReadInt32Array(string address, int length)
    {
        var read = ReadWords(address, length * 2);
        if (!read.IsSuccess) return PlcResult<int[]>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var values = new int[length];
        for (var i = 0; i < length; i++)
            values[i] = FinsData.ToInt32(read.Value, i * 2);

        return PlcResult<int[]>.Ok(values, "OK", read.ProcessTime);
    }

    /// <summary>Doc <paramref name="length"/> phan tu (uint), moi phan tu 2 word.</summary>
    public PlcResult<uint[]> ReadUInt32Array(string address, int length)
    {
        var read = ReadWords(address, length * 2);
        if (!read.IsSuccess) return PlcResult<uint[]>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var values = new uint[length];
        for (var i = 0; i < length; i++)
            values[i] = FinsData.ToUInt32(read.Value, i * 2);

        return PlcResult<uint[]>.Ok(values, "OK", read.ProcessTime);
    }

    /// <summary>Doc <paramref name="length"/> phan tu (long), moi phan tu 4 word.</summary>
    public PlcResult<long[]> ReadInt64Array(string address, int length)
    {
        var read = ReadWords(address, length * 4);
        if (!read.IsSuccess) return PlcResult<long[]>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var values = new long[length];
        for (var i = 0; i < length; i++)
            values[i] = FinsData.ToInt64(read.Value, i * 4);

        return PlcResult<long[]>.Ok(values, "OK", read.ProcessTime);
    }

    /// <summary>Doc <paramref name="length"/> phan tu (ulong), moi phan tu 4 word.</summary>
    public PlcResult<ulong[]> ReadUInt64Array(string address, int length)
    {
        var read = ReadWords(address, length * 4);
        if (!read.IsSuccess) return PlcResult<ulong[]>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var values = new ulong[length];
        for (var i = 0; i < length; i++)
            values[i] = FinsData.ToUInt64(read.Value, i * 4);

        return PlcResult<ulong[]>.Ok(values, "OK", read.ProcessTime);
    }

    /// <summary>Doc <paramref name="length"/> phan tu (float), moi phan tu 2 word.</summary>
    public PlcResult<float[]> ReadFloatArray(string address, int length)
    {
        var read = ReadWords(address, length * 2);
        if (!read.IsSuccess) return PlcResult<float[]>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var values = new float[length];
        for (var i = 0; i < length; i++)
            values[i] = FinsData.ToSingle(read.Value, i * 2);

        return PlcResult<float[]>.Ok(values, "OK", read.ProcessTime);
    }

    /// <summary>Doc <paramref name="length"/> phan tu (double), moi phan tu 4 word.</summary>
    public PlcResult<double[]> ReadDoubleArray(string address, int length)
    {
        var read = ReadWords(address, length * 4);
        if (!read.IsSuccess) return PlcResult<double[]>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var values = new double[length];
        for (var i = 0; i < length; i++)
            values[i] = FinsData.ToDouble(read.Value, i * 4);

        return PlcResult<double[]>.Ok(values, "OK", read.ProcessTime);
    }

    /// <summary>
    /// Doc <paramref name="length"/> bit lien tiep tu dia chi "D100.00".
    /// </summary>
    public PlcResult<bool[]> ReadBoolArray(string address, int length)
    {
        if (!FinsAddress.TryParse(address, out var parsed) || !parsed.IsBit)
            return PlcResult<bool[]>.Fail("ARG", $"Địa chỉ bit không hợp lệ: '{address}'.", default);

        if (length <= 0)
            return PlcResult<bool[]>.Fail("ARG", $"Độ dài mảng phải > 0 (nhận {length}).", default);

        var startBit = parsed.BitAddress!.Value;
        var wordCount = (startBit + length + 15) / 16;

        var read = ReadWordsAt(parsed.WordAddress, wordCount);
        if (!read.IsSuccess)
            return PlcResult<bool[]>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var words = read.Value!;
        var values = new bool[length];
        for (var i = 0; i < length; i++)
        {
            var absoluteBit = startBit + i;
            var word = words[absoluteBit / 16];
            values[i] = FinsData.GetBit(word, absoluteBit % 16);
        }

        return PlcResult<bool[]>.Ok(values, "OK", read.ProcessTime);
    }

    /// <summary>
    /// Doc <paramref name="wordCount"/> word tho (raw).
    /// </summary>
    public PlcResult<ushort[]> ReadWords(string address, int wordCount)
    {
        if (!FinsAddress.TryParse(address, out var parsed))
            return PlcResult<ushort[]>.Fail("ARG", $"Địa chỉ không hợp lệ: '{address}'.", default);

        if (parsed.IsBit)
            return PlcResult<ushort[]>.Fail("ARG", $"Địa chỉ '{address}' là địa chỉ bit, hãy dùng ReadBool.", default);

        if (wordCount <= 0)
            return PlcResult<ushort[]>.Fail("ARG", $"So word phai > 0 (nhan {wordCount}).", default);

        if (!parsed.FitsWordCount(wordCount))
            return PlcResult<ushort[]>.Fail("ARG",
                $"Doc {wordCount} word tu '{address}' sẽ tràn qua giới hạn vùng D (32767).", default);

        return ReadWordsAt(parsed.WordAddress, wordCount);
    }

    /// <summary>Doc tu dia chi word (noi bo, da kiem tra pham vi).</summary>
    private PlcResult<ushort[]> ReadWordsAt(int wordAddress, int wordCount)
    {
        lock (_sync)
        {
            if (_disposed)
                return PlcResult<ushort[]>.Fail("DISPOSED", "Kết nối PLC đã đóng.", default);
            if (!_enabled)
                return PlcResult<ushort[]>.Fail("DISABLED", "PLC đã tắt.", default);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var total = new List<ushort>(wordCount);
            var remaining = wordCount;
            var current = wordAddress;

            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, Fins.MaxWordsPerRequest);
                var result = _transport!.Read(_remoteNode, _localNode, current, chunk);

                if (!result.IsSuccess)
                    return PlcResult<ushort[]>.Fail(result.ErrorCode, result.Message, sw.Elapsed);

                total.AddRange(result.Words);
                current += chunk;
                remaining -= chunk;
            }

            return PlcResult<ushort[]>.Ok([.. total], "OK", sw.Elapsed);
        }
    }

    /// <summary>Doc 1 gia tri don co quy doi tu word.</summary>
    private PlcResult<T> ReadScalar<T>(
        string address, int wordCount, Func<ushort[], int, T> convert)
    {
        var read = ReadWords(address, wordCount);
        if (!read.IsSuccess)
            return PlcResult<T>.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        return PlcResult<T>.Ok(convert(read.Value!, 0), "OK", read.ProcessTime);
    }
}
