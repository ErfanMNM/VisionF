namespace TanTienQR.ViewModels;

internal partial class MainViewModel
{
    private SettingsV2ViewModel? _settingsV2;
    public SettingsV2ViewModel SettingsV2 => _settingsV2 ??= new(_configEditor, this);
}
