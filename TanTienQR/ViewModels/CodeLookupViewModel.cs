using System.Collections.ObjectModel;
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
///
/// <para>
/// Mỗi mã hiển thị trạng thái gửi lên Checkee (Chưa gửi / Đã gửi / Lỗi) đọc
/// từ cột <c>SendStatus</c> của cùng bảng. Bảng "Mã gửi lỗi" liệt kê các mã
/// bị Checkee từ chối (HTTP != 200) kèm chi tiết lỗi và nút "Gửi lại".
/// </para>
/// </summary>
internal sealed partial class CodeLookupViewModel : ViewModelBase, IDisposable
{
    /// <summary>Số bản ghi tối đa tải về một lần tra cứu.</summary>
    private const int MaxRows = 200;

    /// <summary>Số mã lỗi tối đa liệt kê trong bảng "Mã gửi lỗi".</summary>
    private const int MaxErrorRows = 300;

    private readonly IScanRecordQuery _query;
    private readonly IScanRecordWriter _writer;
    private readonly IScanSendStatusStore _sendStore;
    private readonly CheckeeSendWorker _sendWorker;
    private readonly HandheldScannerService _scanner;
    private readonly IUserActionLogger _actionLog;

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    public CodeLookupViewModel(
        IScanRecordQuery query,
        IScanRecordWriter writer,
        IScanSendStatusStore sendStore,
        CheckeeSendWorker sendWorker,
        HandheldScannerService scanner,
        IUserActionLogger actionLog)
    {
        _query = query;
        _writer = writer;
        _sendStore = sendStore;
        _sendWorker = sendWorker;
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
        CheckeeStatusText = _sendWorker.State.Status;
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
    [NotifyPropertyChangedFor(nameof(HasStatus))]
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

    // ---------- Trạng thái gửi Checkee ----------

    /// <summary>
    /// Thông điệp trạng thái worker gửi Checkee (hiển thị trên thanh công cụ).
    /// </summary>
    [ObservableProperty]
    public partial string CheckeeStatusText { get; set; } = string.Empty;

    /// <summary>True khi trạng thái Checkee hiện tại là lỗi (badge đỏ).</summary>
    public bool HasCheckeeError => _sendWorker.State.IsError;

    /// <summary>
    /// Danh sách mã bị gửi lỗi lên Checkee (bảng "Mã gửi lỗi").
    /// </summary>
    public ObservableCollection<CodeSendErrorRowViewModel> ErrorRows { get; } = [];

    /// <summary>True khi bảng "Mã gửi lỗi" có dữ liệu.</summary>
    public bool HasErrorRows => ErrorRows.Count > 0;

    /// <summary>True khi đang trong lúc tải / xử lý bảng mã gửi lỗi.</summary>
    [ObservableProperty]
    public partial bool IsErrorTableBusy { get; set; }

    /// <summary>True khi đang gửi lại các mã lỗi.</summary>
    [ObservableProperty]
    public partial bool IsResending { get; set; }

    /// <summary>Số mã lỗi đang hiển thị (dùng cho tiêu đề bảng).</summary>
    public string ErrorCountText => ErrorRows.Count > 0
        ? $"{ErrorRows.Count} mã lỗi"
        : "Không có mã lỗi";

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

        CheckeeStatusText = _sendWorker.State.Status;
        OnPropertyChanged(nameof(HasCheckeeError));

        // Nạp bảng mã gửi lỗi (nếu có) để người dùng thấy ngay.
        await LoadErrorRowsAsync().ConfigureAwait(true);
    }

    // ---------- Bảng "Mã gửi lỗi" ----------

    /// <summary>
    /// Nạp danh sách mã bị Checkee từ chối (HTTP != 200) kèm chi tiết lỗi.
    /// </summary>
    [RelayCommand]
    public async Task LoadErrorRowsAsync()
    {
        IsErrorTableBusy = true;

        try
        {
            var items = await _sendStore
                .GetBySendStatusAsync(new ScanSendFilter
                {
                    SendStatus = ScanSendStatuses.Failed,
                    Limit = MaxErrorRows,
                })
                .ConfigureAwait(true);

            ErrorRows.Clear();
            foreach (var item in items)
            {
                ErrorRows.Add(new CodeSendErrorRowViewModel(item));
            }

            OnPropertyChanged(nameof(HasErrorRows));
            OnPropertyChanged(nameof(ErrorCountText));
        }
        catch (Exception ex)
        {
            StatusMessage = "Không tải được danh sách mã gửi lỗi: " + ex.Message;
        }
        finally
        {
            IsErrorTableBusy = false;
        }
    }

    /// <summary>
    /// Gửi lại toàn bộ mã đang bị lỗi: đưa chúng về trạng thái chờ gửi rồi
    /// chạy một chu kỳ gửi ngay (không cần chờ hết chu kỳ định kỳ).
    /// </summary>
    [RelayCommand]
    public async Task ResendFailedAsync()
    {
        if (ErrorRows.Count == 0)
        {
            StatusMessage = "Không có mã lỗi nào để gửi lại.";
            return;
        }

        IsResending = true;

        try
        {
            var ids = ErrorRows.Select(r => r.RecordId).ToList();

            StatusMessage = "Đang đưa mã về trạng thái chờ và gửi lại...";
            var (reset, sent) = await _sendWorker.ResendFailedAsync(ids).ConfigureAwait(true);

            StatusMessage = sent > 0
                ? $"Đã gửi lại thành công {sent} mã."
                : "Không gửi lại được mã nào. Xem chi tiết lỗi trong bảng bên dưới.";

            _actionLog.Log(
                "CodeLookup.ResendFailed",
                "ScanRecords",
                $"reset={reset}; sent={sent}",
                sent > 0 ? UserActionResults.Success : UserActionResults.Failed);

            CheckeeStatusText = _sendWorker.State.Status;
            OnPropertyChanged(nameof(HasCheckeeError));

            await LoadErrorRowsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không gửi lại được: " + ex.Message;

            _actionLog.Log(
                "CodeLookup.ResendFailed",
                "ScanRecords",
                $"error={ex.Message}",
                UserActionResults.Failed);
        }
        finally
        {
            IsResending = false;
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
            if (_scanner.IsIntakeSuspended)
                return;

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
/// <para>
/// Bọc <see cref="ScanRecordEntity"/> lại để có thuộc tính hiển thị sẵn (chuỗi
/// đã format, class style cho màu) — giúp XAML không phải dùng converter.
/// </para>
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

        // ---- Trạng thái gửi lên Checkee ----
        SendStatus = ScanSendStatuses.Normalize(entity.SendStatus);
        SendStatusText = SendStatus switch
        {
            ScanSendStatuses.Sent => "Đã gửi",
            ScanSendStatuses.Failed => "Lỗi",
            _ => "Chưa gửi",
        };
        SendMessage = entity.SendMessage ?? string.Empty;
        SendStatusCode = entity.SendStatusCode;
        SendAttempts = entity.SendAttempts;
        SendTimeText = entity.SendTimeUtc is null
            ? "—"
            : entity.SendTimeUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", ViCulture);
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

    // ---------- Trạng thái gửi Checkee ----------

    /// <summary>Trạng thái gửi lên Checkee (Pending / Sent / Failed).</summary>
    public string SendStatus { get; }

    /// <summary>Nhãn trạng thái gửi (Chưa gửi / Đã gửi / Lỗi).</summary>
    public string SendStatusText { get; }

    /// <summary>Thông điệp chi tiết lỗi (nếu có).</summary>
    public string SendMessage { get; }

    /// <summary>Mã HTTP Checkee trả về (0 nếu không có phản hồi).</summary>
    public int SendStatusCode { get; }

    /// <summary>Số lần đã thử gửi.</summary>
    public int SendAttempts { get; }

    /// <summary>Thời điểm gửi thành công gần nhất ("—" nếu chưa gửi).</summary>
    public string SendTimeText { get; }

    /// <summary>True khi mã đã gửi thành công lên Checkee.</summary>
    public bool IsSendSent => SendStatus == ScanSendStatuses.Sent;

    /// <summary>True khi mã gửi lên Checkee bị lỗi.</summary>
    public bool IsSendFailed => SendStatus == ScanSendStatuses.Failed;

    /// <summary>True khi mã chưa được gửi lên Checkee.</summary>
    public bool IsSendPending => SendStatus == ScanSendStatuses.Pending;

    /// <summary>Class style cho badge trạng thái gửi.</summary>
    public string SendBadgeClass => SendStatus switch
    {
        ScanSendStatuses.Sent => "running",
        ScanSendStatuses.Failed => "fault",
        _ => "warning",
    };

    /// <summary>Chi tiết lỗi đầy đủ để hiển thị (rỗng nếu không lỗi).</summary>
    public string SendDetailText => IsSendFailed
        ? BuildSendDetail()
        : string.Empty;

    /// <summary>
    /// Dựng nội dung chi tiết lỗi (mã HTTP + thông điệp + số lần thử).
    /// </summary>
    private string BuildSendDetail()
    {
        var parts = new List<string>();

        if (SendStatusCode > 0)
        {
            parts.Add($"HTTP {SendStatusCode}");
        }

        if (!string.IsNullOrWhiteSpace(SendMessage))
        {
            parts.Add(SendMessage);
        }

        parts.Add($"Số lần thử: {SendAttempts}");

        return string.Join(" · ", parts);
    }

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

/// <summary>
/// Một dòng trong bảng "Mã gửi lỗi" — mã bị Checkee từ chối kèm chi tiết lỗi.
/// </summary>
public sealed class CodeSendErrorRowViewModel
{
    private static readonly CultureInfo ViCulture = CreateViCulture();

    /// <summary>
    /// Bọc một bản ghi bị gửi lỗi.
    /// </summary>
    /// <param name="entity">Bản ghi gốc.</param>
    public CodeSendErrorRowViewModel(ScanRecordEntity entity)
    {
        RecordId = entity.Id;
        CodeText = string.IsNullOrWhiteSpace(entity.Serial)
            ? (string.IsNullOrWhiteSpace(entity.Code) ? entity.Raw : entity.Code)
            : entity.Serial;
        GtinText = string.IsNullOrWhiteSpace(entity.Gtin) ? "—" : entity.Gtin;
        BatchText = string.IsNullOrWhiteSpace(entity.BatchNo)
            || string.Equals(entity.BatchNo, ScanBatchDefaults.NoBatch, StringComparison.Ordinal)
                ? "—"
                : entity.BatchNo;
        TimeText = entity.ReceivedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", ViCulture);

        StatusCode = entity.SendStatusCode;
        Message = string.IsNullOrWhiteSpace(entity.SendMessage)
            ? "Không rõ nguyên nhân (không có thông điệp từ máy chủ)."
            : entity.SendMessage;
        Attempts = entity.SendAttempts;

        LastAttemptText = entity.SendAttemptUtc is null
            ? "—"
            : entity.SendAttemptUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", ViCulture);
    }

    /// <summary>Khoá bản ghi (dùng khi đặt lại trạng thái để gửi lại).</summary>
    public long RecordId { get; }

    /// <summary>Mã / serial bị lỗi.</summary>
    public string CodeText { get; }

    /// <summary>GTIN ("—" nếu không có).</summary>
    public string GtinText { get; }

    /// <summary>Mã lô ("—" nếu không có).</summary>
    public string BatchText { get; }

    /// <summary>Thời điểm quét.</summary>
    public string TimeText { get; }

    /// <summary>Mã HTTP Checkee trả về (0 nếu không có phản hồi).</summary>
    public int StatusCode { get; }

    /// <summary>Thông điệp lỗi (tiếng Việt).</summary>
    public string Message { get; }

    /// <summary>Số lần đã thử gửi.</summary>
    public int Attempts { get; }

    /// <summary>Thời điểm thử gửi gần nhất.</summary>
    public string LastAttemptText { get; }

    /// <summary>Nhãn mã HTTP ("—" nếu không có phản hồi).</summary>
    public string StatusCodeText => StatusCode > 0 ? $"HTTP {StatusCode}" : "Không phản hồi";

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
