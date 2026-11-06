---
name: tab-chinh-plc
overview: Thêm tab "Chỉnh PLC" cho TanTienQR gồm webview truy cập IP camera (http://<Camera.Ip>/monitor), khung tạo Recipe, bảng custom các giá trị PLC (Name / Ô nhớ / loại Read-Write) và đọc/ghi PLC thật. Dữ liệu lưu trong SQLite riêng logs/plc.db, tab hiển thị với quyền EditConfig (Operator trở lên).
todos:
  - id: plc-lib-project
    content: Tạo lib ClassLibraryMT.Plc (net10.0, AOT-safe) + thêm vào VisionF.slnx
    status: completed
  - id: plc-models
    content: Viết PlcModels (PlcParameter, PlcRecipe, PlcValueType, PlcAccessMode, Result)
    status: completed
  - id: plc-store
    content: Viết PlcStore (SQLite logs/plc.db, schema PlcParams/Recipes/RecipeValues/SchemaVersion, migration)
    status: completed
  - id: plc-service
    content: Viết IPlcSettingsService + PlcSettingsService (CRUD param, recipe, đọc/ghi PLC qua OmronPlc)
    status: completed
  - id: plc-config
    content: "Cấu hình: appsettings.json section PlcSettings + AppConfigModels + ConfigStore giữ section + AddPlcSettings DI"
    status: completed
  - id: plc-viewmodel
    content: Viết PlcSettingsViewModel + row viewmodels (bảng custom param, recipe, lệnh đọc/ghi)
    status: completed
  - id: plc-view
    content: Tạo PlcSettingsView.axaml (webview camera + khung recipe + bảng tham số)
    status: completed
  - id: plc-tab-integration
    content: "Tích hợp tab: AppSection.PlcSettings, MainViewModel.Tabs, MainWindow segtab + sidebar + content"
    status: completed
  - id: plc-logging
    content: Ghi log thao tác Plc.Read/Write/ApplyRecipe/Param CRUD vào UserActions
    status: completed
  - id: plc-tests
    content: Viết ClassLibraryMT.Plc.Tests (schema, CRUD, validation, recipe values, đọc/ghi fake PLC) nối với PLC giả lập ở 127.0.0.1:9600 có sẵn
    status: completed
  - id: plc-docs
    content: Viết docs/plc-settings.md
    status: completed
isProject: false
---

## Quyết định đã chốt

- Lưu trữ: SQLite riêng `logs/plc.db` (bảng `PlcParams` + `Recipes` + `RecipeValues` + `SchemaVersion`), không nhúng vào `appsettings.json`.
- Webview: trỏ `http://<Camera.Ip>/monitor`, lấy IP từ cấu hình `Camera.Ip` đã có.
- Quyền: hiển thị với `Permission.EditConfig` (Operator, Administrator, Developer).
- Kiểu dữ liệu ô nhớ: `Int16` / `Int32` / `Float` / `Bool` (bit `D12.05`) — tập con an toàn của API `OmronPlc` sẵn có.
- Không dùng thư viện HslCommunication (WinForms); tái dùng `ClassLibraryMT.Communications.Plc.OmronPlc` đã có trong repo.

## Kiến trúc

```mermaid
flowchart TD
    plcTab[Tab "Chỉnh PLC"] --> plcView[PlcSettingsView]
    plcView --> webview[NativeWebView -> http Camera.Ip/monitor]
    plcView --> recipePanel[Khung Recipe: danh sach, tao, xoa, ap dung]
    plcView --> paramsTable[Bang custom params: Name, O nho, loai Read/Write]
    recipePanel --> recipeVm[PlcSettingsViewModel]
    paramsTable --> recipeVm
    recipeVm --> plcStore[PlcStore - SQLite logs/plc.db]
    recipeVm --> plcClient[OmronPlc - doc/ghi o nho D]
    recipeVm --> actionLog[IUserActionLogger - ghi nhat ky thao tac]
```

## Phần 1: Lib `ClassLibraryMT.Plc` (mới, độc lập, AOT-safe)

Tạo project `ClassLibraryMT.Plc/ClassLibraryMT.Plc.csproj` (`net10.0`, `IsAotCompatible=true`, `GenerateDocumentationFile=true`) và thêm vào [VisionF.slnx](VisionF.slnx). Package: `Microsoft.Data.Sqlite`. ProjectReference tới `ClassLibraryMT` (để dùng `OmronPlc`) và `ClassLibraryMT.Logging` (để ghi nhật ký).

### Model — `ClassLibraryMT.Plc/PlcModels.cs`

```csharp
public enum PlcAccessMode { Read = 0, Write = 1, ReadWrite = 2 }

public enum PlcValueType { Int16 = 0, Int32 = 1, Float = 2, Bool = 3 }

public sealed class PlcParameter
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;   // "Delay Reject"
    public string Address { get; set; } = string.Empty; // "D12" hoac "D12.05"
    public PlcValueType ValueType { get; set; }
    public PlcAccessMode AccessMode { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class PlcRecipe
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
```

`PlcParameterValue` — cặp `(ParameterId, Value)` lưu trong recipe.

### Store — `ClassLibraryMT.Plc/PlcStore.cs`

Theo đúng pattern `ClassLibraryMT.Auth/AuthStore.cs`: `OpenAsync` mở connection, đặt `PRAGMA journal_mode=WAL`, `busy_timeout=5000`, `synchronous=NORMAL`, `foreign_keys=ON`, rồi `EnsureSchemaAsync`.

Schema:

| Bảng | Cột |
|---|---|
| `PlcParams` | `Id INTEGER PK AUTOINCREMENT`, `Name TEXT NOT NULL UNIQUE`, `Address TEXT NOT NULL`, `ValueType INTEGER NOT NULL`, `AccessMode INTEGER NOT NULL`, `Description TEXT NULL`, `SortOrder INTEGER NOT NULL DEFAULT 0`, `UpdatedUtc TEXT NOT NULL` |
| `Recipes` | `Id INTEGER PK AUTOINCREMENT`, `Name TEXT NOT NULL UNIQUE`, `IsDefault INTEGER NOT NULL DEFAULT 0`, `CreatedUtc TEXT NOT NULL`, `UpdatedUtc TEXT NOT NULL` |
| `RecipeValues` | `RecipeId INTEGER NOT NULL` (FK → `Recipes.Id`), `ParameterId INTEGER NOT NULL` (FK → `PlcParams.Id`), `Value TEXT NOT NULL`, `UpdatedUtc TEXT NOT NULL`, PK `(RecipeId, ParameterId)` |

Index: `IX_PlcParams_Name` (đã có UNIQUE), `IX_RecipeValues_ParameterId`.
`SchemaVersion` (`Version INTEGER PK`, `AppliedUtc TEXT`) để migration.

CRUD cần thiết: liệt kê/thêm/sửa/xoá `PlcParameter`; liệt kê/tạo/xoá/đổi-tên `PlcRecipe`; lưu/đọc giá trị recipe; `ApplyRecipeToPlcAsync` đọc từng tham số rồi ghi xuống PLC.

### Service — `ClassLibraryMT.Plc/IPlcSettingsService.cs` + `PlcSettingsService.cs`

```csharp
public interface IPlcSettingsService
{
    Task<IReadOnlyList<PlcParameter>> GetParametersAsync(CancellationToken ct = default);
    Task<Result> SaveParameterAsync(PlcParameter parameter, CancellationToken ct = default);
    Task<Result> DeleteParameterAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<PlcRecipe>> GetRecipesAsync(CancellationToken ct = default);
    Task<Result> CreateRecipeAsync(string name, CancellationToken ct = default);
    Task<Result> DeleteRecipeAsync(long id, CancellationToken ct = default);
    Task<Result> SaveRecipeValuesAsync(long recipeId, IReadOnlyDictionary<long, string> values, CancellationToken ct = default);
    Task<IReadOnlyDictionary<long, string>> GetRecipeValuesAsync(long recipeId, CancellationToken ct = default);

    Task<PlcReadResult> ReadParameterAsync(PlcParameter parameter, CancellationToken ct = default);
    Task<PlcReadResult> ReadAllAsync(CancellationToken ct = default);
    Task<PlcWriteResult> WriteParameterAsync(PlcParameter parameter, string value, CancellationToken ct = default);
    Task<PlcWriteResult> ApplyRecipeAsync(long recipeId, CancellationToken ct = default);
}
```

`Result` / `PlcReadResult` / `PlcWriteResult` đặt trong `PlcModels.cs` (không phụ thuộc lib Auth để tránh kéo theo `users.db`).

Đọc/ghi dùng `OmronPlc` từ `ClassLibraryMT`:
- `Int16` → `ReadInt16` / `WriteInt16`
- `Int32` → `ReadInt32` / `WriteInt32`
- `Float` → `ReadFloat` / `WriteFloat`
- `Bool` → `ReadBool` / `WriteBool` (địa chỉ dạng `D12.05`)

`OmronPlc` không thread-safe → bọc trong `SemaphoreSlim` giống [TanTienQR/Services/PlcWriter.cs](TanTienQR/Services/PlcWriter.cs). Tuy nhiên `OmronPlc` hiện đang là singleton dùng chung với `PlcWriter`; để tránh tranh chấp, `PlcSettingsService` sẽ nhận `Func<OmronPlc>` / `IPlcClientFactory` tạo client riêng cho thao tác thủ công (đọc/ghi thử), tách biệt khỏi luồng heartbeat.

### DI — `ClassLibraryMT.Plc/PlcServiceCollectionExtensions.cs`

```csharp
services.AddPlcSettings(configuration);   // doc section "PlcSettings": { "DbPath": "logs/plc.db" }
```

Bind thủ công (AOT-safe) như `AuthConfigReader`.

## Phần 2: Cấu hình TanTienQR

[TanTienQR/appsettings.json](TanTienQR/appsettings.json) — thêm:

```json
"PlcSettings": {
  "DbPath": "logs/plc.db"
}
```

[TanTienQR/Services/AppConfigModels.cs](TanTienQR/Services/AppConfigModels.cs) — thêm `PlcSettingsConfig` + vào `AppConfigSnapshot` và `AppConfigJsonContext`; sửa [TanTienQR/Services/ConfigStore.cs](TanTienQR/Services/ConfigStore.cs) để giữ nguyên section `PlcSettings` khi lưu (giống cách đang giữ `Logging`).

[TanTienQR/Services/TanTienAppServices.cs](TanTienQR/Services/TanTienAppServices.cs) — gọi `services.AddPlcSettings(configuration)` và đăng ký `PlcSettingsViewModel` singleton.

## Phần 3: Tab "Chỉnh PLC" trong MainWindow

### Enum — [TanTienQR/Models/AppSection.cs](TanTienQR/Models/AppSection.cs)
Thêm `PlcSettings = 5`.

### Quyền — [ClassLibraryMT.Auth/AuthModels.cs](ClassLibraryMT.Auth/AuthModels.cs)
Dùng quyền có sẵn `Permission.EditConfig`, không thêm quyền mới. Worker không thấy tab.

### ViewModel — `TanTienQR/ViewModels/PlcSettingsViewModel.cs`

- `ObservableCollection<PlcParamRowViewModel> ParameterRows` — bảng custom
- `ObservableCollection<PlcRecipeRowViewModel> Recipes` — danh sách recipe
- Lệnh: `LoadParametersCommand`, `AddParameterCommand`, `EditParameterCommand`, `DeleteParameterCommand`, `ReadParameterCommand`, `WriteParameterCommand`, `ReadAllCommand`, `CreateRecipeCommand`, `DeleteRecipeCommand`, `SaveRecipeValuesCommand`, `ApplyRecipeCommand`
- Mỗi dòng có: `Name`, `Address`, `ValueTypeText`, `AccessModeText`, `CurrentValue`, `StatusText`, `IsError`
- Mọi thao tác ghi xuống PLC đều ghi log `Plc.Read` / `Plc.Write` / `Plc.ApplyRecipe` qua `IUserActionLogger`

### View — `TanTienQR/Views/PlcSettingsView.axaml` (+ `.axaml.cs`)

Bố cục 3 vùng theo file tham khảo:

```
Grid RowDefinitions="Auto,Auto,*"
  Row 0: Tiêu đề "Chỉnh PLC" + nút "Tải lại", "Đọc tất cả", "Ghi tất cả"
  Row 1: Grid 2 cột
    Cột trái (60%): Border panel
        NativeWebView x:Name="CameraBrowser"  (Source bind CameraMonitorUrl)
        Thanh địa chỉ: URL + nút "Tải lại" + "Mở trình duyệt"
    Cột phải (40%): StackPanel
        Panel "Khung Recipe"
          ComboBox chọn recipe + nút "Mới" + "Xoá" + "Áp dụng xuống PLC"
          ItemsControl danh sách param của recipe (ô nhập giá trị)
        Panel "Thông số PLC" (bảng custom)
          ItemsControl: Name | Ô nhớ | Loại | Read/Write | Giá trị | Thao tác
          Nút "+ Tham số"
```

Dùng lại style hiện có: `panel`, `soft`, `primary`, `badge`, `field`, `fieldLabel`, `chip`, `mono` — xem [TanTienQR/Views/AccountsView.axaml](TanTienQR/Views/AccountsView.axaml) làm mẫu bảng.

### Tích hợp — [TanTienQR/ViewModels/MainViewModel.Tabs.cs](TanTienQR/ViewModels/MainViewModel.Tabs.cs)

- Thêm `IsPlcSettingsVisible` (=`CurrentSection == AppSection.PlcSettings && CanEditConfig`)
- Thêm `CanEditPlc => _session.Can(Permission.EditConfig)` và cờ quyền tương ứng
- `ShowPlcSettingsTabCommand` — chuyển tab + nạp danh sách tham số
- `PageTitle` / `PageSubtitle` thêm case `AppSection.PlcSettings`

### MainWindow — [TanTienQR/Views/MainWindow.axaml](TanTienQR/Views/MainWindow.axaml)

- Thêm segtab "Chỉnh PLC" (icon slider/gear), `IsVisible="{Binding CanEditPlc}"`
- Thêm nút sidebar "Chỉnh PLC" trong nhóm "CẤU HÌNH" (cùng nhóm với "Cấu hình")
- Thêm `<views:PlcSettingsView IsVisible="{Binding IsPlcSettingsVisible}" />` vào vùng nội dung

### MainViewModel — [TanTienQR/ViewModels/MainViewModel.cs](TanTienQR/ViewModels/MainViewModel.cs)
Inject `PlcSettingsViewModel` (như `AccountsViewModel`), expose property `PlcSettings` để bind.

### MainWindow.axaml.cs — camera URL
`CameraMonitorUrl` tính từ `CameraOptions.Ip`: `http://{Ip}/monitor`. Nếu `Camera.Ip` rỗng → hiện placeholder "Chưa cấu hình IP camera".

## Phần 4: Ghi log thao tác

Dùng `IUserActionLogger` sẵn có (lib Logging) — không cần sửa lib Logging:
- `Plc.Read` — đọc ô nhớ
- `Plc.Write` — ghi ô nhớ
- `Plc.ApplyRecipe` — áp dụng recipe
- `Plc.Param.Create` / `Plc.Param.Update` / `Plc.Param.Delete` — CRUD bảng custom

## Phần 5: Test

`ClassLibraryMT.Plc.Tests/` (runner tự viết, theo mẫu `ClassLibraryMT.Logging.Tests`):
1. Tạo schema đúng: đủ 4 bảng, index, cột.
2. CRUD tham số: thêm, sửa, xoá, từ chối trùng `Name`.
3. Từ chối `Address` sai định dạng (không bắt đầu bằng D/DM, vượt 32767).
4. Từ chối tạo recipe trùng tên; xoá recipe mặc định bị từ chối.
5. Lưu/đọc giá trị recipe đúng số lượng và nội dung.
6. Đọc/ghi giá trị tham số với PLC giả (fake `IPlcClient`): đúng kiểu Int16/Int32/Float/Bool; bool dùng địa chỉ bit.

## Phần 6: Tài liệu

`docs/plc-settings.md` — mô tả tab Chỉnh PLC, cấu trúc bảng tham số, recipe, vị trí `logs/plc.db`, kiểu dữ liệu ô nhớ, quyền truy cập, cách thêm tham số mới.

## Rủi ro & ghi chú

- `OmronPlc` không thread-safe và đang là singleton cho heartbeat -> thao tác thủ công dùng **client riêng** qua factory, không dùng chung instance.
- Ghi PLC là thao tác nguy hiểm: cần xác nhận trước khi ghi (`ApplyRecipe` hiện dialog xác nhận danh sách sẽ ghi).
- `Pbkdf2`/hash không liên quan; lib `Plc` không phụ thuộc lib `Auth`.
- AOT: không dùng reflection, JSON serialize dùng source-generated context.
- `NativeWebView` yêu cầu WebView2 Runtime trên Windows (đã dùng ở tab Sản xuất nên không phát sinh yêu cầu mới).
