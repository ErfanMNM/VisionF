using System.Globalization;
using Avalonia.Threading;
using ClassLibraryMT.Logging;
using ClassLibraryMT.Scans;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanTienQR.Services;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho tab "Tra cứu mã" — tra cứu mã đã chạy qua hệ thống.
///
/// <para>
/// Nguồn dữ liệu là bảng <c>ScanRecords</c> (SQLite <c>logs/scans.db</c>) —
/// mọi mã đã quét qua camera đều được ghi vào đó. Truy vấn bằng
/// <see cref="IScanRecordQuery"/> với bộ lọc <c>CodeContains</c> (khớp theo
/// <c>Code</c>, <c>Serial</c> hoặc <c>Raw</c>).
/// </para>
///
/// <para>
/// Mã có thể được nhập tay (ô nhập + bàn phím ảo) hoặc quét bằng handheld
/// cầm tay — khi handheld bắn mã, tab tự động tra cứu luôn.
/// </para>
/// </summary>
internal sealed partial class CodeLookupViewModel : ViewModelBase, IDisposable
{
    /// <summary>Số bản ghi tối đa tải về một lần tra cứu.</summary>
    private const int MaxRows = 200;

    private readonly IScanRecordQuery _query;
    private readonly IScanRecordWriter _writer;
    private readonly HandheldScannerService _scanner;
    private readonly IUserActionLogger _actionLog;

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    public CodeLookupViewModel(
        IScanRecordQuery query,
        IScanRecordWriter writer,
        HandheldScannerService scanner,
        IUserActionLogger actionLog)
    {
        _query = query;
        _writer = writer;
        _scanner = scanner;
        _actionLog = actionLog;

        // Mã từ handheld -> tự động tra cứu (dispatch về luồng UI vì event
        // được phát từ luồng I/O).
        _scanner.CodeReceived += OnHandheldCodeReceived;

        // Timer làm mới trạng thái kết nối handheld (service cập nhật từ
        // luồng I/O nên không có event).
        _scannerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000),
        };
        _scannerTimer.Tick += (_, _) => ScannerStatusText = _scanner.StatusText;
        _scannerTimer.Start();

        ScannerStatusText = _scanner.StatusText;
    }

    // ---------- Ô nhập ----------

    /// <summary>Nội dung ô nhập mã (nhập tay hoặc mã do handheld quét).</summary>
    [ObservableProperty]
    public partial string CodeInput { get; set; } = string.Empty;

    /// <summary>True khi đang tra cứu (để disable nút).</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Thông báo trạng thái (kết quả / lỗi).</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>True khi có thông báo để hiển thị.</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>True khi chưa có kết quả nào để hiển thị.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>True khi đang có kết quả tra cứu (dùng để ẩn/hiện card tóm tắt).</summary>
    public bool HasResult => Latest is not null;

    // ---------- Kết quả ----------

    /// <summary>Lần quét gần nhất của mã (card tóm tắt).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(IsActivated))]
    [NotifyPropertyChangedFor(nameof(IsPendingActivation))]
    [NotifyPropertyChangedFor(nameof(ActivationStatusText))]
    [NotifyPropertyChangedFor(nameof(ActivationBadgeClass))]
    public partial CodeLookupRowViewModel? Latest { get; set; }

    /// <summary>
    /// Các lần quét của mã (dùng cho bảng thông tin chi tiết bên dưới).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<CodeLookupRowViewModel> Rows { get; set; } = [];

    /// <summary>Tổng số bản ghi khớp (có thể lớn hơn số dòng đang hiển thị).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanCountText))]
    public partial long TotalCount { get; set; }

    /// <summary>
    /// True khi mã đã được kích hoạt (đã gửi lên server).
    ///
    /// <para>
    /// Xác định theo PO: mã đã quét với kết quả Pass và thuộc PO nào đó
    /// thì coi như đã kích hoạt — tương đương logic
    /// <c>GetActiveByQRContent</c> trong FScan.cs của dự án tham khảo.
    /// </para>
    /// </summary>
    public bool IsActivated => Latest is not null && Latest.IsPass && Latest.HasOrder;

    /// <summary>
    /// True khi mã quét được nhưng chưa kích hoạt (Pass nhưng không thuộc
    /// PO nào, hoặc quét nhưng không đạt).
    /// </summary>
    public bool IsPendingActivation =>
        Latest is not null && !IsActivated && Latest.StatusText == "Pass";

    /// <summary>Thông điệp trạng thái kích hoạt (dùng cho banner kết quả).</summary>
    public string ActivationStatusText => Latest switch
    {
        null => string.Empty,
        _ when IsActivated => "Mã đã kích hoạt",
        _ when IsPendingActivation => "Mã chưa kích hoạt (Pass)",
        _ => $"Mã có status: {Latest.StatusText}",
    };

    /// <summary>Class style cho banner trạng thái kích hoạt.</summary>
    public string ActivationBadgeClass => Latest switch
    {
        null => "",
        _ when IsActivated => "running",
        _ when IsPendingActivation => "warning",
        _ => "fault",
    };

    /// <summary>
    /// Số lần mã này đã được quét (tổng số bản ghi khớp).
    /// </summary>
    public string ScanCountText => TotalCount > 0
        ? $"{TotalCount} lần quét"
        : "Chưa quét lần nào";

    /// <summary>Tóm tắt bộ lọc đang áp dụng.</summary>
    public string FilterSummary =>
        string.IsNullOrWhiteSpace(CodeInput)
            ? "Chưa nhập mã"
            : $"Mã: {CodeInput.Trim()}";

    // ---------- Lệnh ----------

    /// <summary>
    /// Tra cứu mã đang nhập.
    /// </summary>
    [RelayCommand]
    public async Task LookupAsync()
    {
        var code = CodeInput?.Trim() ?? string.Empty;

        if (code.Length == 0)
        {
            StatusMessage = "Hãy nhập hoặc quét mã cần tra cứu.";
            Rows = [];
            Latest = null;
            TotalCount = 0;
            return;
        }

        IsBusy = true;

        try
        {
            // Đẩy hết bản ghi đang chờ ghi xuống DB để thấy ngay mã vừa
            // quét (writer ghi theo batch 100 bản ghi / 1 giây).
            await _writer.FlushAsync().ConfigureAwait(true);

            var result = await _query
                .GetAsync(
                    new ScanRecordFilter { CodeContains = code },
                    page: 1,
                    pageSize: MaxRows)
                .ConfigureAwait(true);

            var rows = new List<CodeLookupRowViewModel>(result.Items.Count);
            foreach (var item in result.Items)
            {
                rows.Add(new CodeLookupRowViewModel(item));
            }

            Rows = rows;
            Latest = rows.Count > 0 ? rows[0] : null;
            TotalCount = result.TotalCount;

            StatusMessage = rows.Count > 0
                ? $"Tìm thấy {result.TotalCount} lần quét của mã \"{code}\"."
                : $"Không tìm thấy mã \"{code}\" nào đã quét qua hệ thống.";

            _actionLog.Log(
                "CodeLookup.Search",
                "ScanRecords",
                $"code={code}; matches={result.TotalCount}");
        }
        catch (Exception ex)
        {
            Rows = [];
            Latest = null;
            TotalCount = 0;
            StatusMessage = "Không tra cứu được: " + ex.Message;

            _actionLog.Log(
                "CodeLookup.Search",
                "ScanRecords",
                $"code={code}; error={ex.Message}",
                UserActionResults.Failed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Xoá ô nhập và kết quả.</summary>
    [RelayCommand]
    private void Clear()
    {
        CodeInput = string.Empty;
        Rows = [];
        Latest = null;
        TotalCount = 0;
        StatusMessage = string.Empty;
    }

    /// <summary>
    /// Nạp dữ liệu khi mở tab.
    ///
    /// <para>
    /// KHÔNG tự đổ bảng lịch sử: bảng chỉ hiển thị thông tin của mã vừa
    /// tra cứu (giống FScan.cs của dự án tham khảo). Mở tab không có gì để
    /// hiển thị — người dùng phải nhập/quét mã trước.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        // Chỉ đồng bộ bản ghi đang chờ ghi xuống DB, không nạp danh sách.
        try
        {
            await _writer.FlushAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không đồng bộ được dữ liệu quét mã: " + ex.Message;
        }
    }

    // ---------- Handheld ----------

    /// <summary>
    /// Trạng thái kết nối handheld (hiển thị trên thanh công cụ).
    ///
    /// Được làm mới bằng timer vì <see cref="HandheldScannerService"/> cập nhật
    /// trạng thái từ luồng I/O (không có event).
    /// </summary>
    [ObservableProperty]
    public partial string ScannerStatusText { get; set; } = "Chưa bật";

    /// <summary>Timer làm mới trạng thái handheld.</summary>
    private readonly DispatcherTimer _scannerTimer;

    /// <summary>
    /// Nhận mã từ handheld cầm tay -> điền vào ô nhập và tra cứu luôn.
    /// </summary>
    private void OnHandheldCodeReceived(string code)
    {
        // Event phát từ luồng I/O -> dispatch sang luồng UI.
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            CodeInput = code;
            await LookupAsync().ConfigureAwait(true);
        });
    }

    // ---------- Vòng đời ----------

    partial void OnCodeInputChanged(string value) => OnPropertyChanged(nameof(FilterSummary));

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>Gỡ đăng ký sự kiện khi dispose.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scanner.CodeReceived -= OnHandheldCodeReceived;

        _scannerTimer.Stop();
        _scannerTimer.Tick -= (_, _) => ScannerStatusText = _scanner.StatusText;

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Một dòng kết quả tra cứu mã.
///
/// Bọc <see cref="ScanRecordEntity"/> lại để có thuộc tính hiển thị sẵn (chuỗi
/// đã format, class style cho màu) — giúp XAML không phải dùng converter.
/// </summary>
public sealed class CodeLookupRowViewModel
{
    private static readonly CultureInfo ViCulture = CreateViCulture();

    /// <summary>
    /// Bọc một bản ghi từ database.
    /// </summary>
    /// <param name="entity">Bản ghi gốc.</param>
    public CodeLookupRowViewModel(ScanRecordEntity entity)
    {
        TimestampUtc = entity.ReceivedUtc;
        TimeText = entity.ReceivedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", ViCulture);

        CodeText = string.IsNullOrWhiteSpace(entity.Serial)
            ? (string.IsNullOrWhiteSpace(entity.Code) ? entity.Raw : entity.Code)
            : entity.Serial;

        GtinText = string.IsNullOrWhiteSpace(entity.Gtin) ? "—" : entity.Gtin;
        RawText = string.IsNullOrWhiteSpace(entity.Raw) ? "—" : entity.Raw;

        OrderNoText = string.IsNullOrWhiteSpace(entity.OrderNo) ? "—" : entity.OrderNo;
        HasOrder = !string.IsNullOrWhiteSpace(entity.OrderNo);

        BatchNoText = string.IsNullOrWhiteSpace(entity.BatchNo)
            || string.Equals(entity.BatchNo, ScanBatchDefaults.NoBatch, StringComparison.Ordinal)
                ? "—"
                : entity.BatchNo;

        StatusText = entity.Status switch
        {
            ScanStatuses.Pass => "Pass",
            ScanStatuses.Overload => "Quá tải",
            _ => "Fail",
        };

        PlcText = entity.PlcResult switch
        {
            ScanPlcResults.Ok => "Đã ghi",
            ScanPlcResults.Skipped => "Bỏ qua",
            _ => "Lỗi",
        };

        ProcessingTimeText = $"{entity.ProcessingTimeMs:F3} ms";
    }

    /// <summary>Thời điểm nhận mã (UTC) — dùng để sắp xếp / tính toán.</summary>
    public DateTime TimestampUtc { get; }

    /// <summary>Thời điểm nhận mã (giờ địa phương).</summary>
    public string TimeText { get; }

    /// <summary>Mã / serial.</summary>
    public string CodeText { get; }

    /// <summary>GTIN nếu có.</summary>
    public string GtinText { get; }

    /// <summary>Nội dung gốc camera gửi lên.</summary>
    public string RawText { get; }

    /// <summary>Mã lệnh sản xuất (PO) mà mã này thuộc về ("—" nếu không có).</summary>
    public string OrderNoText { get; }

    /// <summary>True khi mã thuộc một PO (dùng để suy ra trạng thái kích hoạt).</summary>
    public bool HasOrder { get; }

    /// <summary>Số batch sản xuất ("—" nếu mặc định / không có).</summary>
    public string BatchNoText { get; }

    /// <summary>Trạng thái (Pass / Fail / Quá tải).</summary>
    public string StatusText { get; }

    /// <summary>Trạng thái ghi xuống PLC.</summary>
    public string PlcText { get; }

    /// <summary>Thời gian xử lý.</summary>
    public string ProcessingTimeText { get; }

    /// <summary>True khi kết quả là Pass (badge xanh).</summary>
    public bool IsPass => StatusText == "Pass";

    /// <summary>True khi mã bị quá tải (badge vàng).</summary>
    public bool IsOverload => StatusText == "Quá tải";

    /// <summary>True khi kết quả là Fail (badge đỏ).</summary>
    public bool IsFail => StatusText == "Fail";

    /// <summary>Class style cho badge kết quả.</summary>
    public string StatusBadgeClass => StatusText switch
    {
        "Pass" => "running",
        "Quá tải" => "warning",
        _ => "fault",
    };

    /// <summary>Class style cho trạng thái ghi PLC.</summary>
    public string PlcBadgeClass => PlcText switch
    {
        "Đã ghi" => "running",
        "Bỏ qua" => "warning",
        _ => "fault",
    };

    /// <summary>True khi đã ghi xuống PLC thành công (badge xanh).</summary>
    public bool IsPlcOk => PlcText == "Đã ghi";

    /// <summary>True khi bỏ qua ghi PLC (badge vàng).</summary>
    public bool IsPlcSkipped => PlcText == "Bỏ qua";

    /// <summary>True khi ghi PLC thất bại (badge đỏ).</summary>
    public bool IsPlcFailed => PlcText == "Lỗi";

    private static CultureInfo CreateViCulture()
    {
        try
        {
            return new CultureInfo("vi-VN");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
