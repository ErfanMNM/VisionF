using System.Net;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Licensing;

const string hwid = "0123456789ABCDEF0123456789ABCDEF";
var passed = 0;
void Check(string label, bool value)
{
    if (!value) throw new InvalidOperationException(label);
    Console.WriteLine("[OK] " + label);
    passed++;
}

var handler = new PortalHandler();
using var http = new HttpClient(handler);
var client = new LicensePortalClient(http);
var result = await client.SubmitAsync("https://license.example.test/", "0123-4567-89ab-cdef-0123-4567-89ab-cdef", "Công ty thử", "test@example.test", "Cấp mới");
using var sent = JsonDocument.Parse(handler.Body!);
Check("Gửi đúng endpoint công khai bằng POST", handler.Method == HttpMethod.Post
    && handler.Uri!.AbsoluteUri == "https://license.example.test/api/license-requests");
Check("Gửi HWID chuẩn hoá và đúng sản phẩm", sent.RootElement.GetProperty("hwid").GetString() == hwid
    && sent.RootElement.GetProperty("product").GetString() == "TanTienQR");
Check("Giữ thông tin tiếng Việt", sent.RootElement.GetProperty("customer").GetString() == "Công ty thử");
Check("Không mang cookie hoặc tài khoản quản trị", !handler.HadCredentials);
Check("Đọc đúng yêu cầu đang chờ", result.Ok && result.RequestId == "test-request" && result.Status == "pending");

foreach (var badUrl in new[] { "", "file:///C:/test", "http://public.example.test", "https://user:password@example.test", "https://example.test/?token=secret" })
{
    try { LicensePortalClient.ResolveEndpoint(badUrl); throw new Exception("URL sai chưa bị chặn"); }
    catch (ArgumentException) { passed++; }
}
Check("Cho phép localhost để kiểm thử", LicensePortalClient.ResolveEndpoint("http://127.0.0.1:8787").Port == 8787);
var count = handler.Calls;
try { await client.SubmitAsync("https://license.example.test", "bad-hwid"); throw new Exception("HWID sai chưa bị chặn"); }
catch (ArgumentException) { Check("Không gọi mạng khi HWID sai", handler.Calls == count); }

handler.Status = HttpStatusCode.BadRequest;
handler.Response = "{\"error\":\"Sản phẩm đã tắt.\"}";
try { await client.SubmitAsync("https://license.example.test", hwid); throw new Exception("Lỗi server chưa được báo"); }
catch (InvalidOperationException ex) { Check("Báo lỗi nghiệp vụ từ portal", ex.Message == "Sản phẩm đã tắt."); }
handler.Status = HttpStatusCode.OK;
handler.Response = "<html>wrong host</html>";
try { await client.SubmitAsync("https://license.example.test", hwid); throw new Exception("HTML không được coi là thành công"); }
catch (InvalidOperationException ex) { Check("URL sai không báo gửi thành công", ex.Message.Contains("không hợp lệ")); }
handler.Response = "{\"ok\":true}";
try { await client.SubmitAsync("https://license.example.test", hwid); throw new Exception("Response thiếu id chưa bị chặn"); }
catch (InvalidOperationException ex) { Check("Chỉ báo thành công khi có xác nhận đã lưu", ex.Message.Contains("chưa xác nhận")); }
handler.ThrowTimeout = true;
try { await client.SubmitAsync("https://license.example.test", hwid); throw new Exception("Timeout chưa được báo"); }
catch (InvalidOperationException ex) { Check("Timeout có thông báo thử lại", ex.Message.Contains("thời gian chờ")); }

if (args.Length == 2 && args[0] == "--live")
{
    using var liveHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var liveClient = new LicensePortalClient(liveHttp);
    var first = await liveClient.SubmitAsync(args[1], hwid, "Kiểm thử từ TanTienQR", "test@example.test", "Yêu cầu từ .NET client");
    var second = await liveClient.SubmitAsync(args[1], hwid, "Kiểm thử từ TanTienQR");
    Check("Client .NET gửi yêu cầu thật lên Worker/D1 local", first.Ok && first.RequestId.Length > 0);
    Check("Gửi lại cùng HWID trả cùng yêu cầu đang chờ", first.RequestId == second.RequestId);
}
Console.WriteLine($"Kết quả: {passed} kiểm tra qua.");

sealed class PortalHandler : HttpMessageHandler
{
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string Response = "{\"ok\":true,\"requestId\":\"test-request\",\"status\":\"pending\"}";
    public string? Body;
    public Uri? Uri;
    public HttpMethod? Method;
    public bool HadCredentials;
    public bool ThrowTimeout;
    public int Calls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        if (ThrowTimeout) throw new TaskCanceledException();
        Uri = request.RequestUri;
        Method = request.Method;
        Body = await request.Content!.ReadAsStringAsync(ct);
        HadCredentials = request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
        return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
    }
}
