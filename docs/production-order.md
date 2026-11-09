# Quy trình Production Order (PO) — TanTienQR

Tài liệu mô tả luồng quản lý lệnh sản xuất (Production Order — PO) cho SCADA
TanTienQR theo mô hình **local-first**: server chỉ dùng để *cào* (sync một
chiều) dữ liệu về máy, mọi lần đọc danh sách PO cho UI đều đến từ DB local.
PO đủ 100% thì tự động hoàn tất; có nút **Huỷ PO**; khi phát hiện mã đã từng
xuất hiện trong lịch sử quét thì **dừng máy**.

---

## 1. Tổng quan luồng

```
PoServer (console)                 TanTienQR (SCADA)
───────────────────                 ─────────────────────────
POST /api/po       ───────────────▶ ProductionOrderService
GET  /api/po       ◀─── danh sách PO ──▶ PoClient
GET  .../codes     ◀─── tập mã PO  ────▶ PoStore (logs/po/{yyyy-MM}/{po}.db)
POST /api/activate ◀─── mã kích hoạt ─── PoActivationWorker (>2s/lần)
                                      ▲
                                      │ đọc (để đối chiếu trùng lịch sử)
                                      │
                            logs/scans.db (ScanRecords)
```

### Mô hình local-first

| Thao tác | Nguồn dữ liệu | Gọi mạng? |
| -------- | -------------- | --------- |
| **Tải PO** (nút "Tải PO") | Server → ghi/merge vào DB local | Có |
| **Xem danh sách PO** | DB local | **Không** |
| **Bắt đầu / Huỷ / Xem tiến độ** | DB local | **Không** |
| **Gửi mã kích hoạt** | Server (mã lấy từ DB local) | Có |

Nút **Tải PO** có thể bấm **nhiều lần**: PO mới và mã mới được thêm vào, còn mã
cũ và lịch sử đã quét được giữ nguyên (`INSERT OR IGNORE`).

### Luồng xử lý mã trong `CameraSignalWorker`

```
QR từ camera
   ├─ NoRead / FAIL            → Fail (D12 = 0)
   ├─ Sai định dạng             → Fail (D12 = 0)
   ├─ Máy đã dừng (trùng lịch sử) → Halted (LOẠI MÃ — không ghi PLC)
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
| `ProductionOrderStatus.cs` | Enum: `Available`, `Downloaded`, `Running`, `Completed`, `Closed`, `Cancelled` |
| `PoOptions.cs` | Cấu hình section `Po` |
| `PoContracts.cs` | DTO: `PoCode`, `PoCreateRequest`, `PoActivateRequest`, `PoActivateResult` |
| `PoJsonContext.cs` | `JsonSerializerContext` source-generated (AOT-safe) |
| `IPoClient.cs` | Interface gọi API server (cho phép thay bằng bản giả khi test) |
| `PoClient.cs` | Cài đặt `IPoClient` qua `HttpClient` |
| `IPoScanHistoryReader.cs` | Interface đọc lịch sử quét (chống trùng lịch sử toàn cục) |
| `PoStore.cs` | Quản lý file SQLite riêng cho từng PO + `ListLocalOrdersAsync` |
| `PoHistoryStore.cs` | DB lịch sử chạy PO toàn cục (`logs/po/po-history.db`) |
| `PoCodeCache.cs` | Cache RAM: 1 `Dictionary` cho PO **đang chạy** (O(1)) |
| `ProductionOrderService.cs` | Điều phối: sync, bắt đầu/huỷ/hoàn tất, validate mã, gửi kích hoạt |
| `PoServiceCollectionExtensions.cs` | `AddProductionOrders(configuration)` — đăng ký DI |

### 2.2. `tools/PoServer` (server giả lập)

Console app chạy độc lập, dùng `HttpListener` + SQLite.

| File | Vai trò |
| ---- | ------- |
| `Program.cs` | `HttpListener` + routing + seed dữ liệu mẫu + phục vụ giao diện web |
| `PoServerStore.cs` | SQLite `Orders` / `Codes` |
| `PoServerJsonContext.cs` | JSON context (AOT-safe) |
| `www/index.html` | Giao diện web tạo PO mẫu (HTML/CSS/JS thuần, không cần build) |

### 2.3. `tools/PoTests` (kiểm thử tự động)

Console app kiểm thử 9 nhóm kịch bản của luồng PO (không cần app chạy thật).

```powershell
dotnet run --project tools/PoTests/PoTests.csproj
```

### 2.4. Tích hợp trong `TanTienQR`

| File | Thay đổi |
| ---- | -------- |
| `Services/CameraSignalWorker.cs` | Gate PO trước khi check trùng cửa sổ thời gian; ghi `OrderNo` vào log quét |
| `Models/TanTienEnums.cs` | Thêm `ScanOutcome.NoProductionOrder`, `.InvalidForOrder`, `.Halted` |
| `Services/PoActivationWorker.cs` | Worker nền gửi mã kích hoạt định kỳ + auto-resume PO khi khởi động |
| `Services/PoScanHistoryReader.cs` | Cầu nối `ClassLibraryMT.Po` ↔ `ClassLibraryMT.Scans` |
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
| `status` | Trạng thái PO (`Downloaded` / `Running` / `Completed` / `Cancelled`) |
| `createdUtc` | Thời điểm tạo |
| `startedUtc` | Thời điểm bắt đầu sản xuất |
| `completedUtc` | Thời điểm hoàn tất |
| `cancelledUtc` | Thời điểm huỷ |
| `orderQty` | Tổng số mã của PO |

### 3.3. SCADA: `logs/po/po-history.db`

DB riêng (file, không phải thư mục tháng) lưu lịch sử các lần chạy PO.
Dùng cho **auto-resume**: nếu còn dòng nào ở trạng thái `Running` thì app khôi
phục PO đó khi mở máy.

**Bảng `PoRuns`**

| Cột | Kiểu | Ý nghĩa |
| --- | ---- | ------- |
| `RunId` | INTEGER (PK) | Khoá tự tăng |
| `OrderNo` | TEXT | Mã PO |
| `OrderQty` | INTEGER | Tổng số mã khi bắt đầu |
| `CreatedUtc` | TEXT | Thời điểm tạo PO |
| `StartedUtc` | TEXT | Thời điểm bắt đầu sản xuất |
| `CompletedUtc` | TEXT NULL | Thời điểm hoàn tất (tự động khi đủ 100%) |
| `CancelledUtc` | TEXT NULL | Thời điểm huỷ |
| `Status` | TEXT | `Running` / `Completed` / `Cancelled` |

Index: `IX_PoRuns_OrderNo`, `IX_PoRuns_Status`.

### 3.4. SCADA: `logs/scans.db` — log quét toàn hệ thống

Bảng `ScanRecords` (schema v3) — nguồn cho cơ chế chống trùng lịch sử toàn cục:

| Cột | Kiểu | Ý nghĩa |
| --- | ---- | ------- |
| `Id` | INTEGER (PK) | Khoá tự tăng |
| `ReceivedUtc` | TEXT | Thời điểm nhận mã (UTC) |
| `Code` | TEXT | Mã / serial |
| `Gtin` | TEXT | GTIN |
| `Serial` | TEXT | Serial |
| `Status` | TEXT | `Pass` / `Fail` / `Overload` |
| `ProcessingTimeMs` | REAL | Thời gian xử lý (ms) |
| `PlcResult` | TEXT | `Ok` / `Failed` / `Skipped` |
| `BatchNo` | TEXT | Số batch |
| `Raw` | TEXT | Nội dung gốc camera |
| `OrderNo` | TEXT | **Mã PO** mà mã này thuộc về (mới — schema v3) |

Index: `IX_ScanRecords_ReceivedUtc`, `IX_ScanRecords_Status`, `IX_ScanRecords_BatchNo`,
`IX_ScanRecords_OrderNo`.

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

> `DbRoot` chứa cả thư mục tháng (`{yyyy-MM}/`) và file `po-history.db`.

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

### Giao diện web tạo PO mẫu

Mở trình duyệt tại **[http://127.0.0.1:5080/](http://127.0.0.1:5080/)** để dùng công cụ tạo PO:

- **Form "Tạo PO mới"** — nhập *Mã lệnh sản xuất* + *Số lượng mã* → bấm **Tạo PO**
  - Nút **Gợi ý mã PO** tự điền mã theo ngày, ví dụ `PO20261109-001`
  - Mỗi mã được sinh theo chuẩn TXNG Digital Link: `/01/{gtin}/21/{serial}`
- **Bảng "Danh sách PO"** — hiển thị các PO còn hiệu lực (mã PO, số lượng mã, thời điểm tạo)
  - Nút **Làm mới** để tải lại danh sách
- Thông báo kết quả hiển thị ngay dưới form (xanh = thành công, đỏ = lỗi, ví dụ mã PO đã tồn tại)

> Trang HTML nằm tại `tools/PoServer/www/index.html` và được copy tự động vào
> thư mục output khi build (`CopyToOutputDirectory=PreserveNewest`), nên có thể
> chỉnh sửa giao diện rồi build lại là dùng được ngay.

### API

| Method | Endpoint | Mô tả |
| ------ | -------- | ---- |
| `GET` | `/` | Giao diện web tạo PO mẫu (`www/index.html`) |
| `GET` | `/api/po` | Danh sách PO còn hiệu lực |
| `GET` | `/api/po?includeCompleted=true` | Danh sách PO **kể cả** đã hoàn tất / đã đóng |
| `GET` | `/api/po/{orderNo}/codes` | Tải tập mã của PO |
| `POST` | `/api/po` | Tạo PO mới — body `{ "orderNo": "PO001", "orderQty": 100 }` |
| `POST` | `/api/activate` | Kích hoạt mã — body `{ "orderNo": "PO001", "serials": ["..."] }` |

> `?includeCompleted=true` là tham số SCADA luôn dùng khi cào — để danh sách
> PO trên máy SCADA khớp với server (PO đã xong ở server vẫn phải hiện trong
> bảng quản lý local).

Ví dụ:

```powershell
curl http://127.0.0.1:5080/api/po
curl "http://127.0.0.1:5080/api/po?includeCompleted=true"
curl http://127.0.0.1:5080/api/po/PO20261108-001/codes
curl -X POST http://127.0.0.1:5080/api/po -H "Content-Type: application/json" -d '{"orderNo":"PO-TEST","orderQty":50}'
```

---

## 7. Quy trình vận hành

1. **Khởi động `PoServer`** (nếu dùng server giả lập).
   - Mở [http://127.0.0.1:5080/](http://127.0.0.1:5080/) để tạo PO mẫu (không bắt buộc — server cũng tự seed 3 PO × 100 mã).
2. **Khởi động SCADA TanTienQR**, đăng nhập.
   - Nếu có PO đang chạy từ lần trước → app **tự khôi phục** (đọc `po-history.db`)
     và camera ghi kết quả xuống PLC ngay.
3. Mở tab **Truy xuất Sản xuất (Local)** → bấm **Tải PO** để cào dữ liệu từ server.
   - Mỗi PO mới tạo 1 file `logs/po/{yyyy-MM}/{OrderNo}.db`.
   - Bấm lại nhiều lần được: PO/mã mới được thêm, dữ liệu cũ giữ nguyên.
4. Chọn PO trong dropdown → bấm **Bắt đầu**.
   - PO bị khóa; tập mã của PO này được nạp vào RAM cache.
   - Camera bắt đầu ghi kết quả xuống PLC cho các mã thuộc PO này.
5. **Quét mã**:
   - Mã thuộc PO & chưa quét → Pass (D12 = 1), đếm tăng.
   - Quét lại mã đó → Duplicate (Fail).
   - Mã của PO khác / ngoài PO → InvalidForOrder (Fail).
6. **PO đủ 100%** → tự động hoàn tất:
   - PO chuyển `Completed`, dropdown về placeholder, panel hiển thị "Chưa bắt đầu".
   - RAM cache được giải phóng ngay (`PoCodeCache.Clear()`).
7. **Chưa đủ 100%** → hiện nút **Huỷ PO**:
   - PO bị đánh dấu `Cancelled`, không thể chạy lại, biến mất khỏi dropdown
     (vẫn hiện trong bảng quản lý với badge "Đã huỷ").
8. **Kích hoạt**: worker nền tự gửi mã chưa kích hoạt mỗi 2500ms; có thể gửi
   thủ công bằng nút **Gửi kích hoạt** trong tab.
9. **Dừng máy khi trùng lịch sử**: trước khi gửi mã lên server, từng mã được
   đối chiếu với lịch sử quét trong `logs/scans.db`. Nếu mã **đã từng quét**
   trong hệ thống → hệ thống **dừng máy** (xem mục 8).

### Quyền

| Hành động | Quyền |
| --------- | ----- |
| Xem tab Truy xuất Sản xuất | `Permission.ViewProduction` (mọi vai trò) |
| Bắt đầu / Huỷ / Gửi kích hoạt | `Permission.EditConfig` (Operator trở lên) |

Không thêm quyền mới vào `ClassLibraryMT.Auth` vì ma trận quyền dùng chung cho mọi
ứng dụng trong repo.

---

## 8. Cơ chế dừng máy khi trùng lịch sử toàn cục

Trước khi gửi tập mã lên server (điểm `POST /api/activate`), `ProductionOrderService.ActivatePendingAsync()`
lấy danh sách serial **đã từng xuất hiện** trong lịch sử quét:

```sql
SELECT DISTINCT Serial FROM ScanRecords
WHERE Status = 'Pass' AND Serial <> '' AND (OrderNo <> $orderNo OR OrderNo = '');
```

(Bản ghi thuộc chính PO đang chạy bị bỏ qua — mã của PO đó hợp lệ.)

Nếu **bất kỳ** mã nào trong batch chờ kích hoạt nằm trong danh sách đó:

| # | Hành động |
| - | --------- |
| 1 | Đặt cờ `_halted = true` → `ValidateCode` trả `PoCodeValidation.Halted` → `CameraSignalWorker` **không ghi Pass xuống PLC** (chỉ loại mã) |
| 2 | `PoActivationWorker` ngừng gửi mã |
| 3 | **Huỷ PO** hiện tại (`Cancelled` trong `PoMeta` + `PoRuns`) → không chạy lại được |
| 4 | `LogError` + `ScanEvent` mức `Critical` (vạch đỏ trên UI) |
| 5 | Thông báo: *"Phát hiện N mã đã từng quét: {serial}. Đã dừng máy — vui lòng kiểm tra và xử lý."* |

---

## 9. Kiểm thử

### Tự động

```powershell
dotnet build VisionF.slnx -c Release
dotnet run --project tools/PoTests/PoTests.csproj
```

`PoTests` chạy 61 assertion cho 9 nhóm kịch bản: ghi/đọc PO local, sync nhiều
lần, cache RAM 1 entry/mã, lịch sử `PoRuns`, migration `OrderNo`, local-first
(không gọi mạng), auto-complete 100%, huỷ PO, auto-resume, dừng máy khi trùng
lịch sử.

### Thủ công

| # | Bước | Kết quả mong đợi |
| - | ---- | ----------------- |
| 1 | Chạy `PoServer`, seed 3 PO × 100 mã | `GET /api/po` trả về 3 PO |
| 2 | SCADA → bấm **Tải PO** | Tạo `logs/po/{yyyy-MM}/{orderNo}.db` |
| 3 | Bấm **Tải PO** lần 2 | PO/mã cũ không mất, PO/mã mới được thêm |
| 4 | Quét mã **trước khi** bắt đầu sản xuất | Mã bị loại, **không** ghi PLC (`NoProductionOrder`) |
| 5 | Bắt đầu sản xuất → quét mã hợp lệ | Pass, ghi D12 = 1, số đã quét tăng |
| 6 | Quét lại cùng mã | `Duplicate` (Fail) |
| 7 | Quét mã của PO khác | `InvalidForOrder` (Fail) |
| 8 | Quét đủ mã cuối cùng | PO **tự** hoàn tất, dropdown về placeholder |
| 9 | Chưa đủ 100% | Nút **Huỷ PO** hiện; bấm → PO biến mất khỏi dropdown, badge "Đã huỷ" trong bảng |
| 10 | Tắt app giữa chung → mở lại | PO đang chạy được khôi phục, camera ghi PLC tiếp |
| 11 | Thêm bản ghi `Pass` của PO khác vào `scans.db` → chạy kích hoạt | App báo **"Đã dừng máy"**, PO bị huỷ, PO không gửi mã lên server |

---

## 10. Ghi chú kỹ thuật

- **AOT-safe**: mọi tuần tự hoá JSON dùng `JsonSerializerContext` source-generated
  (`PoJsonContext`, `PoServerJsonContext`); cấu hình được bind thủ công (không dùng
  `ConfigurationBinder.GetValue<T>`).
- **RAM cache bounded**: `PoCodeCache` dùng MỘT `Dictionary<string, byte>` cho mã
  của PO **đang chạy** (mỗi mã 1 entry, cờ trạng thái gộp trong 1 byte) → giảm
  ~50% RAM so với 2 `HashSet` trước đây. Cache được `Clear()` ngay khi PO hoàn
  tất / bị huỷ → RAM **không phình theo lịch sử PO**.
- **Thread-safety**: `ProductionOrderService` dùng lock quanh trạng thái PO đang
  chạy; `PoCodeCache` dùng lock cho dictionary; cờ `_completing` chống gọi
  `CompleteProductionAsync` lồng nhau.
- **`logs/scans.db` vẫn lưu log quét toàn hệ thống** — dùng cho cơ chế chống
  trùng lịch sử toàn cục (mục 8) và báo cáo tra cứu.
- **Migration an toàn**: `ScanStore` thêm cột `OrderNo` và tạo index **sau** khi
  migration xong (tạo index trước sẽ lỗi `no such column` với DB cũ).
