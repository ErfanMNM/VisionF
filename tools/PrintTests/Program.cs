using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassLibraryMT.Po;
using ClassLibraryMT.Print;

var root = Path.Combine(Path.GetTempPath(), "visionf-print-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new PoStore(root);
    var order = new ProductionOrder { OrderNo = "PO-PRINT-001", OrderQty = 3, CreatedUtc = DateTime.UtcNow };
    var codes = Enumerable.Range(1, 3).Select(i => new PoCode { Code = $"089|SER{i:000}", Gtin = "089", Serial = $"SER{i:000}" }).ToArray();
    await store.EnsurePoDbAsync(order, codes);
    var print = new PrintQueueStore(store);

    var batch = await print.ClaimNextAsync(order.OrderNo, order.CreatedUtc, "g10-1", 2);
    Check(batch is not null && batch.Items.Count == 2, "Claim đúng số lượng chưa in/chưa active");
    var summary = await print.GetSummaryAsync(order.OrderNo, order.CreatedUtc);
    Check(summary.Pending == 1 && summary.Sent == 2, "Ghi Sent trước khi truyền");

    await print.MarkPrintedAsync(order.OrderNo, order.CreatedUtc, batch!.BatchId, [batch.Items[0].Code], "BACK|OK");
    summary = await print.GetSummaryAsync(order.OrderNo, order.CreatedUtc);
    Check(summary.Printed == 1 && summary.Sent == 1, "Ghi Printed sau xác nhận máy in");

    await store.MarkActivatedAsync(order.OrderNo, order.CreatedUtc, [batch.Items[1].Serial]);
    var changed = await print.ReconcileActivatedAcrossOrdersAsync();
    summary = await print.GetSummaryAsync(order.OrderNo, order.CreatedUtc);
    Check(changed == 1 && summary.NoResponse == 1 && summary.Sent == 0, "Active nhưng không có xác nhận -> NoResponse");

    var next = await print.ClaimNextAsync(order.OrderNo, order.CreatedUtc, "g10-1", 10);
    Check(next is not null && next.Items.Count == 1 && next.Items[0].Serial == "SER003", "Không lấy lại mã đã Printed/NoResponse");

    // Lệnh điều khiển START/STOP phải đi đúng protocol G10 legacy.
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serverTask = Task.Run(async () =>
    {
        using var conn = await listener.AcceptTcpClientAsync();
        var stream = conn.GetStream();
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer);
        var command = Encoding.ASCII.GetString(buffer, 0, read);
        var reply = Encoding.ASCII.GetBytes(command.StartsWith("START") ? "START|OK\r\n" : "STOP|OK\r\n");
        await stream.WriteAsync(reply);
        await stream.FlushAsync();
        return command;
    });
    var client = new G10PrinterClient(new PrinterOptions { Id = "test", Ip = "127.0.0.1", Port = port });
    try
    {
        var response = await client.SendRawCommandAsync("START", "START", 3000);
        var sent = await serverTask;
        Check(sent.Contains("START\r\n") && response.StartsWith("START|OK"), "Lệnh START gửi đúng protocol G10");
    }
    finally
    {
        await client.DisposeAsync();
        listener.Stop();
    }

    Console.WriteLine("Tất cả kiểm thử Print đều đạt.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
