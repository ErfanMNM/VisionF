using System;
using System.IO;
using System.Text.Json;

namespace UiAuditHarnessRunner;

/// <summary>
/// Chuẩn bị môi trường SANDBOX cho lần chạy kiểm tra giao diện.
///
/// Mọi DB của ứng dụng được trỏ tới thư mục sandbox bằng đường dẫn TUYỆT ĐỐI
/// (không phụ thuộc AppContext.BaseDirectory), và mọi cổng mạng được trỏ
/// về cổng chết / bị tắt để không đụng vào hệ thống thật.
///
/// KHÔNG bao giờ ghi vào TanTienQR\bin\Debug\net10.0\logs.
/// </summary>
internal static class Sandbox
{
    /// <summary>Thư mục gốc của sandbox (được ghi đè bằng biến môi trường).</summary>
    public static string Root { get; private set; } = string.Empty;

    /// <summary>Thư mục chứa ảnh chụp PNG.</summary>
    public static string ShotsDir { get; private set; } = string.Empty;

    /// <summary>Thư mục chứa JSON dump cây trực quan.</summary>
    public static string DumpDir { get; private set; } = string.Empty;

    /// <summary>Thư mục chứa log của chính harness.</summary>
    public static string LogDir { get; private set; } = string.Empty;

    /// <summary>Thư mục chứa kết quả tổng hợp (findings.json).</summary>
    public static string OutDir { get; private set; } = string.Empty;

    public static string AppsettingsPath => Path.Combine(Root, "appsettings.json");

    public static string UsersDbPath => Path.Combine(Root, "logs", "users.db");

    /// <summary>
    /// Chuẩn bị thư mục sandbox + file cấu hình cô lập.
    /// </summary>
    /// <param name="appBinDir">
    /// Thư mục output của app đã build (dùng để lấy users.db làm seed).
    /// </param>
    public static void Prepare(string appBinDir)
    {
        Root = Path.GetFullPath(Environment.GetEnvironmentVariable("UI_AUDIT_SANDBOX")
            ?? Path.Combine(AppContext.BaseDirectory, "sandbox"));

        ShotsDir = Path.GetFullPath(Environment.GetEnvironmentVariable("UI_AUDIT_SHOTS")
            ?? Path.Combine(AppContext.BaseDirectory, "shots"));

        DumpDir = Path.GetFullPath(Environment.GetEnvironmentVariable("UI_AUDIT_DUMPS")
            ?? Path.Combine(AppContext.BaseDirectory, "dumps"));

        OutDir = Path.Combine(AppContext.BaseDirectory, "out");
        LogDir = Path.Combine(Root, "harness-logs");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "logs"));
        Directory.CreateDirectory(Path.Combine(Root, "logs", "po"));
        Directory.CreateDirectory(ShotsDir);
        Directory.CreateDirectory(DumpDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(OutDir);

        WriteSandboxAppsettings();

        // Copy users.db của app (nếu có) vào sandbox -> giữ nguyên tài khoản
        // admin / tantien512 mà người dùng đã cung cấp.
        CopySeedUsersDb(appBinDir);

        // Copy các DB khác (chỉ đọc) để view nào cần dữ liệu lịch sử vẫn
        // có dữ liệu để hiển thị (giống môi trường thật).
        foreach (var name in new[] { "app.db", "scans.db", "plc.db" })
        {
            CopyIfExists(Path.Combine(appBinDir, "logs", name), Path.Combine(Root, "logs", name));
        }
    }

    private static void CopyIfExists(string src, string dst)
    {
        if (!File.Exists(src))
        {
            return;
        }

        try
        {
            File.Copy(src, dst, overwrite: true);
        }
        catch (IOException)
        {
            // Không sao: view tương ứng sẽ hiển thị trạng thái rỗng.
        }
    }

    private static void CopySeedUsersDb(string appBinDir)
    {
        var dst = UsersDbPath;

        if (File.Exists(dst))
        {
            TryPromoteAdminToDeveloper(dst);
            return;
        }

        CopyIfExists(Path.Combine(appBinDir, "logs", "users.db"), dst);

        if (!File.Exists(dst))
        {
            // Không có DB seed -> app sẽ tự tạo tài khoản admin từ cấu hình
            // (Auth:SeedAdminPassword = tantien512, MustChangePassword = 1).
            return;
        }

        // Nâng quyền tài khoản admin trong sandbox lên Developer (role 3) để
        // mở được tab Dev (CanViewDev yêu cầu đúng vai trò Lập trình viên).
        // Đây là DB trong sandbox, KHÔNG ảnh hưởng dữ liệu thật.
        TryPromoteAdminToDeveloper(dst);
    }

    /// <summary>
    /// Đổi role của tài khoản 'admin' trong DB sandbox sang Developer (3).
    /// </summary>
    public static bool TryPromoteAdminToDeveloper(string dbPath)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                }.ToString());

            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Users SET role = 3 WHERE lower(username) = 'admin';";
            cmd.ExecuteNonQuery();

            Log("Da nang quyen admin -> Developer trong DB sandbox.");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Sandbox] Khong nang quyen admin -> Developer: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Ghi appsettings.json cô lập cho sandbox:
    ///  - toàn bộ DB trỏ về thư mục sandbox
    ///  - PLC / camera / scanner / web / PO server trỏ về cổng &amp; địa chỉ
    ///    KHÔNG có thật (hoặc đã bị tắt) -> không đụng hệ thống thật.
    /// </summary>
    private static void WriteSandboxAppsettings()
    {
        var cfg = new
        {
            Camera = new
            {
                Ip = "127.0.0.1",
                Port = 59000,          // cổng camera thật (9000) KHÔNG dùng
                DuplicateWindowSeconds = 30,
            },
            Plc = new
            {
                Ip = "127.0.0.1",
                Port = 59600,          // cổng PLC thật (9600) KHÔNG dùng
                RemoteNode = 0,
                LocalNode = 1,
                TimeoutMs = 400,       // timeout ngắn: kết nối fail nhanh
                Retries = 0,
                ResultAddress = "D12",
                ReadyAddress = "D16",
                RefreshIntervalMs = 3000,
            },
            Web = new
            {
                Url = "about:blank",    // không gọi estrace.checkee.vn
                Enabled = false,        // tắt hẳn WebView -> không khởi tạo WebView2
            },
            Scanner = new
            {
                Enabled = false,        // KHÔNG bind TCP 6999
                Mode = "TcpServer",
                TcpHost = "127.0.0.1",
                TcpPort = 56999,       // cổng scanner thật (6999) KHÔNG dùng
                ComPort = "COM3",
                BaudRate = 9600,
            },
            Logging = new
            {
                Sqlite = new
                {
                    Path = "logs/app.db",
                    MinLevel = "Information",
                    RetentionDays = 30,
                    EnableUserActions = true,
                    BatchSize = 100,
                    FlushIntervalMs = 1000,
                    PruneIntervalMs = 3600000,
                },
                History = new
                {
                    Enabled = true,
                    PageSize = 200,
                },
            },
            Auth = new
            {
                DbPath = "logs/users.db",
                MaxFailedAttempts = 5,
                LockoutMinutes = 5,
                Pbkdf2Iterations = 210000,
                SeedAdminUser = "admin",
                SeedAdminPassword = "tantien512",
            },
            PlcSettings = new { DbPath = "logs/plc.db" },
            Scans = new
            {
                DbPath = "logs/scans.db",
                BatchSize = 100,
                FlushIntervalMs = 1000,
                QueueCapacity = 20000,
                RetentionDays = 0,
                PruneIntervalMs = 3600000,
            },
            Po = new
            {
                BaseUrl = "http://127.0.0.1:55080",   // cổng PO server thật (5080) KHÔNG dùng
                DbRoot = "logs/po",
                ActivationIntervalMs = 2500,
                AutoActivate = false,                  // không gửi mã kích hoạt ra ngoài
                CodeSampleSize = 100,
            },
        };

        var json = JsonSerializer.Serialize(
            cfg,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(AppsettingsPath, json);
    }

    /// <summary>Ghi log của harness vào sandbox.</summary>
    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(LogDir, "harness.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Bỏ qua.
        }
    }
}
