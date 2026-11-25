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

    // STATUS có thể trả trạng thái kèm dữ liệu phụ; GFILE chỉ cần đọc ở chu kỳ chậm.
    var statusListener = new TcpListener(IPAddress.Loopback, 0);
    statusListener.Start();
    var statusPort = ((IPEndPoint)statusListener.LocalEndpoint).Port;
    var statusCommands = new System.Collections.Concurrent.ConcurrentQueue<string>();
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
            statusCommands.Enqueue(cmd);
            var reply = cmd switch
            {
                "STATUS" => "STATUS|1,0\r\n",
                "INKV" => "INKV|55,66\r\n",
                "MFDLEN" => "MFDLEN|7\r\n",
                "GFILE" => "GFILE|CU:13,13:Carton\r\n",
                _ => "OK\r\n",
            };
            var bytes = Encoding.ASCII.GetBytes(reply);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    });
    await using var statusClient = new G10PrinterClient(
        new PrinterOptions { Id = "status", Ip = "127.0.0.1", Port = statusPort }, 3000);
    var statusHistory = new List<PrinterCommandLog>();
    statusClient.CommandObserved += statusHistory.Add;
    try
    {
        var state = await statusClient.RefreshStatusAsync();
        Check(state.IsPrinting && state.Ink1Percent == 55 && state.BufferLength == 7,
            "Đọc STATUS dạng 1,0 / INKV / MFDLEN đúng trạng thái");
        Check(state.Job == "Carton" && statusClient.Files.CurrentId == "13",
            "Polling cập nhật bản tin máy in đang dùng");
        await statusClient.RefreshStatusAsync();
        Check(statusCommands.Count(command => command == "GFILE") == 1,
            "GFILE không lặp lại ở mỗi chu kỳ trạng thái nhanh");
        Check(statusHistory.All(log => log.Accepted),
            "Lịch sử STATUS/INKV/MFDLEN/GFILE có dữ liệu hợp lệ được đánh dấu thành công");
    }
    finally
    {
        statusListener.Stop();
    }

    // GFILE trên máy thật kết thúc bằng chuỗi literal "\r\n" chứ không phải CRLF.
    // Kết nối đồng thời từ worker nền và thao tác UI phải dùng chung một socket.
    var concurrentListener = new TcpListener(IPAddress.Loopback, 0);
    concurrentListener.Start();
    var concurrentPort = ((IPEndPoint)concurrentListener.LocalEndpoint).Port;
    var acceptedConnections = 0;
    var concurrentServerTask = Task.Run(async () =>
    {
        using var conn = await concurrentListener.AcceptTcpClientAsync();
        Interlocked.Increment(ref acceptedConnections);
        var stream = conn.GetStream();
        var buffer = new byte[256];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            var command = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            var reply = Encoding.ASCII.GetBytes(command switch
            {
                "STATUS" => "STATUS|0,1\r\n",
                "INKV" => "INKV|55,66\r\n",
                "MFDLEN" => "MFDLEN|7\r\n",
                "GFILE" => "GFILE|CU:13,13:Carton\r\n",
                _ => "COMD|ERR\r\n",
            });
            await stream.WriteAsync(reply);
        }
    });
    await using var concurrentClient = new G10PrinterClient(
        new PrinterOptions { Id = "concurrent", Ip = "127.0.0.1", Port = concurrentPort }, 3000);
    try
    {
        var starts = Enumerable.Range(0, 8).Select(_ => concurrentClient.ConnectAsync());
        var refresh = concurrentClient.RefreshStatusAsync();
        await Task.WhenAll(starts.Append((Task)refresh));
        Check(acceptedConnections == 1 && refresh.Result.Connection == PrinterConnectionState.Connected,
            "Kết nối đồng thời từ worker/UI chỉ mở một socket và truy vấn đọc được trạng thái");
    }
    finally
    {
        await concurrentClient.DisposeAsync();
        await concurrentServerTask;
        concurrentListener.Stop();
    }

    // Khi peer đóng socket, lần refresh sau phải tạo kết nối và channel phản hồi mới.
    var reconnectListener = new TcpListener(IPAddress.Loopback, 0);
    reconnectListener.Start();
    var reconnectPort = ((IPEndPoint)reconnectListener.LocalEndpoint).Port;
    var reconnectCount = 0;
    var reconnectServerTask = Task.Run(async () =>
    {
        using (var first = await reconnectListener.AcceptTcpClientAsync())
        {
            Interlocked.Increment(ref reconnectCount);
            var stream = first.GetStream();
            var request = new byte[64];
            await stream.ReadExactlyAsync(request.AsMemory(0, 6));
        }

        using var second = await reconnectListener.AcceptTcpClientAsync();
        Interlocked.Increment(ref reconnectCount);
        var secondStream = second.GetStream();
        var commandBuffer = new byte[256];
        while (true)
        {
            var read = await secondStream.ReadAsync(commandBuffer);
            if (read == 0) break;
            var command = Encoding.ASCII.GetString(commandBuffer, 0, read).Trim();
            var reply = Encoding.ASCII.GetBytes(command switch
            {
                "STATUS" => "STATUS|1,0\r\n",
                "INKV" => "INKV|40,60\r\n",
                "MFDLEN" => "MFDLEN|3\r\n",
                "GFILE" => "GFILE|CU:13,13:Carton\r\n",
                _ => "COMD|ERR\r\n",
            });
            await secondStream.WriteAsync(reply);
        }
    });
    await using var reconnectClient = new G10PrinterClient(
        new PrinterOptions { Id = "reconnect", Ip = "127.0.0.1", Port = reconnectPort }, 1000);
    try
    {
        try { await reconnectClient.RefreshStatusAsync(); }
        catch (Exception) { }
        var recovered = await reconnectClient.RefreshStatusAsync();
        Check(reconnectCount == 2 && recovered.Connection == PrinterConnectionState.Connected
            && recovered.BufferLength == 3 && recovered.IsPrinting,
            "Mất socket đóng channel cũ và lần refresh tiếp theo kết nối lại thành công");
    }
    finally
    {
        await reconnectClient.DisposeAsync();
        await reconnectServerTask;
        reconnectListener.Stop();
    }

    var fileListener = new TcpListener(IPAddress.Loopback, 0);
    fileListener.Start();
    var filePort = ((IPEndPoint)fileListener.LocalEndpoint).Port;
    var fileServerTask = Task.Run(async () =>
    {
        using var conn = await fileListener.AcceptTcpClientAsync();
        var stream = conn.GetStream();
        var buffer = new byte[256];
        string[] responses =
        [
            "GFILE|CU:13,13:Carton\\r\\n",
            "GFILE|14:Case:Large, CU:14,13:Carton,\r\n",
            "GFILE|CU:14,14:Case,14:Duplicate\r\n",
            "GFILE|CU:99,13:Carton\r\n",
            "GFILE|CU:\r\n",
        ];
        foreach (var response in responses)
        {
            var read = await stream.ReadAsync(buffer);
            Check(Encoding.ASCII.GetString(buffer, 0, read) == "GFILE", "Chỉ gửi truy vấn GFILE khi làm mới bản tin");
            var reply = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(reply);
            await stream.FlushAsync();
        }
    });
    await using var fileClient = new G10PrinterClient(
        new PrinterOptions { Id = "files", Ip = "127.0.0.1", Port = filePort }, 3000);
    var fileHistory = new List<PrinterCommandLog>();
    fileClient.CommandObserved += fileHistory.Add;
    try
    {
        var files = await fileClient.SendRawCommandAsync("GFILE", "GFILE", 3000);
        Check(files == "GFILE|CU:13,13:Carton", "Đọc GFILE với kết thúc literal \\r\\n");
        Check(fileClient.Status.Job == "Carton" && fileClient.Files.Files.Count == 1 && fileHistory[^1].Accepted,
            "GFILE thô cập nhật Job/danh sách và lịch sử ngay");

        // Làm mới bản tin không dùng PO, nhưng đi qua đúng service và kiểm tra license/module.
        var fileService = new PrintService(print, null!, fileClient, new PrintOptions { Enabled = true }, new TestPrintLicenseGate());
        var refreshed = await fileService.RefreshFilesAsync();
        Check(refreshed.Ok && fileClient.Status.Job == "Case:Large" && fileClient.Files.Files.Count == 2,
            "RefreshFiles không đòi ACK GFILE|OK và đọc CU ở bất kỳ vị trí nào");
        Check(fileHistory[^1].Accepted, "Lịch sử GFILE truy vấn thành công không báo lỗi giả");

        var invalidFiles = await fileService.RefreshFilesAsync();
        Check(!invalidFiles.Ok && !fileHistory[^1].Accepted && fileClient.Status.Job == "Case:Large",
            "GFILE lỗi không ghi đè bản tin tốt gần nhất");
        await fileService.RefreshFilesAsync();
        Check(fileClient.Files.CurrentId == "99" && fileClient.Status.Job == "",
            "CU không có trong danh sách không giữ nhầm tên bản tin cũ");
        var emptyFiles = await fileService.RefreshFilesAsync();
        Check(emptyFiles.Ok && fileClient.Files.Files.Count == 0 && fileClient.Status.Job == "",
            "GFILE danh sách rỗng có CU hợp lệ xóa trạng thái bản tin cũ");
        await fileServerTask;
    }
    finally
    {
        fileListener.Stop();
    }

    string[] malformedFiles =
    [
        "", "GFILE|OK", "GFILE|ERROR", "GFILE|13:Carton", "GFILE|CU:13,CU:14,13:Carton",
        "GFILE|CU:13,13:", "GFILE|CU:13,:Carton", "GFILE|CU:13,bad-entry", "GFILEOTHER|CU:13,13:Carton",
    ];
    Check(malformedFiles.All(response => !G10PrinterClient.TryParseFilesResponse(response, out _)),
        "Parser GFILE từ chối phản hồi lỗi, thiếu CU và mục không hợp lệ");

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

sealed class TestPrintLicenseGate : IPrintLicenseGate
{
    public bool CanUse => true;
    public string Message => "";
}
