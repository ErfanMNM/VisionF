namespace TanTienQR.Services;

/// <summary>Chỉ nhận diện tín hiệu lỗi đọc; CodeContent được giữ nguyên để đối chiếu PO.</summary>
internal static class QrParser
{
    public const string NoReadToken = "NoRead";
    public const string FailToken = "FAIL";

    public static bool IsNoRead(string raw) =>
        string.Equals(raw?.Trim(), NoReadToken, StringComparison.OrdinalIgnoreCase)
        || string.Equals(raw?.Trim(), FailToken, StringComparison.OrdinalIgnoreCase);
}
