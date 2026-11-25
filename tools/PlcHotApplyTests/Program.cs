using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.DeviceStatus;
using ClassLibraryMT.Plc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR.Services;

internal static class Program
{
    private static int _passed;
    private static readonly Assembly AppAssembly = typeof(TanTienAppServices).Assembly;

    public static async Task<int> Main()
    {
        try
        {
            await CheckRawClientAsync();
            await CheckWriterAsync();
            await CheckPassAcknowledgementAsync();
            await CheckCountersAsync();
            await CheckResetAuthorizationAsync();
            await CheckHeartbeatAsync();
            Console.WriteLine($"Đã qua {_passed} kiểm tra PLC hot-apply; chỉ dùng UDP localhost.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task CheckRawClientAsync()
    {
        using var first = new FakePlc();
        using var second = new FakePlc();
        using var plc = new OmronPlc("127.0.0.1", first.Port, 10, 20, 2000, 1);
        Check((await Task.Run(() => plc.WriteInt16("D12", 7))).IsSuccess, "Client FINS ghi được endpoint ban đầu");
        Check(first.Requests.Last() is { RemoteNode: 10, LocalNode: 20, Address: 12, Value: 7 }, "Frame ban đầu có đúng node, địa chỉ và giá trị");

        var held = first.HoldNextRead();
        var reading = Task.Run(() => plc.ReadInt16("D40"));
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var swapping = Task.Run(() => plc.Reconfigure("127.0.0.1", second.Port, 30, 31, 300, 2));
        await Task.Delay(50);
        Check(!swapping.IsCompleted, "Reconfigure đợi request đang đọc hoàn tất thay vì dispose socket giữa chừng");
        held.Release.TrySetResult();
        Check((await reading).IsSuccess, "Request cũ nhận đủ phản hồi trong lúc reconfigure chờ");
        await swapping;
        Check(plc.Port == second.Port && plc.RemoteNode == 30 && plc.LocalNode == 31
            && plc.TimeoutMs == 300 && plc.Retries == 2, "Reconfigure cập nhật đủ endpoint/node/timeout/retries");
        Check((await Task.Run(() => plc.WriteInt16("D112", 9))).IsSuccess, "Client cũ ghi được endpoint mới");
        Check(second.Requests.Last() is { RemoteNode: 30, LocalNode: 31, Address: 112, Value: 9 }, "Frame mới dùng node và địa chỉ mới");

        // Read-modify-write phải giữ cùng endpoint cho cả hai nửa của thao tác.
        await Task.Run(() => plc.Reconfigure("127.0.0.1", first.Port, 10, 20, 2000, 0));
        var heldBit = first.HoldNextRead();
        var bitWrite = Task.Run(() => plc.WriteBool("D55.03", true));
        await heldBit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var bitSwap = Task.Run(() => plc.Reconfigure("127.0.0.1", second.Port, 40, 41, 300, 0));
        await Task.Delay(50);
        Check(!bitSwap.IsCompleted, "Đổi cấu hình không xen vào giữa read-modify-write");
        heldBit.Release.TrySetResult();
        Check((await bitWrite).IsSuccess, "Read-modify-write thành công trước swap");
        await bitSwap;
        Check(first.Requests.Last() is { Command: 2, Address: 55, Value: 8 }, "Cả nửa write của thao tác bit vẫn tới PLC cũ");

        await Task.Run(() => plc.Reconfigure("localhost", second.Port, 40, 41, 300, 0));
        Check((await Task.Run(() => plc.ReadInt16("D55"))).IsSuccess, "Hot-apply hỗ trợ hostname hợp lệ");
        await Task.Run(() => plc.Reconfigure("127.0.0.1", second.Port, 40, 41, 300, 0, enabled: false));
        var count = second.Requests.Count;
        Check(plc.WriteInt16("D1", 1).ErrorCode == "DISABLED" && plc.ReadInt16("D1").ErrorCode == "DISABLED", "PLC disabled chặn cả đọc và ghi trực tiếp");
        await Task.Delay(60);
        Check(second.Requests.Count == count, "PLC disabled không phát UDP");
        await Task.Run(() => plc.Reconfigure("127.0.0.1", second.Port, 40, 41, 300, 0));
        Check((await Task.Run(() => plc.WriteInt16("D1", 1))).IsSuccess, "PLC bật lại được trên cùng singleton");

        var writes = Enumerable.Range(0, 12).Select(index => Task.Run(() => plc.WriteInt16("D90", (short)index))).ToArray();
        await Task.WhenAll(writes);
        Check(writes.All(task => task.Result.IsSuccess), "Các request đồng thời không tranh SID hoặc socket");
    }

    private static async Task CheckWriterAsync()
    {
        using var first = new FakePlc();
        using var second = new FakePlc();
        using var host = BuildHost(first.Port, enabled: true, interval: 1000);
        var writer = GetService(host.Services, "PlcWriter");
        var plc = host.Services.GetRequiredService<OmronPlc>();
        var factory = host.Services.GetRequiredService<IPlcMemoryClientFactory>();
        using var borrowedBeforeSwap = factory.Create();
        Check(await Write(writer, "WriteReadyAsync", 1), "Writer ghi Ready trước hot-apply");
        Check(first.Requests.Last() is { Address: 16, Value: 1 }, "Writer dùng ReadyAddress ban đầu");
        Check(await Write(writer, "WriteResultAsync", true), "Writer ghi kết quả trước hot-apply");
        Check(first.Requests.Last() is { Address: 12, Value: 1 }, "Writer dùng ResultAddress ban đầu");

        var next = Config(second.Port, enabled: true, interval: 150);
        next.RemoteNode = 88; next.LocalNode = 89; next.TimeoutMs = 250; next.Retries = 2;
        next.ResultAddress = "D212"; next.ReadyAddress = "D216";
        Check((await Task.Run(() => borrowedBeforeSwap.WriteInt16("D310", 11))).IsSuccess, "Recipe bắt đầu bằng lệnh trên endpoint cũ");
        var applying = Apply(writer, next);
        await Task.Delay(60);
        Check(!applying.IsCompleted, "Hot-apply đợi toàn bộ lifetime client recipe, không xen giữa hai lệnh");
        using (var busy = factory.Create())
            Check(busy.ReadInt16("D1").ErrorCode == "BUSY" && busy.WriteInt16("D1", 1).ErrorCode == "BUSY", "Client mới trả BUSY ngay thay vì block UI trong lúc PLC đang được giữ");
        Check((await Task.Run(() => borrowedBeforeSwap.WriteInt16("D311", 12))).IsSuccess
            && first.Requests.Last() is { Address: 311, Value: 12 }, "Lệnh cuối recipe vẫn đi tới cùng PLC cũ");
        borrowedBeforeSwap.Dispose();
        await applying.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!second.Requests.Any(request => request.Address is 310 or 311), "Không có phần recipe cũ nào bị ghi sang endpoint mới");
        Check(ReferenceEquals(plc, host.Services.GetRequiredService<OmronPlc>()), "Hot-apply giữ nguyên singleton OmronPlc");
        Check((bool)writer.GetType().GetProperty("Enabled")!.GetValue(writer)!
            && (int)writer.GetType().GetProperty("RefreshIntervalMs")!.GetValue(writer)! == 150, "Writer công bố enabled và chu kỳ runtime mới");
        Check(plc.Port == second.Port && plc.RemoteNode == 88 && plc.LocalNode == 89
            && plc.TimeoutMs == 250 && plc.Retries == 2, "Writer áp dụng đủ trường transport PLC");
        Check(await Write(writer, "WriteResultAsync", false), "Ghi kết quả sau apply vẫn hoạt động");
        Check(second.Requests.Last() is { Address: 212, Value: 0, RemoteNode: 88, LocalNode: 89 }, "Kết quả dùng địa chỉ và node mới");
        Check(await Write(writer, "WriteReadyAsync", 1), "Ghi Ready sau apply vẫn hoạt động");
        Check(second.Requests.Last() is { Address: 216, Value: 1 }, "Heartbeat dùng ReadyAddress mới");
        using (var borrowedAfterSwap = factory.Create())
            Check((await Task.Run(() => borrowedAfterSwap.WriteInt16("D300", 33))).IsSuccess
                && second.Requests.Last() is { Address: 300, Value: 33 }, "Client thủ công tạo sau apply dùng endpoint mới");
        Check(await Write(writer, "WriteResultAsync", true), "Dispose wrapper thủ công không đóng singleton heartbeat");
        Check(borrowedBeforeSwap.WriteInt16("D300", 1).ErrorCode == "DISPOSED", "Client đã trả lease không được ghi tiếp sau dispose");

        using (var heldRecipe = factory.Create())
        {
            using var cancellation = new CancellationTokenSource();
            var pending = Apply(writer, Config(first.Port, enabled: true, interval: 100), cancellation.Token);
            await Task.Delay(30);
            Check(!pending.IsCompleted, "Apply đang chờ lease recipe chưa thay cấu hình");
            cancellation.Cancel();
            var cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && plc.Port == second.Port, "Hủy khi đợi recipe giữ nguyên endpoint cũ và trả writer gate");
        }

        var invalid = Config(first.Port, enabled: true, interval: 100);
        invalid.RemoteNode = 256;
        await MustRejectAsync(() => Apply(writer, invalid), "Node ngoài 0–255 bị từ chối trước swap");
        invalid.RemoteNode = 1; invalid.ReadyAddress = "D16.01";
        await MustRejectAsync(() => Apply(writer, invalid), "Địa chỉ Ready dạng bit bị từ chối trước swap");
        Check(plc.Port == second.Port && await Write(writer, "WriteReadyAsync", 0), "Apply không hợp lệ giữ nguyên cấu hình hoạt động");

        next.Enabled = false;
        await Apply(writer, next);
        Check(GlobalVariables.PLCStatus == PLCStatus.Disconnect && GlobalVariables.Ready == 0, "Tắt PLC cập nhật trạng thái và Ready toàn app");
        var count = second.Requests.Count;
        Check(!await Write(writer, "WriteReadyAsync", 1) && !await Write(writer, "WriteResultAsync", true), "Writer không gửi heartbeat hoặc kết quả khi disabled");
        using (var disabledManual = factory.Create())
            Check(disabledManual.WriteInt16("D300", 1).ErrorCode == "DISABLED", "Tab Chỉnh PLC cũng tuân theo module disabled");
        await Task.Delay(80);
        Check(second.Requests.Count == count, "Không có request từ bất kỳ writer nào khi PLC tắt");
        await Apply(writer, Config(first.Port, enabled: true, interval: 1000));
        Check(await Write(writer, "WriteResultAsync", true) && first.Requests.Last().Address == 12, "Rollback dùng cùng API và phục hồi endpoint/địa chỉ cũ");
    }

    private static async Task CheckPassAcknowledgementAsync()
    {
        using var server = new FakePlc();
        using var host = BuildHost(server.Port, enabled: true, interval: 1000);
        var writer = GetService(host.Services, "PlcWriter");
        var plc = host.Services.GetRequiredService<OmronPlc>();
        var config = Config(server.Port, enabled: true, interval: 1000);
        config.Retries = 2;
        await Apply(writer, config);

        Check(await Write(writer, "WriteResultAsync", true), "Pass có ACK hợp lệ trả thành công");
        foreach (var mode in new[]
        {
            WriteResponse.DropAck, WriteResponse.BadHeader, WriteResponse.WrongSid,
            WriteResponse.WrongCommand, WriteResponse.Truncated,
        })
        {
            var before = server.Requests.Count;
            server.RespondToNextWrite(mode);
            var uncertain = false;
            try { await Write(writer, "WriteResultAsync", true); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Không xác định PLC đã nhận Pass"))
            { uncertain = true; }
            Check(uncertain, $"Pass gặp {mode} ném lỗi cần đối soát thay vì trả false cho replay");
            Check(server.Requests.Count == before + 1 && server.Requests.Last() is { Address: 12, Value: 1 },
                $"Pass gặp {mode} chỉ phát một datagram dù cấu hình Retries=2");
        }

        var rejectedBefore = server.Requests.Count;
        server.RespondToNextWrite(WriteResponse.Reject);
        Check(!await Write(writer, "WriteResultAsync", true), "FINS end code từ chối hợp lệ vẫn trả false, không báo bất định");
        Check(server.Requests.Count == rejectedBefore + 1, "FINS từ chối không phát lại Pass");

        var noSendBefore = server.Requests.Count;
        Check(plc.WriteInt16Once("invalid-address", 1).ErrorCode == "ARG", "WriteInt16Once giữ ARG trước khi phát UDP");
        Check(server.Requests.Count == noSendBefore, "Địa chỉ không hợp lệ không phát Pass");

        var ordinaryBefore = server.Requests.Count;
        server.RespondToNextWrite(WriteResponse.DropAck);
        Check((await Task.Run(() => plc.WriteInt16("D80", 7))).IsSuccess
            && server.Requests.Count == ordinaryBefore + 2,
            "WriteInt16 thủ công vẫn dùng retry hiện có khi mất ACK");
        var readyBefore = server.Requests.Count;
        server.RespondToNextWrite(WriteResponse.DropAck);
        Check(await Write(writer, "WriteReadyAsync", 1) && server.Requests.Count == readyBefore + 2,
            "Ready giữ retry hiện có, chỉ Pass dùng single-attempt");
        var failBefore = server.Requests.Count;
        server.RespondToNextWrite(WriteResponse.DropAck);
        Check(await Write(writer, "WriteResultAsync", false) && server.Requests.Count == failBefore + 2,
            "Fail giữ retry hiện có và bool API tương thích");

        config.Enabled = false;
        await Apply(writer, config);
        var disabledBefore = server.Requests.Count;
        Check(!await Write(writer, "WriteResultAsync", true)
            && plc.WriteInt16Once("D12", 1).ErrorCode == "DISABLED",
            "Pass disabled là definite-unsent, vẫn trả false/DISABLED");
        Check(server.Requests.Count == disabledBefore, "Pass disabled không phát datagram");
        plc.Dispose();
        Check(plc.WriteInt16Once("D12", 1).ErrorCode == "DISPOSED", "WriteInt16Once giữ definite-unsent DISPOSED");
        Check(server.Requests.Count == disabledBefore, "Client disposed không phát datagram");
    }

    private static async Task CheckCountersAsync()
    {
        using var first = new FakePlc();
        using var second = new FakePlc();
        using var host = BuildHost(first.Port, enabled: true, interval: 1000);
        var writer = GetService(host.Services, "PlcWriter");
        var plc = host.Services.GetRequiredService<OmronPlc>();
        var expected = new CounterValues(Total: 720_007, ReadFail: 12_003, Pass: 700_004, Timeout: 8_000, Fail: 20_003);
        await SeedCountersAsync(plc, 100, expected);

        Check(await ReadCountersAsync(writer) == expected,
            "Số sản xuất đọc Int32 từ D100/D102/D104/D108; NG cộng đọc lỗi và timeout");
        var beforeMissingReset = first.Requests.Count;
        Check(!await ResetCountersAsync(writer), "Chưa cấu hình địa chỉ reset thì từ chối xóa bộ đếm");
        Check(first.Requests.Count == beforeMissingReset, "Thiếu địa chỉ reset không tự đoán hoặc ghi PLC");

        var resetConfig = Config(first.Port, enabled: true, interval: 1000);
        resetConfig.ResetCounterAddress = "D60";
        await Apply(writer, resetConfig);
        Check(await ResetCountersAsync(writer), "Nút xóa ghi reset khi đã cấu hình địa chỉ PLC");
        Check(first.Requests.Last() is { Command: 2, Address: 60, Value: 1 },
            "Reset gửi đúng giá trị Int16 1 tới địa chỉ đã cấu hình");
        Check(await ReadCountersAsync(writer) == expected,
            "ACK reset không bịa bộ đếm về 0; lần đọc tiếp vẫn lấy giá trị thật từ PLC");
        Check(LatestCounters(writer) is not null, "Writer giữ snapshot PLC sau lần đọc thành công");
        first.FailNextRead();
        Check(await ReadCountersAsync(writer) is null && LatestCounters(writer) is null,
            "Đọc PLC lỗi xóa LatestCounters thay vì để dashboard dùng số cũ");
        Check(await ReadCountersAsync(writer) == expected,
            "Snapshot phục hồi từ PLC thật ở lần đọc thành công sau lỗi");
        var largeFailures = new CounterValues(int.MaxValue, int.MaxValue, 0, int.MaxValue, 4_294_967_294L);
        await SeedCountersAsync(plc, 100, largeFailures);
        Check(await ReadCountersAsync(writer) == largeFailures,
            "Tổng NG dùng Int64, không overflow khi cộng hai bộ đếm Int32 lớn");
        await SeedCountersAsync(plc, 100, expected);

        var next = Config(second.Port, enabled: true, interval: 1000);
        next.CounterAddress = "D200";
        next.ResetCounterAddress = "D260";
        using (var preload = new OmronPlc("127.0.0.1", second.Port, 1, 2, 300, 0))
            await SeedCountersAsync(preload, 200, new CounterValues(91_000, 500, 90_000, 500, 1000));
        var held = first.HoldNextRead();
        var reading = ReadCountersAsync(writer);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var applying = Apply(writer, next);
        var queuedReset = ResetCountersAsync(writer);
        await Task.Delay(40);
        Check(!applying.IsCompleted && !queuedReset.IsCompleted,
            "Hot-apply và reset đợi snapshot bộ đếm đang đọc xong");
        held.Release.TrySetResult();
        Check(await reading == expected, "Snapshot đang đọc hoàn tất trên PLC cũ trước khi hot-apply");
        await applying.WaitAsync(TimeSpan.FromSeconds(3));
        Check(await queuedReset.WaitAsync(TimeSpan.FromSeconds(3))
            && second.Requests.Last() is { Command: 2, Address: 260, Value: 1 },
            "Reset xếp hàng lấy endpoint và địa chỉ mới sau writer gate");
        Check(await ReadCountersAsync(writer) == new CounterValues(91_000, 500, 90_000, 500, 1000),
            "Hot-apply đổi cả vùng số sản xuất sang D200/D202/D204/D208 trên PLC mới");
        Check(!first.Requests.Any(request => request.Address == 260)
            && !second.Requests.Any(request => request.Address == 60),
            "Không ghép nhầm địa chỉ reset cũ với PLC mới hoặc ngược lại");

        next.Enabled = false;
        await Apply(writer, next);
        var disabledCount = second.Requests.Count;
        Check(await ReadCountersAsync(writer) is null && !await ResetCountersAsync(writer),
            "PLC tắt trả snapshot không khả dụng và từ chối reset");
        await Task.Delay(60);
        Check(second.Requests.Count == disabledCount, "Đọc và xóa bộ đếm không phát UDP khi PLC tắt");
    }

    private static async Task SeedCountersAsync(OmronPlc plc, int start, CounterValues values)
    {
        foreach (var (offset, value) in new[]
        {
            (0, values.Total), (2, values.ReadFail), (4, values.Pass), (6, 310_106), (8, values.Timeout),
        })
            Check((await Task.Run(() => plc.WriteInt32($"D{start + offset}", value))).IsSuccess,
                $"Fake PLC nạp được Int32 tại D{start + offset}");
    }

    private static async Task<CounterValues?> ReadCountersAsync(object writer)
    {
        var task = (Task)writer.GetType().GetMethod("ReadCountersAsync")!
            .Invoke(writer, [CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        if (result is null) return null;
        int Read(string name) => (int)result.GetType().GetProperty(name)!.GetValue(result)!;
        var fail = (long)result.GetType().GetProperty("Fail")!.GetValue(result)!;
        return new CounterValues(Read("Total"), Read("ReadFail"), Read("Pass"), Read("Timeout"), fail);
    }

    private static Task<bool> ResetCountersAsync(object writer) =>
        (Task<bool>)writer.GetType().GetMethod("ResetCountersAsync")!
            .Invoke(writer, [CancellationToken.None])!;

    private static object? LatestCounters(object writer) => writer.GetType().GetProperty("LatestCounters")!.GetValue(writer);

    private sealed record CounterValues(int Total, int ReadFail, int Pass, int Timeout, long Fail);

    private static async Task CheckResetAuthorizationAsync()
    {
        using var server = new FakePlc();
        using var host = BuildHost(server.Port, enabled: true, interval: 1000);
        var writer = GetService(host.Services, "PlcWriter");
        var config = Config(server.Port, enabled: true, interval: 1000);
        config.ResetCounterAddress = "D60";
        await Apply(writer, config);

        var beforeDenied = server.Requests.Count;
        Check(!await ResetAuthorizedAsync(writer, () => false),
            "Reset có kiểm tra quyền từ chối callback false");
        Check(server.Requests.Count == beforeDenied, "Không có UDP khi không được cấp quyền reset");

        var permission = 1;
        Func<bool> authorize = () => Volatile.Read(ref permission) == 1;
        Check(await ResetAuthorizedAsync(writer, authorize)
            && server.Requests.Last() is { Command: 2, Address: 60, Value: 1 },
            "Callback quyền hợp lệ cho phép gửi reset đúng địa chỉ");

        var held = server.HoldNextRead();
        var reading = ReadCountersAsync(writer);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var resetWrites = server.Requests.Count(request => request is { Command: 2, Address: 60 });
        var queuedReset = ResetAuthorizedAsync(writer, authorize);
        await Task.Delay(40);
        Check(!queuedReset.IsCompleted, "Reset đã cấp quyền vẫn đợi lượt đọc PLC đang giữ gate");
        Volatile.Write(ref permission, 0);
        held.Release.TrySetResult();
        await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!await queuedReset.WaitAsync(TimeSpan.FromSeconds(3)),
            "Thu hồi quyền khi reset đang chờ gate được kiểm tra lại trước khi ghi");
        Check(server.Requests.Count(request => request is { Command: 2, Address: 60 }) == resetWrites,
            "Reset xếp hàng không phát lệnh sau logout hoặc thu hồi quyền");

        var intake = host.Services.GetRequiredService<ScanIntakeGate>();
        var setSuspended = typeof(ScanIntakeGate).GetMethod("SetSuspended", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setSuspended.Invoke(intake, [true]);
        try
        {
            var beforeSuspended = server.Requests.Count;
            Check(!await ResetAuthorizedAsync(writer, () => true) && !await ResetCountersAsync(writer),
                "Bảo trì tạm dừng intake chặn cả hai đường reset dù user có quyền");
            Check(server.Requests.Count == beforeSuspended, "Reset không phát UDP trong khi intake bị tạm dừng");
        }
        finally { setSuspended.Invoke(intake, [false]); }
        Check(await ResetAuthorizedAsync(writer, () => true), "Kết thúc bảo trì cho phép reset trở lại");
    }

    private static Task<bool> ResetAuthorizedAsync(object writer, Func<bool> authorize) =>
        (Task<bool>)writer.GetType().GetMethod("ResetCountersAuthorizedAsync")!
            .Invoke(writer, [authorize, CancellationToken.None])!;

    private static async Task CheckHeartbeatAsync()
    {
        using var server = new FakePlc();
        using var host = BuildHost(server.Port, enabled: false, interval: 1000);
        var writer = GetService(host.Services, "PlcWriter");
        var type = AppAssembly.GetType("TanTienQR.Services.PlcHeartbeatService")!;
        var constructor = type.GetConstructors().OrderByDescending(candidate => candidate.GetParameters().Length).First();
        using var heartbeat = (BackgroundService)constructor.Invoke(constructor.GetParameters()
            .Select(parameter => host.Services.GetRequiredService(parameter.ParameterType)).ToArray());
        await heartbeat.StartAsync(CancellationToken.None);
        await Task.Delay(120);
        Check(server.Requests.IsEmpty && heartbeat.ExecuteTask is { IsCompleted: false }, "Heartbeat khởi động disabled vẫn sống và không gửi UDP");
        await Apply(writer, Config(server.Port, enabled: true, interval: 1000));
        await server.WaitForCountAsync(1);
        Check(true, "Bật PLC đánh thức heartbeat ngay không cần restart");
        var previous = server.Requests.Count;
        await Apply(writer, Config(server.Port, enabled: true, interval: 100));
        await server.WaitForCountAsync(previous + 3, timeoutMs: 1500);
        Check(true, "Đổi interval đánh thức heartbeat và dùng chu kỳ mới");
        await Apply(writer, Config(server.Port, enabled: false, interval: 100));
        var disabledCount = server.Requests.Count;
        await Task.Delay(250);
        Check(server.Requests.Count == disabledCount, "Tắt PLC dừng heartbeat đang chạy");
        await Apply(writer, Config(server.Port, enabled: true, interval: 100));
        await server.WaitForCountAsync(disabledCount + 1);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await heartbeat.StopAsync(shutdown.Token);
        var stopped = server.Requests.Count;
        await Task.Delay(150);
        Check(heartbeat.ExecuteTask is { IsCompletedSuccessfully: true } && stopped == server.Requests.Count,
            "Shutdown hủy waiter/timer và không còn ghi PLC");
    }

    private static IHost BuildHost(int port, bool enabled, int interval)
    {
        var root = Path.Combine(Path.GetTempPath(), "VisionF-PlcHotApply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Config:BasePath"] = root, ["Plc:Enabled"] = enabled.ToString(), ["Plc:Ip"] = "127.0.0.1",
            ["Plc:Port"] = port.ToString(), ["Plc:RemoteNode"] = "1", ["Plc:LocalNode"] = "2",
            ["Plc:TimeoutMs"] = "300", ["Plc:Retries"] = "0", ["Plc:RefreshIntervalMs"] = interval.ToString(),
            ["Print:Enabled"] = "false", ["Print:Remote:Enabled"] = "false", ["Scanner:Enabled"] = "false",
            ["Web:Enabled"] = "false", ["Po:DbRoot"] = Path.Combine(root, "po"),
            ["Auth:DbPath"] = Path.Combine(root, "auth.db"), ["Scans:DbPath"] = Path.Combine(root, "scans.db"),
            ["PlcSettings:DbPath"] = Path.Combine(root, "plc.db"), ["Logging:Sqlite:Path"] = Path.Combine(root, "app.db"),
        });
        builder.Logging.ClearProviders();
        builder.Services.AddTanTienServices(builder.Configuration);
        // Không Start host: chỉ khởi động heartbeat được tạo tường minh ở bài riêng.
        return builder.Build();
    }

    private static object GetService(IServiceProvider services, string name) =>
        services.GetRequiredService(AppAssembly.GetType("TanTienQR.Services." + name)!);
    private static Task Apply(object writer, PlcConfig config, CancellationToken ct = default) =>
        (Task)writer.GetType().GetMethod("ApplyConfigurationAsync")!.Invoke(writer, [config, ct])!;
    private static Task<bool> Write(object writer, string method, object value) =>
        (Task<bool>)writer.GetType().GetMethod(method)!.Invoke(writer, [value, CancellationToken.None])!;
    private static PlcConfig Config(int port, bool enabled, int interval) => new()
    {
        Enabled = enabled, Ip = "127.0.0.1", Port = port, RemoteNode = 1, LocalNode = 2,
        TimeoutMs = 300, Retries = 0, ResultAddress = "D12", ReadyAddress = "D16", RefreshIntervalMs = interval,
    };
    private static async Task MustRejectAsync(Func<Task> action, string description)
    {
        try { await action(); }
        catch (ArgumentException) { Check(true, description); return; }
        throw new InvalidOperationException("[FAIL] " + description);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("[FAIL] " + description);
        _passed++; Console.WriteLine("[OK] " + description);
    }
}

internal sealed record FinsRequest(byte Command, byte RemoteNode, byte LocalNode, int Address, ushort Value);

internal enum WriteResponse { Normal, DropAck, BadHeader, WrongSid, WrongCommand, Truncated, Reject }

internal sealed class HeldRead
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class FakePlc : IDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Dictionary<int, ushort> _words = new();
    private HeldRead? _held;
    private int _failNextRead;
    private int _nextWriteResponse;
    public ConcurrentQueue<FinsRequest> Requests { get; } = new();
    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
    public FakePlc() => _worker = Task.Run(RunAsync);
    public HeldRead HoldNextRead() => _held = new HeldRead();
    public void FailNextRead() => Interlocked.Exchange(ref _failNextRead, 1);
    public void RespondToNextWrite(WriteResponse response) => Interlocked.Exchange(ref _nextWriteResponse, (int)response);

    public async Task WaitForCountAsync(int count, int timeoutMs = 3000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (Requests.Count < count) await Task.Delay(10, timeout.Token);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _socket.ReceiveAsync(_stop.Token);
                var data = packet.Buffer;
                if (data.Length < 18) continue;
                var address = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(13, 2));
                var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(16, 2));
                var command = data[11];
                var value = command == 2 && data.Length >= 20
                    ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(18, 2)) : (ushort)0;
                Requests.Enqueue(new FinsRequest(command, data[4], data[7], address, value));
                if (command == 1 && Interlocked.Exchange(ref _held, null) is { } hold)
                {
                    hold.Entered.TrySetResult();
                    await hold.Release.Task.WaitAsync(_stop.Token);
                }
                var response = new byte[14 + (command == 1 ? count * 2 : 0)];
                Array.Copy(data, response, 12);
                response[0] = 0xC0;
                var writeResponse = command == 2
                    ? (WriteResponse)Interlocked.Exchange(ref _nextWriteResponse, 0) : WriteResponse.Normal;
                if (writeResponse == WriteResponse.Reject
                    || command == 1 && Interlocked.Exchange(ref _failNextRead, 0) != 0)
                {
                    // FINS 0x1101: vùng nhớ không hợp lệ. Phản hồi ngay, không đợi timeout.
                    response[12] = 0x11;
                    response[13] = 0x01;
                    await _socket.SendAsync(response, packet.RemoteEndPoint, _stop.Token);
                    continue;
                }
                for (var index = 0; index < count; index++)
                {
                    if (command == 2 && data.Length >= 20 + index * 2)
                        _words[address + index] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(18 + index * 2, 2));
                    else if (command == 1)
                        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(14 + index * 2, 2), _words.GetValueOrDefault(address + index));
                }
                // Apply the physical write before losing/corrupting its acknowledgement.
                // A second datagram would receive a normal ACK, exposing accidental retries.
                if (writeResponse == WriteResponse.DropAck) continue;
                if (writeResponse == WriteResponse.BadHeader) response[0] = 0x80;
                if (writeResponse == WriteResponse.WrongSid) response[9]++;
                if (writeResponse == WriteResponse.WrongCommand) response[11] = 0x01;
                if (writeResponse == WriteResponse.Truncated) Array.Resize(ref response, 10);
                await _socket.SendAsync(response, packet.RemoteEndPoint, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _stop.Cancel(); _socket.Dispose();
        _worker.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
