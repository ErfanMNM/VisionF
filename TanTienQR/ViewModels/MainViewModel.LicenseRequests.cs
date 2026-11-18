using ClassLibraryMT.Licensing;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

internal partial class MainViewModel
{
    private readonly LicensePortalClient _licensePortal;

    [ObservableProperty]
    public partial string LicensePortalUrl { get; set; } = "";

    [ObservableProperty]
    public partial string LicenseRequestCustomer { get; set; } = "";

    [ObservableProperty]
    public partial string LicenseRequestContact { get; set; } = "";

    [ObservableProperty]
    public partial string LicenseRequestNote { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLicenseRequestMessage))]
    public partial string LicenseRequestMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLicenseRequestError { get; set; }

    public bool HasLicenseRequestMessage => !string.IsNullOrEmpty(LicenseRequestMessage);

    /// <summary>Người dùng chủ động gửi HWID của máy này, kể cả khi chưa có license.</summary>
    [RelayCommand]
    private async Task RequestLicenseAsync()
    {
        LicenseRequestMessage = "Đang gửi yêu cầu cấp license...";
        IsLicenseRequestError = false;
        try
        {
            var result = await _licensePortal.SubmitAsync(
                LicensePortalUrl, _license.Hwid, LicenseRequestCustomer,
                LicenseRequestContact, LicenseRequestNote, _cts.Token);
            LicenseRequestMessage = $"Đã gửi yêu cầu {result.RequestId}. Đang chờ nhà cung cấp cấp license.";
            _actionLog.Log("License.Request", "License",
                $"Gửi yêu cầu cấp license: {result.RequestId}", UserActionResults.Success);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Ứng dụng đang đóng.
        }
        catch (Exception ex)
        {
            IsLicenseRequestError = true;
            LicenseRequestMessage = ex.Message;
            _actionLog.Log("License.Request", "License", "Không gửi được yêu cầu cấp license", UserActionResults.Failed);
        }
    }
}
