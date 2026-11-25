using System.Collections.Concurrent;
using ClassLibraryMT.Print;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TanTienQR.Services;

/// <summary>Trạng thái máy in được worker nền cập nhật để UI hiển thị.</summary>
internal sealed record PrinterMonitorSnapshot(
    string StatusText,
    string DetailText,
    string InkText,
    string QueueText,
    string Endpoint,
    DateTimeOffset ObservedUtc);

/// <summary>
/// Tự kết nối và thăm dò máy G10 ngay khi app khởi động, giống camera/PLC.
/// Worker cũng gom lịch sử lệnh để tab Máy in hiển thị.
/// </summary>
internal sealed class PrinterMonitorService : BackgroundService
{
    private const int PollIntervalMs = 3000;
    private const int HistoryCapacity = 50;

    private readonly PrintService _print;
    private readonly IPrinterClient _client;
    private readonly PrintOptions _options;
    private readonly IPrintLicenseGate _license;
    private readonly ILogger<PrinterMonitorService> _logger;
    private readonly ConcurrentQueue<PrinterCommandLog> _commands = new();
    private PrinterMonitorSnapshot _snapshot = new(
        "Chưa kết nối", "Chưa kiểm tra máy in.", "Mực: —", "Chưa có dữ liệu hàng đợi.",
        "", DateTimeOffset.Now);
    private int _commandVersion;

    public PrinterMonitorService(
        PrintService print,
        IPrinterClient client,
        PrintOptions options,
        IPrintLicenseGate license,
        ILogger<PrinterMonitorService> logger)
    {
        _print = print;
        _client = client;
        _options = options;
        _license = license;
        _logger = logger;
        if (!_options.Enabled)
            Publish("Đã tắt", "Module máy in đã tắt.", _snapshot.InkText, _snapshot.QueueText);
    }

    public PrinterMonitorSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Tăng mỗi khi có lệnh mới hoặc lịch sử bị xoá.</summary>
    public int CommandVersion => Volatile.Read(ref _commandVersion);

    public IReadOnlyList<PrinterCommandLog> RecentCommands =>
        _commands.ToArray().Reverse().Take(HistoryCapacity).ToArray();

    public void ClearHistory()
    {
        while (_commands.TryDequeue(out _)) { }
        Interlocked.Increment(ref _commandVersion);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            Publish("Đã tắt", "Module máy in đã tắt.", _snapshot.InkText, _snapshot.QueueText);
            return;
        }

        if (!_license.CanUse)
        {
            Publish("Chưa có quyền", _license.Message, _snapshot.InkText, _snapshot.QueueText);
            return;
        }

        _client.CommandObserved += OnCommandObserved;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(PollIntervalMs, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _client.CommandObserved -= OnCommandObserved;
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            var status = await _print.RefreshStatusAsync(ct).ConfigureAwait(false);
            var summary = await _print.GetSummaryAsync(ct).ConfigureAwait(false);
            var detail = $"{(status.IsPrinting ? "Đang in" : "Đang chờ")} · buffer {status.BufferLength?.ToString() ?? "—"} · {status.Message}";
            var ink = $"Mực: {(status.Ink1Percent?.ToString("F1") ?? "—")}% / {(status.Ink2Percent?.ToString("F1") ?? "—")}%";
            var queue = $"Chờ {summary.Pending:N0} · Đã gửi {summary.Sent:N0} · Đã in {summary.Printed:N0} · Không phản hồi {summary.NoResponse:N0}";
            Publish("Đã kết nối", detail, ink, queue);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chưa đọc được trạng thái máy in; sẽ thử lại.");
            Publish("Đang kết nối lại", ex.Message, _snapshot.InkText, _snapshot.QueueText);
        }
    }

    private string Endpoint
    {
        get
        {
            var printer = _options.Printers.FirstOrDefault(p =>
                p.Id.Equals(_options.DefaultPrinterId, StringComparison.OrdinalIgnoreCase));
            return printer is null ? "Chưa cấu hình endpoint" : $"{printer.Protocol} · {printer.Ip}:{printer.Port}";
        }
    }

    private void Publish(string status, string detail, string ink, string queue) =>
        Volatile.Write(ref _snapshot, new PrinterMonitorSnapshot(status, detail, ink, queue, Endpoint, DateTimeOffset.Now));

    private void OnCommandObserved(PrinterCommandLog log)
    {
        _commands.Enqueue(log);
        while (_commands.Count > HistoryCapacity && _commands.TryDequeue(out _)) { }
        Interlocked.Increment(ref _commandVersion);
    }
}
