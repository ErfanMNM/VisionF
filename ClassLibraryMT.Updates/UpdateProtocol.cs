using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassLibraryMT.Updates;

public static class UpdateProtocol
{
    internal const string SessionVariable = "TANTIEN_UPDATER_SESSION";
    internal const string TrialVariable = "TANTIEN_UPDATER_TRIAL";
    internal const string RestartMarker = "--restart-child";
    internal const string LauncherLock = "launcher.lock";
    internal const string ApplicationLock = "application.lock";
    internal const string ResultFile = "last-result.txt";
    internal const string UpdateDirectoryName = ".tantien-updates";
    internal static readonly TimeSpan TrialTimeout = TimeSpan.FromSeconds(60);

    public static string? CurrentRid => RuntimeInformation.ProcessArchitecture != Architecture.X64 ? null
        : OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : null;

    public static string PublicKey { get; } = typeof(UpdateProtocol).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "UpdatePublicKey")?.Value ?? "";

    internal static Version BuildVersion
    {
        get
        {
            var version = typeof(UpdateProtocol).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    public static string? LauncherSession { get; } =
        Guid.TryParseExact(Environment.GetEnvironmentVariable(SessionVariable), "N", out var session)
            ? session.ToString("N") : null;

    public static bool IsHealthCheck => LauncherSession is not null
        && Environment.GetEnvironmentVariable(TrialVariable) == "1";

    public static string GetLauncherPath(string installationRoot) =>
        Path.Combine(installationRoot, OperatingSystem.IsWindows() ? "TanTienLauncher.exe" : "TanTienLauncher");

    internal static string AppFileName(string rid) => rid == "win-x64" ? "TanTienQR.exe" : "TanTienQR";

    internal static string StateDirectory(string installationRoot) =>
        Path.Combine(Path.GetFullPath(installationRoot), UpdateDirectoryName);

    public static string GetStagingDirectory(string installationRoot, string version)
    {
        _ = ParseVersion(version);
        return Path.Combine(StateDirectory(installationRoot), "staged", version);
    }

    internal static string SessionPath(string installationRoot, string session, string suffix) =>
        Path.Combine(StateDirectory(installationRoot), session + "." + suffix);

    public static IDisposable? AcquireApplicationLock() => LauncherSession is null ? null
        : OpenLock(AppContext.BaseDirectory, ApplicationLock);

    internal static FileStream OpenLock(string installationRoot, string name)
    {
        var state = StateDirectory(installationRoot);
        UpdateDisk.EnsureDirectory(state);
        var path = Path.Combine(state, name);
        UpdateDisk.Exists(path, directory: false);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static void RequestInstall(string version)
    {
        _ = ParseVersion(version);
        WriteRequest("install:" + version);
    }

    public static bool TryRequestRestart()
    {
        if (LauncherSession is null) return false;
        try
        {
            WriteRequest("restart");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteRequest(string request)
    {
        if (LauncherSession is null)
            throw new InvalidOperationException("Hãy mở TanTienQR qua TanTienLauncher.");
        UpdateDisk.WriteAtomic(SessionPath(AppContext.BaseDirectory, LauncherSession, "request"),
            Encoding.UTF8.GetBytes(request));
    }

    public static bool TryReportHealthy()
    {
        if (!IsHealthCheck) return true;
        try
        {
            UpdateDisk.WriteAtomic(SessionPath(AppContext.BaseDirectory, LauncherSession!, "ready"), []);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitForCommitAsync(CancellationToken cancellationToken = default)
    {
        if (!IsHealthCheck) return true;
        var marker = SessionPath(AppContext.BaseDirectory, LauncherSession!, "committed");
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TrialTimeout)
        {
            if (File.Exists(marker)) return true;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    public static string? ReadLastResult(string installationRoot)
    {
        try
        {
            var path = Path.Combine(StateDirectory(installationRoot), ResultFile);
            if (!UpdateDisk.Exists(Path.GetDirectoryName(path)!, directory: true)
                || !UpdateDisk.Exists(path, directory: false))
                return null;
            return Encoding.UTF8.GetString(UpdateDisk.ReadSmall(path, 2048));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static Version ParseVersion(string version)
    {
        if (Version.TryParse(version, out var value) && value.Build >= 0 && value.Revision == -1
            && value.ToString(3) == version)
            return value;
        throw new InvalidDataException("Phiên bản cập nhật phải có định dạng X.Y.Z.");
    }
}
