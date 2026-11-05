---
name: PLC result and heartbeat to DemoQR
overview: "Nối DemoQR với PLC Omron theo đúng pattern dự án TApp: ghi kết quả Pass/Fail xuống D12 (1/0), ghi trạng thái Ready xuống D16 mỗi vòng (0/1) để đồng thời làm cơ chế phát hiện kết nối PLC, và xây AppState theo mẫu TApp (e_AppState, CameraStatus, PLCStatus) làm trạng thái toàn app."
todos:
  - id: app-state
    content: "Tạo ClassLibraryMT/DeviceStatus/: e_AppState, CameraStatus, PLCStatus, GlobalVariables, AppError"
    status: completed
  - id: plc-config
    content: Tạo PlcOptions.cs + PlcServiceCollectionExtensions.cs (bind tay AOT-safe, validate, singleton OmronPlc)
    status: completed
  - id: plc-writer
    content: Tạo PlcWriter.cs — wrapper serialize ghi D12 (pass/fail) và D16 (ready), cập nhật PLCStatus
    status: completed
  - id: plc-heartbeat
    content: Tạo PlcHeartbeatService.cs — ghi D16 mỗi 1s với giá trị Ready hiện tại, cập nhật PLCStatus
    status: completed
  - id: worker-plc
    content: "Sửa CameraRDWorker: NoRead/FAIL → D12=0, kết quả Checkee → D12=1/0, cập nhật CameraStatus"
    status: completed
  - id: wire-di
    content: "Sửa AppServices + Program: đăng ký AddPlc, validate PlcOptions, set AppState"
    status: completed
  - id: config-files
    content: Thêm mục Plc vào appsettings.json + appsettings.Development.json
    status: completed
  - id: verify
    content: ""
    status: completed
isProject: false
---

## 1. Yêu cầu đã chốt

| Hành vi | Quy tước |
|---|---|
| Camera trả về **mã hợp lệ** | Ghi `D12 = 1` (Pass) |
| Camera trả về **NoRead / FAIL** | Ghi `D12 = 0` (Fail) |
| Mã sai định dạng / trùng | Ghi `D12 = 0` (Fail) |
| `D16` (Ready) | **Luôn ghi mỗi vòng lặp**, giá trị `0` hoặc `1` theo `e_AppState` |
| `D12` địa chỉ | `D12` (mặc định, cấu hình được) |
| `D16` địa chỉ | `D16` (mặc định, cấu hình được) |
| Cấu hình | Mục `Plc` trong `appsettings.json` |

Quyết định đã chốt qua hỏi:
- **D16 theo đúng pattern TApp**: luôn ghi 0/1 mỗi vòng, **không** chỉ ghi khi Ready
- `AppState` theo mẫu TApp: enum `e_AppState` + `CameraStatus` + `PLCStatus`
- Ghi **trực tiếp** xuống D12 khi camera gửi dữ liệu
- Chỉ chuỗi `"NoRead"` là fail; mã không parse được **cũng** ghi D12 = 0 (khớp TApp: `FormatError` → `Fail`)

## 2. Pattern tham khảo từ dự án TApp

Nguồn: `C:\Users\THUC\source\repos\Mte_CShap\TApp` và `TTManager\PLCHelpers\OmronPLC.cs`.

### 2.1. Enum kết quả PLC — khớp yêu cầu 1 = Pass

```csharp
// TApp/Helpers/OmronPLCHelper.cs
public enum e_PLC_Result { Pass = 1, Fail = 0 }
```

### 2.2. Vòng lặp heartbeat — luôn ghi, giá trị 0/1

```csharp
// TTManager/PLCHelpers/OmronPLC.cs - WK_Update_DoWork
while (!WK_Update.CancellationPending)
{
    Thread.Sleep(Time_Update);
    OperateResult write = plc.Write(PLC_Ready_DM, short.Parse(Ready.ToString()));
    if (write.IsSuccess) { PLC_STATUS = PLCStatus.Connected; }
    else { PLC_STATUS = PLCStatus.Disconnect; }
}
```

Đây là điểm mấu chốt: **vòng lặp heartbeat chính là cơ chế phát hiện kết nối PLC** — `write.IsSuccess` quyết định `PLC_STATUS`.

### 2.3. `Ready` được set theo AppState

```csharp
// TApp/Views/Dashboard/FDashboard.cs - UpdateCountersFromPLC
if (GlobalVarialbles.CurrentAppState == e_AppState.Ready) omronPLC_Hsl1.Ready = 1;
else omronPLC_Hsl1.Ready = 0;
```

### 2.4. AppState tính từ trạng thái thiết bị

```csharp
// TApp/Views/Dashboard/FDashboard.cs - Render_App_Status
bool deviceDisconnected = FD_Globals.pLCStatus != PLCStatus.Connected || FD_Globals.CameraStatus != CameraStatus.Connected;
e_AppState state;
if (_lastDeactiveState) state = e_AppState.Deactive;
else if (deviceDisconnected) state = e_AppState.Stopped;   // Mất kết nối thiết bị
else state = ipBatchNo.Enabled ? e_AppState.Editing : e_AppState.Ready;
GlobalVarialbles.CurrentAppState = state;
```

### 2.5. Camera trả "FAIL" → gửi Fail xuống PLC

```csharp
// TApp/Views/Dashboard/FDashboard.cs
if (string.Equals(data, "FAIL", StringComparison.OrdinalIgnoreCase))
{
    Send_Result_To_PLC(e_PLC_Result.Fail);
    return;
}
```

**Lưu ý:** trong TApp, chuỗi camera báo lỗi đọc là `"FAIL"`; yêu cầu của bạn là `"NoRead"`. Sẽ hỗ trợ **cả hai** để tương thích.

## 3. AppState — trạng thái toàn app (theo mẫu TApp)

Đặt trong `ClassLibraryMT` để `T8T` hoặc module khác dùng lại được. `ClassLibraryMT` đã có cấu trúc domain-based (`Communications/`, `Configuration/`, `Licensing/`) nên thêm thư mục `DeviceStatus/`.

### 3.1. [ClassLibraryMT/DeviceStatus/AppStateEnums.cs](ClassLibraryMT/DeviceStatus/AppStateEnums.cs) — mới

```csharp
namespace ClassLibraryMT.DeviceStatus;

/// Trạng thái vận hành của app (theo mẫu TApp).
public enum e_AppState
{
    Initializing = 0,  // Khởi động ứng dụng
    Ready = 1,         // Đang chạy, sẵn sàng
    Stopped = 2,       // Mất kết nối thiết bị -> dừng máy
    Error = 3,         // Lỗi
}

/// Trạng thái kết nối camera (theo mẫu TApp).
public enum CameraStatus
{
    Disconnected = 0,
    Connected = 1,
    Error = 2,
    Reconnecting = 3,
}

/// Trạng thái kết nối PLC (theo mẫu TApp).
public enum PLCStatus
{
    Connected = 0,
    Disconnect = 1,
}
```

### 3.2. [ClassLibraryMT/DeviceStatus/AppError.cs](ClassLibraryMT/DeviceStatus/AppError.cs) — mới

```csharp
namespace ClassLibraryMT.DeviceStatus;

/// Mã lỗi chuẩn hóa.
///
/// CHỈ chứa lỗi thiết bị / ổn định. Lỗi dữ liệu mã quét (QR sai định dạng,
/// NoRead) là lỗi của code xử lý -> chỉ log, KHÔNG đưa vào AppState.
public enum AppErrorCode
{
    None = 0,
    CameraDisconnected,
    PlcUnreachable,
    CheckeeApiFailed,
    LicenseInvalid,
    Unknown,
}

/// Một lỗi xảy ra trong app.
public sealed record AppError(AppErrorCode Code, string Message, DateTime OccurredUtc);
```

### 3.3. [ClassLibraryMT/DeviceStatus/GlobalVariables.cs](ClassLibraryMT/DeviceStatus/GlobalVariables.cs) — mới

Đặt tên `GlobalVariables` (đúng chính tả, khác `GlobalVarialbles` viết sai ở TApp):

```csharp
namespace ClassLibraryMT.DeviceStatus;

/// Registry trạng thái toàn app.
public static class GlobalVariables
{
    /// Trạng thái vận hành hiện tại.
    public static e_AppState CurrentAppState { get; set; } = e_AppState.Initializing;

    /// Trạng thái camera.
    public static CameraStatus CameraStatus { get; set; } = CameraStatus.Disconnected;

    /// Trạng thái PLC.
    public static PLCStatus PLCStatus { get; set; } = PLCStatus.Disconnect;

    /// Giá trị ghi xuống D16 (0 hoặc 1), theo CurrentAppState.
    public static int Ready { get; set; } = 0;

    /// License hợp lệ.
    public static bool LicenseValid { get; set; }

    /// Danh sách lỗi gần nhất (mới nhất đứng đầu).
    public static IReadOnlyList<AppError> Errors => _errors;
}
```

### 3.4. [ClassLibraryMT/DeviceStatus/AppStateManager.cs](ClassLibraryMT/DeviceStatus/AppStateManager.cs) — mới

Bộ ghi trạng thái, **thread-safe**. Nhiều luồng cùng cập nhật: socket loop của camera, worker xử lý mã, heartbeat service.

- Dùng `lock` quanh cập nhật trạng thái + danh sách lỗi (tần suất thấp, không cần lock-free)
- `Volatile.Write`/`Volatile.Read` cho các cờ đọc từ luồng khác — pattern đã dùng sẵn trong [CodeReader.cs](ClassLibraryMT/Communications/CodeReaderProtocol/CodeReader.cs)
- Danh sách lỗi giới hạn **50 phần tử**, **gộp lỗi trùng** (cùng `AppErrorCode` chỉ giữ entry mới nhất)

API:
```csharp
public static void SetCameraStatus(CameraStatus status);
public static void SetPlcStatus(PLCStatus status);
public static void SetAppState(e_AppState state);   // tự cập nhật Ready
public static void SetLicenseValid(bool valid);
public static void AddError(AppErrorCode code, string message);
public static void ClearErrors();
public static void RecalculateAppState();
```

### 3.5. Luật tính `e_AppState` và `Ready`

`RecalculateAppState()` — một chỗ duy nhất để không lệch logic:

```
Nếu thiếu license                    -> Error
Nếu Camera != Connected              -> Stopped
Nếu PLC != Connected                 -> Stopped
Nếu đang cấu hình (chưa có batch)    -> Initializing
Nếu tất cả OK                        -> Ready
```

`Ready` (giá trị ghi D16) = `CurrentAppState == e_AppState.Ready ? 1 : 0` — **tính tự động**, không ai set thủ công. Điều này tránh lệch logic như ở TApp (TApp phải set `Ready` thủ công trong `UpdateCountersFromPLC`).

### 3.6. ánh xạ trạng thái

| Nguồn | Ghi vào |
|---|---|
| `Reader.ClientCallback` → `Connected` | `CameraStatus = Connected` |
| `Reader.ClientCallback` → `Disconnected` | `CameraStatus = Disconnected` + lỗi `CameraDisconnected` |
| `Reader.ClientCallback` → `Reconnecting` | `CameraStatus = Reconnecting` |
| Heartbeat ghi D16 thành công | `PLCStatus = Connected` |
| Heartbeat ghi D16 lỗi | `PLCStatus = Disconnect` + lỗi `PlcUnreachable` |
| `CheckeeClient.ActivateAsync` thất bại | lỗi `CheckeeApiFailed` |
| License hợp lệ | `LicenseValid = true` |
| ~~QR sai định dạng~~ | **chỉ log, không vào AppState** (lỗi của code) |

**Lỗi dữ liệu mã quét KHÔNG vào AppState.** `AppState` chỉ chứa trạng thái ổn định của thiết bị và app. QR sai định dạng / NoRead là kết quả xử lý của code → chỉ ghi log.

## 4. Thay đổi DemoQR

### 4.1. [DemoQR/PlcOptions.cs](DemoQR/PlcOptions.cs) — mới

```csharp
namespace QRDemoCodeBackend;

// Cấu hình kết nối PLC Omron (đọc từ appsettings.json, mục "Plc").
internal sealed class PlcOptions
{
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9600;
    public byte RemoteNode { get; set; } = 0;   // Node PLC (DA1), thường là octet cuối IP
    public byte LocalNode { get; set; } = 1;    // Node PC (SA1)
    public int TimeoutMs { get; set; } = 1000;
    public int Retries { get; set; } = 0;

    // Địa chỉ vùng D
    public string ResultAddress { get; set; } = "D12";   // 1 = Pass, 0 = Fail
    public string ReadyAddress { get; set; } = "D16";    // 1 = Ready, 0 = chưa sẵn sàng
    public int RefreshIntervalMs { get; set; } = 1000;   // chu kỳ ghi D16
}
```

### 4.2. [DemoQR/PlcServiceCollectionExtensions.cs](DemoQR/PlcServiceCollectionExtensions.cs) — mới

Đăng ký DI theo đúng pattern `AddCheckee` (bind tay qua `ConfigReader` để tương thích AOT):

- `AddOptions<PlcOptions>().Configure<IConfiguration>(...)` + `.Validate(...)` + `.ValidateOnStart()`
- Đăng ký `OmronPlc` là **singleton** (một kết nối UDP dùng chung)
- Đăng ký `PlcWriter` là singleton — wrapper serialize thao tác ghi

### 4.3. [DemoQR/PlcWriter.cs](DemoQR/PlcWriter.cs) — mới

Wrapper mỏng quanh `OmronPlc`:
- `Task WriteResultAsync(bool pass, CancellationToken ct)` — ghi `1`/`0` vào `ResultAddress`
- `Task WriteReadyAsync(int ready, CancellationToken ct)` — ghi `0`/`1` vào `ReadyAddress`
- Serialize thao tác ghi bằng `SemaphoreSlim(1,1)` (FINS/UDP đồng bộ, không thread-safe)
- Cập nhật `GlobalVariables.PLCStatus` theo kết quả ghi
- Log lỗi kèm `ErrorCode`/`Message` từ `PlcResult` (không làm crash app khi PLC mất kết nối)

### 4.4. [DemoQR/PlcHeartbeatService.cs](DemoQR/PlcHeartbeatService.cs) — mới

`BackgroundService` chạy vòng lặp `RefreshIntervalMs` (mặc định 1s), **tương đương `WK_Update` của TApp**:

- Luôn ghi `GlobalVariables.Ready` (0 hoặc 1) xuống `ReadyAddress` — **giống hệt pattern TApp**
- Thành công → `PLCStatus = Connected`; lỗi → `PLCStatus = Disconnect`
- Dùng `PeriodicTimer` (BCL, không cần package)
- Bỏ qua lần ghi lỗi (không spam log), đếm số lần lỗi liên tiếp để log cảnh báo một lần khi vượt ngưỡng

**Vì sao không bị vòng lẫn:** `Ready` được tính từ `e_AppState`, mà `e_AppState` phụ thuộc `PLCStatus`. Nhưng `PLCStatus` được set từ **kết quả chính lần ghi này**, không phải từ điều kiện trước đó. Vòng lặp luôn chạy, nên lần đầu sẽ ghi (giá trị 0) → xác định được kết nối → `PLCStatus = Connected` → `e_AppState` chuyển `Ready` → lần sau ghi 1. Đúng như TApp.

### 4.5. [DemoQR/CameraRDWorker.cs](DemoQR/CameraRDWorker.cs) — sửa

Trong `ProcessQueueAsync` (tương đương `FDashboard.HandleQRData` của TApp):

| Trường hợp | Ghi D12 | Ghi log |
|---|---|---|
| `"NoRead"` hoặc `"FAIL"` | `0` (Fail) | warning |
| Mã không parse được | `0` (Fail) | warning |
| Mã trùng (`IsDuplicate`) | `0` (Fail) | info |
| `ActivateAsync` thất bại | `0` (Fail) | error |
| `ActivateAsync` thành công | `1` (Pass) | info |

Trong `OnCameraEvent`: cập nhật `GlobalVariables.CameraStatus` theo `Connected`/`Disconnected`/`Reconnecting`.
Constructor injection: thêm `PlcWriter`.

### 4.6. [DemoQR/AppServices.cs](DemoQR/AppServices.cs) — sửa

- Thêm `services.AddPlc(configuration)` (extension mới)
- Đảm bảo thứ tự: `AddPlc` trước khi đăng ký worker

### 4.7. [DemoQR/Program.cs](DemoQR/Program.cs) — sửa

- Validate options PLC trong `TryValidateOptions`:
```csharp
_ = services.GetRequiredService<IOptions<PlcOptions>>().Value;
```
- Set `GlobalVariables.LicenseValid = true` sau khi license hợp lệ
- `RecalculateAppState()` sau khi host build xong

### 4.8. [DemoQR/appsettings.json](DemoQR/appsettings.json) và [appsettings.Development.json](DemoQR/appsettings.Development.json) — sửa

Thêm mục `Plc`:
```json
"Plc": {
  "Ip": "192.168.1.10",
  "Port": 9600,
  "RemoteNode": 0,
  "LocalNode": 1,
  "TimeoutMs": 1000,
  "Retries": 0,
  "ResultAddress": "D12",
  "ReadyAddress": "D16",
  "RefreshIntervalMs": 1000
}
```

### 4.9. [DemoQR/QrParser.cs](DemoQR/QrParser.cs) — sửa

Thêm hằng + hàm nhận diện chuỗi lỗi của camera:
```csharp
// Camera bao loi doc ma: "NoRead" (yeu cau cua ban) hoac "FAIL" (dung trong TApp).
public const string NoReadToken = "NoRead";
public const string FailToken = "FAIL";

public static bool IsNoRead(string raw)
{
    var text = raw.Trim();
    return string.Equals(text, NoReadToken, StringComparison.OrdinalIgnoreCase)
        || string.Equals(text, FailToken, StringComparison.OrdinalIgnoreCase);
}
```

Chuỗi `"NoRead"`/`"FAIL"` **không** parse được theo regex hiện tại (`/01/...` hoặc `(01)...`), nên phải bắt **trước** khi gọi `QrParser.TryParse`.

## 5. Vấn đề an toàn luồng

`CameraRDWorker.OnCameraEvent` được gọi từ **socket receive loop** của `Reader`. Nếu ghi PLC (I/O đồng bộ, timeout 1s) ngay trong callback này, nó sẽ **block** việc đọc socket → mất frame. Vì vậy:
- Ghi kết quả thực hiện trong `ProcessQueueAsync` (đã chạy trên luồng riêng qua `Channel`) — an toàn
- Heartbeat chạy trong `BackgroundService` riêng — an toàn
- Cập nhật `GlobalVariables` trong `OnCameraEvent` chỉ gán biến → tức thì, không block

## 6. Kiểm tra

1. `dotnet build VisionF.slnx -c Debug` và `-c Release` — 0 error, 0 warning mới
2. `dotnet publish DemoQR\DemoQR.csproj -c Release -r win-x64` — phải sạch, không `IL2026`/`IL3050`
3. Smoke test: chạy `DemoQR.exe` với license, xác nhận heartbeat ghi D16 mỗi giây (0 khi chưa sẵn sàng, 1 khi camera + PLC OK)
4. Test với PLC thật: đặt `Plc:Ip` đúng IP PLC, quét mã → xác nhận `D12 = 1`; gửi chuỗi `NoRead` tới cổng camera → xác nhận `D12 = 0`

## 7. Phạm vi

- `ClassLibraryMT`: thêm thư mục `DeviceStatus/` (không sửa code cũ)
- `DemoQR`: thêm 4 file mới + sửa 4 file
- Không sửa `T8T`
- Không thêm package mới (giữ nguyên dependency set, giữ AOT sạch)
