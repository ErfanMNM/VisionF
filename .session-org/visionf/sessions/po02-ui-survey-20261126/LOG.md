# Nhật ký khảo sát PO-02 (chỉ đọc)

- Ngày: 2026-11-26
- Agent: `/root/ui_site_survey`
- Thread ID đã xác minh qua `CODEX_THREAD_ID`: `01a21734-cdb2-7711-82e7-f90dddd583e1`.
- Budget riêng lần kiểm tra: 5/100 request hoàn tất, `normal`, nhật ký khớp chính xác; kiểm tra bằng `C:\Users\THUC\.codex\skills\session-ops\scripts\request_budget.py`.
- Phạm vi: chỉ khảo sát repo và API, không sửa source hoặc hồ sơ phòng/session khác.

## Kết quả

- Vị trí phù hợp nhất đang có cho một static demo/client là `tools/PoServer/www/index.html`: console project đã copy `www/**` vào output qua `tools/PoServer/PoServer.csproj` và `PoConsoleServer` phục vụ file này tại URL root. Đường dẫn được ghép từ `AppContext.BaseDirectory/www/index.html` ở nhánh GET `/` hoặc `/index.html` trong `tools/PoServer/PoConsoleServer.cs`.
- Trang hiện tại mang tên “Server PO thử nghiệm”; form upload một file CSV/JSON/XLSX chứa cột `CodeContent`, sau đó nó gọi `POST /api/po` và tải bảng PO bằng `GET /api/po?includeCompleted=true` (nội dung `tools/PoServer/www/index.html`). Những route này là API của console test server chạy mặc định tại `http://127.0.0.1:5080/`, không phải route của receiver trong TanTienQR. README project còn ghi tool này là server thử nghiệm độc lập dùng cho tải PO/kích hoạt.
- Receiver thực tế nằm trong `ClassLibraryMT.PoServer/PoHttpServer.cs`, được đăng ký từ `TanTienQR/Services/TanTienAppServices.cs`; endpoint là `POST /api/po`, health là `GET /api/health`, prefix mặc định theo `ClassLibraryMT.PoServer/PoServerOptions.cs` là `http://127.0.0.1:8085/`. Receiver hiện nhận metadata `poName`, `orderQty` và metadata tùy chọn `batchCode`/`productName`/`customer` qua field multipart hoặc query, cùng đúng một file CodeContent CSV/JSON/XLSX. Response hiện có `success`, `message`, `poName`, `accepted`, `ignoredDuplicates`; lỗi HTTP có thể là 400/401/408/409/413/415/500. Contract chi tiết được ghi trong `ClassLibraryMT.PoServer/PoServerContracts.cs`, `PoUploadHandler.cs`, `PoHttpServer.cs`, và `docs/MES-PO-API.md`.
- Theo contract hiện tại không có metadata `gtin`; `PoUploadHandler.CanonicalMetadataField` từ chối `gtin` và ghi rõ tập mã phải nằm trong file CodeContent, không nhận GTIN/serial/codes/csv ở metadata. Vì PO-01 đang chạy, đây chỉ là hiện trạng có thể thay đổi; UI không nên đóng cứng cho đến khi owner PO-01 chốt contract mới.
- Receiver không thiết lập CORS, không có nhánh OPTIONS/preflight trong `PoHttpServer`, và không có header `Access-Control-Allow-Origin` trong mã receiver. Vì vậy trang static được mở từ file hoặc host khác origin không thể dùng `fetch` để đọc response từ receiver; custom header `x-api-key` còn có thể kích hoạt preflight mà receiver chưa xử lý. Cần lựa chọn host same-origin/CORS đã được owner backend xác nhận hoặc cách chạy hỗ trợ khác trước khi hứa UX có thể đọc thông báo trả về; không tự thêm sửa backend vào phạm vi UI.
- Không tìm thấy Playwright, Puppeteer, Selenium, jsdom, Jest, Vitest hay bộ test browser trong cấu hình/path tìm kiếm của repo. Đã có test harness .NET, trong đó `tools/PoServerTests/e2e/Program.cs` thử HTTP localhost/API bằng DB tạm. Tài liệu `docs/MES-SIMULATOR.md` liệt kê các lệnh chạy test PoServer/PoFlow/console; các harness này không xác minh HTML/browser.
- Chạy console mock để xem trang: `dotnet run --project tools/PoServer/PoServer.csproj`, rồi mở `http://127.0.0.1:5080/`; đây chỉ xác minh UI nhập mã file cho mock server, không xác minh UI PO-02 gửi vào TanTienQR. Kiểm thử client thật cần thêm phương án chạy/host và contract đã chốt.

## Kiểm tra/giới hạn

- Read-only: đọc hồ sơ phòng, project, endpoint, contract và lệnh kiểm thử; dùng `rg`/`git status` để xác minh file hiện có.
- Không chạy test hoặc khởi động service vì nhiệm vụ chỉ khảo sát; không sửa source.
- Trạng thái working tree ban đầu đã có các file xóa và untracked từ các task/session khác; không chạm vào chúng.

## Bàn giao cho trưởng phòng

- Dùng các bằng chứng/đường dẫn nêu trên để chọn static area riêng nếu PO-02 không định biến console mock thành sản phẩm.
- Chờ PO-01 xác nhận role/ý nghĩa GTIN, metadata, status/body và endpoint cuối trước khi nối submit.
- Chốt kiến trúc host hoặc xử lý CORS với owner có thẩm quyền trước khi triển khai `fetch`; không để HTML ở `tools/PoServer/www` rồi vô tình gửi vào mock API.
