# Module máy in G10

Module này lấy mã trong `PoCodes` của PO đang chạy, chỉ chọn mã chưa `ActivatedUtc` và chưa có trạng thái in hoàn tất. Trạng thái in được lưu riêng trong cùng file SQLite của PO ở bảng `PrintCodeStates`, nên không làm thay đổi ý nghĩa của `ActivatedUtc`.

## Bật và cấu hình

1. Mở **Quản trị → Cấu hình → Máy in · G10**.
2. Bật **Bật module máy in**, nhập IP/port (mặc định `192.168.0.3:6007`), kích thước lô và dung lượng buffer.
3. Lưu cấu hình và khởi động lại app vì các thông số kết nối được nạp lúc khởi động.
4. Mở tab **Máy in**, kiểm tra trạng thái kết nối, mực và buffer.

Tab Máy in chỉ cho kết nối khi file license hợp lệ có entitlement `printer`. Entitlement này là gói **Máy in + gói bán kèm**, cấp cùng một license; không có license riêng cho phần bán kèm.

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
