using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassLibraryMT.Auth;
using ClassLibraryMT.Licensing;
using ClassLibraryMT.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;

namespace TanTienQR.Services;

internal sealed partial class UpdateClient : ObservableObject
{
    private readonly HttpClient _http;
    private readonly ISessionContext _session;
    private readonly string _url;
    private readonly string _channel;
    private byte[]? _envelope;
    private UpdateManifest? _manifest;
    private bool _downloaded;

    public UpdateClient(IHttpClientFactory clients, IConfiguration configuration, ISessionContext session)
    {
        _http = clients.CreateClient(nameof(UpdateClient));
        _session = session;
        _url = (configuration["Updates:BaseUrl"] ?? configuration["LicensePortal:BaseUrl"] ??
            "https://tantien-license-portal.okeynhat.workers.dev").TrimEnd('/');
        _channel = configuration["Updates:Channel"] ?? "stable";
        LastResult = UpdateProtocol.ReadLastResult(AppContext.BaseDirectory) ?? "";
        Status = "Chưa kiểm tra bản cập nhật.";
    }

    public string CurrentVersion => typeof(UpdateClient).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    public string LastResult { get; }
    public bool HasLastResult => !string.IsNullOrWhiteSpace(LastResult);

    [ObservableProperty] public partial string Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial string Notes { get; set; } = "";

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableVersionText))]
    public partial string AvailableVersion { get; set; } = "";

    /// <summary>Hiển thị "—" khi chưa có bản mới, tránh nhãn "Bản mới" bị treo.</summary>
    public string AvailableVersionText => string.IsNullOrWhiteSpace(AvailableVersion) ? "—" : AvailableVersion;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool CanDownload { get; set; }
    [ObservableProperty] public partial bool CanInstall { get; set; }
    public Func<string, Task>? InstallRequested { get; set; }

    private UpdateRequest Request(string? version = null)
    {
        var check = LicenseValidator.Validate();
        if (!check.Ok)
            throw new InvalidOperationException("Cần license hợp lệ trên máy này để cập nhật.");
        var license = JsonSerializer.Deserialize(File.ReadAllBytes(LicenseManager.LicensePath),
            UpdateClientJsonContext.Default.LicenseFile)
            ?? throw new InvalidDataException("Không đọc được license.");
        return new UpdateRequest(license, UpdateProtocol.CurrentRid
            ?? throw new PlatformNotSupportedException("Chỉ hỗ trợ Windows và Linux x64."), _channel, version);
    }

    private async Task<HttpResponseMessage> SendAsync(string route, UpdateRequest body, CancellationToken ct)
    {
        if (!Uri.TryCreate(_url + "/api/updates/v1/" + route, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Địa chỉ cập nhật phải dùng HTTPS.");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(body, UpdateClientJsonContext.Default.UpdateRequest)
        };
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException(status switch
            {
                HttpStatusCode.Forbidden => "License chưa được ghi nhận, đã hết hạn hoặc bị thu hồi. Hãy liên hệ kỹ thuật.",
                HttpStatusCode.Conflict => "Bản cập nhật đã được thay hoặc rút. Hãy kiểm tra lại.",
                _ => $"Dịch vụ cập nhật chưa sẵn sàng (HTTP {(int)status})."
            });
        }
        return response;
    }

    [RelayCommand]
    public async Task CheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        CanDownload = CanInstall = _downloaded = false;
        try
        {
            if (string.IsNullOrWhiteSpace(UpdateProtocol.PublicKey))
                throw new InvalidOperationException("Bản build này chưa có khóa xác minh cập nhật. Cần bộ khởi tạo do kỹ thuật phát hành.");
            Status = "Đang kiểm tra bản cập nhật…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await SendAsync("check", Request(), timeout.Token);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                Status = "Chưa có bản phát hành trên kênh này.";
                return;
            }
            await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var manifest = UpdatePackage.ReadManifest(bytes, UpdateProtocol.PublicKey, UpdateProtocol.CurrentRid!);
            if (!Version.TryParse(CurrentVersion, out var current) ||
                !Version.TryParse(manifest.Version, out var next))
                throw new InvalidDataException("Phiên bản build không đúng định dạng X.Y.Z.");
            if (next <= current)
            {
                AvailableVersion = Notes = "";
                Status = "Bạn đang dùng phiên bản mới nhất.";
                return;
            }
            _manifest = manifest;
            _envelope = bytes;
            AvailableVersion = manifest.Version;
            Notes = manifest.Notes;
            CanDownload = true;
            Status = "Có bản mới. Quản trị viên có thể tải và chọn lúc cài.";
        }
        catch (Exception ex) { Status = ex is OperationCanceledException ? "Kiểm tra quá hạn. Có thể thử lại sau." : ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsBusy || _manifest is null || _envelope is null) return;
        IsBusy = true;
        CanInstall = false;
        try
        {
            EnsureAdmin();
            Status = "Đang tải và xác minh gói cập nhật…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using var response = await SendAsync("download", Request(_manifest.Version), timeout.Token);
            if (response.Content.Headers.ContentLength is long length && length != _manifest.Size)
                throw new InvalidDataException("Kích thước gói không khớp manifest.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await UpdatePackage.StageAsync(AppContext.BaseDirectory, _envelope, stream,
                UpdateProtocol.PublicKey, UpdateProtocol.CurrentRid!, timeout.Token);
            _downloaded = CanInstall = true;
            Status = "Gói đã được xác minh. Chỉ cài khi dây chuyền đã dừng.";
        }
        catch (Exception ex) { Status = ex is OperationCanceledException ? "Tải quá hạn. Có thể tải lại." : ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsBusy || !_downloaded || _manifest is null || InstallRequested is null) return;
        IsBusy = true;
        try
        {
            EnsureAdmin();
            await InstallRequested(_manifest.Version);
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    internal async Task VerifyInstallAuthorizationAsync(CancellationToken ct)
    {
        EnsureAdmin();
        using var response = await SendAsync("check", Request(), ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
            throw new InvalidOperationException("Bản cập nhật đã bị rút. Hãy kiểm tra lại.");
        await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var current = UpdatePackage.ReadManifest(bytes, UpdateProtocol.PublicKey, UpdateProtocol.CurrentRid!);
        if (current.Version != _manifest?.Version || current.Sha256 != _manifest.Sha256)
            throw new InvalidOperationException("Bản cập nhật đã thay đổi. Hãy kiểm tra và tải lại.");
    }

    internal void EnsureAdmin()
    {
        if (!_session.IsAuthenticated ||
            _session.CurrentRole is not (UserRole.Administrator or UserRole.Developer))
            throw new InvalidOperationException("Chỉ quản trị viên đã đăng nhập được cài bản cập nhật.");
    }
}

internal sealed record UpdateRequest(
    [property: JsonPropertyName("license")] LicenseFile License,
    [property: JsonPropertyName("rid")] string Rid,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("version"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version);

[JsonSerializable(typeof(UpdateRequest))]
[JsonSerializable(typeof(LicenseFile))]
internal sealed partial class UpdateClientJsonContext : JsonSerializerContext;
