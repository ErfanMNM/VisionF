# Phòng giao diện — UI-01 hoàn tất
- owner_thread_id: 01a215da-221c-7892-b936-86a230b7f5b6
- Trạng thái vai trò: review_ready
- Trạng thái UI-01: done / review_ready
- Cập nhật: 2026-11-26T10:38:00+07:00.
- CEO thread_id: 01a215d7-5953-74c2-90de-dd1799a93c39.
- Project root: C:\Users\THUC\source\repos\ErfanMNM\VisionF
- STATE_ROOT: C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\tantien-settings-dialog
- Repo: master @ 92ba3ca09bdc5ba7706cdc430b6a94805f0d3c37; các thay đổi chưa commit; không đổi nhánh/deploy.
- Worker đã dừng sửa: /root/ui_dialog_impl, thread thật 01a215dc-e4db-7743-b5be-4fe09dd80eaf. Nhật ký: sessions\01a215dc-e4db-7743-b5be-4fe09dd80eaf\LOG.md.
- Budget phòng: normal, 46/100, exact true tại 2026-11-26T10:36:38+07:00; budget.json trong thư mục session phòng. Budget worker riêng 27/100 tại checkpoint cuối.

## Kết quả và phạm vi diff
- Sửa TanTienQR\Views\ConnectionConfigurationConfirmWindow.cs; thêm TanTienQR\Views\ConnectionConfigurationConfirmWindow.axaml.
- Sửa tools\ConfigUiTests\Program.cs; thêm tools\ConfigUiTests\ConnectionConfirmationChecks.cs.
- Giao diện theo palette App.axaml; header icon vector, typography phân cấp, card có nhãn Camera/PLC và số thay đổi, cảnh báo kết nối riêng, Development riêng, footer cố định primary/soft.
- Bảo toàn constructor public string message, Name CancelConfigurationButton/ApplyConfigurationButton, Close(false)/Close(true). Hủy, Escape và Close() trả false; Enter khi mới mở không áp dụng; Tab thật tới nút áp dụng có focus-visible.
- Hai nút canh giữa ngang/dọc và cao ít nhất 44px. Focus mở bằng Unspecified không hiện khung đen; focus do Tab giữ native indicator rõ.
- Parser chỉ nhận envelope và tiền tố thiết bị, giữ nguyên tên trường + giá trị có ':'/'→'; thông điệp lạ/nhiều dòng dùng fallback nguyên văn. Nội dung dài cuộn được đến cuối thay đổi/ghi chú, không có cuộn ngang.
- Không đổi VM/backend, không mở app sản xuất hoặc thay cấu hình thật.
- Diff cả 2 file mới: C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\tantien-settings-dialog\artifacts\worker-dialog.diff. git status cuối có đúng 4 file code/harness nêu trên và .session-org/.

## Kiểm tra và log
```powershell
dotnet build TanTienQR\TanTienQR.csproj --no-restore -v minimal
dotnet run --project tools\ConfigUiTests -- --confirmation-only --confirmation-artifacts C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\tantien-settings-dialog\artifacts
dotnet run --project tools\ConfigUiTests -- --confirmation-artifacts C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\tantien-settings-dialog\artifacts
git diff --check
```
- Worker build cuối thành công: 0 warning, 0 error. Log artifacts\worker-build.log.
- Full harness cuối: 138 pass / 0 fail, exit 0, gồm 21 kiểm tra hộp thoại. Log artifacts\worker-config-ui-tests.log. Kiểm tra riêng trước assertion cuối: 20 pass / 0 fail trong artifacts\worker-confirmation-tests.log.
- Dùng Avalonia.Headless + Skia, fake coordinator và config sandbox trong thư mục build; không khởi động host/thiết bị sản xuất.
- Trưởng phòng đã đọc diff/log, tự chạy git diff --check thành công và tự xem đủ 6 ảnh. CEO đã đọc log/xem ảnh; reviewer độc lập /root/dialog_review PASS theo xác nhận CEO. CEO chỉ đạo không lặp full build/test khi không có thay đổi/lỗi mới; phòng không chạy vòng trùng.
- Lưu ý build: compile XAML đầu có AVLN3001 vì class chỉ có constructor string message, không có public constructor rỗng phục vụ URI runtime-loader. Không thêm constructor thừa; new(string) + AvaloniaXamlLoader.Load(this) đã được headless tạo/render thành công. Build incremental cuối 0 warning không chứng minh clean build sẽ không lặp AVLN3001; cảnh báo đã ghi nhận và không ảnh hưởng đường gọi đang dùng.

## Ảnh thật đã tự xem
Các ảnh dưới C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\tantien-settings-dialog\artifacts:
- confirmation-single-camera.png — Camera · Port TCP: 9000 → 9001, không Development; nút canh giữa, không viền focus lúc mở.
- confirmation-short.png — hai thay đổi và Development.
- confirmation-long-top.png — đầu 24 thay đổi, quấn dòng trong card.
- confirmation-long-last-change.png — dòng 24 hiển thị đủ khi cuộn.
- confirmation-long-bottom.png — cuối ghi chú dài, footer giữ vị trí.
- confirmation-keyboard-focus.png — Tab thật tới áp dụng, focus-visible rõ.

## Bàn giao CEO
- UI-01 done / review_ready; worker và phòng không còn sửa code, không còn build/test đang chạy của nhiệm vụ.
- Không còn chặn/phần triển khai dở. CEO chốt hồ sơ cấp dự án và bàn giao người dùng; phòng không sửa hồ sơ cấp dự án.
