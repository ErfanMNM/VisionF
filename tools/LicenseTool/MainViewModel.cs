using System.Security.Cryptography;
using System.Text;
using ClassLibraryMT.Licensing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LicenseTool;

public sealed partial class MainViewModel : ObservableObject
{
    private string _privateKeyText = "";

    [ObservableProperty] private string hwidRaw = "";
    [ObservableProperty] private string customer = "";
    [ObservableProperty] private int daysValid = 365;
    [ObservableProperty] private string licenseOutPath = "license.lic";
    [ObservableProperty] private string verifyPath = "";
    [ObservableProperty] private string statusMessage = "Sẵn sàng. Hãy nạp private key để bắt đầu cấp phép.";
    [ObservableProperty] private bool statusOk = true;
    [ObservableProperty] private bool busy;

    public string ThisMachineHwid => HardwareId.GetGrouped();

    public string KeyFingerprint =>
        string.IsNullOrEmpty(_privateKeyText)
            ? "Chưa nạp private key"
            : Fingerprint(_privateKeyText);

    private static string Fingerprint(string base64)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(base64.Trim()), out _);
            var pub = rsa.ExportSubjectPublicKeyInfo();
            return "SHA256:" + Convert.ToHexString(SHA256.HashData(pub))[..16];
        }
        catch
        {
            return "Private key lỗi định dạng";
        }
    }

    [RelayCommand]
    private async Task PickPrivateKey()
    {
        var path = await FileDialogs.OpenFileAsync("Chọn file private.key", "Khóa riêng (*.key);*.key|Tất cả (*.*);*.*");
        if (path is null) return;
        await LoadPrivateKeyFromFile(path);
    }

    private async Task LoadPrivateKeyFromFile(string path)
    {
        try
        {
            _privateKeyText = (await File.ReadAllTextAsync(path, Encoding.UTF8)).Trim();
            OnPropertyChanged(nameof(KeyFingerprint));
            SetStatus($"Đã nạp private key: {Path.GetFileName(path)}", true);
        }
        catch (Exception ex)
        {
            SetStatus("Không đọc được private key: " + ex.Message, false);
        }
    }

    [RelayCommand]
    private async Task GenerateKeys()
    {
        var dir = await FileDialogs.PickFolderAsync("Chọn thư mục lưu cặp key mới");
        if (dir is null) return;
        try
        {
            Directory.CreateDirectory(dir);
            using (var rsa = RSA.Create(2048))
            {
                await File.WriteAllTextAsync(Path.Combine(dir, "public.key"),
                    Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
                await File.WriteAllTextAsync(Path.Combine(dir, "private.key"),
                    Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()));
            }
            var pub = (await File.ReadAllTextAsync(Path.Combine(dir, "public.key"))).Trim();
            await ClipboardHelper.SetTextAsync(pub);
            await LoadPrivateKeyFromFile(Path.Combine(dir, "private.key"));
            SetStatus($"Đã tạo cặp key tại {dir}. Public key đã copy vào clipboard — dán vào code rồi build lại app.", true);
        }
        catch (Exception ex)
        {
            SetStatus("Tạo key thất bại: " + ex.Message, false);
        }
    }

    [RelayCommand]
    private async Task PasteHwid()
    {
        var text = await ClipboardHelper.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            HwidRaw = text.Trim();
            SetStatus("Đã dán HWID từ clipboard.", true);
        }
        else
        {
            SetStatus("Clipboard trống — hãy copy HWID của máy khách trước.", false);
        }
    }

    [RelayCommand]
    private async Task CopyHwid()
    {
        var h = HardwareId.Normalize(HwidRaw);
        await ClipboardHelper.SetTextAsync(string.IsNullOrEmpty(h) ? ThisMachineHwid.Replace("-", "") : h);
        SetStatus("Đã copy HWID.", true);
    }

    [RelayCommand]
    private async Task IssueLicense()
    {
        if (string.IsNullOrWhiteSpace(_privateKeyText))
        {
            SetStatus("Chưa nạp private key. Hãy bấm “Chọn private.key…” trước.", false);
            return;
        }
        if (HardwareId.Normalize(HwidRaw).Length != 32)
        {
            SetStatus("HWID phải đủ 32 ký tự hex (copy từ máy khách gửi sang).", false);
            return;
        }
        if (DaysValid <= 0)
        {
            SetStatus("Số ngày hiệu lực phải lớn hơn 0.", false);
            return;
        }
        Busy = true;
        try
        {
            var outPath = string.IsNullOrWhiteSpace(LicenseOutPath) ? "license.lic" : LicenseOutPath;
            var lic = LicenseSigner.Create(_privateKeyText, HwidRaw, Customer.Trim(), DaysValid);
            await File.WriteAllTextAsync(outPath, lic, new UTF8Encoding(false));
            SetStatus($"Đã cấp license ({new FileInfo(outPath).Length} bytes): {outPath}", true);
        }
        catch (Exception ex)
        {
            SetStatus("Cấp license thất bại: " + ex.Message, false);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task PickVerifyFile()
    {
        var path = await FileDialogs.OpenFileAsync("Chọn file license.lic", "Giấy phép (*.lic);*.lic|Tất cả (*.*);*.*");
        if (path is null) return;
        VerifyPath = path;
        Verify();
    }

    [RelayCommand]
    private void Verify()
    {
        if (string.IsNullOrWhiteSpace(VerifyPath) || !File.Exists(VerifyPath))
        {
            SetStatus("Hãy chọn file license.lic cần kiểm tra.", false);
            return;
        }
        var c = LicenseValidator.Validate(VerifyPath);
        if (c.Ok && c.Payload is not null)
            SetStatus($"HỢP LỆ — Khách hàng: {c.Payload.Customer} · Hết hạn: {c.Payload.ExpiresUtc:dd/MM/yyyy} (còn {c.DaysLeft} ngày) · Khóa máy: {c.Payload.Hwid}", true);
        else
            SetStatus("KHÔNG HỢP LỆ: " + c.Message, false);
    }

    [RelayCommand]
    private async Task CopyStatus()
    {
        await ClipboardHelper.SetTextAsync(StatusMessage);
    }

    private void SetStatus(string msg, bool ok)
    {
        StatusMessage = msg;
        StatusOk = ok;
    }
}
