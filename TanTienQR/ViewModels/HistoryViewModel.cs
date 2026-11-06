using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho trang "Lịch sử thao tác" (mục nhà phát triển).
///
/// Đọc dữ liệu từ <see cref="ILogQueryService"/> (bảng <c>UserActions</c>),
/// hỗ trợ lọc theo khoảng thời gian / hành động / kết quả, phân trang và
/// xuất ra JSON hoặc CSV.
/// </summary>
internal partial class HistoryViewModel : ViewModelBase
{
    private readonly ILogQueryService _query;
    private readonly IUserActionLogger _actionLog;
    private readonly LoggingHistoryOptions _opt;
    private Window? _window;

    /// <summary>
    /// Số bản ghi tối đa cho một lần xuất file (tránh xuất quá nhiều làm treo UI).
    /// </summary>
    private const int ExportMaxRows = 50_000;

    public HistoryViewModel(
        ILogQueryService query,
        IUserActionLogger actionLog,
        IOptions<LoggingHistoryOptions> options)
    {
        _query = query;
        _actionLog = actionLog;
        _opt = options.Value;

        // Mặc định xem 7 ngày gần nhất.
        //
        // Dùng DateTime vì CalendarDatePicker.SelectedDate của Avalonia 12 có
        // kiểu DateTime? (DatePicker dùng DateTimeOffset? và rộng tới 296px).
        (FromDate, ToDate) = DefaultDateRange();
    }

    // ---------- Bộ lọc ----------

    /// <summary>Ngày bắt đầu (inclusive).</summary>
    [ObservableProperty]
    public partial DateTime FromDate { get; set; }

    /// <summary>Ngày kết thúc (inclusive).</summary>
    [ObservableProperty]
    public partial DateTime ToDate { get; set; }

    /// <summary>Lọc theo tên hành động (chuỗi con, để trống = tất cả).</summary>
    [ObservableProperty]
    public partial string ActionFilter { get; set; } = string.Empty;

    /// <summary>
    /// Lọc theo kết quả: để trống = tất cả.
    ///
    /// Khai báo nullable vì <c>ComboBox.SelectedItem</c> có kiểu
    /// <c>object?</c> và có thể đẩy giá trị null xuống khi người dùng xoá lựa chọn.
    /// </summary>
    [ObservableProperty]
    public partial string? ResultFilter { get; set; } = AllResultsLabel;

    /// <summary>
    /// Nhãn của mục "không lọc theo kết quả" trong combo box. Trước đây mục này là
    /// chuỗi rỗng nên combo box trông như trống; giờ hiển thị chữ rõ ràng.
    /// </summary>
    public const string AllResultsLabel = "Tất cả";

    /// <summary>Các giá trị kết quả để hiển thị trong combo box.</summary>
    public IReadOnlyList<string> ResultOptions { get; } =
    [
        AllResultsLabel,
        UserActionResults.Success,
        UserActionResults.Failed,
        UserActionResults.Cancelled,
    ];

    // ---------- Dữ liệu ----------

    /// <summary>Các bản ghi hiện tại (đã bọc cho XAML).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HistoryRowViewModel> Items { get; set; } = [];

    /// <summary>True khi danh sách rỗng (để hiện thông báo "không có dữ liệu").</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Tổng số bản ghi khớp bộ lọc.</summary>
    [ObservableProperty]
    public partial long TotalCount { get; set; }

    /// <summary>Trang hiện tại (bắt đầu từ 1).</summary>
    [ObservableProperty]
    public partial int CurrentPage { get; set; } = 1;

    /// <summary>Tổng số trang.</summary>
    [ObservableProperty]
    public partial int TotalPages { get; set; }

    /// <summary>Thông báo trạng thái (lỗi / kết quả xuất file).</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>True khi có thông báo để hiện.</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>True khi còn trang sau.</summary>
    public bool HasNextPage => CurrentPage < TotalPages;

    /// <summary>True khi còn trang trước.</summary>
    public bool HasPreviousPage => CurrentPage > 1;

    /// <summary>Chuỗi phân trang dạng "Trang 1 / 3 — 25 bản ghi".</summary>
    public string PaginationText =>
        TotalPages <= 0
            ? "Không có bản ghi"
            : $"Trang {CurrentPage} / {TotalPages} — {TotalCount} bản ghi";

    /// <summary>
    /// Tiêu đề tóm tắt bộ lọc đang áp dụng, hiển thị trên thanh công cụ.
    /// </summary>
    public string FilterSummary
    {
        get
        {
            var parts = new List<string>(3);

            if (!string.IsNullOrWhiteSpace(ActionFilter))
            {
                parts.Add($"hành động ~ \"{ActionFilter.Trim()}\"");
            }

            if (HasResultFilter)
            {
                parts.Add($"kết quả = {ResultFilter!.Trim()}");
            }

            return parts.Count == 0
                ? "Tất cả thao tác"
                : string.Join(" · ", parts);
        }
    }

    /// <summary>True khi bộ lọc đang lọc (để hiện nút "Xoá lọc" nổi bật).</summary>
    public bool HasActiveFilter =>
        !string.IsNullOrWhiteSpace(ActionFilter) || HasResultFilter;

    /// <summary>True khi người dùng chọn một kết quả cụ thể (khác "Tất cả").</summary>
    private bool HasResultFilter =>
        !string.IsNullOrWhiteSpace(ResultFilter) && ResultFilter != AllResultsLabel;

    /// <summary>
    /// True khi mục nhà phát triển (trang Lịch sử) được bật trong cấu hình
    /// (<c>Logging:History:Enabled</c>).
    /// </summary>
    public bool IsHistoryEnabled => _opt.Enabled;

    // ---------- Lệnh ----------

    /// <summary>Tải dữ liệu theo bộ lọc hiện tại.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var result = await _query
                .GetUserActionsAsync(BuildFilter(), CurrentPage, _opt.PageSize)
                .ConfigureAwait(true);

            // Bọc cho XAML: chuỗi đã format + class màu sẵn.
            var rows = new List<HistoryRowViewModel>(result.Items.Count);
            foreach (var entry in result.Items)
            {
                rows.Add(new HistoryRowViewModel(entry));
            }

            Items = rows;
            TotalCount = result.TotalCount;
            TotalPages = result.TotalPages;
            CurrentPage = result.Page;

            if (result.Items.Count == 0)
            {
                StatusMessage = "Không có bản ghi nào khớp bộ lọc.";
            }
        }
        catch (Exception ex)
        {
            Items = [];
            StatusMessage = "Không đọc được lịch sử: " + ex.Message;
        }
        finally
        {
            OnPropertyChanged(nameof(FilterSummary));
            OnPropertyChanged(nameof(HasActiveFilter));
        }
    }

    /// <summary>
    /// Bộ lọc hiện tại (chuyển sang giờ UTC theo múi giờ máy).
    /// </summary>
    private UserActionFilter BuildFilter()
    {
        // Ngày trên UI là ngày lịch của máy -> đổi sang UTC trước khi truy vấn.
        var from = DateTime.SpecifyKind(FromDate.Date, DateTimeKind.Local).ToUniversalTime();
        var to = DateTime.SpecifyKind(ToDate.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Local).ToUniversalTime();

        return new UserActionFilter
        {
            FromUtc = from,
            ToUtc = to,
            ActionContains = string.IsNullOrWhiteSpace(ActionFilter) ? null : ActionFilter.Trim(),
            Result = HasResultFilter ? ResultFilter : null,
        };
    }

    /// <summary>Áp dụng bộ lọc: về trang 1 rồi tải lại.</summary>
    [RelayCommand]
    private async Task ApplyFilterAsync()
    {
        CurrentPage = 1;
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Xoá bộ lọc về mặc định (7 ngày gần nhất, tất cả hành động).</summary>
    [RelayCommand]
    private async Task ResetFilterAsync()
    {
        (FromDate, ToDate) = DefaultDateRange();
        ActionFilter = string.Empty;
        ResultFilter = AllResultsLabel;
        CurrentPage = 1;

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Khoảng ngày mặc định: 7 ngày gần nhất (từ hôm nay lùi 7 ngày, đến hôm nay).
    ///
    /// Dùng <see cref="DateTime"/> vì <c>CalendarDatePicker.SelectedDate</c> của
    /// Avalonia 12 có kiểu <c>DateTime?</c>.
    /// </summary>
    private static (DateTime From, DateTime To) DefaultDateRange()
    {
        var today = DateTime.Today;

        // ToDate = hôm nay; BuildFilter() tự cộng thêm 1 ngày để bao trọn
        // ngày kết thúc (inclusive đến 23:59:59).
        return (today.AddDays(-7), today);
    }

    /// <summary>Sang trang sau.</summary>
    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!HasNextPage)
        {
            return;
        }

        CurrentPage++;
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Về trang trước.</summary>
    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (!HasPreviousPage)
        {
            return;
        }

        CurrentPage--;
        await LoadAsync().ConfigureAwait(true);
    }

    // ---------- Xuất dữ liệu ----------

    /// <summary>Gắn cửa sổ cha để mở hộp thoại lưu file.</summary>
    public void Attach(Window window) => _window = window;

    /// <summary>Xuất lịch sử ra file JSON.</summary>
    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        await ExportAsync("json").ConfigureAwait(true);
    }

    /// <summary>Xuất lịch sử ra file CSV.</summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        await ExportAsync("csv").ConfigureAwait(true);
    }

    /// <summary>
    /// Quy trình xuất: hỏi đường dẫn -> tải toàn bộ bản ghi khớp bộ lọc -> ghi file.
    /// </summary>
    private async Task ExportAsync(string extension)
    {
        if (_window is null)
        {
            StatusMessage = "Không mở được hộp thoại lưu file.";
            return;
        }

        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var suggested = $"user-actions-{stamp}.{extension}";

            var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = extension == "json" ? "Xuất lịch sử ra JSON" : "Xuất lịch sử ra CSV",
                SuggestedFileName = suggested,
                DefaultExtension = extension,
                FileTypeChoices =
                [
                    extension == "json"
                        ? new FilePickerFileType("JSON file") { Patterns = ["*.json"] }
                        : new FilePickerFileType("CSV file") { Patterns = ["*.csv"] },
                ],
            }).ConfigureAwait(true);

            if (file is null)
            {
                return;
            }

            // Lấy toàn bộ bản ghi khớp bộ lọc (không chỉ trang hiện tại).
            var all = await _query
                .GetUserActionsAsync(BuildFilter(), 1, ExportMaxRows)
                .ConfigureAwait(true);

            var content = extension == "json"
                ? BuildJson(all.Items)
                : BuildCsv(all.Items);

            await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await writer.WriteAsync(content).ConfigureAwait(true);

            StatusMessage = $"Đã xuất {all.Items.Count} bản ghi ra {file.Name}.";

            _actionLog.Log(
                "History.Export",
                extension.ToUpperInvariant(),
                $"file={file.Name}; rows={all.Items.Count}");
        }
        catch (Exception ex)
        {
            StatusMessage = "Không xuất được file: " + ex.Message;
        }
    }

    /// <summary>
    /// Dựng nội dung JSON. Dùng source-generated context để tương thích AOT.
    /// </summary>
    private static string BuildJson(IReadOnlyList<UserActionEntry> entries)
    {
        var payload = new HistoryExportPayload
        {
            ExportedAtUtc = DateTime.UtcNow,
            Count = entries.Count,
            Items = [.. entries],
        };

        // Dùng chung encoder nên chữ tiếng Việt giữ nguyên dạng đọc được
        // (không bị escape thành \uXXXX).
        return JsonSerializer.Serialize(payload, HistoryJsonContext.Indented.HistoryExportPayload);
    }

    /// <summary>
    /// Dựng nội dung CSV: có header, escape dấu ngoặc kép và dấu phẩy.
    /// </summary>
    private static string BuildCsv(IReadOnlyList<UserActionEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TimestampUtc,Action,Target,Detail,Result");

        foreach (var e in entries)
        {
            sb.Append(CsvEscape(e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(CsvEscape(e.Action)).Append(',');
            sb.Append(CsvEscape(e.Target)).Append(',');
            sb.Append(CsvEscape(e.DetailJson ?? string.Empty)).Append(',');
            sb.Append(CsvEscape(e.Result)).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escape một trường CSV: bọc trong dấu ngoặc kép nếu chứa dấu phẩy,
    /// xuống dòng hoặc dấu ngoặc kép (dấu ngoặc kép bị nhân đôi).
    /// </summary>
    private static string CsvEscape(string value)
    {
        if (value.Length == 0) return string.Empty;

        var needsQuote = value.Contains(',')
            || value.Contains('"')
            || value.Contains('\n')
            || value.Contains('\r');

        if (!needsQuote) return value;

        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    /// <summary>
    /// Báo lại các property phụ thuộc khi bộ lọc / trang thay đổi.
    /// </summary>
    private void NotifyPaginationChanged()
    {
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(PaginationText));
        OnPropertyChanged(nameof(HasStatus));
    }

    partial void OnResultFilterChanged(string? value)
    {
        NotifyPaginationChanged();
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(HasActiveFilter));
    }

    partial void OnActionFilterChanged(string value)
    {
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(HasActiveFilter));
    }

    partial void OnItemsChanged(IReadOnlyList<HistoryRowViewModel> value) =>
        OnPropertyChanged(nameof(IsEmpty));

    partial void OnCurrentPageChanged(int value) => NotifyPaginationChanged();

    partial void OnTotalPagesChanged(int value) => NotifyPaginationChanged();

    partial void OnTotalCountChanged(long value) => NotifyPaginationChanged();

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));
}

/// <summary>
/// Payload xuất JSON (gói cả metadata lẫn danh sách bản ghi).
/// </summary>
internal sealed class HistoryExportPayload
{
    /// <summary>Thời điểm xuất (UTC).</summary>
    public DateTime ExportedAtUtc { get; set; }

    /// <summary>Số bản ghi trong file.</summary>
    public int Count { get; set; }

    /// <summary>Danh sách bản ghi thao tác.</summary>
    public List<UserActionEntry> Items { get; set; } = [];
}

/// <summary>
/// Context JSON source-generated cho trang Lịch sử (AOT-safe).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HistoryExportPayload))]
[JsonSerializable(typeof(UserActionEntry))]
[JsonSerializable(typeof(List<UserActionEntry>))]
[JsonSerializable(typeof(string))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Context dùng encoder giữ nguyên chữ tiếng Việt (không escape thành
    /// <c>\uXXXX</c>) cho báo cáo xuất ra.
    /// </summary>
    public static HistoryJsonContext Indented { get; } = new(LogJsonOptions.Indented);
}