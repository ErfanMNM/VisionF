# API nhận PO từ MES

Cập nhật: 2026-11-26. Phiên bản này thay cơ chế mã GTIN/serial bằng nguyên nội dung `CodeContent` và chỉ nhận tập mã dưới dạng file CSV, JSON hoặc XLSX. Mỗi PO có một cơ sở dữ liệu riêng; mọi lần bổ sung, đối chiếu và lưu trạng thái dùng đúng chuỗi mã đầy đủ.

## Endpoint và cấu hình

Máy chạy TanTienQR mở hai endpoint:

| Endpoint | Mục đích |
|---|---|
| `GET /api/health` | Kiểm tra listener và số lần nhận dữ liệu thành công. |
| `POST /api/po` | Nhận một file tập mã, tạo PO mới hoặc bổ sung vào PO chưa bắt đầu. |

Base URL mặc định là `http://127.0.0.1:8085`. MES trên máy khác dùng IP LAN của máy chạy app sau khi cấu hình listener và firewall.

```json
{
  "PoServer": {
    "Enabled": false,
    "Port": 8085,
    "BindAddress": "127.0.0.1",
    "ApiKey": "",
    "MaxBodyMB": 64,
    "MaxFileMB": 64,
    "UploadTempDirectory": "",
    "RequestTimeoutSeconds": 120,
    "MaxConcurrentRequests": 4
  }
}
```

Đặt `Enabled` thành `true` để bật listener. `BindAddress` là `127.0.0.1` khi chỉ nhận từ máy cục bộ, hoặc địa chỉ LAN / `0.0.0.0` khi nhận trong mạng. Nếu `ApiKey` có giá trị, mọi request phải có header `x-api-key` khớp chính xác, kể cả health.

`MaxBodyMB` giới hạn toàn bộ request, `MaxFileMB` giới hạn file. File upload được ghi tạm xuống đĩa và dọn sau xử lý. Thư mục tạm mặc định lấy từ hệ điều hành. Request quá thời gian `RequestTimeoutSeconds` bị hủy; không công bố một PO nhập dở. Listener xử lý tối đa `MaxConcurrentRequests` request cùng lúc.

## File có đúng một cột CodeContent

Tên cột hoặc thuộc tính phải là **`CodeContent`**, đúng hoa thường. Không cần GTIN, serial hay định dạng QR cụ thể. Mã là chuỗi opaque: dấu câu, Unicode, khoảng trắng đầu/cuối và xuống dòng nằm trong một giá trị hợp lệ được giữ nguyên. Không dùng kết quả tách URL, GTIN hay serial để xác định mã.

Hai mã `Case` và `case` là hai mã khác nhau. Hai URL có cùng hậu tố cũng là hai mã khác nhau. Giá trị rỗng hoặc chỉ có khoảng trắng bị từ chối.

### CSV UTF-8

Dòng tiêu đề có đúng một cột `CodeContent`. Mỗi bản ghi sau đó có đúng một giá trị. Dùng quy tắc CSV có dấu ngoặc kép khi mã chứa dấu phẩy, dấu ngoặc kép hoặc xuống dòng; dấu `"` bên trong ô được ghi thành `""`.

```csv
CodeContent
BOX-001
"https://example.invalid/code?value=a,b"
"Mã có ""ngoặc kép"""
"  giữ nguyên khoảng trắng  "
```

Dòng vật lý trống bên ngoài một ô có ngoặc kép được bỏ qua. Ô rỗng `""`, ô chỉ có khoảng trắng, dòng thiếu giá trị, cột thừa hoặc tiêu đề `code`, `serial`, `GTIN` đều không hợp lệ. Xuống dòng bên trong ô có ngoặc kép là một phần của mã và chỉ tính là **một** bản ghi.

### JSON UTF-8

File là một mảng object. Mỗi object chứa duy nhất một thuộc tính `CodeContent` có giá trị chuỗi:

```json
[
  { "CodeContent": "BOX-001" },
  { "CodeContent": "https://first.invalid/21/SAME" },
  { "CodeContent": "https://second.invalid/21/SAME" }
]
```

Các ví dụ sau bị từ chối:

```json
["BOX-001"]
```

```json
[{ "CodeContent": "BOX-001", "serial": "001" }]
```

```json
{ "poName": "PO-001", "orderQty": 1, "codes": ["BOX-001"] }
```

Object thiếu thuộc tính, thuộc tính sai hoa thường, thuộc tính trùng, giá trị `null`/số/object/array, mã trống và envelope `codes` hoặc `csv` đều không được nhận. Metadata PO phải nằm ngoài file.

### XLSX

Sheet đầu tiên là bảng có duy nhất cột A, ô A1 là `CodeContent`; các dòng tiếp theo chứa mã. Lưu mã bằng kiểu văn bản để giữ số 0 đầu và nội dung chính xác. Không thêm cột GTIN/serial/metadata hoặc dùng công thức làm mã.

`.xls` BIFF legacy không được hỗ trợ trong contract mới. Hãy xuất CSV UTF-8, JSON hoặc XLSX một cột.

## Metadata PO

Metadata nằm trong query string khi gửi file thuần, hoặc trong các field riêng khi dùng multipart. Body file chỉ chứa tập mã.

| Tên | Bắt buộc | Ý nghĩa |
|---|---|---|
| `poName` | Có | Mã PO không rỗng, tối đa 200 ký tự. |
| `orderQty` | Có | Số bản ghi mã của **lần upload này**, số nguyên dương, phải khớp chính xác số bản ghi trước lọc trùng. |
| `batchCode` | Không | Mô tả mã lô của PO. |
| `productName` | Không | Mô tả tên sản phẩm. |
| `customer` | Không | Mô tả khách hàng. |

Không gửi `gtin`, `serial`, `codes` hoặc `csv` trong metadata. Các trường này không thay thế cho file CodeContent. Multipart nhận đúng một file; không gửi nhiều file trong cùng request.

Ví dụ file có 5 dòng mã, trong đó hai dòng giống hệt nhau: `orderQty=5`, `accepted=4`, `ignoredDuplicates=1`. Nếu gửi `orderQty=4` hoặc `6`, server trả `400` và không lưu dữ liệu.

## Gửi request

Các ví dụ dùng `curl.exe` trên Windows. Thay `<API_KEY>` bằng giá trị cấu hình của máy; bỏ header khi `PoServer:ApiKey` rỗng.

### File CSV với metadata trong query

```powershell
curl.exe --request POST "http://127.0.0.1:8085/api/po?poName=PO-001&orderQty=4" `
  --header "x-api-key: <API_KEY>" `
  --header "Content-Type: text/csv; charset=utf-8" `
  --data-binary "@codes.csv"
```

`text/csv` được nhận trực tiếp, không bắt buộc header filename. Khi gửi kiểu file khác, dùng `Content-Disposition: attachment; filename="codes.xlsx"` hoặc `X-Filename` để chỉ rõ định dạng phù hợp.

### File JSON với metadata trong query

```powershell
curl.exe --request POST "http://127.0.0.1:8085/api/po?poName=PO-JSON-001&orderQty=3" `
  --header "x-api-key: <API_KEY>" `
  --header "Content-Type: application/json; charset=utf-8" `
  --data-binary "@codes.json"
```

Body vẫn là mảng object CodeContent của file JSON, không phải object PO có `codes`/`csv`.

### Multipart với metadata và một file

```powershell
curl.exe --request POST "http://127.0.0.1:8085/api/po" `
  --header "x-api-key: <API_KEY>" `
  --form "poName=PO-XLSX-001" `
  --form "orderQty=3" `
  --form "batchCode=LOT-001" `
  --form "productName=San pham A" `
  --form "file=@codes.xlsx;type=application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
```

Client tự tạo boundary cho multipart; không tự gán header `Content-Type: multipart/form-data` thiếu boundary.

### C# gửi CSV

```csharp
using var client = new HttpClient();
using var file = File.OpenRead("codes.csv");
using var content = new StreamContent(file);
content.Headers.ContentType = new("text/csv");
using var request = new HttpRequestMessage(
    HttpMethod.Post, "http://127.0.0.1:8085/api/po?poName=PO-001&orderQty=4")
{
    Content = content,
};
request.Headers.Add("x-api-key", "<API_KEY>");
using var response = await client.SendAsync(request);
Console.WriteLine(await response.Content.ReadAsStringAsync());
response.EnsureSuccessStatusCode();
```

## Response và quy tắc trùng

Response thành công:

```json
{
  "success": true,
  "message": "Đã nhận PO PO-001 với 4 mã mới.",
  "poName": "PO-001",
  "accepted": 4,
  "ignoredDuplicates": 0
}
```

`accepted` là số mã mới lưu vào PO đích trong request này. `ignoredDuplicates` là số dòng bỏ qua do lặp nguyên CodeContent trong cùng file hoặc đã có trong **chính PO đích**. Gửi lại cùng file cho cùng PO chưa bắt đầu có thể trả `200`, `accepted=0`; không ghi lại trạng thái cũ. Nội dung giống nhau trong PO khác vẫn được lưu độc lập theo PO. Số thống kê PO lấy từ bản ghi đã lưu; không dùng bộ đếm tổng của PLC làm số mã PO.

PO đã bắt đầu nhưng chưa kết thúc chặn nhận thêm dữ liệu. Khi còn PO dở dang, server trả `409` cho thao tác nhập. PO đã hoàn tất, hủy hoặc đóng không được mở lại bằng upload. Với PO chưa bắt đầu, việc bổ sung không ghi đè trạng thái mã đã tồn tại.

Một file có bất kỳ bản ghi/schema không hợp lệ làm request bị từ chối toàn bộ: không tạo PO mới và không thêm một phần mã vào PO cũ. `orderQty` phải khớp chính xác số bản ghi hợp lệ trước lọc trùng; không tự bỏ dòng sai để nhập phần còn lại.

## Health và lỗi

```powershell
curl.exe --header "x-api-key: <API_KEY>" "http://127.0.0.1:8085/api/health"
```

Health trả `success`, `message`, `prefix`, `received`, `startedUtc`. `received` là số lần nhận có ghi dữ liệu thành công từ lúc bật listener, không phải số mã hay số PO duy nhất.

| HTTP | Trường hợp |
|---|---|
| `200` | Nhận thành công, kể cả upload lại cùng PO và không có mã mới. |
| `400` | Metadata không hợp lệ, sai schema/số dòng, file lỗi/không được hỗ trợ, inline envelope hoặc nhiều file. |
| `401` | Thiếu hoặc sai `x-api-key`. |
| `404` | Endpoint không tồn tại. |
| `408` | Request quá hạn hoặc bị hủy trước khi hoàn tất. |
| `409` | Còn PO dở dang hoặc PO đích đã kết thúc. |
| `413` | Request hoặc file vượt dung lượng cấu hình. |
| `500` | Lỗi lưu/đọc dữ liệu; xem log trên máy chạy. |

Response lỗi vẫn có `success=false`, `message`, `poName`, `accepted=0` và `ignoredDuplicates`. Hãy đọc `message` để xác định lỗi thực tế. Nếu timeout phía MES, kiểm tra lại PO và có thể gửi lại cùng file; việc lọc trùng trong PO ngăn thêm lại mã đã lưu.

Trên Windows, lỗi bind địa chỉ LAN cần kiểm tra URL ACL và firewall cho đúng cổng được cấu hình. Health không truy cập được thì xử lý listener/kết nối trước khi kiểm tra file. Health hoạt động nhưng POST bị `400` thì kiểm tra tên cột `CodeContent`, metadata bên ngoài file, số bản ghi và định dạng UTF-8.
