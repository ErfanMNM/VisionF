# MES Simulator và server PO thử nghiệm

Có ba thành phần phục vụ các hướng trao đổi khác nhau:

| Thành phần | Vai trò | Địa chỉ mặc định |
|---|---|---|
| `ClassLibraryMT.PoServer` trong TanTienQR | Nhận file PO từ MES qua `POST /api/po`; cung cấp `GET /api/health`. | `http://127.0.0.1:8085` |
| `ClassLibraryMT.MesSimulator` trong TanTienQR | Client gửi file PO tới listener nêu trên. Không mở API GET hay kích hoạt. | `MesSimulator:TargetUrl` trỏ tới cổng 8085. |
| Console `tools/PoServer` | Server PO thử nghiệm để ứng dụng tải danh sách/mã và gửi kích hoạt. | `http://127.0.0.1:5080` |

`Po:BaseUrl` trỏ tới server tải/kích hoạt; `MesSimulator:TargetUrl` trỏ tới listener nhận file. Hai cấu hình này không thay thế nhau. Định dạng nhập chính thức nằm ở [MES-PO-API.md](MES-PO-API.md), vòng đời và lưu trạng thái nằm ở [production-order.md](production-order.md).

## Gửi file CodeContent có sẵn

Simulator gửi `multipart/form-data` với một file CSV, JSON hoặc XLSX. File chứa đúng một cột/thuộc tính `CodeContent`. Metadata `poName`, `orderQty` và mô tả đi ở các field riêng; không gửi GTIN/serial và không bọc tập mã trong object PO inline.

Ví dụ CSV:

```csv
CodeContent
BOX-A-001
https://first.invalid/21/SAME
https://second.invalid/21/SAME
```

`OrderQty` phải bằng số bản ghi không rỗng trong file **trước lọc trùng**. CSV có dấu phẩy, dấu ngoặc kép hoặc xuống dòng bên trong mã phải quote đúng quy tắc CSV. JSON dùng object có thuộc tính `CodeContent` đúng hoa thường. XLSX lưu ô dưới dạng văn bản; `.xls` legacy không được nhận. Các [mẫu trong repo](examples/po-codecontent.csv) có thể dùng để thử.

Mặc định worker tắt. Để gửi một file ở lần khởi động ứng dụng:

```json
{
  "PoServer": {
    "Enabled": true,
    "Port": 8085,
    "BindAddress": "127.0.0.1",
    "ApiKey": ""
  },
  "MesSimulator": {
    "Enabled": true,
    "TargetUrl": "http://127.0.0.1:8085",
    "ApiKey": "",
    "IntervalSeconds": 30,
    "OrderQty": 3,
    "PoPrefix": "MES-TEST",
    "CustomFilePath": "D:/test-data/codes.csv",
    "CustomPoName": "PO-TEST-001",
    "TimeoutSeconds": 30
  }
}
```

Thay `CustomFilePath` bằng đường dẫn file thực tế và khởi động lại ứng dụng sau khi lưu cấu hình. Đường dẫn tương đối được tính từ thư mục ứng dụng. `CustomPoName` trống thì tên PO lấy từ tên file. Nếu listener có `ApiKey`, đặt cùng giá trị ở simulator.

Khi có `CustomFilePath`, worker upload file một lần rồi kết thúc; `IntervalSeconds` không làm file đó tự được gửi lại. Tắt `MesSimulator:Enabled` khi kết thúc kiểm thử. Không dùng tên, mã hoặc dữ liệu sản xuất thật cho các lượt thử.

Worker không tự sinh mã khi thiếu file. `IntervalSeconds` và `PoPrefix` được giữ để đọc cấu hình cũ, không tạo PO hay mã định kỳ trong chế độ hiện tại.

Một host .NET khác có thể đăng ký `services.AddMesSimulator(configuration)`. Khi đã có `MesSimulatorClient`, gọi trực tiếp để gửi theo yêu cầu:

```csharp
var result = await client.UploadExistingFileAsync(
    "PO-TEST-001", 3, @"D:\test-data\codes.csv", ct: cancellationToken);
```

Trong mã kiểm thử, `UploadTestOrderAsync` nhận danh sách CodeContent được cung cấp rõ ràng rồi tạo file CSV một cột có quote đầy đủ trước khi gửi multipart. Hàm này không suy GTIN, không sinh hậu tố serial và không gửi tập mã bằng JSON PO inline:

```csharp
var result = await client.UploadTestOrderAsync(
    "PO-TEST-002", ["BOX-A-001", "  mã,nguyên vẹn  "], cancellationToken);
```

Client trả `MesSimulatorResult` chứa tên PO, cờ thành công, HTTP status và body phản hồi. Lỗi mạng/timeout không tự biến thành một PO mới. Kiểm tra phản hồi và trạng thái PO trước khi gửi lại.

## Kết quả nhập và thử lại

Nhập lại cùng file vào **chính PO chưa bắt đầu** giữ nguyên trạng thái cũ, chỉ nhận mã mới và trả số dòng bỏ qua trong `ignoredDuplicates`. Khi không có mã mới, server có thể trả `200` với `accepted=0`. Cùng CodeContent trong PO khác vẫn được lưu độc lập.

Khi còn bất kỳ PO đang chạy hoặc dở dang, listener trả `409` cho thao tác nhập. PO đã hoàn tất, hủy hoặc đóng không được mở lại bằng upload. Schema, metadata hoặc số lượng sai trả `400` trước khi lưu; giới hạn dung lượng trả `413`. Chi tiết response và các mẫu gửi raw-file/multipart nằm ở [hướng dẫn API](MES-PO-API.md).

## Server thử nghiệm cho tải và kích hoạt

Chạy console riêng từ thư mục repo:

```powershell
dotnet run --project tools/PoServer/PoServer.csproj
```

Console mặc định lắng nghe cổng 5080; có thể truyền prefix HTTP làm đối số thứ nhất và đường dẫn DB làm đối số thứ hai. Đây là môi trường thử nghiệm độc lập với listener cổng 8085 trong ứng dụng. DB mặc định nằm ở `logs/po-server.db` tương đối với thư mục output của console.

Console mới khởi động với danh sách rỗng, không seed mã tự sinh. Mở trang gốc, chọn file CodeContent, nhập mã PO và số bản ghi để import. `POST /api/po` của console cũng dùng hợp đồng file thuần hoặc multipart như listener; metadata đi riêng. Ví dụ:

```powershell
curl.exe --request POST "http://127.0.0.1:5080/api/po?poName=PO-TEST-001&orderQty=3" --header "Content-Type: text/csv" --data-binary "@D:/test-data/codes.csv"
```

Store giữ mã theo cặp PO và toàn bộ CodeContent. Khi mở DB console cũ, schema mã được chuyển sang khóa theo PO trong transaction, giữ nguyên nội dung mã và timestamp kích hoạt đã có.

Các API phục vụ `PoClient`:

| Method | Endpoint | Nội dung |
|---|---|---|
| POST | `/api/po` | Import file CodeContent với metadata tách riêng; không nhận yêu cầu sinh mã từ số lượng. |
| GET | `/api/po` | Danh sách PO còn hiệu lực. |
| GET | `/api/po?includeCompleted=true` | Danh sách gồm cả PO đã hoàn tất/đóng; ứng dụng dùng biến thể này khi đồng bộ. |
| GET | `/api/po/{orderNo}/codes` | Tập mã đầy đủ của PO, có trạng thái kích hoạt đã lưu. |
| POST | `/api/activate` | Nhận mã cần kích hoạt theo đúng `orderNo` và `codeContents`. |

API trao đổi giữa `PoClient` và server dùng camelCase, khác tên cột file import `CodeContent` đúng hoa thường. Ví dụ một phần tử từ API mã là `{ "codeContent": "BOX-A-001" }`; trạng thái có thể kèm `activatedUtc`.

Payload kích hoạt:

```json
{
  "orderNo": "PO-TEST-001",
  "codeContents": ["BOX-A-001", "https://first.invalid/21/SAME"]
}
```

Mỗi giá trị `codeContents` là nguyên CodeContent; không gửi phần đuôi serial. Server trả `ok`, `activated`, `message`. Nếu một mã không thuộc PO, toàn bộ lượt kích hoạt bị từ chối mà không đánh dấu một phần; gửi lại mã đã được kích hoạt là idempotent. Việc gửi kích hoạt không thay thế nhập file và không có ở listener chỉ nhận PO. Trong ứng dụng, các mã đã Pass nhưng chưa có ACK kích hoạt được giữ trong DB và gửi lại theo worker, kể cả PO đã kết thúc hoặc sau khởi động lại; xem [vòng đời PO](production-order.md).

## Kiểm thử cục bộ

```powershell
dotnet run --project tools/PoServerTests/PoServerTests.csproj
dotnet run --project tools/PoServerTests/e2e/PoServerE2E.csproj
dotnet run --project tools/PoFlowTests/PoFlowTests.csproj
dotnet run --project tools/MesSimulatorTests/MesSimulatorTests.csproj
dotnet run --project tools/PoServer/Tests/PoConsoleTests.csproj
```

Chạy tuần tự. Các harness dùng HTTP localhost và DB tạm; không xác nhận kết nối tới MES hoặc thiết bị của dây chuyền thực tế.
