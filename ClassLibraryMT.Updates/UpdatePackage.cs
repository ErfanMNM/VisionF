using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClassLibraryMT.Updates;

public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("rid")] public string Rid { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("files")] public List<UpdateFile> Files { get; set; } = [];
}

public sealed class UpdateFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("executable")] public bool Executable { get; set; }
}

internal sealed class SignedEnvelope
{
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    [JsonPropertyName("sig")] public string Signature { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(SignedEnvelope))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(UpdateJournal))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;

public static partial class UpdatePackage
{
    public const long MaxPackageBytes = 256L * 1024 * 1024;
    public const long MaxExpandedBytes = 512L * 1024 * 1024;
    public const int MaxEnvelopeBytes = 64 * 1024;
    public const int MaxFiles = 128;

    public static UpdateManifest ReadManifest(ReadOnlyMemory<byte> envelope, string publicKey, string expectedRid)
    {
        if (envelope.Length == 0 || envelope.Length > MaxEnvelopeBytes)
            throw new InvalidDataException("Manifest cập nhật quá lớn hoặc rỗng.");
        if (string.IsNullOrWhiteSpace(publicKey))
            throw new InvalidDataException("Bản ứng dụng chưa được cấu hình khóa phát hành.");
        if (expectedRid is not ("win-x64" or "linux-x64"))
            throw new InvalidDataException("Nền tảng cập nhật không được hỗ trợ.");
        var signed = JsonSerializer.Deserialize(envelope.Span, UpdateJsonContext.Default.SignedEnvelope)
            ?? throw new InvalidDataException("Manifest cập nhật không hợp lệ.");
        var data = Convert.FromBase64String(signed.Data);
        var signature = Convert.FromBase64String(signed.Signature);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out var consumed);
        if (consumed != Convert.FromBase64String(publicKey).Length || rsa.KeySize < 2048
            || !rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Chữ ký bản cập nhật không hợp lệ.");
        var manifest = JsonSerializer.Deserialize(data, UpdateJsonContext.Default.UpdateManifest)
            ?? throw new InvalidDataException("Nội dung manifest không hợp lệ.");
        _ = UpdateProtocol.ParseVersion(manifest.Version);
        if (manifest.Rid != expectedRid || manifest.Size <= 0 || manifest.Size > MaxPackageBytes
            || !IsHash(manifest.Sha256) || manifest.Files is not { Count: > 0 and <= MaxFiles }
            || manifest.Notes is null || manifest.Notes.Length > 16000)
            throw new InvalidDataException("Thông tin bản cập nhật không hợp lệ.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || !IsRuntimePath(file.Path, manifest.Rid) || !names.Add(file.Path)
                || !IsHash(file.Sha256) || file.Size < 0 || file.Size > MaxExpandedBytes)
                throw new InvalidDataException("Danh sách tệp cập nhật không hợp lệ.");
            expanded = checked(expanded + file.Size);
            if (expanded > MaxExpandedBytes)
                throw new InvalidDataException("Gói cập nhật giải nén quá lớn.");
        }
        var app = manifest.Files.SingleOrDefault(file => file.Path == UpdateProtocol.AppFileName(expectedRid));
        if (app is null || app.Size == 0 || (expectedRid == "linux-x64" && !app.Executable))
            throw new InvalidDataException("Gói cập nhật thiếu chương trình chính.");
        return manifest;
    }

    public static async Task<UpdateManifest> StageAsync(string installationRoot, ReadOnlyMemory<byte> envelope,
        Stream zip, string publicKey, string expectedRid, CancellationToken ct = default)
    {
        var manifest = ReadManifest(envelope, publicKey, expectedRid);
        var staged = UpdateProtocol.GetStagingDirectory(installationRoot, manifest.Version);
        UpdateDisk.EnsureDirectory(Path.GetDirectoryName(staged)!);
        var partial = staged + "-" + Guid.NewGuid().ToString("N") + ".partial";
        UpdateDisk.EnsureDirectory(partial);
        try
        {
            var zipPath = Path.Combine(partial, "package.zip");
            await using (var target = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await CopyCheckedAsync(zip, target, manifest.Size, manifest.Sha256, ct).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }
            var content = Path.Combine(partial, "content");
            UpdateDisk.EnsureDirectory(content);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                if (archive.Entries.Count != manifest.Files.Count)
                    throw new InvalidDataException("ZIP chứa tệp thừa hoặc thiếu.");
                var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    var type = (entry.ExternalAttributes >> 16) & 0xf000;
                    if (!expected.TryGetValue(entry.FullName, out var file) || !seen.Add(entry.FullName)
                        || type is not (0 or 0x8000) || (entry.ExternalAttributes & 0x410) != 0
                        || entry.Length != file.Size)
                        throw new InvalidDataException("ZIP chứa đường dẫn, liên kết hoặc kích thước không hợp lệ.");
                    var path = Path.Combine(content, file.Path);
                    await using (var source = entry.Open())
                    await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await CopyCheckedAsync(source, target, file.Size, file.Sha256, ct).ConfigureAwait(false);
                        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, FileModeFor(file.Executable));
                        target.Flush(flushToDisk: true);
                    }
                }
            }
            UpdateDisk.DeleteFile(zipPath);
            UpdateDisk.WriteAtomic(Path.Combine(partial, "manifest.json"), envelope.Span);
            UpdateDisk.SyncDirectory(content);
            UpdateDisk.SyncDirectory(partial);
            UpdateDisk.DeleteDirectory(staged);
            UpdateDisk.Replace(partial, staged, overwrite: false);
            return manifest;
        }
        finally
        {
            UpdateDisk.DeleteDirectory(partial);
        }
    }

    internal static UpdateManifest VerifyStaged(string installationRoot, string version, string publicKey, string rid)
    {
        var stage = UpdateProtocol.GetStagingDirectory(installationRoot, version);
        UpdateDisk.Exists(stage, directory: true);
        var envelope = UpdateDisk.ReadSmall(Path.Combine(stage, "manifest.json"), MaxEnvelopeBytes);
        var manifest = ReadManifest(envelope, publicKey, rid);
        if (manifest.Version != version) throw new InvalidDataException("Phiên bản staging không khớp.");
        var content = Path.Combine(stage, "content");
        UpdateDisk.Exists(content, directory: true);
        if (Directory.EnumerateFileSystemEntries(content).Count() != manifest.Files.Count)
            throw new InvalidDataException("Staging chứa tệp thừa hoặc thiếu.");
        foreach (var file in manifest.Files)
            VerifyFile(Path.Combine(content, file.Path), file.Size, file.Sha256);
        return manifest;
    }

    internal static bool IsRuntimePath(string path, string rid)
    {
        if (path is null || path.Length > 120 || !SimpleName().IsMatch(path)
            || path.StartsWith("TanTienLauncher", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = path.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || ReservedDevice().IsMatch(stem))
            return false;
        if (path == UpdateProtocol.AppFileName(rid)) return true;
        return rid == "win-x64" ? path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            : LinuxLibrary().IsMatch(path);
    }

    internal static void VerifyFile(string path, long size, string hash)
    {
        if (!UpdateDisk.Exists(path, directory: false))
            throw new FileNotFoundException("Thiếu tệp cập nhật.", path);
        using var stream = File.OpenRead(path);
        if (stream.Length != size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp cập nhật không khớp SHA-256.");
    }

    internal static bool IsHash(string hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    internal static UnixFileMode FileModeFor(bool executable) => UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.OtherRead
        | (executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0);

    internal static async Task CopyCheckedAsync(Stream source, Stream target, long size, string hash, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long count = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            count = checked(count + read);
            if (count > size) throw new InvalidDataException("Tệp tải về vượt kích thước đã ký.");
            sha.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        if (count != size || !Convert.ToHexString(sha.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp tải về không khớp SHA-256 hoặc bị gián đoạn.");
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleName();
    [GeneratedRegex("^(COM|LPT)[0-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDevice();
    [GeneratedRegex(@"\.so(\.[0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxLibrary();
}
