using System.Security.Cryptography;
using System.Text;
using ClassLibraryMT.Licensing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LicenseTool;

public sealed partial class MainViewModel : ObservableObject
{
    // Thu muc mac dinh luu private key (KHONG commit git).
    private const string DefaultKeyDir = "keys";
    private const string PrivateKeyFileName = "private.key";

    private string _privateKeyText = "";

    [ObservableProperty] private string hwidRaw = "";
    [ObservableProperty] private string customer = "";
    [ObservableProperty] private string product = "DemoQR";
    [ObservableProperty] private int daysValid = 365;
    [ObservableProperty] private string licenseOutPath = "license.lic";
    [ObservableProperty] private string verifyPath = "";
    [ObservableProperty] private string statusMessage = "Sẵn sàng.";
    [ObservableProperty] private bool statusOk = true;
    [ObservableProperty] private bool busy;

    public string ThisMachineHwid => HardwareId.GetGrouped();

    // Key id cua private key dang dung (tu tinh ra tu public key).
    public string KeyId => string.IsNullOrEmpty(_privateKeyText)
        ? "—"
        : ComputeKeyId(_privateKeyText);

    public string KeyFingerprint =>
        string.IsNullOrEmpty(_privateKeyText)
            ? "Chưa nạp private key"
            : Fingerprint(_privateKeyText);

    // Private key co khop voi public key dang nhung trong app khong?
    public bool KeyMatchesApp => string.IsNullOrEmpty(_privateKeyText)
        ? false
        : ComputeKeyId(_privateKeyText) == LicenseKeys.CurrentKeyId;

    public MainViewModel()
    {
        // Tu dong nap private key neu co sanh trong thu muc "keys".
        TryLoadDefaultKey();
    }

    private void TryLoadDefaultKey()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, DefaultKeyDir, PrivateKeyFileName);
            if (!File.Exists(path)) return;
            _privateKeyText = File.ReadAllText(path, Encoding.UTF8).Trim();
            OnPropertyChanged(nameof(KeyFingerprint));
            OnPropertyChanged(nameof(KeyId));
            OnPropertyChanged(nameof(KeyMatchesApp));
            SetStatus($"Đã tự động nạp private key ({KeyId}).", true);
        }
        catch (Exception ex)
        {
            SetStatus("Không tự nạp được private key: " + ex.Message, false);
        }
    }

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

    private static string ComputeKeyId(string base64)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(base64.Trim()), out _);
            var pub = rsa.ExportSubjectPublicKeyInfo();
            return LicenseKeys.ComputeKeyId(Convert.ToBase64String(pub));
        }
        catch
        {
            return "LỖI";
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
            OnPropertyChanged(nameof(KeyId));
            OnPropertyChanged(nameof(KeyMatchesApp));
            var warn = KeyMatchesApp
                ? ""
                : $" LƯU Ý: key này ({KeyId}) KHÔNG khớp key đang dùng trong app ({LicenseKeys.CurrentKeyId}) — license cấp sẽ không được app chấp nhận.";
            SetStatus($"Đã nạp private key: {Path.GetFileName(path)}{warn}", KeyMatchesApp);
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
                var pub = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
                var priv = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
                var utf8NoBom = new UTF8Encoding(false);
                await File.WriteAllTextAsync(Path.Combine(dir, "public.key"), pub, utf8NoBom);
                await File.WriteAllTextAsync(Path.Combine(dir, PrivateKeyFileName), priv, utf8NoBom);
                var kid = LicenseKeys.ComputeKeyId(pub);

                await ClipboardHelper.SetTextAsync(pub);
                await LoadPrivateKeyFromFile(Path.Combine(dir, PrivateKeyFileName));

                SetStatus(
                    $"Đã tạo cặp key mới (kid={kid}) tại {dir}.\n" +
                    $"Public key đã copy vào clipboard — dán vào LicenseKeys.CurrentPublicKey trong " +
                    $"ClassLibraryMT/Licensing/LicenseKeys.cs rồi build lại app.\n" +
                    $"License đã cấp bằng key cũ vẫn chạy nếu bạn giữ cả hai key trong LicenseKeys.All.",
                    true);
            }
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

    // Dung HWID cua chinh may nay (de cap license de test nhanh).
    [RelayCommand]
    private void UseThisMachine()
    {
        HwidRaw = ThisMachineHwid;
        SetStatus("Đã điền HWID của máy này.", true);
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
            var productName = string.IsNullOrWhiteSpace(Product) ? "DemoQR" : Product.Trim();
            var lic = LicenseSigner.Create(_privateKeyText, HwidRaw, Customer.Trim(), DaysValid, productName);
            await File.WriteAllTextAsync(outPath, lic, new UTF8Encoding(false));
            SetStatus($"Đã cấp license (product={productName}, kid={KeyId}, {new FileInfo(outPath).Length} bytes): {outPath}", true);
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
        var productName = string.IsNullOrWhiteSpace(Product) ? "DemoQR" : Product.Trim();
        var c = LicenseValidator.Validate(VerifyPath, productName);
        if (c.Ok && c.Payload is not null)
            SetStatus(
                $"HỢP LỆ — Sản phẩm: {c.Payload.Product} · Khách hàng: {c.Payload.Customer} · " +
                $"Hết hạn: {c.Payload.ExpiresUtc:dd/MM/yyyy} " +
                $"(còn {c.DaysLeft} ngày) · Key: {c.Payload.KeyId} · Máy: {c.Payload.Hwid}",
                true);
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
