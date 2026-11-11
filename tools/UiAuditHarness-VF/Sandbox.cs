using System;
using System.IO;

namespace UiAuditHarness;

/// <summary>
/// Chuẩn bị môi trường SANDBOX cho harness.
///
/// Mọi DB của ứng dụng được trỏ tới thư mục sandbox bằng đường dẫn TUYỆT ĐỐI
/// (nên không phụ thuộc AppContext.BaseDirectory), và mọi cổng mạng được trỏ
/// về cổng chết / bị tắt để không đụng vào instance ứng dụng đang chạy.
///
/// KHÔNG bao giờ ghi vào TanTienQR\bin\Debug\net10.0\logs.
/// </summary>
internal static class Sandbox
{
    /// <summary>Thư mục gốc của harness (tools/UiAuditHarness).</summary>
    public static string HarnessRoot { get; } = AppContext.BaseDirectory;

    /// <summary>Thư mục sandbox chứa bản sao DB + appsettings.</summary>
    public static string SandboxRoot { get; private set; } = string.Empty;

    /// <summary>Thư mục lưu ảnh chụp.</summary>
    public static string ShotsRoot { get; private set; } = string.Empty;

    /// <summary>Thư mục lưu JSON visual-tree dump.</summary>
    public static string DumpRoot { get; private set; } = string.Empty;

    /// <summary>Thư mục gốc của repo (dùng để tìm appsettings mẫu của app).</summary>
    public static string RepoRoot { get; private set; } = string.Empty;

    /// <summary>
    /// Chuẩn bị sandbox. Trả về false nếu không tìm thấy DB nguồn.
    /// </summary>
    public static bool Prepare()
    {
        // tools/UiAuditHarness-VF/bin/Debug/net10.0/  ->  tools/UiAuditHarness-VF/
        var harnessProjectDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        // tools/UiAuditHarness-VF/  ->  repo root (có .git hoặc TanTienQR/)
        RepoRoot = FindRepoRoot(harnessProjectDir);

        SandboxRoot = Path.Combine(harnessProjectDir, "sandbox");
        ShotsRoot = Path.Combine(RepoRoot, "docs", "ui-audit-shots");
        DumpRoot = ShotsRoot;

        Directory.CreateDirectory(SandboxRoot);
        Directory.CreateDirectory(ShotsRoot);
        Directory.CreateDirectory(DumpRoot);

        var srcLogs = Path.Combine(
            RepoRoot, "TanTienQR", "bin", "Debug", "net10.0", "logs");

        var dstLogs = Path.Combine(SandboxRoot, "logs");
        Directory.CreateDirectory(dstLogs);

        // Copy toàn bộ DB người dùng vào sandbox (đọc-only từ nguồn).
        var copied = 0;
        if (Directory.Exists(srcLogs))
        {
            foreach (var db in Directory.GetFiles(srcLogs, "*.db", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcLogs, db);
                var dst = Path.Combine(dstLogs, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                CopyFile(db, dst);
                copied++;
            }
        }

        Console.WriteLine($"[Sandbox] repo      = {RepoRoot}");
        Console.WriteLine($"[Sandbox] sandbox   = {SandboxRoot}");
        Console.WriteLine($"[Sandbox] shots     = {ShotsRoot}");
        Console.WriteLine($"[Sandbox] copied {copied} db file(s) from real logs -> sandbox (read-only source)");

        return copied > 0;
    }

    /// <summary>
    /// Dò lên trên cho thư mục gốc repo (chứa .git và TanTienQR/).
    /// </summary>
    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "TanTienQR"))
                && Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        // Dự phòng: start/../..
        return Path.GetFullPath(Path.Combine(start, "..", ".."));
    }

    /// <summary>Copy file, bỏ qua nếu giống nhau (size + mtime).</summary>
    private static void CopyFile(string src, string dst)
    {
        try
        {
            var s = new FileInfo(src);
            if (File.Exists(dst))
            {
                var d = new FileInfo(dst);
                if (d.Length == s.Length && d.LastWriteTimeUtc == s.LastWriteTimeUtc)
                {
                    return;
                }
            }

            File.Copy(src, dst, overwrite: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[Sandbox] copy fail {src} -> {dst}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ghi đè appsettings trong sandbox bằng bản "an toàn":
    /// - DB trỏ tới đường dẫn tuyệt đối trong sandbox
    /// - Camera / PLC / Scanner / PO trỏ về cổng chết hoặc bị tắt
    /// - Web tắt (tránh WebView2 nhảy ra ngoài mạng trong audit)
    /// </summary>
    public static void WriteSandboxConfig()
    {
        var path = Path.Combine(SandboxRoot, "appsettings.json");
        var logs = Path.Combine(SandboxRoot, "logs");

        var json = $$"""
        {
          "Camera": {
            "Ip": "127.0.0.1",
            "Port": 1,
            "DuplicateWindowSeconds": 30
          },
          "Plc": {
            "Ip": "127.0.0.1",
            "Port": 1,
            "RemoteNode": 0,
            "LocalNode": 1,
            "TimeoutMs": 200,
            "Retries": 0,
            "ResultAddress": "D12",
            "ReadyAddress": "D16",
            "RefreshIntervalMs": 5000
          },
          "Web": {
            "Url": "about:blank",
            "Enabled": false
          },
          "Scanner": {
            "Enabled": false,
            "Mode": "TcpServer",
            "TcpHost": "127.0.0.1",
            "TcpPort": 1,
            "ComPort": "COM3",
            "BaudRate": 9600
          },
          "Logging": {
            "Sqlite": {
              "Path": {{Quote(Path.Combine(logs, "app.db"))}},
              "MinLevel": "Warning",
              "RetentionDays": 30,
              "EnableUserActions": true,
              "BatchSize": 100,
              "FlushIntervalMs": 1000,
              "PruneIntervalMs": 3600000
            },
            "History": {
              "Enabled": true,
              "PageSize": 200
            }
          },
          "Auth": {
            "DbPath": {{Quote(Path.Combine(logs, "users.db"))}},
            "MaxFailedAttempts": 5,
            "LockoutMinutes": 5,
            "Pbkdf2Iterations": 210000,
            "SeedAdminUser": "admin",
            "SeedAdminPassword": "admin"
          },
          "PlcSettings": {
            "DbPath": {{Quote(Path.Combine(logs, "plc.db"))}}
          },
          "Scans": {
            "DbPath": {{Quote(Path.Combine(logs, "scans.db"))}},
            "BatchSize": 100,
            "FlushIntervalMs": 1000,
            "QueueCapacity": 20000,
            "RetentionDays": 0,
            "PruneIntervalMs": 3600000
          },
          "Po": {
            "BaseUrl": "http://127.0.0.1:1",
            "DbRoot": {{Quote(Path.Combine(logs, "po"))}},
            "ActivationIntervalMs": 2500,
            "AutoActivate": false,
            "CodeSampleSize": 100
          }
        }
        """;

        File.WriteAllText(path, json);
        Console.WriteLine($"[Sandbox] wrote sandbox appsettings -> {path}");
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\") + "\"";
}
