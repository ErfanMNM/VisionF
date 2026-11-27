# Gửi serial lên Checkee

Luồng kiểm tra mã của PO dùng toàn bộ `CodeContent`; GTIN và serial không còn là định danh sản xuất. Checkee vẫn dùng giao thức bên ngoài hiện có tại `POST /middleware-api/CTEs/active` với các trường `GTIN`, `batchCode`, `type` và `serialNumbers`. Payload gửi kèm `fromIndex: 0` và `toIndex: 0`; cả hai trường luôn được serialize dưới dạng số, kể cả khi có giá trị mặc định bằng 0.

Với URL HTTP/HTTPS có path kết thúc bằng `/21/{serial}`, client lấy riêng `{serial}` cho `serialNumbers`, bỏ query/fragment và giải mã percent escape đúng một lần. Ví dụ `https://beta-elink.checkee.vn/01/8935348800215/21/UhtX8gAftf` được gửi thành `UhtX8gAftf`. Serial giữ nguyên hoa/thường; các serial trùng trong cùng payload được gộp theo so sánh ordinal. Serial thuần và nội dung cũ không phải URL phù hợp được giữ nguyên để tương thích. Không dùng metadata `Serial` cũ để thay thế nội dung.

Phép tách chỉ áp dụng tại bước tạo payload Checkee. PO, đối chiếu quét, tra cứu, Raw và CodeContent trong SQLite tiếp tục giữ nguyên URL đầy đủ. Khi nhiều bản ghi tương ứng cùng một serial gửi, kết quả gửi vẫn được cập nhật riêng cho từng bản ghi.

## Metadata bắt buộc của request

- `GTIN`: lấy từ PO của phiên gửi, giữ nguyên biểu diễn nguồn, không thêm số `0` đầu cho đủ 14 số. Ví dụ PO có GTIN `8935348800215` thì payload, lịch sử và audit đều ghi đúng 13 số này. Khóa GTIN 14 số chỉ dùng để đối chiếu nội bộ với mã quét.
- `batchCode`: lấy metadata PO theo thứ tự `lot`, `lotNo`, `batchCode`, `batchNo` (không phân biệt hoa thường); nếu thiếu thì dùng số PO.
- `type`: tiếp tục dùng `Checkee:SerialType` theo giao thức Checkee hiện có.

Ví dụ phần cấu hình liên quan:

```json
{
  "Checkee": {
    "SerialType": "DECLARE"
  }
}
```

GTIN và lô thuộc metadata PO. Việc đối chiếu GTIN và danh tính PO của mã quét vẫn được giữ trước khi gửi; payload sử dụng GTIN nguồn thay vì khóa so khớp nội bộ.

## Chờ gửi, từ chối và gửi lại

Thiếu GTIN sản phẩm hoặc mã lô không được tự chuyển bản ghi Pass thành lỗi vĩnh viễn. Worker giữ `SendStatus = Pending`, ghi hướng dẫn bổ sung `Checkee:ProductGtin` / `Checkee:BatchCode` trong `SendMessage`, và hiển thị lý do chờ trong trạng thái worker. Bản ghi thiếu CodeContent cũng được giữ chờ đối soát; không gửi Serial thay thế hoặc đánh dấu Sent nhờ mã khác trong cùng nhóm.

Mỗi batch ưu tiên bản ghi chưa từng thử, rồi bản ghi có lần thử cũ nhất; thời điểm nhận và ID phân định khi cùng lần thử. Vì vậy một batch đầy bản ghi còn thiếu dữ liệu không chặn vĩnh viễn mã hợp lệ phía sau. Mã blocked vẫn Pending và được thử lại luân phiên, kể cả sau khi bổ sung metadata hoặc khởi động lại ứng dụng.

Timeout/lỗi mạng giữ Pending để chu kỳ sau thử lại. Khi Checkee phản hồi từ chối HTTP hoặc lỗi nghiệp vụ, chỉ trạng thái **gửi** thành Failed; kết quả quét Pass và định danh CodeContent được giữ nguyên. Người vận hành xem lý do và dùng Gửi lại sau khi xử lý. Gửi lại cũng áp dụng phép tách serial tại client; không sửa dữ liệu mã gốc hoặc tự reset các mã Failed khi cập nhật ứng dụng. Khả năng server chấp nhận serial còn phụ thuộc metadata và trạng thái lô trên Checkee.

## Lịch sử gửi Checkee

Tab Tra cứu mã hiển thị 60 lần gửi batch gần nhất thay cho bảng mã gửi lỗi.
Mỗi dòng có thời gian, mã lô, số mã OK/Fail/chưa xác định; nhấn dòng để xem
toàn bộ CodeContent, PO, HTTP và thông điệp của từng mã. Kết quả từng mã
được ghi theo phản hồi batch, vì API không trả danh sách kết quả riêng từng mã.
`activeSerials` không được diễn giải thành số mã chấp nhận riêng request khi
spec chưa xác nhận ý nghĩa đó. Chỉ envelope `status=true` xác nhận OK batch;
`status=false` là Fail kể cả HTTP200. HTTP200 thiếu envelope hợp lệ là Chưa xác định.

Hai bảng `CheckeeSendAttempts`/`CheckeeSendAttemptItems` ở schema scans v5
lưu snapshot trước khi gọi Checkee. Hoàn tất transaction lưu cả kết quả lịch sử
và trạng thái gửi hiện hành. Retry tạo lần mới và giữ nguyên lịch sử trước;
thiếu metadata hoặc chu kỳ không có mã không tạo lần gửi giả. Giữ tối đa 60
lần và xóa cascade chi tiết cũ, không xóa bản ghi quét. Không dựng lại lịch sử
từ các bản ghi trước khi tính năng này được cài đặt.

Nếu ứng dụng bị gián đoạn sau khi bắt đầu gửi nhưng chưa xác nhận kết quả,
lịch sử vẫn giữ trạng thái Chưa xác định qua restart; mã cần đối soát và chủ
động gửi lại, không tự gửi lần nữa. Lỗi transport đã được xác định vẫn giữ
Pending để thử lại theo chính sách hiện có. Nút Gửi lại mã lỗi lấy trạng thái
Failed hiện hành từ DB, không reset mã đã Sent chỉ vì lịch sử cũ từng lỗi.

## Kiểm thử lịch sử

Chạy `dotnet run --project tools/CheckeeSendTests`. Harness biên dịch trực tiếp worker gửi và cổng bảo trì, tham chiếu thư viện Scans/Checkee; không build app hoặc PO backend. HTTP được giả lập hoàn toàn và SQLite nằm trong thư mục tạm. Các ca bao gồm payload serial từ URL, query/fragment, percent escape, giữ nguyên dữ liệu mã gốc, metadata cấu hình/legacy, thiếu metadata/nội dung, batch blocked không làm kẹt mã hợp lệ phía sau, metadata repair, HTTP từ chối, lỗi mạng, gửi lại thủ công và cổng bảo trì.
