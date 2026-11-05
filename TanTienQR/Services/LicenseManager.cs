using ClassLibraryMT.DeviceStatus;
using ClassLibraryMT.Licensing;
using Microsoft.Extensions.Logging;

namespace TanTienQR.Services;

/// <summary>
/// Quản lý vòng đời license của TanTienQR: đọc, nhập (import) và hủy (revoke).
///
/// License hợp lệ là điều kiện để app chuyển sang trạng thái Ready — nếu không,
/// <see cref="AppStateManager"/> sẽ đặt app ở trạng thái Error và D16 luôn bằng 0.
/// </summary>
internal sealed class LicenseManager
{
    /// <summary>Tên sản phẩm mà license phải khớp.</summary>
    public const string ProductName = "TanTienQR";

    private readonly ILogger<LicenseManager> _logger;

    public LicenseManager(ILogger<LicenseManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Đường dẫn file license trong thư mục ứng dụng.</summary>
    public static string LicensePath => Path.Combine(AppContext.BaseDirectory, "license.lic");

    /// <summary>Kết quả kiểm tra license hiện tại.</summary>
    public LicenseCheck Current { get; private set; } =
        new(false, "Chua kiem tra license.", null);

    /// <summary>Mã máy (HWID) để người dùng gửi cho nhà cung cấp cấp license.</summary>
    public string Hwid => HardwareId.GetGrouped();

    /// <summary>Kiểm tra license trong thư mục ứng dụng và cập nhật trạng thái app.</summary>
    public void Load()
    {
        Current = LicenseValidator.Validate(LicensePath, ProductName);
        AppStateManager.SetLicenseValid(Current.Ok);

        if (Current.Ok)
        {
            _logger.LogInformation(
                "License hop le: khach hang={Customer}, het han={Expiry}, con {Days} ngay",
                Current.Payload!.Customer, Current.Payload.ExpiresUtc, Current.DaysLeft);
        }
        else
        {
            _logger.LogWarning("License khong hop le: {Message}", Current.Message);
        }
    }

    /// <summary>
    /// Nhập license từ file ngoài: copy vào thư mục ứng dụng rồi kiểm tra lại.
    /// Trả về true nếu file đã được nhập và hợp lệ.
    /// </summary>
    public bool Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            Current = new LicenseCheck(false, "Khong tim thay file license da chon.", null);
            AppStateManager.SetLicenseValid(false);
            return false;
        }

        try
        {
            // Copy sang file tạm trước rồi mới thay thế, để không để lại
            // file license hỏng nếu copy thất bại giữa chừng.
            var temp = LicensePath + ".tmp";
            File.Copy(sourcePath, temp, overwrite: true);
            File.Move(temp, LicensePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong the nhap file license tu {Path}", sourcePath);
            Current = new LicenseCheck(false, "Khong the ghi file license: " + ex.Message, null);
            AppStateManager.SetLicenseValid(false);
            return false;
        }

        Load();
        return Current.Ok;
    }

    /// <summary>
    /// Hủy license: xoá file license khỏi thư mục ứng dụng.
    /// </summary>
    public bool Revoke()
    {
        try
        {
            if (File.Exists(LicensePath))
                File.Delete(LicensePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong the xoa file license.");
            Current = new LicenseCheck(false, "Khong the xoa file license: " + ex.Message, null);
            AppStateManager.SetLicenseValid(false);
            return false;
        }

        _logger.LogInformation("Da huy license.");
        Load();
        return true;
    }
}
