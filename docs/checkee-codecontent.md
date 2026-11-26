# Gửi serial lên Checkee

Luồng kiểm tra mã của PO dùng toàn bộ `CodeContent`; GTIN và serial không còn là định danh sản xuất. Checkee vẫn dùng giao thức bên ngoài hiện có tại `POST /middleware-api/CTEs/active` với các trường `GTIN`, `batchCode`, `type` và `serialNumbers`. Payload gửi kèm `fromIndex: 0` và `toIndex: 0`; cả hai trường luôn được serialize dưới dạng số, kể cả khi có giá trị mặc định bằng 0.

Với URL HTTP/HTTPS có path kết thúc bằng `/21/{serial}`, client lấy riêng `{serial}` cho `serialNumbers`, bỏ query/fragment và giải mã percent escape đúng một lần. Ví dụ `https://beta-elink.checkee.vn/01/8935348800215/21/UhtX8gAftf` được gửi thành `UhtX8gAftf`. Serial giữ nguyên hoa/thường; các serial trùng trong cùng payload được gộp theo so sánh ordinal. Serial thuần và nội dung cũ không phải URL phù hợp được giữ nguyên để tương thích. Không dùng metadata `Serial` cũ để thay thế nội dung.

Phép tách chỉ áp dụng tại bước tạo payload Checkee. PO, đối chiếu quét, tra cứu, Raw và CodeContent trong SQLite tiếp tục giữ nguyên URL đầy đủ. Khi nhiều bản ghi tương ứng cùng một serial gửi, kết quả gửi vẫn được cập nhật riêng cho từng bản ghi.

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

Timeout/lỗi mạng giữ Pending để chu kỳ sau thử lại. Khi Checkee phản hồi từ chối HTTP hoặc lỗi nghiệp vụ, chỉ trạng thái **gửi** thành Failed; kết quả quét Pass và định danh CodeContent được giữ nguyên. Người vận hành xem lý do và dùng Gửi lại sau khi xử lý. Gửi lại cũng áp dụng phép tách serial tại client; không sửa dữ liệu mã gốc hoặc tự reset các mã Failed khi cập nhật ứng dụng. Khả năng server chấp nhận serial còn phụ thuộc metadata và trạng thái lô trên Checkee.

## Kiểm thử độc lập

Chạy `dotnet run --project tools/CheckeeSendTests`. Harness biên dịch trực tiếp worker gửi và cổng bảo trì, tham chiếu thư viện Scans/Checkee; không build app hoặc PO backend. HTTP được giả lập hoàn toàn và SQLite nằm trong thư mục tạm. Các ca bao gồm payload serial từ URL, query/fragment, percent escape, giữ nguyên dữ liệu mã gốc, metadata cấu hình/legacy, thiếu metadata/nội dung, batch blocked không làm kẹt mã hợp lệ phía sau, metadata repair, HTTP từ chối, lỗi mạng, gửi lại thủ công và cổng bảo trì.
