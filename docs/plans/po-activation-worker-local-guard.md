# Kế hoạch: tạm dừng `PoActivationWorker` khi PoServer cùng máy đang tắt

> Trạng thái: **kế hoạch** — chưa implement.
> Nhánh: `ai/opencode`.
> Liên quan: fix #2 trong ghi chú log ngày 2026-11-21.

## 1. Vấn đề hiện tại

`PoActivationWorker` chạy vòng lặp mỗi `Po.ActivationIntervalMs` (2.500 ms). Mỗi vòng:

1. Kiểm tra `_po.IsHalted` / `_po.IsProductionRunning`.
2. Nếu có PO đang chạy → gọi `_po.ActivatePendingAsync()` → HTTP `POST /api/activate`
   tới `Po.BaseUrl`.

Khi máy chạy chế độ dev không bật PoServer:

- `PoServer:Enabled = false` → không có HTTP server nào lắng nghe.
- `Po.BaseUrl = http://127.0.0.1:5080` → trỏ về chính máy đó.

Kết quả: mỗi 2,5 giây một `HttpRequestException` (Connection refused) kèm
**stack trace đầy đủ**, làm log thật bị chìm trong hàng nghìn dòng nhiễu.
Xem log ngày 2026-11-21: ~1.600 dòng chỉ trong vài phút chạy.

Đây là cấu hình **chắc chắn thất bại** — không phải lỗi mạng tạm thời — nên
việc retry vô hạn không có ý nghĩa.

## 2. Mục tiêu

- Không chạy vòng lặp gửi kích hoạt khi cấu hình chắc chắn thất bại:
  `Po:AutoActivate = true` **và** `PoServer:Enabled = false` **và**
  `Po:BaseUrl` trỏ về loopback (cùng máy).
- Thay bằng **một dòng log Warning duy nhất** nêu rõ nguyên nhân + cách khắc phục.
- Không thay đổi hành vi nào khác:
  - Vẫn `RestoreRunningOrderAsync()` để khôi phục PO đang chạy (camera vẫn quét,
    vẫn ghi PLC bình thường).
  - Gửi thủ công từ tab "Truy xuất Sản xuất" vẫn hoạt động.
  - Khi `PoServer:Enabled = true` (cùng máy) → chạy như cũ.
  - Khi `Po.BaseUrl` trỏ server thật (LAN / MES) → chạy như cũ dù PoServer tắt.

## 3. Ma trận hành vi sau khi fix

| `Po:AutoActivate` | `PoServer:Enabled` | `Po:BaseUrl` | Hành vi worker |
|---|---|---|---|
| false | * | * | Không chạy (như hiện tại, log Information 1 lần) |
| true | true | loopback | **Chạy** bình thường |
| true | true | server khác | **Chạy** bình thường |
| true | false | server khác | **Chạy** bình thường |
| true | false | loopback | **Không chạy** + Warning 1 lần (mới) |

## 4. Thiết kế

### 4.1. Tách logic thuần sang `ClassLibraryMT.Po`

Lý do: test được bằng `tools/PoTests` (đã reference `ClassLibraryMT.Po`),
không kéo theo Avalonia/UI, và `ClassLibraryMT.Po` không cần biết
`ClassLibraryMT.PoServer`.

File mới `ClassLibraryMT.Po/PoActivationPolicy.cs`:

```csharp
namespace ClassLibraryMT.Po;

/// <summary>
/// Quyết định worker gửi kích hoạt có nên chạy hay không, dựa trên cấu hình
/// <c>Po</c> và trạng thái PoServer cùng máy.
/// </summary>
public static class PoActivationPolicy
{
    /// <summary>
    /// True nếu <paramref name="baseUrl"/> trỏ về chính máy đang chạy
    /// (localhost, 127.0.0.0/8, ::1).
    /// URL rỗng / không parse được → false (không chặn, để worker báo lỗi như cũ).
    /// </summary>
    public static bool IsLoopbackBaseUrl(string? baseUrl);

    /// <summary>
    /// False khi mọi request chắc chắn thất bại: PoServer cùng máy đang tắt
    /// và BaseUrl trỏ về loopback.
    /// </summary>
    public static bool ShouldRun(PoOptions options, bool poServerEnabled);
}
```

Cài đặt `IsLoopbackBaseUrl`:

- `Uri.TryCreate(url, UriKind.Absolute, out var uri)` — thất bại → `false`.
- `uri.IsLoopback` của .NET đã xử lý `localhost`, `127.x.x.x`, `[::1]`.
- Bổ sung: so sánh `uri.Host` với `Dns.GetHostName()`? **Không** — tránh phân giải
  DNS, tránh phụ thuộc mạng. Chỉ dùng `Uri.IsLoopback`.

`ShouldRun`:

```csharp
if (!options.AutoActivate) return false;              // đã xử lý riêng, giữ nguyên
if (poServerEnabled) return true;
return !IsLoopbackBaseUrl(options.BaseUrl);
```

> Ghi chú: worker hiện return sớm khi `AutoActivate = false`; hàm `ShouldRun`
> chỉ dùng cho nhánh còn lại để tránh trùng logic.

### 4.2. Sửa `TanTienQR/Services/PoActivationWorker.cs`

- Inject thêm `IOptions<PoServerOptions>` (`ClassLibraryMT.PoServer` đã được
  đăng ký trong `TanTienAppServices`), giữ nguyên các dependency cũ.
- Sau khối `if (!_opt.AutoActivate) { ... return; }`, thêm:

```csharp
if (!PoActivationPolicy.ShouldRun(_opt, _poServerOpt.Enabled))
{
    _logger.LogWarning(
        "PoActivationWorker không chạy: PoServer:Enabled = false và Po:BaseUrl " +
        "trỏ về máy cục bộ ({BaseUrl}). Bật PoServer hoặc trỏ BaseUrl tới server MES thật.",
        _opt.BaseUrl);
    return;
}
```

- Cập nhật XML doc của class: thêm điều kiện dừng mới.

### 4.3. Không đổi

- `PeriodicTimer`, chu kỳ tối thiểu 2.001 ms.
- Xử lý `IsHalted` / `IsProductionRunning`.
- `RestoreRunningOrderAsync()` chạy **trước** mọi kiểm tra (giữ nguyên vị trí).

## 5. File thay đổi

| File | Loại |
|---|---|
| `ClassLibraryMT.Po/PoActivationPolicy.cs` | mới |
| `TanTienQR/Services/PoActivationWorker.cs` | sửa |
| `tools/PoTests/Program.cs` | thêm test |
| `docs/production-order.md` | cập nhật bảng cấu hình `Po` (mục `AutoActivate`) + luồng kích hoạt (mục 8) |
| `docs/plans/po-activation-worker-local-guard.md` | file này |

## 6. Kịch bản test

### 6.1. Unit test (`tools/PoTests`)

Bảng case cho `IsLoopbackBaseUrl`:

| Input | Kỳ vọng |
|---|---|
| `http://localhost:5080` | `true` |
| `http://127.0.0.1:5080` | `true` |
| `http://127.0.0.5:5080/api` | `true` |
| `http://[::1]:5080` | `true` |
| `http://192.168.1.10:5080` | `false` |
| `https://mes.congty.vn` | `false` |
| `""`, `null`, `"abc"` | `false` |

Bảng case cho `ShouldRun`:

| AutoActivate | PoServerEnabled | BaseUrl | Kỳ vọng |
|---|---|---|---|
| false | false | loopback | `false` |
| true | false | loopback | `false` |
| true | true | loopback | `true` |
| true | false | `http://10.0.0.30:5080` | `true` |
| true | true | `http://10.0.0.30:5080` | `true` |

### 6.2. Kiểm thử thủ công

1. Chạy app với `appsettings.json` hiện tại (`PoServer:Enabled = false`,
   `Po:BaseUrl = http://127.0.0.1:5080`):
   - Log có đúng **1** dòng Warning, không còn stack trace lặp.
   - App vẫn khôi phục PO đang chạy, vẫn quét mã, vẫn ghi PLC.
2. Đặt `PoServer:Enabled = true` → khởi động lại: worker chạy, log Information
   "PoActivationWorker bắt đầu...".
3. Đặt `Po.BaseUrl = http://10.0.0.30:5080` (server lab) → worker chạy và chỉ báo
   lỗi mạng khi server thật sự không tới được.

## 7. Rủi ro & lưu ý

- **Cấu hình đọc 1 lần khi khởi động** (`RequiresRestart` trong UI) → đổi
  PoServer/BaseUrl phải khởi động lại app, khớp hành vi hiện tại.
- **Không che lỗi cấu hình**: URL rỗng / sai định dạng → `false` → worker vẫn
  chạy và báo lỗi như cũ, tránh "im lặng" khi người dùng gõ sai.
- **Không xử lý fix #1** (spam log khi server thật mất kết nối): vẫn là việc
  riêng; có thể làm backoff riêng sau.
- Nếu sau này worker chạy đa máy (mỗi máy một PoServer), logic vẫn đúng vì chỉ
  chặn đúng trường hợp loopback + server tắt.

## 8. Definition of Done

- [ ] `dotnet build VisionF.slnx` — 0 lỗi, 0 warning.
- [ ] `dotnet run --project tools/PoTests` — pass toàn bộ case mục 6.1.
- [ ] Chạy app theo mục 6.2 bước 1: log chỉ 1 dòng Warning.
- [ ] `docs/production-order.md` mô tả điều kiện worker không chạy.
- [ ] Commit trên nhánh `ai/opencode`.
