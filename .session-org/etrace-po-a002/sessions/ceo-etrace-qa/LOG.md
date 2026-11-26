# Nhật ký QA độc lập eTrace — QA-02

- Vai trò: nhân viên QA chỉ đọc của CEO `/root`, thread `01a215d7-5953-74c2-90de-dd1799a93c39`.
- Agent: `/root/dialog_review`; thread riêng đã xác minh qua `CODEX_THREAD_ID`: `01a215db-8006-7a12-b672-47c6a4f84bcf`.
- Project root: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`.
- STATE_ROOT: `C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\etrace-po-a002`.
- Journal-key: `ceo-etrace-qa`; chỉ ghi nhận xét và ảnh PDF tham chiếu trong thư mục riêng này.
- Không sở hữu executor mutation; không credential, không tạo PO, không gọi API, không build/test, không sửa code/hồ sơ cấp dự án.
- Executor mutation do phòng tích hợp `01a21616-70a0-7d80-9ccc-73bb2ba6c4f0` quản lý.
- Trạng thái: `report_review_passed_api_failure_confirmed`.

## 2026-11-26T11:23:26+07:00 — Khảo sát đặc tả và dữ liệu

- Đã đọc skill PDF, BRIEF/TASKS/DECISIONS/STATE phòng tích hợp và hai tài liệu `docs/checkee-codecontent.md`, `docs/checkee-uat-report-2026-11-25.md`.
- PDF 115 trang; đã trích nội dung và đọc trọn các trang liên quan 6-8 (auth/envelope), 55-70 (batch-lots), 81-94 (serials/activation); render trang 92-93 bằng Poppler và xem ảnh để xác minh thứ tự/độ bắt buộc của bảng.
- Ảnh tham chiếu riêng: `spec-page-092.png`, `spec-page-093.png`; không thay đổi PDF nguồn.
- Nhánh/commit: `master` @ `92ba3ca09bdc5ba7706cdc430b6a94805f0d3c37`; thay đổi UI của nhiệm vụ trước và PDF chưa theo dõi được giữ nguyên.
- Budget riêng tại 11:23:26+07:00: 31 request hoàn tất, còn 69, `normal`, `exact=true`; dùng `request_budget.py --thread-id 01a215db-8006-7a12-b672-47c6a4f84bcf`, không dùng counter CEO.
- Đã giao subagent `/root/dialog_review/codecontent_static` khảo sát độc lập code/docs, chỉ đọc, không API/credential/build/test. Kết quả đã nhận và đối chiếu với code nguồn; không có mutation do QA.

### Thống kê CSV độc lập

- Nguồn `Danh-sach-ma-khoi-tao.csv`: 218.099 byte, SHA-256 `e66074967f69c688b61b2c20a70a129ea631629b570308deaabdf24bd66d1bb0`.
- 1 header + 1.000 dòng dữ liệu, mỗi dòng 8 cột; không dòng trùng hoàn toàn.
- `No.` là chuỗi 1211..2210 liên tục, chưa có bằng chứng là `index` của API; không suy `fromIndex/toIndex` từ cột này.
- Một sản phẩm `Thực Phẩm Chức Năng LaoTao`; một GTIN raw `8931234567892 ` có đúng một khoảng trắng cuối trên cả 1.000 dòng; metadata trim được `8931234567892`.
- Một loại `NGẪU NHIÊN`, ánh xạ hợp lý sang `RANDOM` theo bảng type PDF trang 81; GTIN/type cần đối chiếu sản phẩm/mã thật ở eTrace trước báo cáo kết quả.
- 1.000 `Mã serial` unique, không rỗng, 10 ký tự ASCII chữ/số, không whitespace đầu/cuối; chỉ dùng để đối chiếu suffix, KHÔNG dùng làm CodeContent sau chỉ đạo mới.
- Hai cột link có 1.000 giá trị unique/cột và suffix sau `/21/` khớp serial từng dòng; cả hai chứa khoảng trắng thừa sau GTIN. Cột `Ngày tạo` duy nhất `09:23 25/11/2026`.
- CSV không có batch/lô. `A002` là yêu cầu người dùng, cần phân biệt PO local với batch-lot trên eTrace.
- Kiểm tra số học checksum GS1-13 cho GTIN trim cho expected digit 7, raw digit 2. Đây là dữ liệu UAT người dùng cung cấp; QA đã báo CEO để đối chiếu product API, không coi phép tính này là quyền sửa GTIN và không tự sửa thành GTIN khác. Đặc tả hiện tại không quy định rõ validation checksum tại endpoint active.

### Chỉ đạo mới được tiếp nhận — toàn bộ Link tra cứu 1

- CEO chuyển chỉ đạo trực tiếp mới của người dùng: `https://beta-elink.checkee.vn/01/8931234567892/21/BN8joxxKwx lấy link số 1 làm content nha`.
- Chỉ đạo mới thay thế lựa chọn serial-only ban đầu. Nội dung import/gửi phải là toàn bộ cột thứ 6 `Link tra cứu 1`, không dùng `Mã serial`, `Link tra cứu 2` hoặc cắt suffix.
- Phép chuẩn hóa được người dùng/CEO nêu: trim ngoài và bỏ dấu cách thừa sau GTIN trước `/21/`, giữ nguyên phần còn lại.
- Kiểm tra độc lập 1.000 dòng: tất cả chỉ đổi một ký tự space trong `/01/8931234567892 /21/` thành `/01/8931234567892/21/`; thực tế không có khoảng trắng ngoài phải trim, không query/fragment/whitespace khác.
- Kết quả 1.000 URL unique, dài 60 ký tự; tất cả khớp `https://beta-elink.checkee.vn/01/8931234567892/21/[A-Za-z0-9]{10}`; suffix khớp serial của chính dòng đó.
- Mã đầu: `https://beta-elink.checkee.vn/01/8931234567892/21/BN8joxxKwx`; mã cuối: `https://beta-elink.checkee.vn/01/8931234567892/21/KNuH0VhCwZ`.
- SHA-256 UTF-8 của 1.000 URL đã chuẩn hóa nối bằng `\n` theo thứ tự CSV, không newline cuối: `28d0a35253255c3bbf144cac214db0ff89e91bbe591227be81029fa818dfb226`.
- SHA-256 UTF-8 JSON compact mảng URL (`ensure_ascii=False`, separators `(',', ':')`): `d79799596ce7defe0c48a80bc77aa82ef743d94906310e6da07f5541553face4`.
- Đã gửi CEO hai hash và quy tắc mapping để đối chiếu import/payload; QA không ghi CSV mới hay dữ liệu PO.

### Quy tắc API được tài liệu chứng minh

- Trang 6-8: host UAT `https://beta-api-eco.checkee.vn`, production riêng; credential theo môi trường; token qua `/onboarding/token`, Middleware dùng Authorization và x-api-key. Envelope thành công có `code`, `status=true`, `message`, `data`; lỗi `status=false`, `data=null`.
- Trang 56-58: tạo batch-lot bằng `POST /middleware-api/batch-lots`, multipart; cần productId, locationId thuộc doanh nghiệp, code unique, manufacturingDate, workers ít nhất 1; trùng lô có thể trả `CONFLICTED_BATCH_LOT` 409. Tạo PO local không tự chứng minh batch eTrace tồn tại.
- Trang 66-70: có list batch-lots với search/GTIN và detail theo ObjectId để xác minh A002; không bịa product/location/nhân sự/ngày sản xuất chỉ từ CSV vốn không có thông tin đó.
- Trang 81-83: type `RANDOM`, `INCREASING`, `DECLARE`; tạo mã mới `/serials` khác nghiệp vụ kích hoạt mã đã có, không cần tái khởi tạo 1.000 mã người dùng cung cấp nếu đã tồn tại.
- Trang 84-89: `/serials/list` lọc gtin/type/isActive và có pagination; `/serials/find` có thể tra bằng `serial` riêng, hoặc GTIN+serialNumber, không nên gửi cả hai cách; response chi tiết có trạng thái isActive, GTIN, serial/serialNumber, serialType, index và batchLotCode/id để đối soát.
- Trang 92-93 (đã nhìn ảnh): `POST /middleware-api/CTEs/active`, JSON; GTIN, batchCode, type bắt buộc; chỉ được chọn serialNumbers hoặc cặp fromIndex/toIndex. DTO hiện có đúng serialNumbers, không chứa hai trường index.
- Trang 93: data thành công có name, GTIN, batch, quantityInBatch, activeSerials; KHÔNG có danh sách thành công/lỗi từng mã và chưa nói activeSerials là delta hay tổng lũy kế. Không thể chỉ dựa vào một số tổng để khẳng định từng mã đã nhận.
- Trang 94: `NOTFOUND_BATCH_LOT`, `INVALID_BATCH_LOT_STATUS`, `NO_MATCHED_SERIALS` là các lỗi nghiệp vụ kích hoạt.
- Không tìm thấy quy định số serial tối đa/lần kích hoạt hoặc bảo đảm idempotency/retry trong toàn bộ PDF; giới hạn rows=25/50/75/100 là pagination, không phải kích thước activation batch.
- PDF gọi trường này là danh sách mã serial dạng string[], không chứng minh riêng URL đầy đủ được chấp nhận. Lần thử URL theo chỉ đạo người dùng phải báo đúng phản hồi thực tế; không được fallback serial-only để ép server nhận.

### Review code và các rủi ro đã báo sớm

- `docs/checkee-codecontent.md` và `CheckeeSendWorker.cs:280` gửi toàn bộ CodeContent theo Ordinal, không parse QR/GS1 hoặc cắt serial; phù hợp chỉ đạo URL mới khi import đúng nội dung.
- `ClassLibraryMT.Checkee/CheckeeClient.cs:321-355` có thể coi 2xx malformed/rỗng là Ok; `status=false` chỉ bị chặn khi message không rỗng, không kiểm GTIN/batch/code và activeSerials. `CheckeeSendWorker.cs:303-310` đánh dấu Sent toàn nhóm khi Ok. Executor phải giữ raw HTTP/envelope và đối soát đọc sau ghi; Sent nội bộ không đủ bằng chứng thành công từng mã.
- Worker lấy Pending toàn kho, không lọc PO (`:214-216`), ProductGtin cấu hình toàn cục ưu tiên mọi record (`:382-385`); tránh bật worker app để gửi A002 vì có thể kéo theo backlog ngoài phạm vi.
- `batchCode` import lưu trong `ProductionOrder.Additional` không được worker đọc trực tiếp. Camera ghi BatchNo=NoBatch; thứ tự batch gửi là BatchNo record → Checkee:BatchCode cấu hình → OrderNo. Cần payload evidence batch A002 thật, không suy từ việc tạo local PO.
- Giới hạn tối đa 500 trong chú thích options/UI chưa là giới hạn API; options validation chỉ >0, store clamp 1..5000, worker không chunk nội bộ mỗi nhóm. Executor chọn lô thận trọng sau thăm dò, không khẳng định tài liệu giới hạn 100/500/1000.
- Timeout/mạng có thể xảy ra sau server commit; worker giữ Pending rồi retry chu kỳ sau. Khóa trong process chỉ chống timer/manual trùng lúc, không tạo idempotency phía server; không retry mutation kết quả không rõ trước đối soát.
- Import dedupe Ordinal trong PO, không trim CodeContent, orderQty phải bằng số dòng trước lọc trùng; cơ chế này không thay thế idempotency API ngoài.
- Báo cáo UAT 25/11 chỉ chứng minh token lấy được và 401/400 nghiệp vụ; không có kích hoạt thành công. Đây là baseline trước khi eTrace báo đã sửa, không thể mặc định lỗi cũ còn tồn tại hoặc đã hết mà chưa xem evidence mới.

### Checklist review báo cáo/evidence tiếp theo

1. Môi trường UAT xác minh, credential/token đã che, chỉ một executor mutation; không dữ liệu ngoài A002.
2. Local PO A002 tồn tại/được tạo, 1.000 CodeContent đúng toàn Link1 chuẩn hóa; payload mẫu có URL đầy đủ và hash/danh sách đối chiếu không lẫn serial-only/Link2.
3. Metadata GTIN `8931234567892`, type RANDOM và batchCode A002 được xác minh từ nguồn thực; không tự sửa GTIN, type hay nội dung khi bị từ chối.
4. Mỗi request có thời gian, endpoint, body đã che phần bí mật nếu có, số mã, HTTP/envelope response nguyên văn; thăm dò 1 trước các lô tiếp theo.
5. Đọc sau ghi xác nhận serial/isActive/batch tương ứng khi API cho phép; nếu chỉ đối soát mẫu hoặc không thể tra thì báo chính xác giới hạn, không gọi 1.000 mã thành công chỉ vì HTTP 2xx hoặc Sent nội bộ.
6. Tách số đã gửi, được xác nhận chấp nhận, lỗi chắc chắn, kết quả chưa rõ, chưa gửi; không cộng response lũy kế thành số delta, không retry mù.

- Đã gửi CEO kết quả khảo sát và rủi ro chính qua collaboration. Chờ report/evidence cuối; không tự gọi API để bổ sung bằng chứng.

## 2026-11-26T11:35:27+07:00 — Review báo cáo và bằng chứng thực thi cuối: PASS về báo cáo, API có lỗi

- Nhận phân công kiểm chứng evidence đã lưu và tự review khi pilot/report xuất hiện. Chỉ dùng file cục bộ và SQLite read-only; không gọi mạng/API, không credential, không mutation PO/scan hoặc code.
- Đã đọc toàn bộ `docs/etrace-po-a002-report-2026-11-26.md` sau cập nhật pilot; bản review có SHA-256 `70af331556e55249c1ff0245f83a1d3ef93c3bc824b2559ef24ce252a96f5c47`.
- Đã đọc evidence qua bộ lọc che các key token/authorization/API key/client ID trước output; không đọc file credential tạm. Kiểm tra các JSON artifacts có key bí mật trả 0 đường dẫn nghi chưa che; không in giá trị bí mật.

### Đối chiếu cục bộ độc lập

- QA mở chính DB `TanTienQR/bin/Debug/net10.0/logs/po/2026-11/A002.db` bằng SQLite URI `mode=ro` và `PRAGMA query_only=ON`; không dùng app/harness mutation.
- DB đã có từ 25/11: `orderNo=A002`, `orderQty=1000`, `status=Running`; việc tái sử dụng/no-op import là phù hợp, không cần tạo PO trùng hoặc ghi đè PO đang chạy.
- Đọc `PoCodes.Code`: đúng 1.000 dòng, 1.000 unique; so tập Link1 chuẩn hóa từ CSV thiếu 0/thừa 0.
- SHA-256 tập mã sắp xếp tăng dần, nối LF không LF cuối: `d57a33e706ffd058103298bae8dfe1d3e6536ed457a080c4aa5f44c4cd2e36f1`, khớp report và `local-preflight.json`.
- Trạng thái DB xác minh riêng: 1.000 Pending, 0 ScannedUtc, 0 ActivatedUtc. File DB hash vẫn `ef3318263c40282f0cf9a501d294b00459d018f376d12142f4c3260c663665d5`; WAL hiện 0 byte. Không suy trạng thái remote từ trạng thái cục bộ.

### Đối chiếu HTTP và phạm vi lần gửi

- JSON evidence tổng cộng 13 HTTP response: 12 GET (3×200 token, 7×401, 2×400) và đúng 1 POST trả 400. Không có evidence POST thứ hai; `automaticRetries=0` ở các request đã lưu.
- Các GET profile/product/batch-list/find toàn URL/serials-list trả `401 NOT_VERIFIED_USER`; tra metadata GTIN+serialNumber trả `400 Either GTIN and Serial Number or Serial is required`. Không diễn giải lỗi auth/validation là không có sản phẩm/lô/mã.
- `pilot-authorized.json` đã ghi đúng cờ remote chưa xác minh, căn cứ CEO cho đúng một POST từ quyền người dùng; không giả cờ mapping/batch/inactive thành true. `pilot-attempt.json` được tạo trước POST để chặn gửi lặp.
- `pilot-response.json`: POST UAT `/middleware-api/CTEs/active` lúc `2026-11-26T11:32:15.643239+07:00`; GTIN `8931234567892`, batchCode `A002`, type `RANDOM`; `serialNumbers` có đúng URL1 `https://beta-elink.checkee.vn/01/8931234567892/21/BN8joxxKwx`; không có fromIndex/toIndex, không serial-only hoặc Link2.
- Phản hồi `HTTP 400`, envelope `code=400`, `status=false`, `message=Provide either serialNumbers or fromIndex and toIndex, not both`, `data=null`. Đây là lỗi validation vẫn tái hiện trên lần thử hiện tại, dù payload chỉ dùng serialNumbers.
- GET hậu kiểm toàn URL và batch tiếp tục 401; metadata tiếp tục 400. Vì vậy chưa xác minh trạng thái remote, không tuyên bố chắc chắn server không thay đổi gì hoặc mã đã được kích hoạt.

### Kết luận nghiệm thu QA-02

- Bản report cuối đã sửa các câu nháp cũ “chưa POST”; số đếm đúng evidence: 1 request/1 URL đã gửi, 0 được API xác nhận chấp nhận, 1 URL bị API trả lỗi, 999 chưa gửi, cả 1.000 chưa xác minh trạng thái remote.
- Report phân biệt rõ PO local có sẵn với remote activation, token200 với quyền middleware, response bị từ chối với trạng thái remote chưa đối soát; không cộng số lũy kế hoặc nội bộ thành accepted.
- Không retry/bulk/fallback; dừng sau đúng một pilot theo chỉ đạo CEO; không bật worker toàn cục, không sửa code sản phẩm/cấu hình hoặc dữ liệu quét.
- **PASS review báo cáo/bằng chứng; API không PASS.** Tác vụ kiểm tra thực tế đã thực hiện và chứng minh lỗi còn tái hiện trong phạm vi lần thử. Không còn finding cần sửa báo cáo hoặc yêu cầu thêm API/test trong phạm vi QA được giao.
- Đã báo CEO kết luận PASS cùng số đếm và đối chiếu DB độc lập để chốt nhanh. QA-02 hoàn tất; giữ nguyên quyền hồ sơ tổng của CEO và quyền executor của phòng tích hợp.
- Budget riêng checkpoint 11:35:27+07:00: 50 request hoàn tất, còn 50, `normal`, `exact=true`; không mượn request parent và không đổi giới hạn.
