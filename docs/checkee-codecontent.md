# Gửi CodeContent lên Checkee

Luồng kiểm tra mã của PO dùng toàn bộ `CodeContent`; GTIN và serial không còn là định danh sản xuất. Checkee vẫn dùng giao thức bên ngoài hiện có tại `POST /middleware-api/CTEs/active` với các trường `GTIN`, `batchCode`, `type` và `serialNumbers`.

`serialNumbers` chứa **nguyên CodeContent** của từng bản ghi, gồm đường dẫn, dấu phân cách, Unicode, chữ hoa/thường và khoảng trắng có ý nghĩa. Worker không parse QR/GS1, không cắt đuôi serial và không thay nội dung bằng metadata `Serial` cũ. Hai CodeContent khác nhau chỉ cùng phần đuôi vẫn được gửi thành hai mã riêng.

## Metadata bắt buộc của request

- `Checkee:ProductGtin`: GTIN sản phẩm do người vận hành cấu hình độc lập với nội dung mã. Worker ưu tiên giá trị này. Khi đọc dữ liệu cũ đã lưu GTIN, worker có thể fallback metadata `Gtin` trên bản ghi; không suy GTIN từ CodeContent.
- `batchCode`: ưu tiên mã lô trên bản ghi nếu khác `NoBatch`, sau đó `Checkee:BatchCode`, cuối cùng mã PO của bản ghi.
- `type`: tiếp tục dùng `Checkee:SerialType` theo giao thức Checkee hiện có.

Ví dụ phần cấu hình liên quan:

```json
{
  "Checkee": {
    "ProductGtin": "08934567890123",
    "BatchCode": "",
    "SerialType": "DECLARE",
    "OnlyPassCodes": true
  }
}
```

Đây là metadata của tích hợp gửi, không phải điều kiện để một mã thuộc PO được Pass. Cấu hình sản phẩm phải đúng với PO đang vận hành và cần áp dụng theo vòng đời cấu hình/restart hiện có của ứng dụng.

## Chờ gửi, từ chối và gửi lại

Thiếu GTIN sản phẩm hoặc mã lô không được tự chuyển bản ghi Pass thành lỗi vĩnh viễn. Worker giữ `SendStatus = Pending`, ghi hướng dẫn bổ sung `Checkee:ProductGtin` / `Checkee:BatchCode` trong `SendMessage`, và hiển thị lý do chờ trong trạng thái worker. Bản ghi thiếu CodeContent cũng được giữ chờ đối soát; không gửi Serial thay thế hoặc đánh dấu Sent nhờ mã khác trong cùng nhóm.

Mỗi batch ưu tiên bản ghi chưa từng thử, rồi bản ghi có lần thử cũ nhất; thời điểm nhận và ID phân định khi cùng lần thử. Vì vậy một batch đầy bản ghi còn thiếu dữ liệu không chặn vĩnh viễn mã hợp lệ phía sau. Mã blocked vẫn Pending và được thử lại luân phiên, kể cả sau khi bổ sung metadata hoặc khởi động lại ứng dụng.

Timeout/lỗi mạng giữ Pending để chu kỳ sau thử lại. Khi Checkee phản hồi từ chối HTTP hoặc lỗi nghiệp vụ, chỉ trạng thái **gửi** thành Failed; kết quả quét Pass và định danh CodeContent được giữ nguyên. Người vận hành xem lý do và dùng Gửi lại sau khi xử lý. Worker không tự chuyển CodeContent sang một định dạng khác để làm server chấp nhận; khả năng server chấp nhận nội dung mã phụ thuộc hợp đồng tích hợp với Checkee.

## Kiểm thử độc lập

Chạy `dotnet run --project tools/CheckeeSendTests`. Harness biên dịch trực tiếp worker gửi và cổng bảo trì, tham chiếu thư viện Scans/Checkee; không build app hoặc PO backend. HTTP được giả lập hoàn toàn và SQLite nằm trong thư mục tạm. Các ca bao gồm giữ đầy đủ nội dung, metadata cấu hình/legacy, thiếu metadata/nội dung, batch blocked không làm kẹt mã hợp lệ phía sau, metadata repair, HTTP từ chối, lỗi mạng, gửi lại thủ công và cổng bảo trì.
