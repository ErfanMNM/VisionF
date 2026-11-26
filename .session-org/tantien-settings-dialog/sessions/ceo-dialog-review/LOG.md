# Nhật ký QA độc lập — QA-01

- Vai trò / parent: nhân viên review của CEO `01a215d7-5953-74c2-90de-dd1799a93c39`.
- Agent collaboration: `/root/dialog_review`.
- Thread ID riêng đã xác minh qua `CODEX_THREAD_ID`: `01a215db-8006-7a12-b672-47c6a4f84bcf`.
- Journal-key do parent cấp: `ceo-dialog-review`; giữ nhật ký ở đường dẫn này theo phân công.
- Project root: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`.
- STATE_ROOT: `C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\tantien-settings-dialog`.
- Quyền ghi: chỉ nhật ký riêng; mã nguồn và hồ sơ cấp dự án/phòng ban chỉ đọc.
- Trạng thái: `review_passed`.

## 2026-11-26T10:18:15+07:00 — Khảo sát hành vi và checklist nghiệm thu

- Đã đọc skill `session-worker`, `session-ops`, giao thức và mẫu nhật ký; đọc BRIEF/TASKS/REGISTRY/DECISIONS và STATE phòng UI.
- Nhánh/commit lúc nhận: `master` / `92ba3ca09bdc5ba7706cdc430b6a94805f0d3c37`. `git status --short` chỉ có `.session-org/` chưa theo dõi; chưa có diff code ở mốc khảo sát.
- Đã đọc hộp thoại, caller trong `MainViewModel.Start`, đoạn tạo message và lưu snapshot của `ConfigEditorViewModel`, schema diff trong `DeviceConfigurationChanges`, palette/style trong `App.axaml`, kiểm tra `tools/ConfigUiTests` và `tools/DeviceConfigurationTests`.
- Chưa chạy build/test: phòng UI đang triển khai và sở hữu quyền chạy/render; tránh build trùng và tránh thao tác thiết bị thật.
- Budget riêng: script `request_budget.py --thread-id 01a215db-8006-7a12-b672-47c6a4f84bcf` trả `normal`, 4 request hoàn tất, còn 96, `exact=true`, tại `2026-11-26T10:18:15+07:00`. Không mượn counter CEO.

### Hợp đồng hành vi cần bảo toàn

1. `MainViewModel.cs:669` gắn `Func<string, Task<bool>>`; owner phải hiện và chưa dispose, sau đó `ShowDialog<bool>(_window)`. Không thay constructor hoặc kiểu kết quả nếu không có nhu cầu được giao.
2. `ConfigEditorViewModel.cs:425` chụp snapshot một lần trước xác nhận, kiểm tra `DeviceConfigurationChanges.Describe`; chỉ gọi `SaveSnapshotAsync` sau `true`. `false` giữ file/runtime và giữ bản soạn dirty để người dùng sửa tiếp.
3. Message hiện có bốn phần: lời dẫn; các dòng `Camera/PLC · nhãn: cũ → mới`; cảnh báo tạm ngắt + giữ ứng dụng/phiên đăng nhập + tự kết nối lại + chỉ xác nhận khi dây chuyền tạm dừng nhận mã được; ghi chú Development tùy môi trường.
4. Hộp thoại cũ có `CancelConfigurationButton` (`IsCancel=true`) và `ApplyConfigurationButton`, bấm lần lượt trả `false`/`true`; không đặt `IsDefault` trên nút áp dụng.

### Rủi ro thực tế cho bản UI mới

- Tách chuỗi thành card/dòng có thể vô tình mất nội dung, nhất là ghi chú Development hoặc giá trị có dấu `:`/`→`/xuống dòng. Chỉ nhận diện mẫu đủ chắc; phần không nhận diện vẫn phải đọc đầy đủ.
- Đặt nút chính `IsDefault=true` hoặc focus ban đầu lên nút chính có thể khiến Enter áp dụng ngay. Cần giữ Enter ở cửa sổ mới mở không trả `true`; Enter khi người dùng chủ động focus nút áp dụng là hành vi bàn phím có chủ đích.
- Chỉ dùng `MaxHeight` cho khối text có thể không chừa chỗ cho tiêu đề/card cảnh báo/footer ở biến thể dài. Cần chứng cứ nút luôn nằm trong cửa sổ, nội dung cuộn tới cuối, không cắt dấu tiếng Việt.
- Đóng bằng Esc, Hủy, nút X phải đều kết thúc `ShowDialog<bool>` bằng `false`; kiểm tra callback giả hiện có chưa chứng minh các nhánh UI này.
- Headless khởi tạo không dùng `App.axaml` sẽ bỏ lỡ style thật; render cần nạp App, đối chiếu palette và tương phản ở cả trạng thái thường/hover/focus.

### Checklist nghiệm thu cụ thể

- [ ] Cấu trúc nhìn: tiêu đề rõ, lời dẫn phụ nhẹ; card thay đổi đọc nhanh được thiết bị/nhãn/giá trị cũ/giá trị mới; cảnh báo vàng riêng và có nhãn/chỉ báo ngoài màu.
- [ ] `Button.primary`/`Button.soft` hoặc style tương đương cục bộ dùng đúng palette hiện có; chữ nút chính trắng, tên nút rõ, vùng bấm tối thiểu 44 px và khoảng cách đủ.
- [ ] Ảnh ngắn với 1 thay đổi; ảnh thông thường Camera + PLC; ảnh dài nhiều thay đổi + Development. Toàn bộ nội dung nguồn còn hiển thị đúng, không rút gọn cảnh báo quan trọng.
- [ ] Nội dung dài cuộn dọc được tới dòng cuối; không cuộn ngang, không ellipsis/cắt giá trị; footer vẫn trong khung, nút không bị đẩy khỏi cửa sổ; trường hợp nhãn/giá trị rất dài và chữ tiếng Việt có dấu vẫn đọc đủ.
- [ ] Kiểm tra trên cửa sổ thực được render trong Headless + Skia với `App.axaml`; không chỉ kiểm tra cây control/chụp khung trống.
- [ ] Khi mở dialog chưa tương tác: Enter không hoàn tất bằng `true`; Tab/Shift+Tab chạy được, focus hiển thị và không tự áp dụng.
- [ ] Hủy, Esc, nút X trả `false`; bấm áp dụng trả `true`, mỗi lần chỉ một kết quả. Không đổi `Name` hai nút khi không cần thiết để giữ điểm tìm control ổn định.
- [ ] Bộ ConfigUiTests tiếp tục chứng minh: từ chối không persist/apply; xác nhận áp dụng đúng snapshot; thay đổi Camera+PLC chỉ hỏi một lần; không thay đổi thì không hỏi.
- [ ] Có kết quả build và kiểm tra off-screen cùng ảnh thật. QA review diff cuối sau khi phòng UI dừng sửa; không chạy song song build với phòng UI.

### Giới hạn kiểm tra hiện có

`tools/ConfigUiTests/Program.cs:165` thay callback xác nhận bằng hàm trả bool và đếm message; đoạn `291..335` kiểm tra tác dụng của từ chối/xác nhận lên ViewModel. Không có tham chiếu `ConnectionConfigurationConfirmWindow` trong tools ở mốc khảo sát, vì vậy chưa có kiểm tra trực tiếp layout/keyboard/dialog result của cửa sổ này. Đã có hàm chụp `CaptureRenderedFrame` bằng Skia trong ConfigUiTests có thể làm mẫu cho harness riêng.

- Chủ sở hữu code/kiểm tra: phòng UI `01a215da-221c-7892-b936-86a230b7f5b6`.
- Bước kế tiếp: báo CEO rủi ro/checklist; chờ CEO cung cấp diff và kết quả UI-01 để review cuối.

## 2026-11-26T10:24:00+07:00 — Review sớm bản code và harness đang hoàn thiện

- Parent yêu cầu review ngay diff xuất hiện; tiếp tục chế độ chỉ đọc code, không chạy build/test trùng worker UI.
- Đã đọc diff `ConnectionConfigurationConfirmWindow.cs`, toàn bộ `.axaml` mới, harness `ConnectionConfirmationChecks.cs` và vị trí gọi harness mới trong `tools/ConfigUiTests/Program.cs`.
- Kết luận tại mốc này: chưa phát hiện lỗi tích hợp hay lỗi hành vi phải chặn; chưa nghiệm thu ảnh vì chưa có kết quả render được chuyển cho QA.
- Tích hợp đúng: constructor vẫn nhận `string message`, XAML cùng `x:Class` và class `partial`; tên nút giữ nguyên; caller/ViewModel không đổi; kết quả click vẫn `Close(false)`/`Close(true)`.
- Bàn phím theo mã: `Opened` focus Hủy; nút áp dụng không có `IsDefault`; Escape có handler đặt `Handled` rồi `Close(false)`. Enter lúc mở phải được xác nhận thêm bằng kết quả chạy harness.
- Message: nhận diện prefix và cảnh báo bằng envelope chính xác, chỉ bỏ nhãn thiết bị để đưa vào badge; phần Description giữ nguyên cả nhãn/giá trị/dấu `:`/`→`/`·`. Ghi chú Development giữ trọn sau hai newline. Dòng lạ/nhiều dòng ngoài định dạng đi vào card nguyên văn; không lược nội dung nguồn.
- Kích thước theo cấu trúc: header và footer ở hai hàng Auto ngoài vùng cuộn; vùng giữa `MaxHeight=470`, `MaxHeight` cửa sổ 760. Header/warning/description có LineHeight và wrapping phù hợp; cần ảnh để xác nhận cách đo/arrange thực tế.
- Style: `primary`/`soft` tái sử dụng App.axaml, vùng bấm 44; cảnh báo có cả biểu tượng, nhãn và nền WarnSoft; không cần sửa palette dùng chung.
- Harness mới dùng App + Headless/Skia và nhận vị trí output từ CLI; đã có kiểm tra dữ liệu 2 dòng/24 dòng/ghi chú dài/fallback, màu primary, click vật lý trên nút, Enter/Escape, cuộn tới dòng cuối và ghi chú cuối. Đây là review mã kiểm tra, chưa phải kết quả đã chạy.
- Khoảng trống bằng chứng nhỏ đã báo CEO: case envelope chuẩn không có Development chưa thấy trong harness; window.Close()/nút X chưa được kiểm tra kết quả mặc định false; Tab/Shift+Tab và focus hữu hình cần kiểm tra sau render.
- Ngân sách riêng: snapshot `2026-11-26T10:23:54+07:00`, 11 request hoàn tất, còn 89, `normal`, `exact=true`.
- Trạng thái tiếp tục: `waiting_for_render_evidence`; chờ CEO giao ảnh/kết quả kiểm tra để review cuối, quyền code vẫn thuộc phòng UI.

## 2026-11-26T10:30:18+07:00 — Review thị giác 3 ảnh render thật

- Đã tự xem `artifacts/confirmation-short.png`, `confirmation-long-top.png`, `confirmation-long-bottom.png` dưới STATE_ROOT; các ảnh được ghi lúc 10:28:30. Không chạy build/test hoặc sửa code.
- Kết quả tổng thể: phân cấp tiêu đề tốt, card tách thay đổi theo thiết bị đọc nhanh, cảnh báo vàng có biểu tượng/tiêu đề riêng, màu nút chính và footer đồng bộ palette. Nội dung tiếng Việt có dấu hiển thị đầy đủ trong cả ba ảnh.
- Hai lỗi thị giác cụ thể xác nhận được và đã báo CEO: (1) chữ Hủy canh sát mép trên trong vùng bấm; XAML hiện chỉ đặt `HorizontalContentAlignment`, cần thêm `VerticalContentAlignment=Center` cho cả hai nút; (2) ảnh ngắn có focus adorner đen hình chữ nhật, không theo bo góc; mở cửa sổ hiện dùng `cancel.Focus(NavigationMethod.Tab)`, nên đổi sang `Unspecified` khi mở hoặc dùng adorner bo góc, đồng thời giữ focus-visible khi Tab thật.
- Không thấy clipping làm mất dữ liệu trong nội dung dài: ảnh long-top có hàng 06 bị cắt ở mép dưới viewport là hành vi cuộn bình thường; footer và hai nút nằm đầy đủ ngoài vùng cuộn. Ảnh long-bottom thấy toàn bộ cảnh báo, ghi chú và dòng kết `KẾT THÚC GHI CHÚ.`; không bị footer đè lên chữ cuối.
- Ghi nhận giới hạn thị giác: cảnh báo ở sau danh sách 24 thay đổi nên chưa nằm trong viewport lúc mới mở; cấu trúc hiện tại cho phép cuộn tới và đọc trọn. Không coi đây là lỗi clipping.
- Bước kế tiếp đã đề nghị CEO: sau sửa canh nút/focus, gửi ảnh ngắn mới và một ảnh sau Tab để xác nhận focus hữu hình vẫn tồn tại. Quyền sửa vẫn thuộc phòng UI.
- Budget riêng: 19 request hoàn tất, còn 81, `normal`, `exact=true`, snapshot 10:30:18+07:00.
- Trạng thái tiếp tục: `waiting_for_button_polish`.

## 2026-11-26T10:33:05+07:00 — Nghiệm thu QA cuối: PASS

- Đã đọc diff cuối của `.cs`/`Program.cs`, phần canh nút trong `.axaml`, phần bổ sung của `ConnectionConfirmationChecks.cs` và toàn bộ `artifacts/worker-confirmation-tests.log`. Không chạy build/test; dựa trên log đã ghi và ảnh thật của phòng UI, kết hợp review độc lập mã.
- Đã tự xem lại ảnh `confirmation-single-camera.png`, `confirmation-short.png`, `confirmation-long-top.png`, `confirmation-long-bottom.png`, đồng thời xem thêm `confirmation-keyboard-focus.png`.
- Hai lỗi thị giác đã sửa đạt: chữ Hủy căn giữa theo chiều dọc; mở hộp thoại bằng `cancel.Focus(NavigationMethod.Unspecified)` không vẽ focus adorner đen mặc định. Ảnh sau Tab cho thấy focus bàn phím vẫn hiển thị trên nút áp dụng; không làm mất khả năng dùng bàn phím.
- Biến thể một thay đổi/no Development hiển thị gọn, chỉ có card thay đổi và cảnh báo; biến thể hai thay đổi giữ ghi chú Development; biến thể 24 thay đổi cuộn tới cuối đọc đủ và footer/nút luôn ở ngoài vùng cuộn. Không phát hiện clipping làm mất dữ liệu.
- Review tích hợp cuối đạt: constructor/caller và tên nút giữ nguyên; click trả `false`/`true`; Esc và Close() hủy an toàn; Enter sau mở không tự áp dụng; không đặt `IsDefault`; snapshot/lưu thiết bị ở ViewModel không đổi.
- Message được giữ đủ ở envelope chuẩn lẫn fallback nguyên văn; cảnh báo và ghi chú Development không bị lược. Badge thiết bị, tiêu đề, nền cảnh báo và nút chính/phụ dùng palette/style sẵn có, giữ phạm vi cục bộ hộp thoại.
- Log kiểm tra hiện kết thúc `KẾT QUẢ HỘP THOẠI: 20 pass / 0 fail`, đã gồm case không Development, Close() trả false, Enter/Esc, click thực, cuộn nội dung dài, giữ đủ message và Tab focus-visible. Số 20 mới hơn mốc 19 parent báo ban đầu.
- Không còn lỗi cần sửa hoặc concern thực tế đòi hỏi thêm test trong phạm vi QA này. Phòng UI tiếp tục chốt build/test toàn bộ; CEO giữ quyền nghiệm thu cấp dự án.
- File code mới cần đi cùng bản thay đổi (QA chỉ ghi nhận, không stage): `TanTienQR/Views/ConnectionConfigurationConfirmWindow.axaml`, `tools/ConfigUiTests/ConnectionConfirmationChecks.cs`. Hai file tracked sửa là `ConnectionConfigurationConfirmWindow.cs` và `tools/ConfigUiTests/Program.cs`.
- Budget riêng tại checkpoint: 22 request hoàn tất, còn 78, `normal`, `exact=true`.
- Đã báo CEO kết luận PASS qua collaboration; QA-01 hoàn tất, không tự commit hoặc sửa hồ sơ cấp dự án.
