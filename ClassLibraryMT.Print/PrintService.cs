using ClassLibraryMT.Po;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Print;

public sealed class PrintService
{
    private readonly PrintQueueStore _store;
    private readonly ProductionOrderService _po;
    private readonly IPrinterClient _printer;
    private readonly PrintOptions _options;
    private readonly ILogger<PrintService>? _logger;
    private readonly IPrintLicenseGate _license;

    public PrintService(PrintQueueStore store, ProductionOrderService po, IPrinterClient printer, PrintOptions options, IPrintLicenseGate license, ILogger<PrintService>? logger = null)
    {
        _store = store; _po = po; _printer = printer; _options = options; _license = license; _logger = logger;
        _printer.PrintConfirmed += OnPrintConfirmedAsync;
    }

    public IPrinterClient Printer => _printer;
    public ProductionOrder? CurrentOrder => _po.CurrentOrder;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        EnsureLicensed();
        EnsureEnabled();
        await _printer.ConnectAsync(ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) => await _printer.DisconnectAsync(ct).ConfigureAwait(false);

    public async Task<PrinterStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default)
    {
        EnsureLicensed();
        EnsureEnabled();
        return await _printer.RefreshStatusAsync(ct).ConfigureAwait(false);
    }

    public async Task<PrintBatchResult?> SendNextBatchAsync(int quantity, CancellationToken ct = default)
    {
        EnsureLicensed();
        var order = _po.CurrentOrder;
        if (order is null || !_po.IsProductionRunning)
            throw new InvalidOperationException("Chưa có PO đang chạy.");
        EnsureEnabled();
        var status = await _printer.RefreshStatusAsync(ct).ConfigureAwait(false);
        var printerOptions = _options.Printers.FirstOrDefault(p => p.Id.Equals(_printer.PrinterId, StringComparison.OrdinalIgnoreCase));
        var capacity = Math.Min(printerOptions?.BufferCapacity ?? _options.BatchSize, _options.BatchSize);
        var available = capacity - (status.BufferLength ?? 0);
        if (available <= 0) throw new InvalidOperationException("Buffer máy in đang đầy; chờ máy lấy bớt dữ liệu rồi thử lại.");
        quantity = Math.Clamp(quantity, 1, Math.Min(available, 1000));
        var batch = await _store.ClaimNextAsync(order.OrderNo, order.CreatedUtc, _printer.PrinterId, quantity, ct).ConfigureAwait(false);
        if (batch is null) return null;

        var accepted = 0;
        var waiting = new List<string>();
        foreach (var item in batch.Items)
        {
            try
            {
                var result = await _printer.SendCodeAsync(item.Code, ct).ConfigureAwait(false);
                await _store.RecordSendResponseAsync(order.OrderNo, order.CreatedUtc, batch.BatchId, item.Code, result.Response, ct).ConfigureAwait(false);
                if (result.Accepted) accepted++; else waiting.Add(item.Code);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Gửi mã in thất bại {Code}", item.Code);
                await _store.RecordSendResponseAsync(order.OrderNo, order.CreatedUtc, batch.BatchId, item.Code, ex.Message, ct).ConfigureAwait(false);
                waiting.Add(item.Code);
            }
        }
        var noResponse = await _store.ReconcileActivatedAcrossOrdersAsync(ct).ConfigureAwait(false);
        return new PrintBatchResult { Batch = batch, AcceptedCount = accepted, WaitingCount = waiting.Count, NoResponseCount = noResponse,
            Message = $"Đã ghi trạng thái gửi cho {batch.Items.Count} mã; máy nhận {accepted}, chờ xác nhận in {waiting.Count}, không phản hồi {noResponse}." };
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        EnsureLicensed();
        return await _store.ReconcileActivatedAcrossOrdersAsync(ct).ConfigureAwait(false);
    }

    public Task<PrintQueueSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        EnsureLicensed();
        var order = _po.CurrentOrder;
        return order is null ? Task.FromResult(new PrintQueueSummary(0, 0, 0, 0)) : _store.GetSummaryAsync(order.OrderNo, order.CreatedUtc, ct);
    }

    private void EnsureLicensed()
    {
        if (!_license.CanUse)
            throw new InvalidOperationException(_license.Message);
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Module máy in chưa được bật trong Cấu hình.");
    }

    private async Task OnPrintConfirmedAsync(string response)
    {
        var printed = await _store.MarkNextPrintedAnyAsync(_printer.PrinterId, response).ConfigureAwait(false);
        if (printed is not null)
            _logger?.LogInformation("Máy in {PrinterId} xác nhận đã in mã {Code}", _printer.PrinterId, printed);
    }
}
