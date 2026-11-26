# Nghiệm thu HTTP PO độc lập

Suite exe `net10.0` này khởi động `PoHttpServer` thật trên cổng ngẫu nhiên của `127.0.0.1`, dùng key giả lập và SQLite/tệp mẫu trong thư mục temp duy nhất. Không dùng dữ liệu sản xuất, không gọi production và không mở cổng LAN.

Chỉ chạy sau khi chủ sở hữu backend xác nhận snapshot nguồn đã sẵn sàng:

```powershell
dotnet run --project tools/PoAcceptanceTests/PoAcceptanceTests.csproj -c Release
```

Mã thoát `0` chỉ có nghĩa tất cả assertion đã chạy đều đạt. Mã `1` có ít nhất một nhóm lỗi; assertion lỗi dừng nhóm tương ứng, những nhóm khác tiếp tục để thu thập thêm bằng chứng. Suite in đường dẫn temp và giữ DB/tệp mẫu để chẩn đoán. Nó không in key giả lập trong thông báo lỗi.

Phạm vi bao gồm:

- Multipart CSV/JSON/XLSX cùng bốn record: valid, duplicate, invalidStructure, gtinMismatch; kiểm JSON trên wire, raw mã bị loại và mã đã lưu.
- Dừng/khởi động lại server cùng DB: planned `orderQty=4`, actual `1`, GTIN chuẩn hóa và raw ordinal; GTIN khác sau restart bị từ chối; replay không làm thay dữ liệu.
- Metadata thiếu/sai checksum/nhiều GTIN, planned thấp/cao hơn record count, append sai planned không mutation, alias `poName`, alias lặp, toàn mã sai và toàn trùng.
- GTIN metadata 8/12/13/14 khớp mã GTIN-14, phân biệt toàn bộ URL/case, từ chối AI (01) lặp chứa GTIN mâu thuẫn.
- `GET /po-demo/` cùng origin loopback có configured key: trả HTML embedded dù working directory có file giả; không nhúng key. Asset runtime chính xác là `validation.js` với MIME JavaScript; file test/README không được phục vụ. API thiếu/sai key trả 401, đúng key nhận file. Các method khác và đường dẫn lạ/traversal không trả HTML/tệp trên đĩa. Traversal được gửi qua TCP thô để tránh `HttpClient` tự chuẩn hóa URI trước khi gửi.

Giới hạn: đây là suite receive/static/persistence, không phải browser smoke, download parity hoặc kiểm định phát hành single-file/Native AOT. Suite chỉ bind loopback; hành vi bind wildcard/LAN cần kiểm tra riêng có chủ đích, không được suy ra từ kết quả này. GTIN persistence được kiểm qua metadata sau reload và guard append; suite không assert trực tiếp property `LocalPoInfo.Gtin`, nên kết quả này không thay thế các kiểm tra domain/download tương ứng.
