using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassLibraryMT.Updates;

internal sealed class UpdateJournal
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Rid { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
    public List<UpdateJournalFile> Files { get; set; } = [];
}

internal sealed class UpdateJournalFile
{
    public string Path { get; set; } = "";
    public bool HadOriginal { get; set; }
    public long OriginalSize { get; set; }
    public string OriginalSha256 { get; set; } = "";
    public int? OriginalMode { get; set; }
}

/// <summary>
/// Giao dịch chỉ thay tập binary đã ký. Nhật ký được ghi bền trước lần thay đầu;
/// khi chưa commit, mọi lần khởi chạy đều phục hồi toàn bộ tập cũ.
/// </summary>
public sealed class UpdateTransaction
{
    private const string JournalName = "active.json";
    private const string CurrentVersionName = "current-version.txt";
    private readonly string _root;
    private readonly string _state;
    private readonly string _publicKey;
    private readonly string _rid;
    internal Action<string>? Checkpoint { get; set; }

    public UpdateTransaction(string installationRoot, string publicKey, string rid)
    {
        _root = Path.GetFullPath(installationRoot);
        _state = UpdateProtocol.StateDirectory(_root);
        _publicKey = publicKey;
        _rid = rid;
    }

    public bool HasPending => File.Exists(Path.Combine(_state, JournalName));

    public UpdateManifest Apply(string version)
    {
        UpdateDisk.EnsureDirectory(_state);
        if (HasPending) throw new InvalidOperationException("Phải phục hồi giao dịch cũ trước khi cài.");
        var baselinePath = Path.Combine(_state, CurrentVersionName);
        var baseline = UpdateDisk.Exists(baselinePath, directory: false)
            ? UpdateProtocol.ParseVersion(Encoding.UTF8.GetString(UpdateDisk.ReadSmall(baselinePath, 100)))
            : UpdateProtocol.BuildVersion;
        if (UpdateProtocol.ParseVersion(version) <= baseline)
            throw new InvalidDataException("Chỉ được cài phiên bản cao hơn bản hiện tại.");
        var manifest = UpdatePackage.VerifyStaged(_root, version, _publicKey, _rid);
        var journal = new UpdateJournal
        {
            Id = Guid.NewGuid().ToString("N"), Version = version, Rid = _rid,
            PreviousVersion = baseline.ToString(3)
        };
        var backup = Path.Combine(_state, "backups", journal.Id);
        UpdateDisk.EnsureDirectory(backup);
        long backupSize = 0;
        foreach (var file in manifest.Files)
        {
            var source = Path.Combine(_root, file.Path);
            var item = new UpdateJournalFile { Path = file.Path, HadOriginal = UpdateDisk.Exists(source, directory: false) };
            if (item.HadOriginal)
            {
                using (var stream = File.OpenRead(source))
                {
                    item.OriginalSize = stream.Length;
                    if (item.OriginalSize > UpdatePackage.MaxExpandedBytes)
                        throw new InvalidDataException("Binary đang cài quá lớn để sao lưu an toàn.");
                    backupSize = checked(backupSize + item.OriginalSize);
                    if (backupSize > UpdatePackage.MaxExpandedBytes)
                        throw new InvalidDataException("Bộ binary đang cài quá lớn để sao lưu an toàn.");
                    item.OriginalSha256 = Convert.ToHexString(SHA256.HashData(stream));
                }
                if (OperatingSystem.IsLinux()) item.OriginalMode = (int)File.GetUnixFileMode(source);
                CopyDurable(source, Path.Combine(backup, file.Path), item.OriginalMode);
                UpdatePackage.VerifyFile(Path.Combine(backup, file.Path), item.OriginalSize, item.OriginalSha256);
            }
            journal.Files.Add(item);
        }
        UpdateDisk.SyncDirectory(backup);
        Checkpoint?.Invoke("backed-up");
        UpdateDisk.WriteAtomic(Path.Combine(_state, JournalName),
            JsonSerializer.SerializeToUtf8Bytes(journal, UpdateJsonContext.Default.UpdateJournal));
        Checkpoint?.Invoke("journaled");
        var content = Path.Combine(UpdateProtocol.GetStagingDirectory(_root, version), "content");
        foreach (var file in manifest.Files)
        {
            // Sao chép sang tệp tạm cùng volume, sau đó rename; staging được giữ
            // để kiểm tra lại và không bao giờ trở thành nguồn dữ liệu của app.
            ReplaceFrom(Path.Combine(content, file.Path), Path.Combine(_root, file.Path),
                OperatingSystem.IsLinux() ? (int)UpdatePackage.FileModeFor(file.Executable) : null);
            UpdatePackage.VerifyFile(Path.Combine(_root, file.Path), file.Size, file.Sha256);
            Checkpoint?.Invoke("replaced:" + file.Path);
        }
        return manifest;
    }

    public bool Rollback(string reason = "Đã phục hồi phiên bản trước vì lần cập nhật bị gián đoạn.")
    {
        if (!HasPending) return false;
        var journal = ReadJournal();
        var backup = Path.Combine(_state, "backups", journal.Id);
        UpdateDisk.Exists(backup, directory: true);
        // Kiểm đủ bản sao trước khi phục hồi bất kỳ tệp nào. Thiếu bản sao thì
        // dừng tại đây để kỹ thuật viên sửa; tuyệt đối không khởi chạy bộ trộn.
        foreach (var file in journal.Files.Where(file => file.HadOriginal))
            UpdatePackage.VerifyFile(Path.Combine(backup, file.Path), file.OriginalSize, file.OriginalSha256);
        foreach (var file in journal.Files)
        {
            var target = Path.Combine(_root, file.Path);
            if (file.HadOriginal)
            {
                ReplaceFrom(Path.Combine(backup, file.Path), target, file.OriginalMode);
                UpdatePackage.VerifyFile(target, file.OriginalSize, file.OriginalSha256);
            }
            else UpdateDisk.DeleteFile(target);
            Checkpoint?.Invoke("restored:" + file.Path);
        }
        WriteResult(reason);
        UpdateDisk.WriteAtomic(Path.Combine(_state, CurrentVersionName), Encoding.UTF8.GetBytes(journal.PreviousVersion));
        UpdateDisk.DeleteFile(Path.Combine(_state, JournalName));
        UpdateDisk.DeleteDirectory(backup);
        return true;
    }

    public void Commit()
    {
        var journal = ReadJournal();
        WriteResult("Đã cập nhật thành công lên phiên bản " + journal.Version + ".");
        // Ghi phiên bản sau khi bản mới kiểm tra dữ liệu/UI xong. Việc xóa nhật ký
        // là điểm commit: sau điểm này app mới có thể ra khỏi chế độ bảo trì.
        UpdateDisk.WriteAtomic(Path.Combine(_state, CurrentVersionName), Encoding.UTF8.GetBytes(journal.Version));
        Checkpoint?.Invoke("version-recorded");
        UpdateDisk.DeleteFile(Path.Combine(_state, JournalName));
        Checkpoint?.Invoke("committed");
        // Giữ một bản sao binary sau commit phục vụ kiểm tra. Dọn các bản sao
        // cũ chỉ sau commit; lỗi dọn dẹp không làm hỏng bản đã cài.
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Path.Combine(_state, "backups")))
                if (Path.GetFileName(folder) != journal.Id && Guid.TryParseExact(Path.GetFileName(folder), "N", out _))
                    UpdateDisk.DeleteDirectory(folder);
            foreach (var folder in Directory.EnumerateDirectories(Path.Combine(_state, "staged")))
            {
                var name = Path.GetFileName(folder);
                if (name == journal.Version) continue;
                try { _ = UpdateProtocol.ParseVersion(name); }
                catch (InvalidDataException) { continue; }
                UpdateDisk.DeleteDirectory(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal void WriteResult(string message)
    {
        UpdateDisk.EnsureDirectory(_state);
        UpdateDisk.WriteAtomic(Path.Combine(_state, UpdateProtocol.ResultFile), Encoding.UTF8.GetBytes(message));
    }

    private UpdateJournal ReadJournal()
    {
        var journal = JsonSerializer.Deserialize(UpdateDisk.ReadSmall(Path.Combine(_state, JournalName), 64 * 1024),
            UpdateJsonContext.Default.UpdateJournal) ?? throw new InvalidDataException("Nhật ký cập nhật hỏng.");
        if (!Guid.TryParseExact(journal.Id, "N", out _) || journal.Rid != _rid
            || journal.Files is not { Count: > 0 and <= UpdatePackage.MaxFiles })
            throw new InvalidDataException("Nhật ký cập nhật không hợp lệ.");
        _ = UpdateProtocol.ParseVersion(journal.Version);
        _ = UpdateProtocol.ParseVersion(journal.PreviousVersion);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in journal.Files)
        {
            if (file is null || !UpdatePackage.IsRuntimePath(file.Path, _rid) || !names.Add(file.Path)
                || (file.HadOriginal && (file.OriginalSize < 0 || file.OriginalSize > UpdatePackage.MaxExpandedBytes
                    || !UpdatePackage.IsHash(file.OriginalSha256)))
                || (file.OriginalMode is int mode && (mode < 0 || mode > 0xfff)))
                throw new InvalidDataException("Danh sách phục hồi không hợp lệ.");
        }
        return journal;
    }

    private static void ReplaceFrom(string source, string target, int? mode)
    {
        UpdateDisk.Exists(target, directory: false);
        var temp = Path.Combine(Path.GetDirectoryName(target)!, ".tantien-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CopyDurable(source, temp, mode);
            UpdateDisk.Replace(temp, target);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static void CopyDurable(string source, string target, int? mode)
    {
        if (!UpdateDisk.Exists(source, directory: false)) throw new FileNotFoundException("Thiếu tệp nguồn cập nhật.", source);
        using var input = File.OpenRead(source);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        if (OperatingSystem.IsLinux() && mode is int permissions)
            File.SetUnixFileMode(target, (UnixFileMode)permissions);
        output.Flush(flushToDisk: true);
    }
}
