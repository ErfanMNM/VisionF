# Thực hiện ba nhiệm vụ giao ngày 26/11/2026

- Owner CEO: `/root`, session `01a21820-2fb7-76b3-9426-a9830cf140e5`.
- Thẩm quyền: user yêu cầu thực hiện `docs/plans/nhiemvu.md` theo skill ceo-orchestrator; tối đa 10 session cùng lúc. Host hiện giới hạn 4 gồm CEO, dùng 3 phòng nội bộ. Chưa được giao commit/push/deploy.
- Repo đã xác minh: `D:/DuAn/VisionF`, master, HEAD `ecfcc19e2459476873e6ffbed3dd3361526614c3`, origin `https://github.com/ErfanMNM/VisionF.git`.
- Thay đổi có sẵn cần giữ: `docs/plans/nhiemvu.md`. Không có tiến trình dotnet/MSBuild ở checkpoint ban đầu. Hồ sơ cũ ở cấp visionf là lịch sử các nhiệm vụ PO, không chuyển quyền của chúng cho phòng mới.

## Nghiệm thu

1. Cấu hình riêng địa chỉ D Total/Pass/Fail/Timeout/reset/clear, tương thích cấu hình khối CounterAddress cũ; chọn hiển thị số app hoặc PLC và nhãn nguồn chính xác. Không phát lệnh reset/clear do đổi cấu hình hoặc refresh. Lệnh thủ công giữ kiểm tra quyền. Số app là counter xử lý runtime, số PLC từ thiết bị. Mặc định giữ Fail = ReadFail + Timeout; tùy chọn chỉ rõ nếu thanh ghi Fail đã gồm Timeout.
2. Lịch sử bền vững 60 lần gửi Checkee gần nhất, số OK/Fail và chi tiết mã/kết quả mỗi lần. Retry tạo lần mới, không dựng lịch sử từ trạng thái mới nhất. Giữ nguyên CodeContent và luồng gửi hiện hành; trạng thái chưa hoàn tất không giả thành OK.
3. Thêm Cài đặt V2 theo mẫu sơ đồ luồng trong Downloads, QRBox là PLC, các cài đặt khác có khu riêng; giữ trang cũ. Tái sử dụng validation/quyền/save/hotapply thật.

## Phối hợp

- Counters sở hữu PLC/config/runtime counters/InfoView và MainViewModel vùng counter.
- Checkee sở hữu send-history store/worker/lookup và fake interface UpdateSendTests.
- Settings V2 sở hữu file mới V2, navigation enum/tabs/MainWindow/LazyTabFactories/ViewLocator.
- CEO sở hữu tích hợp chung, `tools/ApplicationUiTests/Program.cs`, cập nhật kết quả giao việc và QA cuối.
- Một build slot duy nhất. Ban đầu CLOSED; chỉ CEO cấp sau yêu cầu. Không chạy build/test/run song song ghi chung obj/bin.
- Không gọi thiết bị thật hay Checkee production trong test. Chốt source freeze trước nghiệm thu tích hợp; kiểm tra lại phần chịu ảnh hưởng sau delta.

## Pha

1. Đã đọc nhiệm vụ/mẫu, kiểm tra repo, phân phòng và chốt contract.
2. Đang triển khai ba phòng độc lập; CEO chuẩn bị nghiệm thu, test và tích hợp.
3. Chờ freeze, chạy suite liên quan và kiểm tra ảnh render.
4. Chờ cập nhật kết quả vào giao việc, tổng hợp bằng chứng và báo user.

## Giới hạn response

CEO đã xác minh đúng log session, helper đếm 19 response_id duy nhất ở checkpoint đầu, không lỗi định dạng. Kiểm tra lại sau mốc lớn, từ80 mỗi response; >85 chỉ bàn giao. Mỗi phòng tự kiểm log riêng. Không dùng token/tool count thay response_id.
