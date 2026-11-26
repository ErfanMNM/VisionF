namespace TanTienQR.Services;

// Cấu hình kết nối PLC Omron (đọc từ appsettings.json, mục "Plc").
internal sealed class PlcOptions
{
    public bool Enabled { get; set; } = true;
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9600;
    public byte RemoteNode { get; set; } = 0;   // Node PLC (DA1), thường là octet cuối IP
    public byte LocalNode { get; set; } = 1;    // Node PC (SA1)
    public int TimeoutMs { get; set; } = 1000;
    public int Retries { get; set; } = 0;

    // Địa chỉ vùng D
    public string ResultAddress { get; set; } = "D12";   // 1 = Pass, 0 = Fail
    public string ReadyAddress { get; set; } = "D16";    // 1 = Ready, 0 = chưa sẵn sàng
    public int RefreshIntervalMs { get; set; } = 1000;   // chu kỳ ghi D16

    public string CounterAddress { get; set; } = "D100";
    public string TotalCounterAddress { get; set; } = string.Empty;
    public string PassCounterAddress { get; set; } = string.Empty;
    public string FailCounterAddress { get; set; } = string.Empty;
    public string TimeoutCounterAddress { get; set; } = string.Empty;
    public bool FailIncludesTimeout { get; set; }
    public string CounterDisplaySource { get; set; } = "PLC";
    public string ResetCounterAddress { get; set; } = string.Empty;
    public string ClearErrorAddress { get; set; } = string.Empty;

    public bool UsesLegacyCounterBlock => string.IsNullOrWhiteSpace(TotalCounterAddress)
        && string.IsNullOrWhiteSpace(PassCounterAddress) && string.IsNullOrWhiteSpace(FailCounterAddress)
        && string.IsNullOrWhiteSpace(TimeoutCounterAddress);
    public string EffectiveTotalCounterAddress => ResolveCounterAddress(TotalCounterAddress, 0);
    public string EffectivePassCounterAddress => ResolveCounterAddress(PassCounterAddress, 4);
    public string EffectiveFailCounterAddress => ResolveCounterAddress(FailCounterAddress, 2);
    public string EffectiveTimeoutCounterAddress => ResolveCounterAddress(TimeoutCounterAddress, 8);

    private string ResolveCounterAddress(string? address, int offset)
    {
        if (!string.IsNullOrWhiteSpace(address)) return address.Trim();
        var legacy = CounterAddress.Trim();
        var prefix = legacy.StartsWith("DM", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var word = int.Parse(legacy.AsSpan(prefix), System.Globalization.CultureInfo.InvariantCulture);
        return "D" + (word + offset).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public PlcOptions Copy() => new()
    {
        Enabled = Enabled, Ip = Ip, Port = Port, RemoteNode = RemoteNode, LocalNode = LocalNode,
        TimeoutMs = TimeoutMs, Retries = Retries, ResultAddress = ResultAddress,
        ReadyAddress = ReadyAddress, RefreshIntervalMs = RefreshIntervalMs,
        CounterAddress = CounterAddress, ResetCounterAddress = ResetCounterAddress,
        TotalCounterAddress = TotalCounterAddress, PassCounterAddress = PassCounterAddress,
        FailCounterAddress = FailCounterAddress, TimeoutCounterAddress = TimeoutCounterAddress,
        FailIncludesTimeout = FailIncludesTimeout, CounterDisplaySource = CounterDisplaySource,
        ClearErrorAddress = ClearErrorAddress,
    };

    public void CopyTo(PlcOptions target)
    {
        target.Enabled = Enabled;
        target.Ip = Ip;
        target.Port = Port;
        target.RemoteNode = RemoteNode;
        target.LocalNode = LocalNode;
        target.TimeoutMs = TimeoutMs;
        target.Retries = Retries;
        target.ResultAddress = ResultAddress;
        target.ReadyAddress = ReadyAddress;
        target.RefreshIntervalMs = RefreshIntervalMs;
        target.CounterAddress = CounterAddress;
        target.ResetCounterAddress = ResetCounterAddress;
        target.TotalCounterAddress = TotalCounterAddress;
        target.PassCounterAddress = PassCounterAddress;
        target.FailCounterAddress = FailCounterAddress;
        target.TimeoutCounterAddress = TimeoutCounterAddress;
        target.FailIncludesTimeout = FailIncludesTimeout;
        target.CounterDisplaySource = CounterDisplaySource;
        target.ClearErrorAddress = ClearErrorAddress;
    }

    public static PlcOptions FromConfig(PlcConfig target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ConfigValidation.IsValidIpOrHost(target.Ip))
            throw new ArgumentException("IP/hostname PLC không hợp lệ.", nameof(target));
        if (target.Port is < 1 or > 65535 || target.RemoteNode is < 0 or > 255
            || target.LocalNode is < 0 or > 255 || target.TimeoutMs is < 1 or > 60000
            || target.Retries is < 0 or > 10 || target.RefreshIntervalMs is < 100 or > 60000)
            throw new ArgumentException("Thông số kết nối PLC ngoài phạm vi cho phép.", nameof(target));
        if (!IsWordAddress(target.ResultAddress) || !IsWordAddress(target.ReadyAddress))
            throw new ArgumentException("Địa chỉ kết quả và Ready phải là word D0–D32767 (VD: D12, D16).", nameof(target));
        if (!ConfigValidation.IsValidPlcWordAddress(target.CounterAddress, 10)
            || new[] { target.TotalCounterAddress, target.PassCounterAddress, target.FailCounterAddress,
                target.TimeoutCounterAddress }.Any(address => !string.IsNullOrWhiteSpace(address)
                    && !ConfigValidation.IsValidPlcWordAddress(address, 2))
            || new[] { target.ResetCounterAddress, target.ClearErrorAddress }.Any(address =>
                !string.IsNullOrWhiteSpace(address) && !IsWordAddress(address)))
            throw new ArgumentException("Địa chỉ bộ đếm phải đủ 2 word; reset và Clear phải là word D0–D32767.", nameof(target));
        if (target.CounterDisplaySource?.Trim() is not ("PLC" or "App"))
            throw new ArgumentException("Nguồn số đếm hiển thị phải là PLC hoặc App.", nameof(target));
        return new PlcOptions
        {
            Enabled = target.Enabled, Ip = target.Ip.Trim(), Port = target.Port,
            RemoteNode = (byte)target.RemoteNode, LocalNode = (byte)target.LocalNode,
            TimeoutMs = target.TimeoutMs, Retries = target.Retries,
            ResultAddress = target.ResultAddress.Trim(), ReadyAddress = target.ReadyAddress.Trim(),
            RefreshIntervalMs = target.RefreshIntervalMs,
            CounterAddress = target.CounterAddress.Trim(), ResetCounterAddress = target.ResetCounterAddress?.Trim() ?? string.Empty,
            TotalCounterAddress = target.TotalCounterAddress?.Trim() ?? string.Empty,
            PassCounterAddress = target.PassCounterAddress?.Trim() ?? string.Empty,
            FailCounterAddress = target.FailCounterAddress?.Trim() ?? string.Empty,
            TimeoutCounterAddress = target.TimeoutCounterAddress?.Trim() ?? string.Empty,
            FailIncludesTimeout = target.FailIncludesTimeout, CounterDisplaySource = target.CounterDisplaySource.Trim(),
            ClearErrorAddress = target.ClearErrorAddress?.Trim() ?? string.Empty,
        };
    }

    private static bool IsWordAddress(string? address)
    {
        var value = address?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return false;
        var prefixLength = value.StartsWith("DM", StringComparison.OrdinalIgnoreCase) ? 2
            : value.StartsWith("D", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return prefixLength > 0 && int.TryParse(value.AsSpan(prefixLength),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var word) && word is >= 0 and <= 32767;
    }
}
