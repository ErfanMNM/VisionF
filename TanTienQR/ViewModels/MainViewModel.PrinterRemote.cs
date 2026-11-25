using TanTienQR.Models;

namespace TanTienQR.ViewModels;

internal partial class MainViewModel
{
    private bool _printerRemoteOverlayBlocked;

    /// <summary>Ẩn surface HWND trước popup, modal hoặc khi cửa sổ bị thu nhỏ.</summary>
    public void SetPrinterRemoteOverlayBlocked(bool blocked)
    {
        _printerRemoteOverlayBlocked = blocked;
        RefreshPrinterRemotePresentation();
    }

    private void RefreshPrinterRemotePresentation()
    {
        Print.Remote.SetPresentationState(
            CurrentSection == AppSection.Print && !_disposed,
            _session.IsAuthenticated && Print.CanConnect && !_disposed,
            IsLoginOverlayVisible || _printerRemoteOverlayBlocked);
    }

    partial void OnCurrentSectionChanged(AppSection value) => RefreshPrinterRemotePresentation();

    partial void OnIsLoginOverlayVisibleChanged(bool value) => RefreshPrinterRemotePresentation();
}
