# Module máy in G10

Module này lấy mã trong `PoCodes` của PO đang chạy, chỉ chọn mã chưa `ActivatedUtc` và chưa có trạng thái in hoàn tất. Trạng thái in được lưu riêng trong cùng file SQLite của PO ở bảng `PrintCodeStates`, nên không làm thay đổi ý nghĩa của `ActivatedUtc`.

## Bật và cấu hình

1. Mở **Quản trị → Cấu hình → Máy in · G10**.
2. Bật **Bật module máy in**, nhập IP/port (mặc định `192.168.0.3:6007`), kích thước lô và dung lượng buffer.
3. Lưu cấu hình và khởi động lại app vì các thông số kết nối được nạp lúc khởi động.
4. Mở tab **Máy in**, kiểm tra trạng thái kết nối, mực và buffer.

Tab Máy in chỉ cho kết nối khi file license hợp lệ có entitlement `printer`. Entitlement này là gói **Máy in + gói bán kèm**, cấp cùng một license; không có license riêng cho phần bán kèm.

## Màn hình máy in trong tab Máy in

Trên Windows, tab **Máy in** nhúng giao diện RDP của G10 để thao tác trực tiếp. Vùng màn hình luôn giữ **16:9**, yêu cầu desktop 1920×1080 và hiển thị trọn nội dung nếu thiết bị trả về tỷ lệ khác. Nút **Phóng to** thu gọn cột thao tác ngay trong tab. Cửa sổ hẹp chuyển phần thao tác xuống dưới; phần này cuộn riêng, màn hình máy in không nằm trong vùng cuộn.

1. Mở **Cấu hình → Máy in · Điều khiển từ xa** và bật **Bật màn hình máy in**.
2. Điền cổng RDP, tên tài khoản của máy in và tùy chọn host riêng. Host trống dùng IP máy in đang chọn. Cổng `3389` là mặc định RDP, độc lập với cổng dữ liệu G10 `6007`; cần đối chiếu thiết bị thực tế.
3. Lưu rồi khởi động lại app để áp dụng kết nối mới.
4. Mở tab **Máy in → Đăng nhập máy in**, nhập mật khẩu và bấm **Lưu mật khẩu trên máy này**. Thông tin được lưu trong Windows Credential Manager theo người dùng Windows; mật khẩu không nằm trong `appsettings.json`.
5. Bấm **Kết nối** hoặc dùng tùy chọn tự kết nối khi mở tab. Nút **Ngắt** ngăn tự kết nối lại cho tới khi người dùng chủ động kết nối.

Trạng thái **Dữ liệu** (TCP) và **Màn hình** (RDP) hiển thị độc lập. Ngắt màn hình hoặc đổi tab không gửi START/STOP/CLR/RESET, không gửi lại mã và không đổi trạng thái mã PO. Thay đổi in hoặc bản tin trực tiếp trên HMI được đọc lại qua TCP; bản tin được cập nhật tối đa khoảng 15 giây khi kết nối hoạt động.

Màn hình nhúng được ẩn và khóa input khi đổi tab, mở menu/hộp thoại hoặc thu nhỏ cửa sổ. Đăng xuất, hết quyền máy in và thoát app ngắt phiên remote. Lỗi mạng/timeout có tối đa ba lần thử lại với khoảng chờ tăng dần; lỗi tài khoản/chứng thư dừng để chỉnh cấu hình. Windows cần tin cậy chứng thư RDP của thiết bị. Bản Linux vẫn dùng phần in TCP và hiển thị thông báo remote chưa hỗ trợ.

Chuột và bàn phím cứng do client RDP chuyển đến thiết bị. Bàn phím ảo của TanTienApp dùng cho ô nhập của ứng dụng, gồm số lượng và mật khẩu; không chuyển text từ bàn phím này vào HMI. Nếu HMI có bàn phím cảm ứng riêng thì thao tác qua giao diện remote. Việc nhập chạm, Unicode, DPI nhiều màn hình và RDP có vào đúng phiên HMI trên màn hình vật lý phải nghiệm thu trên G10 thật. Hiện chưa có máy in trong môi trường kiểm tra cục bộ.

### Build và đóng gói

App chính tiếp tục Native AOT. Helper WinForms chứa ActiveX RDP chạy riêng, trao đổi qua named pipe giới hạn người dùng và kiểm tra PID hai phía. Build Debug trên Windows tự tạo helper vào `TanTienQR/bin/<Configuration>/net10.0/PrinterRemoteHost/`; gói phát hành đặt `TanTien.PrinterRemoteHost.exe` self-contained ở thư mục gốc, ưu tiên dùng bản này.

`deploy/update/build-release.ps1` publish helper thành một EXE chứa runtime .NET; máy cài không cần .NET SDK/runtime riêng. Bộ kiểm tra manifest chỉ cho thêm đúng EXE này ở Windows. Trước khi bàn giao cập nhật, app đợi helper thoát và nhả tệp; helper được backup/phục hồi cùng chương trình chính.

Máy đang dùng **launcher/updater cũ** chỉ chấp nhận chương trình chính và DLL sẽ từ chối gói có helper EXE. Khi nâng cấp lần đầu sang tính năng này, dùng **bootstrap mới có cả TanTienLauncher mới** theo quy trình cài bootstrap trong `docs/cloudflare-updates.md`; chỉ nâng riêng app chưa thay được bộ kiểm tra nằm trong launcher cũ.

Kiểm tra cục bộ, không cần kết nối máy in:

```powershell
dotnet run --project tools/PrinterUiTests -c Debug
dotnet run --project tools/PrinterUiTests -c Debug -- --native-smoke
dotnet run --project tools/PrinterRemoteTests -c Debug
dotnet run --project tools/PrinterRemoteHost -c Debug -- --self-test
dotnet run --project tools/PrinterRemoteHost -c Debug -- --self-test-ipc
```

Hai bài thử helper tạo ActiveX/COM và kết nối loopback để kiểm tra IPC, phân loại lỗi, HWND, co giãn, kho credential tạm và dọn tiến trình. Chúng không xác nhận máy G10 thật hoặc phiên HMI thực tế.

## Điều khiển máy in

Card **ĐIỀU KHIỂN MÁY IN** trên tab Máy in port các lệnh từ mã legacy `Print/ucG10Printer.cs` và `Print/command/CommandString.cs`:

| Nút | Lệnh G10 | Ý nghĩa |
|---|---|---|
| Bật in / Tạm dừng in | `START` / `STOP` | Nút toggle đổi theo trạng thái máy in. `STOP` chỉ tạm dừng, **không xoá** dữ liệu trong buffer. |
| Xóa buffer | `CLR` | Xoá dữ liệu cũ trong máy in. |
| Reset số đếm | `RESET\|1` | Đặt lại số mã đã in. |
| Vệ sinh đầu in | `CLENOZ` | Chạy chu trình vệ sinh đầu phun. |
| Test phun JET | `JET` | Phun thử để kiểm tra đầu in. |
| Danh sách bản tin | `GFILE` | Đọc danh sách file bản tin trên máy; phản hồi thô hiển thị ở dòng thông báo. |

Các nút chỉ bật khi module máy in được bật và đã kết nối (`Print:Enabled = true`, license có entitlement `printer`). Mỗi lệnh đợi phản hồi có prefix tương ứng (`START|OK`, `STOP|OK`, …); timeout riêng 3–8 giây tuỳ lệnh. Kết quả được ghi vào log và hiển thị ngay dưới hàng nút.

> Dùng **Tạm dừng in** khi cần dừng dây chuyền tạm thời; dùng **Xóa buffer** chỉ khi chắc chắn muốn bỏ dữ liệu đã gửi mà chưa in.

## Cấp quyền license

Trong LicenseTool hoặc License Portal, chọn **Cấp quyền Máy in + gói bán kèm** trước khi cấp/renew license. Payload ký số có `features: ["printer"]`. License cũ không có `features` vẫn hợp lệ cho các chức năng hiện có nhưng không mở module máy in.

## Luồng trạng thái mã

Khi bấm **Gửi mã vào máy in**, hệ thống khóa số lượng cần gửi trong transaction và ghi `Sent` **trước** khi mở lệnh TCP `MFD|POD01:<code>`. Phản hồi `MFD|OK` được lưu lại để ghép với sự kiện `BACK` của máy in.

- `Pending`: mã chưa active, chưa gửi vào máy in.
- `Sent`: đã ghi nhận gửi; đang chờ máy in xác nhận in.
- `Printed`: máy in gửi `BACK`, mã lâu nhất đã được nhận `MFD|OK` được xác nhận đã in.
- `NoResponse`: mã đã active nhưng không có xác nhận `BACK`; worker đối soát định kỳ ghi trạng thái này.

Mã ở `Printed` hoặc `NoResponse` không được lấy lại. Nếu mất kết nối sau khi đã ghi `Sent`, không gửi lại tự động để tránh in trùng; cần kiểm tra máy in và xử lý theo mã đang chờ.

## Kiểm tra trước khi đưa vào chuyền

```powershell
dotnet run --project tools/PrintTests -c Debug
dotnet build TanTienQR/TanTienQR.csproj -c Debug
```

`tools/PrintTests` kiểm tra claim đúng số lượng, ghi `Sent` trước khi truyền, xác nhận `Printed`, đối soát `NoResponse` và không lấy lại mã đã kết thúc. Cần thử thêm với máy G10 thật để xác nhận IP, port và định dạng phản hồi của firmware.
