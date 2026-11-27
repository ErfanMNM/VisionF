# API nhận PO từ MES

Cập nhật: 2026-11-26. API nhận metadata PO và đúng một file CSV/JSON/XLSX có cột `CodeContent`. Mỗi PO dùng đúng một GTIN có checksum. Server giữ nguyên chuỗi mã gốc để lưu, đối chiếu trùng và báo lỗi; GTIN được tách riêng chỉ để xác thực cấu trúc và giới hạn sản phẩm.

## Endpoint và cấu hình

| Endpoint | Mục đích |
|---|---|
| `GET /api/health` | Kiểm tra listener và số lần nhận có ghi dữ liệu thành công. |
| `POST /api/po` | Nhận file, tạo PO hoặc bổ sung vào PO chưa bắt đầu. |
| `GET /po-demo/` | Trang tạo PO demo được nhúng trong assembly. |
| `GET /po-demo/validation.js` | Mã kiểm tra phía trình duyệt được nhúng trong assembly. |

Base URL mặc định: `http://127.0.0.1:8085`. Bật listener theo cấu hình:

```json
{
  "PoServer": {
    "Enabled": true,
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

Khi cấu hình `ApiKey`, tất cả API vẫn yêu cầu header `x-api-key` khớp chính xác, kể cả health. Chỉ hai route GET tĩnh nêu trên được mở không cần key khi **listener bind loopback**. Listener bind LAN/wildcard vẫn yêu cầu key cho cả tài nguyên tĩnh, kể cả request được gửi từ loopback. Không có CORS wildcard hoặc static file server trên đĩa; path khác, alias `/po-demo/index.html`, method khác và traversal không phục vụ tài nguyên nhúng. HTML và JS được đóng gói lúc build; sửa tài liệu nguồn UI cần build lại assembly.

`MaxBodyMB` giới hạn toàn request, `MaxFileMB` giới hạn file. Upload được stream xuống file tạm và dọn sau xử lý. API key chỉ gửi qua header, không đưa vào file, query, body, browser storage hay log. Trang demo gọi relative `/api/po` cùng origin và nhận key qua password input.

## Metadata PO

Metadata nằm ở query khi gửi file thuần, hoặc ở các field multipart riêng. Không nhét metadata vào bảng mã.

| Tên | Bắt buộc | Ý nghĩa |
|---|---|---|
| `orderNo` | Có | Mã PO không rỗng, tối đa 200 ký tự. `poName` là alias legacy; `po`/`lot` cũng được hỗ trợ. |
| `orderQty` | Có | Số lượng **planned**, nguyên dương. Phải khớp số record gốc trong file trước mọi bước lọc. Alias `qty` được hỗ trợ. |
| `gtin` | Có | Đúng một GTIN gồm 8/12/13/14 chữ số ASCII và checksum đúng, không khoảng trắng hoặc danh sách. |
| `batchCode` | Không | Mã lô; alias `batch`. |
| `productName` | Không | Tên sản phẩm; alias `product`. |
| `customer` | Không | Khách hàng. |

Một trường và alias của nó chỉ được gửi **một lần** trên toàn query + multipart. Ví dụ cùng gửi `orderNo` và `poName`, hoặc lặp `gtin`, bị `400`. Metadata không hỗ trợ như `serial`, `codes`, `csv` cũng bị `400`. Multipart phải có đúng một file.

GTIN hợp lệ được giữ nguyên khi lưu metadata PO và gửi Checkee; nhập `4006381333931` thì vẫn lưu và gửi đúng 13 chữ số đó, không tự thêm số 0 đầu. Khóa GTIN 14 chữ số chỉ dùng để so sánh nội bộ: metadata `4006381333931` vẫn khớp AI (01) `04006381333931`. GTIN 14 chữ số được cung cấp rõ ràng cũng được giữ nguyên, kể cả số 0 đầu. Không chuẩn hóa, cắt, trim hoặc đổi hoa thường `CodeContent` để dedupe.

`orderQty` không bị thay bằng accepted/actual sau khi lọc. Với request hợp lệ có mã mới hoặc toàn duplicate, planned đã lưu là `max(planned hiện có, orderQty lần này)`, không cộng lại target và không hạ target do batch nhỏ hơn. Actual là số mã thực trong DB; vì lọc hoặc bổ sung, actual và planned có thể khác nhau. Upload toàn duplicate vẫn tăng planned nếu cần, nhưng giữ metadata mô tả, các mã và lịch sử scan cũ.

## File một cột CodeContent

Tên cột/thuộc tính phải đúng **`CodeContent`**, phân biệt hoa thường. CSV UTF-8, JSON UTF-8 và sheet đầu của XLSX được hỗ trợ; `.xls` BIFF cũ không được hỗ trợ.

```csv
CodeContent
(01)09506000134352(21)Case
(01)09506000134352(21)case
https://first.invalid/01/09506000134352/21/SAME
https://second.invalid/01/09506000134352/21/SAME
```

Bốn mã trên khác nhau theo toàn bộ raw string. JSON dùng mảng object, mỗi object chỉ có một thuộc tính chuỗi:

```json
[
  { "CodeContent": "(01)09506000134352(21)Case" },
  { "CodeContent": "https://first.invalid/01/09506000134352/21/SAME" }
]
```

CSV dùng quoting chuẩn cho dấu phẩy, dấu ngoặc kép hoặc xuống dòng trong một ô. Dòng vật lý trống ngoài ô có quote được bỏ qua; xuống dòng bên trong ô là một phần mã và chỉ tính một record. XLSX có ô A1 là `CodeContent`, các ô mã trong cột A phải là văn bản để giữ số 0 đầu; không nhận công thức hoặc cột thừa.

Lỗi **schema/file** từ chối toàn request `400`: header sai, nhiều cột, object thừa/thiếu/trùng thuộc tính, array chuỗi, `null`/số thay chuỗi, ô rỗng/chỉ khoảng trắng, file rỗng/hỏng hoặc multipart thiếu phần kết thúc. Không ghi một phần file lỗi vào PO cũ.

Sau khi file đúng schema, server xét từng mã theo `PoCodeParser`: Digital Link `/01/<GTIN>/21/<serial>` (có thể có host/prefix phù hợp), dạng `(01)<GTIN-14>(21)<serial>`, hoặc element string AI (01)/(21). Serial phải dài 1–20 ký tự trong tập ký tự parser hỗ trợ. Mã opaque text, khoảng trắng thừa, Unicode/xuống dòng không hợp grammar hoặc định danh AI lặp bị báo `invalidStructure`; raw vẫn được trả nguyên vẹn. Đây là các grammar cụ thể của parser, không phải cam kết nhận mọi biểu diễn GS1.

## Quy tắc lọc và trạng thái

Thứ tự cho mỗi record trong file hợp lệ:

1. Không tách được GTIN/cấu trúc/checksum mã không hợp lệ: thêm `{code, reason: "invalidStructure"}` vào `invalidCodes`.
2. GTIN mã không tương đương GTIN metadata theo khóa đối chiếu nội bộ: thêm `{code, reason: "gtinMismatch"}` vào `invalidCodes`.
3. Raw trùng ordinal trong file hoặc đã có trong **chính PO đích**: thêm raw vào `duplicateCodes`.
4. Còn lại: lưu nguyên `CodeContent` và tăng `acceptedCount`.

Một mã sai lặp lại vẫn được báo sai theo thứ tự trên, không đổi thành duplicate. Cùng raw trong PO khác vẫn độc lập. Không dùng serial/hậu tố URL để dedupe.

Ví dụ file có bốn record: một mã đúng GTIN, một bản lặp của nó, một chuỗi sai cấu trúc và một mã GTIN khác. Gửi `orderQty=4`: `acceptedCount=1`, `plannedCount=4`, hai `invalidCodes`, một `duplicateCodes`; DB giữ planned=4, actual=1. Gửi `orderQty=3` hoặc `5`: `400`, không thay DB.

PO đã gắn GTIN khác bị `422` trước mutation. Với PO legacy, store chỉ suy GTIN khi có bằng chứng metadata hợp lệ hoặc toàn bộ mã cũ cùng một GTIN hợp lệ; append còn kiểm tra mọi mã đã lưu. PO thiếu GTIN không xác định được, có mã legacy sai hoặc mã trộn bị `422`; không tự sửa code/lịch sử scan cũ để cho nhập.

Khi còn PO đã chạy nhưng chưa kết thúc, upload bị `409`. PO đã hoàn tất, hủy hoặc đóng không được mở lại bằng upload. Append vào PO chưa bắt đầu giữ nguyên identity, lịch sử và trạng thái mã cũ. Lỗi lưu SQLite rollback cả mã lẫn metadata trong lần ghi.

## Gửi request

Ví dụ Windows `curl.exe`; thay `<API_KEY>` bằng cấu hình cục bộ và bỏ header khi không cấu hình key.

```powershell
curl.exe --request POST "http://127.0.0.1:8085/api/po?orderNo=PO-001&orderQty=4&gtin=09506000134352" `
  --header "x-api-key: <API_KEY>" `
  --header "Content-Type: text/csv; charset=utf-8" `
  --data-binary "@codes.csv"
```

Gửi JSON cũng dùng metadata ở query và `Content-Type: application/json`; body vẫn là mảng object CodeContent, không phải envelope PO có `codes`/`csv`. Có thể chỉ rõ filename bằng `Content-Disposition: attachment; filename="codes.json"` hoặc `X-Filename`.

```powershell
curl.exe --request POST "http://127.0.0.1:8085/api/po" `
  --header "x-api-key: <API_KEY>" `
  --form "orderNo=PO-XLSX-001" `
  --form "orderQty=3" `
  --form "gtin=09506000134352" `
  --form "batchCode=LOT-001" `
  --form "file=@codes.xlsx;type=application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
```

Client/`FormData` tự đặt boundary multipart; không tự gán `Content-Type: multipart/form-data` thiếu boundary.

## Response

Ví dụ nhận một phần:

```json
{
  "success": true,
  "message": "Đã nhận 1/4 mã từ file cho PO PO-001; loại 2 mã sai và 1 mã trùng.",
  "orderNo": "PO-001",
  "poName": "PO-001",
  "accepted": 1,
  "acceptedCount": 1,
  "plannedCount": 4,
  "ignoredDuplicates": 1,
  "invalidCodes": [
    { "code": "raw-invalid", "reason": "invalidStructure" },
    { "code": "(01)00012345678905(21)Other", "reason": "gtinMismatch" }
  ],
  "duplicateCodes": ["(01)09506000134352(21)Case"]
}
```

`acceptedCount` là mã mới lưu trong request, `plannedCount` là `orderQty` của request. Alias response legacy `poName`, `accepted`, `ignoredDuplicates` còn được giữ. Danh sách chứa raw theo thứ tự record bị loại; không có trường `rejectedCodes` tổng hợp riêng. Client cần đọc HTTP status, `success` và cả hai danh sách để phân biệt nhận một phần, validation, duplicate và lỗi vận chuyển.

| HTTP | Trường hợp |
|---|---|
| `200` | Có mã mới được lưu (có thể partial); hoặc toàn bộ mã hợp lệ đã trùng, `success=true`, accepted=0. |
| `400` | Metadata lặp/không hỗ trợ, thiếu orderNo/orderQty, sai số record/schema/file hoặc nhiều file. |
| `401` | Thiếu/sai `x-api-key` ở route được bảo vệ. |
| `404` | Endpoint/method không hỗ trợ. |
| `408` | Timeout/hủy request. |
| `409` | Còn PO đang chạy, PO đích đã kết thúc hoặc tên PO đụng file sau chuẩn hóa. |
| `413` | Vượt giới hạn body/file. |
| `415` | Không nhận diện được kiểu upload. |
| `422` | GTIN metadata thiếu/sai/khác PO, legacy không an toàn; hoặc accepted=0 và có ít nhất một mã sai. |
| `500` | Lỗi đọc/lưu DB; xem log cục bộ. |

Nếu toàn invalid (hoặc invalid cộng duplicate nhưng không có mã mới), trả `422`, `success=false`, accepted=0 và không ghi DB. Nếu toàn duplicate hợp lệ, trả `200`, accepted=0, vẫn persist planned max khi tăng; không sửa mã/scan hoặc metadata mô tả. Response lỗi vẫn có shape trên nhưng các field chưa xác định có thể rỗng/0.

Health có xác thực trả `success`, `message`, `prefix`, `received`, `startedUtc`. `received` là số lần upload có ghi mã mới thành công từ lúc bật listener, không phải số mã hoặc số PO duy nhất. Timeout phía client là lỗi vận chuyển; kiểm tra trạng thái rồi replay cùng file nếu cần, không coi response validation là lỗi mạng hoặc coi HTTP lỗi là thành công rỗng.

## Kiểm chứng cục bộ

```powershell
dotnet run --project tools/PoServerTests/PoServerTests.csproj --no-restore
dotnet run --project tools/PoServerTests/e2e/PoServerE2E.csproj --no-restore
```

Bộ kiểm thử kiểm CSV/JSON/XLSX, partial/raw/planned/reload, metadata nguyên tử, alias, giữ nguyên GTIN 8/12/13/14 chữ số khi lưu và đọc lại, đối chiếu GTIN tương đương/append legacy, lifecycle, lỗi DB, auth và route nhúng loopback/wildcard/path/method. Browser smoke của trang demo cần chạy sau khi UI nguồn hoàn tất và build lại assembly; xem `docs/po-demo/README.md`.

Host cục bộ dùng đúng `PoHttpServer`, store thư mục tạm mới và key công khai dành riêng cho thử nghiệm `po-demo-local-test-key`:

```powershell
dotnet run --project tools/PoServerTests/PoServerTests.csproj --no-restore -- --serve --port 18085
```

Mở `http://127.0.0.1:18085/po-demo/`; Ctrl+C để dừng. Bỏ `--port` để tự chọn cổng trống và đọc URL trong stdout. Host này không đọc cấu hình/key thật hoặc DB production; thư mục store tạm được in ra để kiểm chứng sau browser smoke. Không có API giả lập.

MES simulator cũng phải cấu hình `MesSimulator:Gtin` hợp lệ để tạo/upload file GS1. `CustomOrderNo` được ưu tiên, `CustomPoName` còn là alias cũ. Khi dùng client API C#, tham số override `gtin` tùy chọn nằm sau `CancellationToken` để giữ tương thích vị trí tham số cũ; không bỏ GTIN khỏi request thực tế.
