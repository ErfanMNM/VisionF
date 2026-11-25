namespace TanTienQR.ViewModels;

/// <summary>Thông tin cần hiển thị khi chọn tài khoản trên thiết bị.</summary>
internal sealed record LoginAccountOption(string Username, string DisplayName)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName)
        || string.Equals(DisplayName.Trim(), Username, StringComparison.OrdinalIgnoreCase)
            ? Username
            : $"{DisplayName.Trim()} · {Username}";
}
