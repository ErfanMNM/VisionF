# PO-01-ROUTE-01 — khảo sát hosting same-origin

- Thời điểm: 2026-11-26 (Asia/Saigon).
- Agent ID: `/root/po_hosting_review`; parent: CEO `/root`.
- Thread ID thật: chưa được công cụ cung cấp/xác minh; journal-key do parent cấp: `po-hosting-review`; budget riêng chưa xác minh.
- Phạm vi được giao: chỉ đọc kiến trúc `PoHttpServer`, worker/options, publish/runtime và đường dẫn cài đặt; chỉ ghi file log này. Không sửa source, hồ sơ khác hoặc chạy service/test.
- Repo: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`; branch `master`; HEAD `b66dce690674bebbbe395f47cd052cbc2777fe47`.
- Working tree đã có thay đổi của các task khác trước khi khảo sát, gồm `ClassLibraryMT.PoServer/PoHttpServer.cs` (modified) và `docs/po-demo/` (untracked); tôi không sửa/chạm các thay đổi đó.

## Kết quả và đề xuất route

Đề xuất **phục vụ trang qua chính `PoHttpServer`** bằng route `GET /po-demo/` (có thể hỗ trợ alias `GET /po-demo/index.html`), giữ API nhận PO tại `POST /api/po`. Với cấu hình mặc định, URL trình duyệt là `http://127.0.0.1:8085/po-demo/`; trang nên dùng URL tương đối `/api/po` để giữ cùng origin. Không dùng `/` làm landing page, tránh thay đổi ý nghĩa root hiện hữu/endpoint tương lai. Cùng origin cần giữ đúng scheme + host + port; `localhost` và `127.0.0.1`, hoặc cổng 8765 và 8085, là các origin khác nhau.

Hiện chưa có route/path phục vụ trang: `PoHttpServer.HandleAsync` nhận `GET /api/health` và `POST /api/po`; method/path khác (gồm `/`, `OPTIONS`, `/po-demo/`) đi về JSON 404. `CheckApiKey` chạy trước dispatch route, nên khi cấu hình API key khác rỗng, kể cả `GET` HTML cũng bị 401 nếu thêm route sau bước này. `PoServerOptions` không có cấu hình CORS, thư mục static, TLS hoặc HTTPS; `ResolvePrefix()` dựng `http://{host}:{port}/`.

`PoServerWorker` được đăng ký như hosted service trong `TanTienAppServices`; sau khi intake gate mở, worker gọi `_server.Start()`. `PoServerOptions` mặc định là `Enabled=false`, `BindAddress=127.0.0.1`, `Port=8085`, `ApiKey=""`. Vì thế hướng dẫn preview hiện tại bằng `python -m http.server 8765 --directory docs/po-demo` không phải origin của API và không thể đọc response bằng `fetch` same-origin.

## Asset và đường dẫn cài đặt

`docs/po-demo/` hiện không được `TanTienQR.csproj` khai báo copy/publish. `appsettings.json` được copy output; app đọc config từ `AppContext.BaseDirectory`. Không có một đường dẫn cài cố định được mã hóa trong repo: đây là thư mục chứa executable/runtime do quy trình triển khai cài vào, và docs updater yêu cầu bootstrap được giải nén vào thư mục hiện tại.

Không nên giả định có thể đọc trực tiếp `docs/po-demo/index.html` tại máy cài. `deploy/update/UpdateRelease.psm1` chỉ đưa executable/runtime libraries dạng file ở root app directory vào update; nó từ chối thư mục con. `ClassLibraryMT.Updates/UpdatePackage.cs` chỉ nhận tên tệp runtime đơn giản, hiện không cho path con hoặc `.html`; vì vậy chỉ thêm `CopyToOutputDirectory`/folder có thể làm hỏng quy tắc package/update và folder sẽ không theo đường cập nhật. Hướng đóng gói phù hợp nhất để xác minh tiếp là embed trang (và asset cần thiết) trong assembly đã phát hành rồi chỉ expose route cố định. Nếu dùng file ngoài thay cho embedded resource thì phải chủ động mở rộng và kiểm thử quy tắc publish, bootstrap/update package, manifest allowlist và rollback trên cả Windows/Linux.

## Rủi ro cần quyết định

- **Traversal/path:** ưu tiên trả embedded resource theo exact route, không diễn giải URL thành đường dẫn file. Nếu bắt buộc dùng filesystem, canonicalize bằng full path có boundary root, chỉ cho static subtree allowlist, từ chối encoded/dot segments và reparse/symlink, không liệt kê thư mục; cần test `%2e%2e`, encoded slash/backslash, đường dẫn không tồn tại và query. Không lấy route `/api/*` hoặc file cấu hình/log làm static root.
- **TLS/bind:** listener chỉ xây HTTP prefix; TLS/certificate termination chưa có. Giữ loopback (`127.0.0.1`) cho browser UI; không mở route lên `0.0.0.0`/LAN như cách tiện để né same-origin. Nếu MES/network access cần thiết thì phải có quyết định riêng về TLS, firewall và bảo vệ API key; truyền key qua HTTP mạng sẽ không được mã hóa.
- **Auth:** route HTML cần tải được khi gõ URL nên không thể yêu cầu header API key cho `GET /po-demo/`. Tách route static khỏi API-key gate, nhưng không đưa giá trị `PoServer:ApiKey` vào HTML/JavaScript vì người dùng tải HTML sẽ đọc được. Nếu key có cấu hình, UI cần nhận key do người vận hành cung cấp và gửi `x-api-key` cùng `POST`; giữ auth check cho API. Khi cùng origin, custom header không cần CORS preflight. Nếu API để trống key, cần xem xét request giả mạo từ website khác tới loopback (CORS không ngăn side-effect của form POST); kiểm tra `Origin`/CSRF hoặc yêu cầu auth là lựa chọn cần đánh giá.
- **CORS:** không cần thêm CORS cho URL cùng origin. Nếu chọn UI ở origin khác, cần `OPTIONS` + allowlist origin cụ thể, methods/headers đúng (`POST`, `Content-Type`, `x-api-key` nếu dùng), không wildcard; test origin bị từ chối. CORS không thay thế xác thực hoặc chống CSRF.

## Kiểm tra nên thêm

1. HTTP integration test cho `GET /po-demo/` và alias nếu hỗ trợ: 200, `text/html; charset=utf-8`, nội dung UI; cùng listener/prefix với `POST /api/po` và `GET /api/health`.
2. Test 404 cho path lạ và test không thể truy cập file ngoài/static root traversal (các dạng `../`, `%2e%2e`, `%2f`, `%5c`, route-encoded); không lộ listing hoặc file cấu hình.
3. Test cấu hình `ApiKey` rỗng và có giá trị: static page đọc được, API vẫn 401 khi thiếu/sai key và thành công khi key đúng; không nhúng key vào response HTML.
4. Browser e2e: mở trang bằng `http://127.0.0.1:8085/po-demo/`, gửi `FormData` tới URL tương đối `/api/po`, đọc status/body success và error/rejected records; kiểm tra host/port mismatch bị nhận diện rõ. Cùng origin không cần CORS/preflight; nếu phát triển CORS fallback thì thêm OPTIONS allow/deny tests riêng.
5. Release/publish tests cho Windows và Linux: embedded asset còn khả dụng sau Native AOT/single-file publish, bootstrap package, staged update và rollback; xác nhận quy tắc update không đòi thêm thư mục runtime ngoài dự kiến.

## Kiểm tra đã làm

Read-only: đọc `ClassLibraryMT.PoServer/PoHttpServer.cs`, `PoServerOptions.cs`, `PoServerServiceCollectionExtensions.cs`, `TanTienQR/Services/PoServerWorker.cs`, `TanTienQR/Services/TanTienAppServices.cs`, `TanTienQR/TanTienQR.csproj`, `appsettings.json`, `ConfigStore.cs`, `deploy/update/UpdateRelease.psm1`, `ClassLibraryMT.Updates/UpdatePackage.cs`, và `docs/po-demo/README.md`; dùng `rg`/`git status` để xác minh route, options, content/publish/update. Không chạy build/test/service theo phạm vi khảo sát.

## Bước tiếp theo

CEO chuyển route đề xuất `/po-demo/`, yêu cầu embed/static-resource packaging và ranh giới auth cho owner PO-01; PO-01 cần ghi route chính xác vào hồ sơ contract/implementation để PO-02 nối URL relative rồi kiểm thử browser với server thật. Không có code thay đổi hoặc test cần cherry-pick từ agent này.
