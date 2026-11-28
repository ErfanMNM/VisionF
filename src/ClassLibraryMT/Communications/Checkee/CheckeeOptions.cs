namespace ClassLibraryMT.Communications.Checkee;

// Cấu hình kết nối API Checkee (đọc từ appsettings.json, mục "Checkee").
public sealed class CheckeeOptions
{
    public string BaseUrl { get; set; } = "https://beta-api-eco.checkee.vn"; // UAT; Production: https://api-eco.checkee.vn
    public string ClientId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string BatchCode { get; set; } = "";   // Mã lô/lệnh sản xuất đang chạy
    public string SerialType { get; set; } = "RANDOM"; // RANDOM | DECLARE | INCREASING
    public int DuplicateWindowSeconds { get; set; } = 30; // Bỏ qua mã quét lặp trong khoảng này
}
