# Trang tạo PO cho TanTienApp

Trang nhập `orderNo`/LOT, planned `orderQty`, một GTIN có checksum và chọn một tệp CSV/JSON/XLSX chứa duy nhất cột `CodeContent`. Trang chuyển nguyên đối tượng `File` bằng multipart; không đọc, tách, chuẩn hóa hoặc viết lại nội dung mã. Máy chủ kiểm tra nội dung và số record trước lọc phải bằng `orderQty`.

## Mở trang và gửi PO local

1. Bật máy chủ nhận PO của TanTienApp với listener **loopback** (`127.0.0.1`, `localhost` hoặc `::1`) và xác định port đã cấu hình. Dùng môi trường/dữ liệu thử nghiệm.
2. Mở đúng `http://127.0.0.1:<port>/po-demo/` trên listener đó. Ví dụ khi port là `8085`: `http://127.0.0.1:8085/po-demo/`. Trang không có trường chỉnh endpoint; POST luôn đến relative `/api/po` cùng origin.
3. Nhập LOT, planned quantity và GTIN dài 8/12/13/14 chữ số có checksum. Chọn tệp một cột `CodeContent`. Ví dụ metadata GTIN `4006381333931` khớp mã AI (01) GTIN-14 `04006381333931`.
4. Nếu máy chủ có cấu hình key, nhập key vào trường **API key**. Chọn **Kiểm tra thông tin**, xem tóm tắt rồi bấm **Gửi PO đến TanTienApp**. Kiểm tra phía trình duyệt chỉ xác nhận trường nhập; máy chủ chịu trách nhiệm kiểm tra file.
5. Đọc kết quả và phần chi tiết mã bị loại. Mỗi lần gửi chỉ chạy một request; các trường bị khóa trong lúc chờ. Sau kết quả phải kiểm tra lại thông tin trước khi gửi tiếp.

Trang và `validation.js` được nhúng vào assembly, phục vụ qua đúng hai route `GET /po-demo/` và `GET /po-demo/validation.js` (MIME JavaScript). Sửa HTML/JS cần **rebuild và khởi động lại host** để dùng bản mới. README và file kiểm thử không phải asset runtime. Không cần CORS hoặc static root trên đĩa. Xem contract và cấu hình trong [`docs/MES-PO-API.md`](../MES-PO-API.md).

## Tệp mẫu

Lưu CSV dưới đây với planned quantity `4` và GTIN `4006381333931`. Đây là dữ liệu thử nghiệm: một mã hợp lệ, một bản trùng, một mã sai cấu trúc, một mã có GTIN khác.

```csv
CodeContent
https://example.test/01/04006381333931/21/PO-DEMO-001
https://example.test/01/04006381333931/21/PO-DEMO-001
ma-khong-hop-le
https://example.test/01/09506000134352/21/PO-DEMO-002
```

Với PO mới, kết quả dự kiến là nhận `1/4`, sai cấu trúc `1`, GTIN không khớp `1`, trùng `1`. `acceptedCount` là số **mã mới** được lưu trong request, không phải tổng mã hiện có của PO. Không thay planned quantity bằng số accepted.

## Kết quả và lỗi

- `200`, nhận đủ hoặc một phần: hiển thị `acceptedCount`/`plannedCount`, số `invalidStructure`, `gtinMismatch`, số `duplicateCodes`; cảnh báo nếu số nhận mới khác planned hoặc planned phản hồi khác form. Phần chi tiết giữ nội dung raw và lý do, hiển thị tối đa 200 record đầu để tránh làm treo trang. Lý do mới chưa nhận biết vẫn hiện trong chi tiết.
- `200`, toàn mã trùng: thông báo **Không có mã mới được nhận**, không coi là đã bổ sung mã hoặc lỗi kết nối.
- `401`: yêu cầu nhập lại key. `409`: hiển thị xung đột trạng thái/tên PO. `422`: hiển thị lỗi GTIN hoặc mã; lỗi metadata có thể không có danh sách mã bị loại. Các lỗi `400`/`413`/`415` và lỗi khác hiển thị thông báo máy chủ khi có JSON hợp lệ.
- Mất kết nối, timeout 60 giây, HTTP `408`/`5xx` hoặc phản hồi `2xx` không đọc được: báo **chưa xác định đã lưu hay chưa**. Kiểm tra PO trong TanTienApp trước khi thử lại; trang không tự retry.
- Khi sửa trường metadata hoặc tệp, tóm tắt/kết quả cũ bị xóa và nút gửi bị khóa đến lần kiểm tra tiếp theo.

## API key

Key là password không có `name`, không đưa vào FormData, URL, summary hay browser storage. Key chỉ truyền qua header `x-api-key` nếu được nhập; không ghi log. Ô nhập được xóa ngay khi bắt đầu gửi, khi kết thúc và khi rời trang. Biến/header tạm được bỏ sau request; không có cơ chế ghi nhớ key hoặc tự điền cấu hình server. Browser/devtools vẫn có thể xem request do chính người sử dụng gửi, vì vậy chỉ dùng key thử nghiệm khi ghi hình hoặc chụp network.

## Xem trước và kiểm thử

Mở trực tiếp HTML hoặc chạy preview tĩnh ở origin riêng để xem form:

```powershell
python -m http.server 8765 --bind 127.0.0.1 --directory docs/po-demo
```

Mở `http://127.0.0.1:8765/`. Nút gửi luôn khóa ở preview; không dùng origin riêng để gửi tới receiver. Luồng gửi chỉ bật ở path `/po-demo/` trên origin loopback, sau khi kiểm tra thông tin.

Chạy các test thuần Node.js; fetch trong test được thay thế, không có request thật:

```powershell
node --test docs/po-demo/*.test.mjs
```

Browser smoke phải chạy trên **PoHttpServer thật đã rebuild** với temp store/key thử nghiệm: route HTML và JS MIME, GTIN sai/đúng, input đổi khóa gửi, `401`, nhận partial, all duplicates, `400` quantity/file, `422`, `409`, network error, khóa gửi kép, key không xuất hiện trong storage/summary và bố cục mobile. Host mock hoặc server preview không thay thế kiểm tra này.
