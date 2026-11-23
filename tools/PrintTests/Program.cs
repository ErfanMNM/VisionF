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
    PrinterCommandLog? observed = null;
    client.CommandObserved += log => observed = log;
    try
    {
        var response = await client.SendRawCommandAsync("START", "START", 3000);
        var sent = await serverTask;
        Check(sent == "START" && !sent.Contains('\r') && response.StartsWith("START|OK"), "Lệnh START gửi đúng protocol G10 (không có CRLF)");
        Check(observed?.Command == "START" && observed.Response.StartsWith("START|OK"), "Lịch sử lệnh G10 nhận được phản hồi");
    }
    finally
    {
        await client.DisposeAsync();
        listener.Stop();
    }

    // Đọc trạng thái: STATUS trả "1,0" / CSTATUS trả "0,3131" -> phải lấy số trước dấu phẩy.
    var statusListener = new TcpListener(IPAddress.Loopback, 0);
    statusListener.Start();
    var statusPort = ((IPEndPoint)statusListener.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        using var conn = await statusListener.AcceptTcpClientAsync();
        var stream = conn.GetStream();
        var buffer = new byte[256];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            var cmd = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            var reply = cmd switch
            {
                "STATUS" => "STATUS|1,0\r\n",
                "INKV" => "INKV|55,66\r\n",
                "MFDLEN" => "MFDLEN|7\r\n",
                _ => "OK\r\n",
            };
            var bytes = Encoding.ASCII.GetBytes(reply);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    });
    await using var statusClient = new G10PrinterClient(
        new PrinterOptions { Id = "status", Ip = "127.0.0.1", Port = statusPort }, 3000);
    try
    {
        var state = await statusClient.RefreshStatusAsync();
        Check(state.IsPrinting && state.Ink1Percent == 55 && state.BufferLength == 7,
            "Đọc STATUS dạng 1,0 / INKV / MFDLEN đúng trạng thái");
    }
    finally
    {
        statusListener.Stop();
    }

    // GFILE trên máy thật kết thúc bằng chuỗi literal "\r\n" chứ không phải CRLF.
    var fileListener = new TcpListener(IPAddress.Loopback, 0);
    fileListener.Start();
    var filePort = ((IPEndPoint)fileListener.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        using var conn = await fileListener.AcceptTcpClientAsync();
        var stream = conn.GetStream();
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer);
        var reply = Encoding.ASCII.GetBytes("GFILE|CU:13,13:Carton\\r\\n");
        await stream.WriteAsync(reply);
        await stream.FlushAsync();
    });
    await using var fileClient = new G10PrinterClient(
        new PrinterOptions { Id = "files", Ip = "127.0.0.1", Port = filePort }, 3000);
    try
    {
        var files = await fileClient.SendRawCommandAsync("GFILE", "GFILE", 3000);
        Check(files == "GFILE|CU:13,13:Carton", "Đọc GFILE với kết thúc literal \\r\\n");
    }
    finally
    {
        fileListener.Stop();
    }

    // Frame MFD: MFD|MFD_NAME:data, các ký tự điều khiển phải escape.
    var mfdListener = new TcpListener(IPAddress.Loopback, 0);
    mfdListener.Start();
    var mfdPort = ((IPEndPoint)mfdListener.LocalEndpoint).Port;
    string? sentFrame = null;
    var mfdServerTask = Task.Run(async () =>
    {
        using var conn = await mfdListener.AcceptTcpClientAsync();
        var stream = conn.GetStream();
        var buffer = new byte[512];
        var read = await stream.ReadAsync(buffer);
        sentFrame = Encoding.ASCII.GetString(buffer, 0, read);
        var reply = Encoding.ASCII.GetBytes("MFD|OK\r\n");
        await stream.WriteAsync(reply);
        await stream.FlushAsync();
    });
    await using var mfdClient = new G10PrinterClient(
        new PrinterOptions { Id = "mfd", Ip = "127.0.0.1", Port = mfdPort, MfdName = "POD01" }, 3000);
    try
    {
        var sent = await mfdClient.SendCodeAsync("https://a/b:1,c|d");
        await mfdServerTask;
        Check(sent.Accepted && sentFrame == @"MFD|POD01:https\\://a/b\\:1\\,c\\|d",
            "Frame MFD dùng MFD_NAME và escape 2 backslash trước ':' ',' '|'");
    }
    finally
    {
        mfdListener.Stop();
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
