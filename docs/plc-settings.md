# Tab Chỉnh PLC

Tài liệu kỹ thuật và hướng dẫn sử dụng cho tab **Chỉnh PLC** trong ứng dụng `TanTienQR`.

---

## 1. Tổng quan

Tab Chỉnh PLC gồm 3 phần:

| Vùng | Nội dung |
|---|---|
| **Webview camera** | Nhúng trang `http://<Camera.Ip>/monitor` để xem hình trực tiếp từ camera. |
| **Bảng thông số PLC** | Bảng tùy chỉnh các ô nhớ PLC cần đọc / ghi, kèm nút Đọc / Ghi / Sửa / Xoá. |
| **Khung Recipe** | Lưu bộ giá trị cho từng ô nhớ theo từng recipe, rồi áp dụng xuống PLC một lần. |

Tab nằm trong nhóm **CẤU HÌNH** và dùng chung quyền `Permission.EditConfig`:
- **Worker**: không thấy tab.
- **Operator**: thấy tab.
- **Administrator / Developer**: thấy tab.

---

## 2. Kiến trúc

```mermaid
flowchart TD
    plcTab[Tab "Chỉnh PLC"] --> plcView[PlcSettingsView]
    plcView --> webview[NativeWebView -> http Camera.Ip/monitor]
    plcView --> recipePanel[Khung Recipe]
    plcView --> paramsTable[Bang tham so PLC]
    recipePanel --> plcVm[PlcSettingsViewModel]
    paramsTable --> plcVm
    plcVm --> plcSvc[IPlcSettingsService - ClassLibraryMT.Plc]
    plcSvc --> plcStore[PlcStore - SQLite logs/plc.db]
    plcSvc --> plcClient[IPlcMemoryClientFactory -> OmronPlc]
    plcVm --> actionLog[IUserActionLogger - UserActions]
```

### Thư viện `ClassLibraryMT.Plc`

Project độc lập (`net10.0`, `IsAotCompatible=true`), dùng chung cho app khác sau này.

| File | Vai trò |
|---|---|
| `PlcModels.cs` | `PlcParameter`, `PlcRecipe`, `PlcValueType`, `PlcAccessMode`, `Result`, `PlcReadResult`, `PlcWriteResult`, `PlcEnumNames`. |
| `PlcStore.cs` | Quản lý schema SQLite, migration, CRUD tham số và recipe. |
| `IPlcSettingsService.cs` | API công khai cho app layer. |
| `PlcSettingsService.cs` | Triển khai service + validation + đọc/ghi PLC. |
| `PlcServiceCollectionExtensions.cs` | `services.AddPlcSettings(configuration)` — bind cấu hình thủ công (AOT-safe). |

Project reference:

```xml
<ProjectReference Include="..\ClassLibraryMT\ClassLibraryMT.csproj" />
<ProjectReference Include="..\ClassLibraryMT.Logging\ClassLibraryMT.Logging.csproj" />
```

---

## 3. Cơ sở dữ liệu

File: `logs/plc.db` (SQLite, WAL mode). Đường dẫn cấu hình trong `appsettings.json`:

```json
"PlcSettings": {
  "DbPath": "logs/plc.db"
}
```

### Bảng `PlcParams` — danh sách ô nhớ tùy chỉnh

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `Id` | `INTEGER PK AUTOINCREMENT` | Khoá chính. |
| `Name` | `TEXT NOT NULL UNIQUE` | Tên tham số, ví dụ `Delay Reject`. |
| `Address` | `TEXT NOT NULL` | Địa chỉ ô nhớ vùng D: `D12`, `DM12`, `D12.05`. |
| `ValueType` | `INTEGER NOT NULL` | Kiểu dữ liệu (xem bảng bên dưới). |
| `AccessMode` | `INTEGER NOT NULL` | Chế độ truy cập (xem bảng bên dưới). |
| `Description` | `TEXT NULL` | Mô tả tuỳ chọn. |
| `SortOrder` | `INTEGER NOT NULL` | Thứ tự hiển thị trong bảng. |
| `UpdatedUtc` | `TEXT NOT NULL` | Thời điểm cập nhật (ISO-8601). |

### Bảng `Recipes`

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `Id` | `INTEGER PK AUTOINCREMENT` | Khoá chính. |
| `Name` | `TEXT NOT NULL UNIQUE` | Tên recipe. |
| `IsDefault` | `INTEGER NOT NULL` | Recipe đầu tiên tạo ra sẽ là mặc định, không xoá được. |
| `CreatedUtc` | `TEXT NOT NULL` | Thời điểm tạo. |
| `UpdatedUtc` | `TEXT NOT NULL` | Thời điểm cập nhật. |

### Bảng `RecipeValues`

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `RecipeId` | `INTEGER NOT NULL` | FK → `Recipes.Id` (`ON DELETE CASCADE`). |
| `ParameterId` | `INTEGER NOT NULL` | FK → `PlcParams.Id` (`ON DELETE CASCADE`). |
| `Value` | `TEXT NOT NULL` | Giá trị (chuỗi, parse theo `ValueType` của tham số). |
| `UpdatedUtc` | `TEXT NOT NULL` | Thời điểm cập nhật. |

Khóa chính: `(RecipeId, ParameterId)`.

### Bảng `SchemaVersion`

Lưu phiên bản schema để migration (`Version INTEGER PK`, `AppliedUtc TEXT`).

> Migration dùng `PRAGMA table_info(...)` để kiểm tra cột trước khi `ALTER TABLE` — dữ liệu cũ không bị mất.

---

## 4. Kiểu dữ liệu ô nhớ

| Kiểu | Enum | API `OmronPlc` | Số word | Ghi chú |
|---|---|---|---|---|
| Int16 | `PlcValueType.Int16` | `ReadInt16` / `WriteInt16` | 1 | Giá trị trong khoảng `-32768..32767`. |
| Int32 | `PlcValueType.Int32` | `ReadInt32` / `WriteInt32` | 2 | |
| Float | `PlcValueType.Float` | `ReadFloat` / `WriteFloat` | 2 | Số thực 32 bit. |
| Bool | `PlcValueType.Bool` | `ReadBool` / `WriteBool` | 1 | **Bắt buộc** dùng địa chỉ có phần bit: `D12.05` (bit 0–15). |

Quy tắc kiểm tra địa chỉ:
- Phải bắt đầu bằng `D` hoặc `DM` (không phân biệt hoa thường).
- Phần word trong khoảng `0..32767` (giới hạn vùng D).
- Phần bit (nếu có) trong khoảng `0..15`.
- Kiểu `Bool` bắt buộc có phần bit; các kiểu khác bắt buộc **không** có phần bit.

## 5. Chế độ truy cập

| Chế độ | Enum | Đọc | Ghi |
|---|---|---|---|
| Chỉ đọc | `PlcAccessMode.Read` | Có | Không |
| Chỉ ghi | `PlcAccessMode.Write` | Không | Có |
| Đọc / Ghi | `PlcAccessMode.ReadWrite` | Có | Có |

Khi áp dụng recipe, chỉ những tham số có chế độ **ghi được** mới được ghi xuống PLC.

---

## 6. Hướng dẫn sử dụng

### Thêm ô nhớ mới

1. Mở tab **Chỉnh PLC**.
2. Bấm **"+ Tham số"**.
3. Điền:
   - **Tên tham số** — bắt buộc, duy nhất (ví dụ `Delay Reject`).
   - **Ô nhớ** — bắt buộc (ví dụ `D12`).
   - **Kiểu dữ liệu** — `Int16` / `Int32` / `Float` / `Bool`.
   - **Chế độ truy cập** — Chỉ đọc / Chỉ ghi / Đọc-Ghi.
   - **Mô tả**, **Thứ tự sắp xếp** — tuỳ chọn.
4. Bấm **Lưu**.

### Đọc / ghi một ô nhớ

- Nút **Đọc**: đọc giá trị hiện tại từ PLC, hiển thị ở cột *Giá trị*.
- Nút **Ghi**: ghi giá trị trong ô nhập xuống PLC.
- Nút **Đọc tất cả** (thanh công cụ trên cùng): đọc toàn bộ tham số.

### Tạo và áp dụng recipe

1. Nhập tên recipe ở ô **"Tên recipe mới"** rồi bấm **Tạo**.
2. Nhập giá trị cho từng tham số ở bảng bên phải.
3. Bấm **Lưu vào recipe** — lưu bộ giá trị hiện tại vào recipe đang chọn.
4. Chọn recipe khác để nạp lại bộ giá trị của nó.
5. Bấm **Áp dụng xuống PLC** — ghi tất cả tham số có quyền ghi của recipe xuống PLC.

> **Lưu ý**: ghi PLC là thao tác ảnh hưởng trực tiếp tới thiết bị. Kiểm tra kỹ giá trị trước khi áp dụng.

---

## 7. Nhật ký thao tác

Mọi thao tác đọc/ghi PLC và CRUD tham số đều được ghi vào bảng `UserActions` (lib `ClassLibraryMT.Logging`) kèm `Username` / `UserId` của người đang đăng nhập:

| Action | Target | Mô tả |
|---|---|---|
| `Plc.Read` | `PlcParam` | Đọc một hoặc tất cả ô nhớ. |
| `Plc.Write` | `PlcParam` | Ghi giá trị xuống PLC. |
| `Plc.ApplyRecipe` | `PlcRecipe` | Áp dụng recipe xuống PLC. |
| `Plc.Param.Delete` | `PlcParam` | Xoá tham số. |
| `Plc.Recipe.Create` | `PlcRecipe` | Tạo recipe. |
| `Plc.Recipe.Delete` | `PlcRecipe` | Xoá recipe. |
| `Plc.Recipe.Save` | `PlcRecipe` | Lưu giá trị vào recipe. |

Xem lại trong tab **Lịch sử** (quyền `ViewHistory`).

---

## 8. Cấu hình liên quan

Tab Chỉnh PLC dùng chung cấu hình `Plc` (kết nối PLC) và `Camera` (IP camera):

```json
{
  "Camera": {
    "Ip": "192.168.250.10",
    "Port": 9000
  },
  "Plc": {
    "Ip": "192.168.250.1",
    "Port": 9600,
    "RemoteNode": 0,
    "LocalNode": 1,
    "TimeoutMs": 1000,
    "Retries": 0
  },
  "PlcSettings": {
    "DbPath": "logs/plc.db"
  }
}
```

- `Camera.Ip` → webview trỏ `http://<Camera.Ip>/monitor`.
- `Plc.*` → client đọc/ghi ô nhớ (tạo client riêng, không dùng chung instance heartbeat).

Phần `PlcSettings` được giữ nguyên khi lưu cấu hình từ tab Cấu hình (giống `Logging` và `Auth`).

---

## 9. Ghi chú kỹ thuật & rủi ro

- **Thread-safety**: `OmronPlc` không thread-safe. `PlcSettingsService` serialize mọi lệnh đọc/ghi bằng `SemaphoreSlim`, và dùng **client riêng** (`IPlcMemoryClientFactory`) tách biệt khỏi instance singleton cho heartbeat — tránh tranh chấp.
- **Blocking I/O**: `OmronPlc` gọi UDP đồng bộ. Service chạy trong thread pool nên không chặn luồng UI.
- **Native AOT**: toàn bộ lib dùng ADO.NET thuần + bind cấu hình thủ công, không reflection.
- **`NativeWebView`**: cần WebView2 Runtime trên Windows (đã dùng ở tab Sản xuất nên không phát sinh yêu cầu mới).
- **Quyền ghi**: tham số có chế độ "Chỉ đọc" sẽ bị từ chối khi ghi; tham số "Chỉ ghi" sẽ bị từ chối khi đọc.

---

## 10. Kiểm thử

Project `ClassLibraryMT.Plc.Settings.Tests` (runner tự viết, không dùng xUnit) — 14 test:

1. Tạo schema đủ 4 bảng + index.
2. CRUD tham số: thêm / sửa / xoá.
3. Từ chối trùng tên tham số.
4. Từ chối địa chỉ sai định dạng (không bắt đầu bằng D/DM, vượt 32767, bit ngoài 0–15).
5. Từ chối kiểu dữ liệu không khớp địa chỉ (Bool ↔ bit).
6. Tạo / xoá recipe, từ chối xoá recipe mặc định.
7. Từ chối tạo recipe trùng tên.
8. Lưu / đọc / ghi đè giá trị recipe.
9. Đọc đúng kiểu Int16 / Int32 / Float / Bool.
10. Ghi đúng kiểu Int16 / Int32 / Float / Bool.
11. Từ chối đọc tham số chỉ ghi.
12. Từ chối ghi tham số chỉ đọc.
13. Từ chối giá trị sai kiểu dữ liệu.
14. Áp dụng recipe — bỏ qua tham số chỉ đọc.

Chạy:

```powershell
dotnet run --project ClassLibraryMT.Plc.Settings.Tests\ClassLibraryMT.Plc.Settings.Tests.csproj -c Debug
```
