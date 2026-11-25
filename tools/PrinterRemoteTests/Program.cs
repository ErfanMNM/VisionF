using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using TanTienQR.Services.PrinterRemote;
using TanTienQR.ViewModels;

AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var tests = new (string Name, Func<Task> Run)[]
{
    ("Từ chối remote khi chưa đăng nhập, module tắt hoặc nền tảng không hỗ trợ", DeniedAccess),
    ("Mật khẩu nhập chỉ dùng một lần và được xóa khi lưu/đăng xuất", CredentialLifetime),
    ("Chuyển tab và gắn surface nhiều lần không tạo phiên kết nối trùng", TabLifecycle),
    ("Ngắt thủ công không tự kết nối lại khi đổi tab", ExplicitDisconnect),
    ("Lỗi xác thực không tự thử lại", AuthenticationFailure),
    ("Logout khi đang kết nối hủy phiên và bỏ phản hồi Connected đến muộn", LogoutWhileConnecting),
    ("Mất quyền trong thời gian chờ thử lại không mở phiên mới", RevokeDuringRetry),
    ("Mạng lỗi chỉ thử lại tối đa ba lần rồi dừng", BoundedNetworkRetry),
};
var failures = 0;
foreach (var test in tests)
{
    try { Pump(test.Run(), TimeSpan.FromSeconds(25)); Console.WriteLine("PASS: " + test.Name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL: " + test.Name + ": " + error.Message); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} kiểm thử remote đạt.");
return failures == 0 ? 0 : 1;

static Task DeniedAccess()
{
    foreach (var blocked in new[] { "login", "disabled", "unsupported" })
    {
        var options = Options();
        options.Enabled = blocked != "disabled";
        var session = new FakeSession { IsSupported = blocked != "unsupported" };
        var credentials = new FakeCredentials();
        using var vm = new PrinterRemoteViewModel(options, session, credentials);
        vm.AttachSurface(1);
        vm.SetPresentationState(true, blocked != "login", false);
        vm.Password = "mật khẩu kiểm thử";
        Check(!vm.CanConnect && !vm.CanSaveCredential, "Không mở thao tác remote/credential khi quyền chưa đủ.");
        Check(!vm.ConnectCommand.CanExecute(null) && !vm.SaveCredentialCommand.CanExecute(null), "Command phải giữ cùng điều kiện quyền với UI.");
        Check(session.ConnectCount == 0 && credentials.ReadCount == 0 && credentials.SaveCount == 0,
            "Không đọc hoặc gửi thông tin đăng nhập khi remote bị chặn.");
        Check(!session.Visible, "Surface bị chặn phải ẩn.");
    }
    return Task.CompletedTask;
}

static async Task CredentialLifetime()
{
    var session = new FakeSession();
    var credentials = new FakeCredentials { HasCredential = false };
    var options = Options();
    options.AutoConnect = false;
    using var vm = new PrinterRemoteViewModel(options, session, credentials);
    vm.AttachSurface(1);
    vm.SetPresentationState(true, true, false);
    vm.Password = "mật khẩu dùng một lần";
    await vm.ConnectCommand.ExecuteAsync(null);
    Check(vm.Password == "" && credentials.ReadCount == 0 && session.ConnectCount == 1,
        "Kết nối bằng mật khẩu nhập phải xóa ô nhập và không đọc kho credential.");
    Check(credentials.SaveCount == 0, "Kết nối không tự lưu mật khẩu người dùng.");
    vm.Password = "mật khẩu lưu kiểm thử";
    vm.SaveCredentialCommand.Execute(null);
    Check(vm.Password == "" && credentials.SaveCount == 1 && vm.HasCredential,
        "Lưu credential xóa ô mật khẩu ngay khi thành công.");
    vm.Password = "mật khẩu chưa lưu";
    vm.SetPresentationState(false, false, true);
    Check(vm.Password == "" && !session.Visible, "Đăng xuất xóa mật khẩu còn trong ô nhập và ẩn surface.");
}

static async Task TabLifecycle()
{
    var session = new FakeSession
    {
        ConnectAction = async (self, token) =>
        {
            // Mô phỏng backend chưa phát Connecting ngay khi bắt đầu tác vụ.
            await Task.Delay(80, token);
            self.Publish(PrinterRemoteState.Connected);
        }
    };
    using var vm = new PrinterRemoteViewModel(Options(), session, new FakeCredentials());
    vm.SetPresentationState(true, true, false);
    vm.AttachSurface(1);
    for (var i = 0; i < 5; i++)
    {
        vm.SetPresentationState(false, true, false);
        vm.SetPresentationState(true, true, false);
        vm.AttachSurface(1);
    }
    await Task.Delay(140);
    Check(session.ConnectCount == 1 && session.MaximumConcurrentConnections == 1,
        "Backend báo trạng thái chậm vẫn chỉ được mở một kết nối.");
    Check(vm.IsConnected && session.Visible, "Phiên kết nối hiện trong tab đang được phép xem.");
    vm.SetPresentationState(false, true, false);
    Check(!vm.IsSurfaceVisible && !session.Visible, "Chuyển tab ẩn surface và khóa input ngay.");
    vm.SetPresentationState(true, true, true);
    Check(!vm.IsSurfaceVisible && !session.Visible, "Overlay chặn surface dù phiên vẫn còn.");
    vm.SetPresentationState(true, true, false);
    Check(vm.IsSurfaceVisible && session.ConnectCount == 1, "Quay lại tab tái sử dụng phiên đang có.");
}

static async Task ExplicitDisconnect()
{
    var session = new FakeSession();
    using var vm = new PrinterRemoteViewModel(Options(), session, new FakeCredentials());
    vm.SetPresentationState(true, true, false);
    vm.AttachSurface(1);
    Check(vm.IsConnected, "Thiết lập phiên kết nối thử.");
    await vm.DisconnectCommand.ExecuteAsync(null);
    vm.SetPresentationState(false, true, false);
    vm.SetPresentationState(true, true, false);
    vm.DetachSurface(1);
    vm.AttachSurface(2);
    await Task.Delay(100);
    Check(session.ConnectCount == 1 && !vm.IsConnected && !session.Visible,
        "Ngắt thủ công phải giữ trạng thái ngắt qua chu kỳ tab/surface.");
}

static async Task AuthenticationFailure()
{
    var session = new FakeSession
    {
        ConnectAction = (self, _) => { self.Publish(PrinterRemoteState.Faulted, PrinterRemoteFailure.Authentication); return Task.CompletedTask; }
    };
    using var vm = new PrinterRemoteViewModel(Options(), session, new FakeCredentials());
    vm.SetPresentationState(true, true, false);
    vm.AttachSurface(1);
    await Task.Delay(2300);
    Check(session.ConnectCount == 1 && vm.State == PrinterRemoteState.Faulted && !vm.IsConnecting,
        "Sai tài khoản/mật khẩu dừng lại để người dùng sửa, không reconnect.");
}

static async Task LogoutWhileConnecting()
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var session = new FakeSession
    {
        ConnectAction = async (self, _) =>
        {
            self.Publish(PrinterRemoteState.Connecting);
            await completion.Task;
            self.Publish(PrinterRemoteState.Connected);
        }
    };
    using var vm = new PrinterRemoteViewModel(Options(), session, new FakeCredentials());
    vm.SetPresentationState(true, true, false);
    vm.AttachSurface(1);
    vm.SetPresentationState(false, false, true);
    completion.SetResult();
    await Task.Delay(100);
    Check(session.DisconnectCount >= 1 && session.ConnectTokenCancelled,
        "Mất quyền phải hủy cả tác vụ đang kết nối.");
    Check(!vm.IsConnected && !vm.IsSurfaceVisible && !session.Visible,
        "Phản hồi Connected đến sau logout không được phục hồi phiên/surface.");
}

static async Task RevokeDuringRetry()
{
    var session = new FakeSession
    {
        ConnectAction = (self, _) => { self.Publish(PrinterRemoteState.Faulted, PrinterRemoteFailure.Network); return Task.CompletedTask; }
    };
    using var vm = new PrinterRemoteViewModel(Options(), session, new FakeCredentials());
    vm.SetPresentationState(true, true, false);
    vm.AttachSurface(1);
    Check(vm.State == PrinterRemoteState.Reconnecting, "Mạng lỗi bắt đầu thời gian chờ retry.");
    vm.SetPresentationState(false, false, true);
    await Task.Delay(2300);
    Check(session.ConnectCount == 1 && !session.Visible && !vm.IsConnecting,
        "Hủy retry khi logout/mất license, không dùng lại credential để mở phiên.");
}

static async Task BoundedNetworkRetry()
{
    var session = new FakeSession
    {
        ConnectAction = (self, _) =>
        {
            self.Publish(PrinterRemoteState.Faulted,
                self.ConnectCount % 2 == 0 ? PrinterRemoteFailure.Timeout : PrinterRemoteFailure.Network);
            return Task.CompletedTask;
        }
    };
    using var vm = new PrinterRemoteViewModel(Options(), session, new FakeCredentials());
    vm.SetPresentationState(true, true, false);
    vm.AttachSurface(1);
    await Task.Delay(15_000);
    Check(session.ConnectCount == 4 && vm.State == PrinterRemoteState.Faulted && !vm.IsConnecting,
        "Chỉ có lần kết nối ban đầu và ba lần thử lại cho Network/Timeout.");
    Check(session.MaximumConcurrentConnections == 1, "Các lần retry không chạy chồng nhau.");
}

static PrinterRemoteOptions Options() => new()
{
    Enabled = true, AutoConnect = true, Host = "192.0.2.1", Username = "remote-test", CredentialTarget = "test-only"
};

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Pump(Task task, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (!task.IsCompleted)
    {
        Dispatcher.UIThread.RunJobs();
        if (timer.Elapsed > timeout) throw new TimeoutException("Kiểm thử vượt thời gian cho phép.");
        Thread.Sleep(5);
    }
    Dispatcher.UIThread.RunJobs();
    task.GetAwaiter().GetResult();
}

sealed class FakeCredentials : IPrinterCredentialStore
{
    public bool IsSupported => true;
    public bool HasCredential { get; set; } = true;
    public int ReadCount { get; private set; }
    public int SaveCount { get; private set; }
    public bool Exists(string target) => HasCredential;
    public string? ReadPassword(string target, string username) { ReadCount++; return HasCredential ? "mật khẩu giả" : null; }
    public void Save(string target, string username, string password) { SaveCount++; HasCredential = true; }
}

sealed class FakeSession : IPrinterRemoteSession
{
    private CancellationTokenSource? _connectCancellation;
    private int _connecting;
    public bool IsSupported { get; init; } = true;
    public bool HasSurface { get; private set; }
    public bool Visible { get; private set; }
    public int ConnectCount { get; private set; }
    public int DisconnectCount { get; private set; }
    public int MaximumConcurrentConnections { get; private set; }
    public bool ConnectTokenCancelled { get; private set; }
    public Func<FakeSession, CancellationToken, Task>? ConnectAction { get; init; }
    public PrinterRemoteSnapshot Snapshot { get; private set; } = new(PrinterRemoteState.Disconnected, "Chưa kết nối thử.");
    public event Action<PrinterRemoteSnapshot>? Changed;
    public void AttachSurface(nint handle) { HasSurface = handle != 0; Changed?.Invoke(Snapshot); }
    public void DetachSurface(nint handle) { HasSurface = false; Visible = false; }
    public void SetSurfaceVisible(bool visible) => Visible = visible;
    public void Publish(PrinterRemoteState state, PrinterRemoteFailure failure = PrinterRemoteFailure.None)
    {
        Snapshot = new(state, "Trạng thái kiểm thử.", failure);
        Changed?.Invoke(Snapshot);
    }
    public async Task ConnectAsync(string password, CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        _connecting++;
        MaximumConcurrentConnections = Math.Max(MaximumConcurrentConnections, _connecting);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connectCancellation = cts;
        using var registration = cts.Token.Register(() => ConnectTokenCancelled = true);
        try
        {
            if (ConnectAction is null) Publish(PrinterRemoteState.Connected);
            else await ConnectAction(this, cts.Token);
        }
        finally
        {
            _connecting--;
            if (_connectCancellation == cts) _connectCancellation = null;
            cts.Dispose();
        }
    }
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        DisconnectCount++;
        _connectCancellation?.Cancel();
        Visible = false;
        Publish(PrinterRemoteState.Disconnected);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
