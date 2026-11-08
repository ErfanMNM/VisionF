# Quy trình Production Order (PO) — TanTienQR

Tài liệu mô tả luồng quản lý lệnh sản xuất (Production Order — PO) cho SCADA
TanTienQR: server giả lập quản lý PO, SCADA tải PO về và lưu tập mã riêng theo
PO/tháng, khóa PO khi bắt đầu sản xuất, và worker gửi kích hoạt định kỳ.

---

## 1. Tổng quan luồng

```
PoServer (console)                 TanTienQR (SCADA)
───────────────────                 ─────────────────────────
POST /api/po       ───────────────▶ ProductionOrderService
GET  /api/po       ◀─── danh sách PO ──▶ PoClient
GET  .../codes     ◀─── tập mã PO  ────▶ PoStore (logs/po/{yyyy-MM}/{po}.db)
POST /api/activate ◀─── mã kích hoạt ─── PoActivationWorker (>2s/lần)
```

Luồng xử lý mã trong `CameraSignalWorker`:

```
QR từ camera
   ├─ NoRead / FAIL            → Fail (D12 = 0)
   ├─ Sai định dạng             → Fail (D12 = 0)
   ├─ Chưa bắt đầu sản xuất     → NoProductionOrder  (LOẠI MÃ — không ghi PLC)
   ├─ Mã ngoài PO đang chạy      → InvalidForOrder     → Fail (D12 = 0)
   ├─ Mã đã quét trong PO này    → Duplicate           → Fail (D12 = 0)
   └─ Mã hợp lệ                 → Pass (D12 = 1) + đánh dấu đã quét
```

> **Lưu ý:** trước khi có PO đang chạy, **mọi mã đều bị loại bỏ** và không ghi
> xuống PLC (kể cả mã đúng định dạng).

---

## 2. Thành phần

### 2.1. `ClassLibraryMT.Po` (thư viện dùng chung)

| File | Vai trò |
| ---- | ------- |
| `ProductionOrder.cs` | Model PO: `OrderNo`, `OrderQty`, `CreatedUtc`, `Additional` |
| `ProductionOrderStatus.cs` | Enum: `Available`, `Downloaded`, `Running`, `Completed`, `Closed` |
| `PoOptions.cs` | Cấu hình section `Po` |
| `PoContracts.cs` | DTO: `PoCode`, `PoCreateRequest`, `PoActivateRequest`, `PoActivateResult` |
| `PoJsonContext.cs` | `JsonSerializerContext` source-generated (AOT-safe) |
| `PoClient.cs` | Gọi API server PO qua `HttpClient` |
| `PoStore.cs` | Quản lý file SQLite riêng cho từng PO |
| `PoCodeCache.cs` | Cache RAM: tập mã hợp lệ + mã đã quét (O(1)) |
| `ProductionOrderService.cs` | Điều phối: tải PO, bắt đầu/hoàn tất, validate mã, gửi kích hoạt |
| `PoServiceCollectionExtensions.cs` | `AddProductionOrders(configuration)` — đăng ký DI |

### 2.2. `tools/PoServer` (server giả lập)

Console app chạy độc lập, dùng `HttpListener` + SQLite.

| File | Vai trò |
| ---- | ------- |
| `Program.cs` | `HttpListener` + routing + seed dữ liệu mẫu |
| `PoServerStore.cs` | SQLite `Orders` / `Codes` |
| `PoServerJsonContext.cs` | JSON context (AOT-safe) |

### 2.3. Tích hợp trong `TanTienQR`

| File | Thay đổi |
| ---- | -------- |
| `Services/CameraSignalWorker.cs` | Gate PO trước khi check trùng cửa sổ thời gian |
| `Models/TanTienEnums.cs` | Thêm `ScanOutcome.NoProductionOrder`, `ScanOutcome.InvalidForOrder` |
| `Services/PoActivationWorker.cs` | Worker nền gửi mã kích hoạt định kỳ + nạp cache lúc khởi động |
| `Models/AppSection.cs` | Thêm `AppSection.ProductionOrders = 8` |
| `ViewModels/ProductionInfoViewModel.cs` | Bảng "Thông tin sản xuất" trong `InfoView` |
| `ViewModels/ProductionOrdersViewModel.cs` | Tab "Truy xuất Sản xuất (Local)" |
| `Views/ProductionOrdersView.axaml` | View của tab mới |
| `Views/InfoView.axaml` | `TabControl` 2 tab: Sự kiện gần đây / Thông tin sản xuất |
| `Views/MainWindow.axaml` | Nút header + chip sidebar + vùng nội dung tab mới |

---

## 3. Cấu trúc dữ liệu

### 3.1. Server: `logs/po-server.db`

**Bảng `Orders`**

| Cột | Kiểu | Ý nghĩa |
| --- | ---- | ------- |
| `OrderNo` | TEXT (PK) | Mã lệnh sản xuất |
| `OrderQty` | INTEGER | Số lượng mã |
| `CreatedUtc` | TEXT | Thời điểm tạo (UTC) |
| `IssuedUtc` | TEXT NULL | Thời điểm SCADA lấy mã |
| `CompletedUtc` | TEXT NULL | Thời điểm hoàn tất |
| `Status` | TEXT | `Available` / `Downloaded` / `Completed` / `Closed` |

**Bảng `Codes`**

| Cột | Kiểu | Ý nghĩa |
| --- | ---- | ------- |
| `OrderNo` | TEXT | Mã PO (FK → `Orders`) |
| `Code` | TEXT (PK) | Mã đầy đủ (TXNG Digital Link) |
| `Gtin` | TEXT | GTIN (mã sản phẩm) |
| `Serial` | TEXT | Serial của mã |
| `ActivatedUtc` | TEXT NULL | Thời điểm được kích hoạt |

### 3.2. SCADA: `logs/po/{yyyy-MM}/{OrderNo}.db`

Mỗi PO một file riêng — nhờ vậy kiểm tra trùng chỉ diễn ra trong phạm vi 1 PO.
Lịch sử quét tổng vẫn nằm ở `logs/scans.db` (không thay đổi).

**Bảng `PoCodes`**

| Cột | Kiểu | Ý nghĩa |
| --- | ---- | ------- |
| `Code` | TEXT (PK) | Mã đầy đủ |
| `Gtin` | TEXT | GTIN |
| `Serial` | TEXT | Serial |
| `ScannedUtc` | TEXT NULL | Thời điểm quét (NULL = chưa quét) |
| `Status` | TEXT | `Pending` / `Scanned` / `Activated` |
| `ActivatedUtc` | TEXT NULL | Thời điểm đã gửi kích hoạt thành công |

**Bảng `PoMeta`** — các cặp `Key`/`Value`:

| Key | Ý nghĩa |
| --- | ------- |
| `orderNo` | Mã PO |
| `status` | Trạng thái PO |
| `createdUtc` | Thời điểm tạo |
| `startedUtc` | Thời điểm bắt đầu sản xuất |
| `completedUtc` | Thời điểm hoàn tất |

---

## 4. Định dạng mã

Mã mẫu theo chuẩn **TXNG Digital Link**:

```
https://digital-link.txng.vn/01/{gtin}/21/{serial}
```

- GTIN mặc định: `08934567890123`
- Serial mẫu: `PO{orderNo}{i:D6}` (ví dụ `PO20261108-001000001`)

`QrParser` của SCADA hỗ trợ sẵn pattern Digital Link nên camera đọc được mã thật.

---

## 5. Cấu hình

Section `Po` trong `TanTienQR/appsettings.json`:

```json
{
  "Po": {
    "BaseUrl": "http://127.0.0.1:5080",
    "DbRoot": "logs/po",
    "ActivationIntervalMs": 2500,
    "AutoActivate": true,
    "CodeSampleSize": 100
  }
}
```

| Khoá | Mặc định | Ý nghĩa |
| ---- | -------- | ------- |
| `BaseUrl` | `http://127.0.0.1:5080` | Địa chỉ server PO |
| `DbRoot` | `logs/po` | Thư mục gốc lưu file DB theo PO |
| `ActivationIntervalMs` | `2500` | Chu kỳ gửi mã kích hoạt (bắt buộc > 2000) |
| `AutoActivate` | `true` | Bật/tắt worker gửi kích hoạt định kỳ |
| `CodeSampleSize` | `100` | Số mã mẫu sinh cho mỗi PO (seed server) |

Biến môi trường có tiền tố `TANTIEN_` cũng áp dụng, ví dụ:
`TANTIEN_Po__BaseUrl=http://10.0.0.5:5080`.

---

## 6. Chạy server giả lập

```powershell
dotnet run --project tools/PoServer/PoServer.csproj
```

Mặc định lắng nghe `http://127.0.0.1:5080/`. Có thể đổi địa chỉ:

```powershell
dotnet run --project tools/PoServer/PoServer.csproj -- http://0.0.0.0:5080/
```

Khi khởi động lần đầu, server tự **seed 3 PO × 100 mã** (nếu database còn trống).

### API

| Method | Endpoint | Mô tả |
| ------ | -------- | ---- |
| `GET` | `/api/po` | Danh sách PO còn hiệu lực |
| `GET` | `/api/po/{orderNo}/codes` | Tải tập mã của PO |
| `POST` | `/api/po` | Tạo PO mới — body `{ "orderNo": "PO001", "orderQty": 100 }` |
| `POST` | `/api/activate` | Kích hoạt mã — body `{ "orderNo": "PO001", "serials": ["..."] }` |

Ví dụ:

```powershell
curl http://127.0.0.1:5080/api/po
curl http://127.0.0.1:5080/api/po/PO20261108-001/codes
curl -X POST http://127.0.0.1:5080/api/po -H "Content-Type: application/json" -d '{"orderNo":"PO-TEST","orderQty":50}'
```

---

## 7. Quy trình vận hành

1. **Khởi động `PoServer`** (nếu dùng server giả lập).
2. **Khởi động SCADA TanTienQR**, đăng nhập.
3. Mở tab **Truy xuất Sản xuất (Local)** → bấm **Tải PO từ server**.
   - Mỗi PO mới tạo 1 file `logs/po/{yyyy-MM}/{OrderNo}.db`.
4. Chọn PO trong dropdown → bấm **Bắt đầu sản xuất**.
   - PO bị khóa; tập mã hợp lệ được nạp vào RAM cache.
   - Camera bắt đầu ghi kết quả xuống PLC cho các mã thuộc PO này.
5. **Quét mã**:
   - Mã thuộc PO & chưa quét → Pass (D12 = 1), đếm tăng.
   - Quét lại mã đó → Duplicate (Fail).
   - Mã của PO khác / ngoài PO → InvalidForOrder (Fail).
6. **Hoàn tất sản xuất** → PO chuyển `Completed`, mở khoá chọn PO.
7. **Kích hoạt**: worker nền tự gửi mã chưa kích hoạt mỗi 2500ms; có thể gửi
   thủ công bằng nút **Gửi kích hoạt** trong tab.

### Quyền

| Hành động | Quyền |
| --------- | ----- |
| Xem tab Truy xuất Sản xuất | `Permission.ViewProduction` (mọi vai trò) |
| Bắt đầu / Hoàn tất / Gửi kích hoạt | `Permission.EditConfig` (Operator trở lên) |

Không thêm quyền mới vào `ClassLibraryMT.Auth` vì ma trận quyền dùng chung cho mọi
ứng dụng trong repo.

---

## 8. Kiểm thử

```powershell
dotnet build VisionF.slnx
```

Kịch bản kiểm thử:

| # | Bước | Kết quả mong đợi |
| - | ---- | ----------------- |
| 1 | Chạy `PoServer`, seed 3 PO × 100 mã | `GET /api/po` trả về 3 PO |
| 2 | SCADA → Tải PO từ server | Tạo `logs/po/{yyyy-MM}/{orderNo}.db` |
| 3 | Quét mã **trước khi** bắt đầu sản xuất | Mã bị loại, **không** ghi PLC (`NoProductionOrder`) |
| 4 | Bắt đầu sản xuất → quét mã hợp lệ | Pass, ghi D12 = 1, số đã quét tăng |
| 5 | Quét lại cùng mã | `Duplicate` (Fail) |
| 6 | Quét mã của PO khác | `InvalidForOrder` (Fail) |
| 7 | Hoàn tất sản xuất | Mở khoá chọn PO; PO đã hoàn tất không chạy |
| 8 | Kiểm tra worker kích hoạt | Server nhận mã định kỳ > 2s/lần |

---

## 9. Ghi chú kỹ thuật

- **AOT-safe**: mọi tuần tự hoá JSON dùng `JsonSerializerContext` source-generated
  (`PoJsonContext`, `PoServerJsonContext`); cấu hình được bind thủ công (không dùng
  `ConfigurationBinder.GetValue<T>`).
- **RAM cache**: `PoCodeCache` giữ `HashSet<string>` cho mã hợp lệ và mã đã quét
  của từng PO → kiểm tra trùng O(1). Cache được nạp lại từ các file DB khi app
  khởi động (`ProductionOrderService.RebuildCacheAsync`).
- **Thread-safety**: `ProductionOrderService` dùng lock quanh trạng thái PO đang
  chạy; `PoCodeCache` dùng lock cho dictionary.
- **`logs/scans.db` giữ nguyên**: lịch sử tra cứu toàn cục không bị ảnh hưởng bởi
  cơ chế tách DB theo PO.
