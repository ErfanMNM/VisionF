using System.Diagnostics;
using System.Text;
using ClassLibraryMT.Updates;

namespace TanTienLauncher;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var root = AppContext.BaseDirectory;
        var rid = UpdateProtocol.CurrentRid;
        if (rid is null) return 1;
        try
        {
            using var launcherLock = UpdateProtocol.OpenLock(root, UpdateProtocol.LauncherLock);
            // Nếu launcher trước bị đóng nhưng app vẫn chạy, khóa này ngăn phục
            // hồi/thay binary khi tiến trình cũ chưa thoát.
            using (UpdateProtocol.OpenLock(root, UpdateProtocol.ApplicationLock)) { }
            var transaction = new UpdateTransaction(root, UpdateProtocol.PublicKey, rid);
            transaction.Rollback();
            var trial = false;
            var restart = args.Contains(UpdateProtocol.RestartMarker, StringComparer.Ordinal);
            while (true)
            {
                var session = Guid.NewGuid().ToString("N");
                var info = new ProcessStartInfo(Path.Combine(root, UpdateProtocol.AppFileName(rid)))
                {
                    WorkingDirectory = root,
                    UseShellExecute = false
                };
                info.Environment[UpdateProtocol.SessionVariable] = session;
                info.Environment[UpdateProtocol.TrialVariable] = trial ? "1" : "0";
                foreach (var argument in args.Where(argument => argument != UpdateProtocol.RestartMarker))
                    info.ArgumentList.Add(argument);
                if (restart) info.ArgumentList.Add(UpdateProtocol.RestartMarker);
                Process process;
                try
                {
                    process = Process.Start(info) ?? throw new IOException("Không khởi chạy được TanTienQR.");
                }
                catch when (trial)
                {
                    transaction.Rollback("Không mở được bản mới; đã phục hồi phiên bản trước.");
                    trial = false;
                    restart = true;
                    continue;
                }
                using var child = process;
                if (trial)
                {
                    if (!await SuperviseTrialAsync(child, root, session, transaction).ConfigureAwait(false))
                    {
                        trial = false;
                        restart = true;
                        continue;
                    }
                    trial = false;
                }
                await child.WaitForExitAsync().ConfigureAwait(false);
                var requestPath = UpdateProtocol.SessionPath(root, session, "request");
                var request = child.ExitCode == 0 && UpdateDisk.Exists(requestPath, directory: false)
                    ? Encoding.UTF8.GetString(UpdateDisk.ReadSmall(requestPath, 100)) : "";
                CleanupSession(root, session);
                if (request == "restart")
                {
                    restart = true;
                    continue;
                }
                if (!request.StartsWith("install:", StringComparison.Ordinal)) return child.ExitCode;
                using (UpdateProtocol.OpenLock(root, UpdateProtocol.ApplicationLock)) { }
                try
                {
                    transaction.Apply(request["install:".Length..]);
                    trial = true;
                }
                catch (Exception e)
                {
                    if (!transaction.Rollback("Cài đặt không thành công; đã phục hồi phiên bản trước."))
                        transaction.WriteResult("Chưa cài bản cập nhật: " + e.Message);
                }
                restart = true;
            }
        }
        catch (Exception e)
        {
            // Khóa đang được giữ bởi phiên khác cũng kết thúc tại đây, không cố
            // giết tiến trình hoặc sửa tệp của phiên đó.
            Console.Error.WriteLine("Không thể mở TanTienQR: " + e.Message);
            return 1;
        }
    }

    internal static async Task<bool> SuperviseTrialAsync(Process child, string root, string session,
        UpdateTransaction transaction, TimeSpan? timeout = null)
    {
        try
        {
            var deadline = Stopwatch.StartNew();
            var ready = UpdateProtocol.SessionPath(root, session, "ready");
            while (deadline.Elapsed < (timeout ?? UpdateProtocol.TrialTimeout))
            {
                if (child.HasExited) break;
                if (UpdateDisk.Exists(ready, directory: false))
                {
                    transaction.Commit();
                    UpdateDisk.WriteAtomic(UpdateProtocol.SessionPath(root, session, "committed"), []);
                    return true;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        catch
        {
            // Có thể journal đã được commit bền nhưng marker không ghi được.
            // Trong cả hai trường hợp đều dừng trial trước khi mở app lần nữa;
            // chỉ rollback nếu nhật ký còn tồn tại.
        }
        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().ConfigureAwait(false);
        }
        using (UpdateProtocol.OpenLock(root, UpdateProtocol.ApplicationLock)) { }
        if (transaction.HasPending)
            transaction.Rollback("Bản mới không khởi động được trong thời hạn; đã phục hồi phiên bản trước.");
        CleanupSession(root, session);
        return false;
    }

    private static void CleanupSession(string root, string session)
    {
        foreach (var suffix in new[] { "ready", "committed", "request" })
        {
            try { UpdateDisk.DeleteFile(UpdateProtocol.SessionPath(root, session, suffix)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
