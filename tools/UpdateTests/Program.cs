using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Updates;

namespace UpdateTests;

internal static class Program
{
    private static readonly string Rid = UpdateProtocol.CurrentRid ?? throw new PlatformNotSupportedException();
    private static readonly string AppName = UpdateProtocol.AppFileName(Rid);
    private static readonly string LibraryName = Rid == "win-x64" ? "native.dll" : "libnative.so.1";
    private static readonly string NewLibraryName = Rid == "win-x64" ? "added.dll" : "libadded.so";
    private static readonly byte[] OldApp = "chương trình cũ"u8.ToArray();
    private static readonly byte[] OldLibrary = "thư viện cũ"u8.ToArray();
    private static readonly byte[] NewApp = "chương trình mới"u8.ToArray();
    private static readonly byte[] NewLibrary = "thư viện mới"u8.ToArray();

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--verify-release", var releaseRoot])
        {
            var key = File.ReadAllText(Path.Combine(releaseRoot, "public-key.spki")).Trim();
            var files = Path.Combine(releaseRoot, Rid);
            var envelope = File.ReadAllBytes(Path.Combine(files, "manifest.json"));
            var manifest = UpdatePackage.ReadManifest(envelope, key, Rid);
            var installRoot = Path.Combine(releaseRoot, "verified-" + Guid.NewGuid().ToString("N"));
            await using var stream = File.OpenRead(Path.Combine(files, "package.zip"));
            await UpdatePackage.StageAsync(installRoot, envelope, stream, key, Rid);
            Console.WriteLine($"Đã xác minh gói phát hành {manifest.Version} ({manifest.Rid}): {manifest.Files.Count} tệp.");
            return 0;
        }
        if (args is ["--fixture", var root, var session, var mode])
        {
            if (mode == "exit") return 42;
            if (mode == "healthy")
            {
                UpdateDisk.WriteAtomic(UpdateProtocol.SessionPath(root, session, "ready"), []);
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(20))
                {
                    if (File.Exists(UpdateProtocol.SessionPath(root, session, "committed"))) return 0;
                    await Task.Delay(50);
                }
                return 3;
            }
            await Task.Delay(TimeSpan.FromMinutes(2));
            return 4;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Từ chối chữ ký, nền tảng và manifest hỏng", ManifestChecks),
            ("Từ chối ZIP thừa, payload sai, symlink và tải thiếu", ZipChecks),
            ("Giữ cấu hình/license/DB và phục hồi sau mất điện", InterruptedReplacement),
            ("Phục hồi idempotent khi mất điện lần nữa", InterruptedRollback),
            ("Mất điện ngay trước commit trả cả version và binary cũ", InterruptedCommit),
            ("Bản sao thiếu thì dừng, không chạy bộ binary trộn", MissingBackup),
            ("Launcher commit sau xác nhận của tiến trình con", HealthyTrial),
            ("Lỗi marker sau commit vẫn dừng trial trước khi mở lại", CommitSignalFailure),
            ("Launcher rollback khi tiến trình con thoát lỗi", FailedTrial),
            ("Launcher rollback khi hết hạn chờ", TimedOutTrial),
            ("Khóa phiên ngăn hai launcher cùng sửa tệp", SessionLocks),
            ("Chặn hạ phiên bản sau commit", PreventDowngrade),
            ("Từ chối staging trỏ qua liên kết thư mục", RejectLinkedState),
        };
        var failures = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + e); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} kiểm thử đạt.");
        return failures == 0 ? 0 : 1;
    }

    private static Task ManifestChecks()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        var key = fixture.PublicKey;
        var changed = JsonSerializer.Deserialize(package.Envelope, UpdateJsonContext.Default.SignedEnvelope)!;
        var payload = Convert.FromBase64String(changed.Data);
        payload[0] ^= 1;
        changed.Data = Convert.ToBase64String(payload);
        Throws(() => UpdatePackage.ReadManifest(JsonSerializer.SerializeToUtf8Bytes(changed,
            UpdateJsonContext.Default.SignedEnvelope), key, Rid));
        Throws(() => UpdatePackage.ReadManifest(package.Envelope, key, Rid == "win-x64" ? "linux-x64" : "win-x64"));
        foreach (var invalid in new[] { "appsettings.json", "license.lic", "../TanTienQR", "/TanTienQR",
                     "CON.dll", "TanTienLauncher.exe", "logs/app.db", "thing.dll:stream", "native.dll." })
        {
            var manifest = fixture.Manifest(package.Zip);
            manifest.Files.Add(new UpdateFile { Path = invalid, Sha256 = new string('a', 64), Size = 1 });
            Throws(() => UpdatePackage.ReadManifest(fixture.Sign(manifest), key, Rid));
        }
        var duplicates = fixture.Manifest(package.Zip);
        duplicates.Files.Add(duplicates.Files[0]);
        Throws(() => UpdatePackage.ReadManifest(fixture.Sign(duplicates), key, Rid));
        var oversize = fixture.Manifest(package.Zip);
        oversize.Size = UpdatePackage.MaxPackageBytes + 1;
        Throws(() => UpdatePackage.ReadManifest(fixture.Sign(oversize), key, Rid));
        var noncanonical = fixture.Manifest(package.Zip);
        noncanonical.Version = "02.0.0";
        Throws(() => UpdatePackage.ReadManifest(fixture.Sign(noncanonical), key, Rid));
        return Task.CompletedTask;
    }

    private static async Task ZipChecks()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        var damaged = fixture.Manifest(package.Zip);
        damaged.Files[0].Sha256 = new string('0', 64);
        await ThrowsAsync(() => fixture.Stage(fixture.Sign(damaged), package.Zip));
        Check(!Directory.Exists(UpdateProtocol.GetStagingDirectory(fixture.Root, "2.0.0")), "Không công bố staging lỗi.");
        await ThrowsAsync(() => fixture.Stage(package.Envelope, package.Zip[..^1]));
        var extra = fixture.Package(extra: true);
        await ThrowsAsync(() => fixture.Stage(extra.Envelope, extra.Zip));
        var symlink = fixture.Package(symlink: true);
        await ThrowsAsync(() => fixture.Stage(symlink.Envelope, symlink.Zip));
        await fixture.Stage(package.Envelope, package.Zip);
        var stage = UpdateProtocol.GetStagingDirectory(fixture.Root, "2.0.0");
        Check(File.ReadAllBytes(Path.Combine(stage, "content", AppName)).SequenceEqual(NewApp), "Nội dung staging đúng.");
        if (OperatingSystem.IsLinux())
            Check((File.GetUnixFileMode(Path.Combine(stage, "content", AppName)) & UnixFileMode.UserExecute) != 0,
                "Chương trình Linux có bit executable.");
    }

    private static async Task InterruptedReplacement()
    {
        using var fixture = new Fixture();
        await fixture.Stage();
        var transaction = fixture.Transaction();
        transaction.Checkpoint = point =>
        {
            if (point == "replaced:" + AppName) throw new SimulatedPowerLoss();
        };
        Throws<SimulatedPowerLoss>(() => transaction.Apply("2.0.0"));
        Check(File.ReadAllBytes(Path.Combine(fixture.Root, AppName)).SequenceEqual(NewApp), "Đã thay ít nhất một binary.");
        Check(fixture.Transaction().Rollback(), "Khôi phục được nhật ký từ phiên mới.");
        fixture.AssertOld();
        Check(!fixture.Transaction().HasPending, "Xóa nhật ký sau khi phục hồi xong.");
        // Cài lại cùng phiên bản sau rollback được phép.
        fixture.Transaction().Apply("2.0.0");
        fixture.Transaction().Rollback();
    }

    private static async Task InterruptedRollback()
    {
        using var fixture = new Fixture();
        await fixture.Stage();
        fixture.Transaction().Apply("2.0.0");
        var transaction = fixture.Transaction();
        transaction.Checkpoint = point =>
        {
            if (point == "restored:" + AppName) throw new SimulatedPowerLoss();
        };
        Throws<SimulatedPowerLoss>(() => transaction.Rollback());
        Check(fixture.Transaction().HasPending, "Giữ nhật ký cho tới khi toàn bộ bộ cũ được phục hồi.");
        fixture.Transaction().Rollback();
        fixture.AssertOld();
    }

    private static async Task MissingBackup()
    {
        using var fixture = new Fixture();
        await fixture.Stage();
        fixture.Transaction().Apply("2.0.0");
        var backup = Directory.GetDirectories(Path.Combine(UpdateProtocol.StateDirectory(fixture.Root), "backups")).Single();
        File.Delete(Path.Combine(backup, AppName));
        Throws(() => fixture.Transaction().Rollback());
        Check(fixture.Transaction().HasPending, "Giữ bằng chứng để sửa thủ công.");
        Check(File.ReadAllBytes(Path.Combine(fixture.Root, AppName)).SequenceEqual(NewApp), "Không phục hồi từng phần khi backup thiếu.");
    }

    private static async Task InterruptedCommit()
    {
        using var fixture = new Fixture();
        await fixture.Stage();
        var transaction = fixture.Transaction();
        transaction.Apply("2.0.0");
        transaction.Checkpoint = point =>
        {
            if (point == "version-recorded") throw new SimulatedPowerLoss();
        };
        Throws<SimulatedPowerLoss>(() => transaction.Commit());
        fixture.Transaction().Rollback();
        fixture.AssertOld();
        // Version pointer đã trả về baseline, nên có thể thử cài 2.0.0 lần nữa.
        fixture.Transaction().Apply("2.0.0");
        fixture.Transaction().Rollback();
    }

    private static async Task HealthyTrial()
    {
        using var fixture = new Fixture();
        await fixture.Stage();
        var transaction = fixture.Transaction();
        transaction.Apply("2.0.0");
        var session = Guid.NewGuid().ToString("N");
        using var child = StartFixture(fixture.Root, session, "healthy");
        Check(await TanTienLauncher.Program.SuperviseTrialAsync(child, fixture.Root, session, transaction,
            TimeSpan.FromSeconds(10)), "Chấp nhận trial khỏe.");
        await child.WaitForExitAsync();
        Check(child.ExitCode == 0, "App nhận được marker commit trước khi mở hoạt động.");
        Check(!transaction.HasPending, "Không còn giao dịch dang dở sau commit.");
        Check(!transaction.Rollback(), "Không rollback nhầm bản đã commit.");
        Check(File.ReadAllBytes(Path.Combine(fixture.Root, AppName)).SequenceEqual(NewApp), "Giữ bản mới sau commit.");
        fixture.AssertData();
    }

    private static async Task FailedTrial() => await TrialFailure("exit", TimeSpan.FromSeconds(10));
    private static async Task TimedOutTrial() => await TrialFailure("sleep", TimeSpan.FromMilliseconds(250));

    private static async Task CommitSignalFailure()
    {
        using var fixture = new Fixture();
        await fixture.Stage();
        var transaction = fixture.Transaction();
        transaction.Apply("2.0.0");
        var session = Guid.NewGuid().ToString("N");
        // Mô phỏng lỗi ghi sau điểm commit: destination marker bị thay thành thư mục.
        var badMarker = UpdateProtocol.SessionPath(fixture.Root, session, "committed");
        Directory.CreateDirectory(badMarker);
        using var child = StartFixture(fixture.Root, session, "healthy");
        try
        {
            await TanTienLauncher.Program.SuperviseTrialAsync(child, fixture.Root, session, transaction,
                TimeSpan.FromSeconds(10));
        }
        catch (IOException)
        {
            // Cleanup marker lỗi có thể tiếp tục báo lỗi, nhưng child đã được dừng.
        }
        Check(child.HasExited, "Không để trial mồ côi sau lỗi marker.");
        Check(!transaction.HasPending, "Bản đã commit không bị rollback lại.");
        Check(File.ReadAllBytes(Path.Combine(fixture.Root, AppName)).SequenceEqual(NewApp), "Giữ bản mới đã được kiểm tra khỏe.");
        fixture.AssertData();
    }

    private static async Task TrialFailure(string mode, TimeSpan timeout)
    {
        using var fixture = new Fixture();
        await fixture.Stage();
        var transaction = fixture.Transaction();
        transaction.Apply("2.0.0");
        var session = Guid.NewGuid().ToString("N");
        using var child = StartFixture(fixture.Root, session, mode);
        Check(!await TanTienLauncher.Program.SuperviseTrialAsync(child, fixture.Root, session, transaction, timeout),
            "Trial hỏng phải thất bại.");
        Check(child.HasExited, "Tiến trình mới đã thoát trước khi phục hồi.");
        fixture.AssertOld();
        Check(UpdateProtocol.ReadLastResult(fixture.Root)?.Contains("phục hồi") == true, "Có thông điệp rollback cho người vận hành.");
    }

    private static Task SessionLocks()
    {
        using var fixture = new Fixture();
        using var file = UpdateProtocol.OpenLock(fixture.Root, UpdateProtocol.LauncherLock);
        Throws<IOException>(() => UpdateProtocol.OpenLock(fixture.Root, UpdateProtocol.LauncherLock));
        return Task.CompletedTask;
    }

    private static async Task PreventDowngrade()
    {
        using var fixture = new Fixture();
        await fixture.Stage();
        var transaction = fixture.Transaction();
        transaction.Apply("2.0.0");
        transaction.Commit();
        Throws(() => transaction.Apply("2.0.0"));
        Check(!transaction.HasPending, "Không chuẩn bị giao dịch hạ phiên bản.");
    }

    private static async Task RejectLinkedState()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "giữ nguyên");
        Directory.CreateSymbolicLink(UpdateProtocol.StateDirectory(fixture.Root), outside);
        var package = fixture.Package();
        await ThrowsAsync(() => fixture.Stage(package.Envelope, package.Zip));
        Check(File.ReadAllText(sentinel) == "giữ nguyên" && Directory.GetFileSystemEntries(outside).Length == 1,
            "Không ghi hoặc xóa dữ liệu qua liên kết.");
    }

    private static Process StartFixture(string root, string session, string mode)
    {
        var process = Environment.ProcessPath!;
        var info = new ProcessStartInfo(process) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var value in new[] { "--fixture", root, session, mode }) info.ArgumentList.Add(value);
        return Process.Start(info)!;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws(Action operation) => Throws<Exception>(operation);
    private static void Throws<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new Exception("Thao tác cần bị từ chối.");
    }
    private static async Task ThrowsAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception) { return; }
        throw new Exception("Thao tác cần bị từ chối.");
    }

    private sealed class SimulatedPowerLoss : Exception;

    private sealed class Fixture : IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tantien-update-tests-" + Guid.NewGuid().ToString("N"));
        public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Path.Combine(Root, AppName), OldApp);
            File.WriteAllBytes(Path.Combine(Root, LibraryName), OldLibrary);
            File.WriteAllText(Path.Combine(Root, "appsettings.json"), "cấu hình khách");
            File.WriteAllText(Path.Combine(Root, "license.lic"), "license khách");
            Directory.CreateDirectory(Path.Combine(Root, "logs"));
            File.WriteAllText(Path.Combine(Root, "logs", "po.db"), "dữ liệu PO khách");
        }

        public (byte[] Envelope, byte[] Zip) Package(bool extra = false, bool symlink = false)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, bytes) in new[]
                    { (AppName, NewApp), (LibraryName, NewLibrary), (NewLibraryName, NewLibrary) })
                {
                    var entry = zip.CreateEntry(path);
                    if (symlink && path == AppName) entry.ExternalAttributes = unchecked((int)0xa1ff0000);
                    using var output = entry.Open();
                    output.Write(bytes);
                }
                if (extra) zip.CreateEntry("appsettings.json");
            }
            var archive = stream.ToArray();
            return (Sign(Manifest(archive)), archive);
        }

        public UpdateManifest Manifest(byte[] zip) => new()
        {
            Version = "2.0.0", Rid = Rid, Notes = "Thử nghiệm cập nhật", Size = zip.Length, Sha256 = Hash(zip),
            Files =
            [
                new() { Path = AppName, Size = NewApp.Length, Sha256 = Hash(NewApp), Executable = true },
                new() { Path = LibraryName, Size = NewLibrary.Length, Sha256 = Hash(NewLibrary) },
                new() { Path = NewLibraryName, Size = NewLibrary.Length, Sha256 = Hash(NewLibrary) }
            ]
        };

        public byte[] Sign(UpdateManifest manifest)
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJsonContext.Default.UpdateManifest);
            return JsonSerializer.SerializeToUtf8Bytes(new SignedEnvelope
            {
                Data = Convert.ToBase64String(data),
                Signature = Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            }, UpdateJsonContext.Default.SignedEnvelope);
        }

        public async Task Stage()
        {
            var package = Package();
            await Stage(package.Envelope, package.Zip);
        }

        public async Task Stage(byte[] envelope, byte[] zip)
        {
            using var stream = new MemoryStream(zip);
            await UpdatePackage.StageAsync(Root, envelope, stream, PublicKey, Rid);
        }

        public UpdateTransaction Transaction() => new(Root, PublicKey, Rid);

        public void AssertOld()
        {
            Check(File.ReadAllBytes(Path.Combine(Root, AppName)).SequenceEqual(OldApp), "EXE cũ đã phục hồi.");
            Check(File.ReadAllBytes(Path.Combine(Root, LibraryName)).SequenceEqual(OldLibrary), "Thư viện cũ đã phục hồi.");
            Check(!File.Exists(Path.Combine(Root, NewLibraryName)), "Thư viện mới không còn sau rollback.");
            AssertData();
        }

        public void AssertData()
        {
            Check(File.ReadAllText(Path.Combine(Root, "appsettings.json")) == "cấu hình khách", "Giữ cấu hình.");
            Check(File.ReadAllText(Path.Combine(Root, "license.lic")) == "license khách", "Giữ license.");
            Check(File.ReadAllText(Path.Combine(Root, "logs", "po.db")) == "dữ liệu PO khách", "Giữ SQLite.");
        }

        public void Dispose()
        {
            _key.Dispose();
            // Root được tạo trực tiếp trong temp và giữ nguyên trong suốt fixture.
            Directory.Delete(Root, recursive: true);
        }
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }
}
