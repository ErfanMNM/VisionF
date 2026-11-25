namespace ClassLibraryMT.MesSimulator;

/// <summary>Cấu hình plugin giả lập MES đẩy PO qua HTTP.</summary>
public sealed class MesSimulatorOptions
{
    public const string SectionName = "MesSimulator";

    /// <summary>Bật worker upload một file CodeContent khi khởi động.</summary>
    public bool Enabled { get; set; }

    /// <summary>Địa chỉ TanTienQR đang chạy HTTP server nhận PO.</summary>
    public string TargetUrl { get; set; } = "http://127.0.0.1:8085";

    /// <summary>API key gửi ở header x-api-key (có thể để trống).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Legacy setting retained for configuration compatibility; file uploads are one-shot.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Số dòng mã của file trước lọc trùng.</summary>
    public int OrderQty { get; set; } = 10;

    /// <summary>Legacy setting retained for configuration compatibility; codes are never generated.</summary>
    public string PoPrefix { get; set; } = "MES-TEST";

    /// <summary>File CSV/JSON/XLSX một cột CodeContent; bắt buộc khi bật worker.</summary>
    public string CustomFilePath { get; set; } = string.Empty;

    /// <summary>Tên PO dùng khi tải file custom.</summary>
    public string CustomPoName { get; set; } = string.Empty;

    /// <summary>Timeout một request upload.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public TimeSpan ResolveInterval() =>
        TimeSpan.FromSeconds(Math.Clamp(IntervalSeconds, 1, 86_400));

    public TimeSpan ResolveTimeout() =>
        TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 5, 3_600));

    public string ResolveEndpoint()
    {
        var url = (TargetUrl ?? string.Empty).Trim();
        if (url.Length == 0)
            url = "http://127.0.0.1:8085";

        return url.TrimEnd('/') + "/api/po";
    }

    public string ResolveCustomFilePath() =>
        string.IsNullOrWhiteSpace(CustomFilePath)
            ? string.Empty
            : Path.IsPathRooted(CustomFilePath)
                ? Path.GetFullPath(CustomFilePath.Trim())
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, CustomFilePath.Trim()));
}
