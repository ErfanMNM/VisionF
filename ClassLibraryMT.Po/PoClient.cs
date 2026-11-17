using System.Net.Http.Json;
using System.Text.Json;

namespace ClassLibraryMT.Po;

/// <summary>
/// Client gọi API của server quản lý Production Order.
///
/// <para>
/// Dùng <see cref="PoJsonContext"/> (source-generated) nên tương thích
/// Native AOT — không dùng reflection của <c>JsonSerializer</c>.
/// </para>
/// </summary>
public sealed class PoClient : IPoClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>
    /// Khởi tạo client với <see cref="HttpClient"/> bên ngoài (dùng chung).
    /// </summary>
    public PoClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = false;
    }

    /// <summary>
    /// Khởi tạo client tự tạo <see cref="HttpClient"/>.
    /// </summary>
    public PoClient(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("BaseUrl không được để trống.", nameof(baseUrl));

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _ownsHttp = true;
    }

    /// <summary>
    /// Lấy danh sách PO còn hiệu lực trên server.
    /// </summary>
    /// <param name="includeCompleted">
    /// True nếu muốn lấy cả PO đã hoàn tất / đã đóng.
    ///
    /// <para>
    /// Mô hình local-first cần lấy cả PO cũ để danh sách trên máy SCADA khớp
    /// với server (PO đã hoàn tất ở server vẫn phải hiện trong bảng quản lý
    /// local).
    /// </para>
    /// </param>
    /// <param name="ct">Token hủy.</param>
    public async Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(
        bool includeCompleted = false,
        CancellationToken ct = default)
    {
        var path = includeCompleted ? "api/po?includeCompleted=true" : "api/po";

        var orders = await GetJsonAsync(path, PoJsonContext.Default.ListProductionOrder, ct)
            .ConfigureAwait(false);

        return orders ?? [];
    }

    /// <summary>
    /// GET + đọc JSON; mọi lỗi mạng/HTTP/JSON được bọc thành <see cref="PoServerException"/>.
    /// </summary>
    private async Task<T?> GetJsonAsync<T>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new PoServerException(
                    $"Server PO trả về lỗi HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return await response.Content.ReadFromJsonAsync(typeInfo, ct).ConfigureAwait(false);
        }
        catch (PoServerException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new PoServerException("Server PO không phản hồi (quá thời gian chờ).", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PoServerException(
                "Không kết nối được tới server PO. Kiểm tra server đã chạy chưa và địa chỉ (Po:BaseUrl).", ex);
        }
        catch (JsonException ex)
        {
            throw new PoServerException("Dữ liệu server PO trả về không hợp lệ.", ex);
        }
    }

    /// <summary>
    /// Tải tập mã của một PO.
    /// </summary>
    public async Task<IReadOnlyList<PoCode>> GetCodesAsync(
        string orderNo,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNo))
            return [];

        var codes = await GetJsonAsync(
                $"api/po/{Uri.EscapeDataString(orderNo.Trim())}/codes",
                PoJsonContext.Default.ListPoCode, ct)
            .ConfigureAwait(false);

        return codes ?? [];
    }

    /// <summary>
    /// Gửi yêu cầu kích hoạt một tập mã của PO.
    /// </summary>
    public async Task<PoActivateResult> ActivateAsync(
        string orderNo,
        IReadOnlyList<string> serials,
        CancellationToken ct = default)
    {
        var request = new PoActivateRequest
        {
            OrderNo = orderNo,
            Serials = [.. serials],
        };

        using var content = JsonContent.Create(
            request, PoJsonContext.Default.PoActivateRequest);
        using var response = await _http
            .PostAsync("api/activate", content, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new PoActivateResult
            {
                Ok = false,
                Message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
            };
        }

        var result = await response.Content
            .ReadFromJsonAsync(PoJsonContext.Default.PoActivateResult, ct)
            .ConfigureAwait(false);

        return result ?? new PoActivateResult { Ok = false, Message = "Phản hồi rỗng." };
    }

    /// <summary>
    /// Tạo một PO mới trên server (tuỳ chọn — thường chỉ dùng khi test).
    /// </summary>
    public async Task<ProductionOrder?> CreateOrderAsync(
        string orderNo,
        int orderQty,
        CancellationToken ct = default)
    {
        var request = new PoCreateRequest
        {
            OrderNo = orderNo,
            OrderQty = orderQty,
        };

        using var content = JsonContent.Create(request, PoJsonContext.Default.PoCreateRequest);
        using var response = await _http.PostAsync("api/po", content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content
            .ReadFromJsonAsync(PoJsonContext.Default.ProductionOrder, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
