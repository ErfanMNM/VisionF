using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TanTienQR;
using TanTienQR.Services;

var passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("[FAIL] " + description);
    passed++;
    Console.WriteLine("[OK] " + description);
}

async Task WaitForAsync(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    while (!condition()) await Task.Delay(10, timeout.Token);
}

static async Task SendAsync(TcpClient client, string frame) =>
    await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(frame));

static async Task<bool> ClosedAsync(TcpClient client)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try { return await client.GetStream().ReadAsync(new byte[1], timeout.Token) == 0; }
    catch (Exception ex) when (ex is IOException or SocketException) { return true; }
}

static async Task<TcpClient> AcceptAsync(TcpListener listener) =>
    await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(4));

try
{
    using var listenerA = new TcpListener(IPAddress.Loopback, 0);
    using var listenerB = new TcpListener(IPAddress.Loopback, 0);
    listenerA.Start();
    listenerB.Start();
    var portA = ((IPEndPoint)listenerA.LocalEndpoint).Port;
    var portB = ((IPEndPoint)listenerB.LocalEndpoint).Port;
    var frames = new ConcurrentQueue<string>();
    var states = new ConcurrentQueue<eReaderState>();
    var reader = new Reader("127.0.0.1", portA, TimeSpan.FromSeconds(10));
    reader.ClientCallback += (state, data) =>
    {
        states.Enqueue(state);
        if (state == eReaderState.Received) frames.Enqueue(data);
    };
    using var stop = new CancellationTokenSource();
    var running = reader.RunAsync(stop.Token);
    using var first = await AcceptAsync(listenerA);
    await SendAsync(first, "A\r\n");
    await WaitForAsync(() => frames.Count == 1);
    Check(frames.ToArray().SequenceEqual(new[] { "A" }), "Nhận một callback cho frame đầu tiên");

    // Một frame chưa hoàn tất trên socket cũ không được ghép sang endpoint mới.
    await SendAsync(first, "PARTIAL");
    await reader.ReconfigureAsync("localhost", portB);
    Check(await ClosedAsync(first), "Đổi endpoint đóng socket camera cũ");
    using var second = await AcceptAsync(listenerB);
    await SendAsync(second, "B\r\n");
    await WaitForAsync(() => frames.Count == 2);
    Check(frames.ToArray().SequenceEqual(new[] { "A", "B" }), "Không giữ frame dở hoặc nhân đôi callback qua reconnect");
    Check(!running.IsCompleted && reader.IP == "localhost" && reader.Port == portB,
        "Đổi endpoint giữ cùng vòng Reader đang chạy và hỗ trợ hostname IPv4");

    var invalidRejected = false;
    try { await reader.ReconfigureAsync("127.0.0.1", 0); }
    catch (ArgumentOutOfRangeException) { invalidRejected = true; }
    Check(invalidRejected && reader.Port == portB, "Port không hợp lệ không thay endpoint đang chạy");
    using var alreadyCancelled = new CancellationTokenSource();
    alreadyCancelled.Cancel();
    var cancelled = false;
    try { await reader.ReconfigureAsync("127.0.0.1", portA, alreadyCancelled.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && reader.Port == portB, "Hủy trước apply giữ nguyên endpoint");
    await reader.ReconfigureAsync("localhost", portB);
    await SendAsync(second, "B2\n");
    await WaitForAsync(() => frames.Count == 3);
    Check(frames.Last() == "B2" && reader.Connected, "Áp dụng lại cùng endpoint không cắt socket đang hoạt động");

    await reader.ReconfigureAsync("127.0.0.1", portA);
    Check(await ClosedAsync(second), "Rollback endpoint đóng phiên mới và phục hồi đích cũ");
    using var restored = await AcceptAsync(listenerA);
    await SendAsync(restored, "RESTORED\n");
    await WaitForAsync(() => frames.Count == 4);
    Check(frames.Last() == "RESTORED", "Sau rollback tiếp tục nhận mã trong cùng Reader");

    // Đích offline phải vào reconnect; apply kế tiếp không chờ hết retry 10 giây.
    using var unused = new TcpListener(IPAddress.Loopback, 0);
    unused.Start();
    var unusedPort = ((IPEndPoint)unused.LocalEndpoint).Port;
    unused.Stop();
    var disconnectedBefore = states.Count(state => state == eReaderState.Disconnected);
    await reader.ReconfigureAsync("127.0.0.1", unusedPort);
    await WaitForAsync(() => states.Count(state => state == eReaderState.Disconnected) > disconnectedBefore);
    Check(!reader.Connected && !running.IsCompleted, "Đích offline không làm Reader chết hoặc báo kết nối giả");
    var reconnectWatch = Stopwatch.StartNew();
    await reader.ReconfigureAsync("127.0.0.1", portA);
    using var afterOffline = await AcceptAsync(listenerA);
    Check(reconnectWatch.Elapsed < TimeSpan.FromSeconds(3), "Apply đánh thức ngay retry cũ không đợi 10 giây");
    await SendAsync(afterOffline, "AFTER-OFFLINE\n");
    await WaitForAsync(() => frames.Count == 5);
    Check(frames.Last() == "AFTER-OFFLINE", "Đích mới phục hồi nhận mã sau cấu hình offline");
    reader.Disconnect();
    await running.WaitAsync(TimeSpan.FromSeconds(4));
    Check(!reader.Connected && await ClosedAsync(afterOffline), "Disconnect kết thúc vòng chạy và đóng socket sau nhiều apply");

    // Kiểm thử worker không khởi động host, không kết nối PLC hoặc phần cứng thật.
    var assembly = typeof(App).Assembly;
    var workerType = assembly.GetType("TanTienQR.Services.CameraSignalWorker")!;
    var optionsType = assembly.GetType("TanTienQR.Services.CameraOptions")!;
    var options = Activator.CreateInstance(optionsType)!;
    optionsType.GetProperty("Ip")!.SetValue(options, "127.0.0.1");
    optionsType.GetProperty("Port")!.SetValue(options, portA);
    var wrapped = Activator.CreateInstance(typeof(OptionsWrapper<>).MakeGenericType(optionsType), options)!;
    var intake = new ScanIntakeGate();
    var suspendedMethod = typeof(ScanIntakeGate).GetMethod("SetSuspended", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var logger = typeof(NullLogger<>).MakeGenericType(workerType).GetField("Instance")!.GetValue(null)!;
    var workerReader = new Reader("127.0.0.1", portA);
    var worker = Activator.CreateInstance(workerType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
        new object?[] { workerReader, null, null, null, wrapped, null, null, intake, logger }, null)!;
    var apply = workerType.GetMethod("ApplyConfigurationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    Task Apply(CameraConfig target, CancellationToken token = default) => (Task)apply.Invoke(worker, new object[] { target, token })!;
    var unsafeRejected = false;
    try { await Apply(new CameraConfig { Ip = "127.0.0.1", Port = portB }); }
    catch (InvalidOperationException) { unsafeRejected = true; }
    Check(unsafeRejected, "Worker từ chối đổi camera nếu chưa chặn nhận mã");
    suspendedMethod.Invoke(intake, new object[] { true });
    await Apply(new CameraConfig { Ip = "127.0.0.1", Port = portB });
    Check(workerReader.Port == portB && (int)optionsType.GetProperty("Port")!.GetValue(options)! == portB,
        "Apply worker cập nhật Reader và options dùng chung không restart");
    var endpointValidationRejected = false;
    try { await Apply(new CameraConfig { Ip = "127.0.0.1", Port = 0 }); }
    catch (ArgumentOutOfRangeException) { endpointValidationRejected = true; }
    Check(endpointValidationRejected && workerReader.Port == portB
        && (int)optionsType.GetProperty("Port")!.GetValue(options)! == portB,
        "Endpoint không hợp lệ không áp dụng một phần Reader/options");
    ((IDisposable)worker).Dispose();
    passed += await CameraCodeContentChecks.RunAsync();
    Console.WriteLine($"Đã qua {passed} kiểm tra hot-apply Camera. Chỉ dùng TCP loopback, không gọi PLC thật.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
