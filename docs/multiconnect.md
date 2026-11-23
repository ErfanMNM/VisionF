# MultiConnect Gateway

Gateway chuyển các giao thức thông dụng (**Serial/RS232**, **TCP client**, **UDP**) sang **TCP server**, để có thể dùng thay cho camera của TanTienQR mà không phải sửa code app.

## Ý tưởng

Hiện tại app đọc mã bằng `CameraSignalWorker`: nó là **TCP client**, kết nối tới `Camera:Ip:Camera:Port` và đọc frame text UTF-8 kết thúc bằng `\r` hoặc `\n`.

MultiConnect mở ra các **TCP server** phát đúng dạng frame đó. Vì vậy:

- Giữ nguyên camera thật: trỏ `Camera` tới camera.
- Dùng gateway: đổi `Camera:Ip` / `Camera:Port` tới một `ListenIp:ListenPort` của kênh gateway.
- Muốn "local transfer" từ camera thật sang gateway: khai báo kênh `Type = "TcpClient"` trỏ tới IP/port của camera thật, rồi cho app kết nối vào `ListenPort` của kênh đó.

```
[Serial COM3] ─┐
[TCP camera ] ─┼─> MultiConnect ──> TCP server :9101 ──> TanTienQR (Camera:Port = 9101)
[UDP :9100   ] ─┘
```

Mỗi kênh có thể phục vụ **nhiều client TCP cùng lúc** (fan-out). Các kênh chạy độc lập: kênh này mất kết nối/không có COM port không làm kênh khác ngừng, và tự thử lại sau `ReconnectDelayMs`.

## Cấu hình

Đặt trong `appsettings.json` (đã có mẫu, mặc định `Enabled = false`):

```json
"MultiConnect": {
  "Enabled": true,
  "ReconnectDelayMs": 3000,
  "MaxFrameLength": 512,
  "Channels": [
    {
      "Name": "Serial COM3",
      "Type": "Serial",
      "Enabled": true,
      "ListenIp": "127.0.0.1",
      "ListenPort": 9101,
      "PortName": "COM3",
      "BaudRate": 9600,
      "DataBits": 8,
      "Parity": "None",
      "StopBits": 1
    },
    {
      "Name": "Camera forward",
      "Type": "TcpClient",
      "Enabled": true,
      "ListenIp": "127.0.0.1",
      "ListenPort": 9102,
      "Host": "192.168.1.50",
      "Port": 9000
    },
    {
      "Name": "UDP 9100",
      "Type": "Udp",
      "Enabled": true,
      "ListenIp": "127.0.0.1",
      "ListenPort": 9103,
      "UdpListenIp": "127.0.0.1",
      "UdpListenPort": 9100
    }
  ]
}
```

### Ý nghĩa

| Trường | Ý nghĩa |
|---|---|
| `Enabled` (section) | Bật/tắt toàn gateway. Mặc định `false`. |
| `ReconnectDelayMs` | Trễ trước khi thử lại khi một kênh lỗi/mất kết nối. |
| `MaxFrameLength` | Giới hạn số ký tự của một frame, tránh buffer phình khi thiết bị gửi rác. |
| `Channels[]` | Danh sách kênh. `ListenPort` phải duy nhất giữa các kênh đang bật. |
| `Name` | Tên hiển thị trong log. |
| `Type` | `Serial` (có thể viết `Com`), `TcpClient` (`Tcp`, `Forward`) hoặc `Udp`. |
| `ListenIp`, `ListenPort` | Địa chỉ/port TCP server của kênh. Để `0.0.0.0` nếu cần nghe mọi card mạng. |
| `PortName`, `BaudRate`, `DataBits`, `Parity`, `StopBits` | Chỉ dùng cho `Serial`. `Parity`: `None`, `Even`, `Odd`, `Mark`, `Space`. |
| `Host`, `Port` | Chỉ dùng cho `TcpClient` — địa chỉ thiết bị cần chuyển tiếp. |
| `UdpListenIp`, `UdpListenPort` | Chỉ dùng cho `Udp` — nơi nhận datagram. |

Frame từ Serial/TCP client được tách bởi `\r` hoặc `\n`; frame UDP là nội dung datagram (cũng tách tiếp nếu có nhiều dòng). Khi phát ra TCP, gateway luôn nối thêm `\r\n`.

> Form **Cấu hình** trong app chỉ hiển thị 3 field chung (`Enabled`, `ReconnectDelayMs`, `MaxFrameLength`). Mảng `Channels` được giữ nguyên trong `appsettings.json` khi lưu cấu hình từ giao diện (ConfigStore merge theo key lạ), nên bạn sửa trực tiếp file JSON cho các kênh.

## Chạy như module trong app

Đã đăng ký sẵn trong `TanTienAppServices.AddTanTienServices`. Chỉ cần bật `MultiConnect:Enabled = true`, khởi động lại app. Gateway chạy như `BackgroundService`.

## Tab giám sát trong app

Tab **MultiConnect** (menu "Thêm") hiển thị 2 cột cạnh nhau:

- **Dữ liệu vào**: 50 frame gần nhất do thiết bị gửi vào gateway (mới nhất ở đầu).
- **Dữ liệu ra**: 50 frame gần nhất gateway phát cho các client TCP (có số client đang nhận).

Mỗi dòng có giờ, tên kênh và nội dung frame. Thanh trạng thái cho biết gateway đang bật/tắt, số kênh đang chạy và tổng frame đã nhận. Nút **Xoà dữ liệu** chỉ xoá bộ đệm hiển thị, không ảnh hưởng luồng dữ liệu.

Gateway giữ tối đa 500 dòng trong bộ đệm nội bộ; UI lấy theo `Id` mỗi 250 ms nên không bị trễ luồng hay cập nhật collection từ background thread.

> Tab này chỉ thấy dữ liệu của gateway **chạy trong app**. Nếu bạn chạy `MultiConnectGateway` như tool riêng, app không có kết nối IPC tới tool đó nên tab sẽ trống — hãy chạy gateway trong app (`MultiConnect:Enabled = true`) để giám sát.

## Chạy như tool độc lập

```powershell
dotnet run --project tools\MultiConnectGateway\MultiConnectGateway.csproj -c Debug
```

Tool đọc `appsettings.json` ngay trong thư mục chạy (đã copy ra output). File mẫu bật sẵn 1 kênh UDP `9100 -> 9101`; hai kênh Serial/TCP client để sẵn nhưng tắt. Nhấn `Ctrl+C` để dừng.

App không cần biết tool này tồn tại — chỉ cần trỏ `Camera:Port` tới port của kênh.

## Kiểm tra nhanh

```powershell
dotnet run --project tools\MultiConnectTests\MultiConnectTests.csproj -c Debug
```

Test tự dựng upstream TCP + UDP, bật gateway bằng cấu hình in-memory, rồi kiểm tra:

- 2 client cùng kết nối một TCP output đều nhận được frame (fan-out).
- Frame gửi vào UDP source xuất hiện ở TCP output của kênh UDP.

Kết quả mong đợi: `KET QUA: DAT`.

Gửi thử frame bằng PowerShell:

```powershell
$c = [Net.Sockets.TcpClient]::new('127.0.0.1', 9101)
$s = $c.GetStream()
$b = [Text.Encoding]::UTF8.GetBytes("01012345678901234567`r`n")
$s.Write($b, 0, $b.Length)
```

## Giới hạn

- Serial cần driver COM port và quyền truy cập cổng; nếu cổng bị chiếm, kênh sẽ log cảnh báo và thử lại định kỳ.
- Không phải plugin load DLL động: các loại nguồn được biên dịch cố định (`Serial`, `TcpClient`, `Udp`) và chọn theo cấu hình — phù hợp với Native AOT của app.
- Gateway chuyển **text/frame**, không giải mã giao thức cấp cao của từng thiết bị. Nếu thiết bị cần lệnh trigger hoặc handshake đặc biệt, cần mở rộng source tương ứng.
