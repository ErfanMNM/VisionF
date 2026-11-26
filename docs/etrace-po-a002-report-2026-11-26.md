# Báo cáo kiểm tra eTrace và PO A002 ngày 26/11/2026

**Kết quả ngày 26/11/2026 (UTC+07:00):** PO A002 cục bộ đã chứa đúng 1.000 link số 1 được người dùng chọn; không cần nhập lại. Đã gửi thật **đúng một URL đầy đủ** lên UAT: API kích hoạt trả **HTTP400**, `status=false`, lỗi **`Provide either serialNumbers or fromIndex and toIndex, not both`**, dù request chỉ có `serialNumbers`. Đã dừng, không retry và không gửi 999 mã còn lại. API tra cứu trước/sau vẫn trả `401 NOT_VERIFIED_USER`. **Lỗi kích hoạt của ngày 25/11 vẫn tái hiện với URL1 ngày 26/11; chưa có mã nào được xác nhận chấp nhận.**

## 1. Phạm vi và nguồn chứng cứ

- Môi trường: UAT, `https://beta-api-eco.checkee.vn`. Không gọi production.
- Tài liệu: `docs/Checkee_Middleware_API_Integration_Specification_v1.1.1.pdf`, 115 trang; đã đọc trích xuất và kiểm hình trang 93. Mục 2.2–2.3 và 5.1 quy định token/header; mục 7.3 sản phẩm; 10.5–10.6 lô; 12 và 13.1 tra mã/kích hoạt.
- CSV: `Danh-sach-ma-khoi-tao.csv`, SHA-256 `e66074967f69c688b61b2c20a70a129ea631629b570308deaabdf24bd66d1bb0`.
- Tham chiếu trước: `docs/checkee-uat-report-2026-11-25.md`, `docs/checkee-codecontent.md`, `ClassLibraryMT.Checkee`, `ClassLibraryMT.PoServer`, `TanTienQR/Services/CheckeeSendWorker.cs`.
- Credential thật do người dùng cấp qua file tạm cục bộ; chỉ đọc trong bộ nhớ. Không đưa khóa, client ID hoặc token vào source, output, báo cáo hay chứng cứ. Không sửa cấu hình sản phẩm. File credential tạm cụ thể do CEO cấp đã được xóa sau khi hoàn tất kiểm tra; không xóa file cấu hình khác.
- Chỉ worker `01a21617-7b39-7351-ba10-f19c286c018d` thực thi; trưởng phòng và QA chỉ đọc. Có đúng một POST kích hoạt trong đợt này; không ghi/sửa PO, scan hoặc cấu hình sản phẩm.

Chứng cứ nằm trong `.session-org/etrace-po-a002/artifacts/`. Runner và file chuẩn bị nhập nằm trong `.tmp-etrace-po-a002/`. Các đường dẫn dưới đây tương đối với project root.

## 2. CSV và cách chọn CodeContent

CSV có 8 cột và 1.000 dòng dữ liệu. `GTIN` thô duy nhất là `8931234567892 `, có một dấu cách cuối; metadata GTIN sau Trim là `8931234567892`. Loại mã CSV là `NGẪU NHIÊN`, ánh xạ `RANDOM` theo loại mã trong PDF. Có 1.000 serial và 1.000 link số 1 duy nhất.

Chỉ đạo mới của người dùng chọn **toàn bộ cột `Link tra cứu 1` làm CodeContent**, ví dụ:

```text
https://beta-elink.checkee.vn/01/8931234567892/21/BN8joxxKwx
```

Theo URL mẫu đã được chỉ định, chuẩn hóa đúng dấu cách sau GTIN trước `/21/`, đồng thời trim ngoài URL. Cả 1.000 dòng có thay đổi khoảng trắng này. Giữ nguyên host, path còn lại, chữ hoa/thường và serial; không cắt đuôi để gửi serial thuần, không dùng `Link tra cứu 2`. Lựa chọn serial thuần ban đầu đã bị thay thế trước mọi mutation.

SHA-256 của tập 1.000 URL chuẩn hóa, sắp xếp tăng dần và nối bằng LF, không có LF cuối: `d57a33e706ffd058103298bae8dfe1d3e6536ed457a080c4aa5f44c4cd2e36f1`.

Đã đối chiếu metadata với CSV, PDF và PO cục bộ; **chưa xác minh được quyền sở hữu GTIN, loại mã và trạng thái lô/mã từ xa** vì API bị chặn xác thực.

## 3. PO A002 cục bộ và quyết định không nhập lại

PO thật đã tồn tại tại:

```text
TanTienQR/bin/Debug/net10.0/logs/po/2026-11/A002.db
```

DB có `orderNo=A002`, `orderQty=1000`, `createdUtc=2026-11-25T03:01:10.2420817Z`, `status=Running`, metadata GTIN `8931234567892` và tên sản phẩm `Thực Phẩm Chức Năng LaoTao`. Đọc SQLite ở chế độ read-only cho kết quả:

| Nội dung đối chiếu | Kết quả |
| --- | ---: |
| Mã trong DB A002 | 1.000 |
| CodeContent duy nhất | 1.000 |
| Khớp tập URL1 chuẩn hóa từ CSV | 1.000 |
| Thiếu so với CSV | 0 |
| Thừa so với CSV | 0 |
| Đã Scanned cục bộ | 0 |
| Đã Activated cục bộ | 0 |
| Trạng thái mã Pending | 1.000 |
| Mã được nhập mới trong đợt này | 0 |

Hash tập mã DB khớp hash URL1 ở trên. DB SHA-256 trước preflight và sau pilot/hậu kiểm đều là `ef3318263c40282f0cf9a501d294b00459d018f376d12142f4c3260c663665d5`.

Đây là **tái sử dụng PO đã có/no-op import**, không phải tạo mới thêm 1.000 mã. Giữ nguyên PO `Running`, lịch sử và trạng thái quét. Không merge/ghi đè DB và không gọi app hoặc worker nền. `PoHttpServer` có chặn nhận PO khi đang có PO sản xuất dở dang; không vượt qua cơ chế này. File một cột `.tmp-etrace-po-a002/A002-CodeContent.csv` được chuẩn bị theo hợp đồng `PoCodeFileParser`, có đúng 1.000 giá trị khớp tập PO, nhưng không gửi nhập lại.

Chứng cứ: `local-preflight.json`.

## 4. HTTP UAT thực tế

Loạt preflight chạy khoảng **11:29:31 ngày 26/11/2026 (UTC+07:00)**. Kiểm hình dạng auth bổ sung kết thúc lúc **11:30:14**. POST bắt đầu **2026-11-26T11:32:15.643239+07:00**, kết thúc **2026-11-26T11:32:15.729950+07:00**; thời điểm GET hậu kiểm trong các evidence tương ứng. Tổng cộng **12 GET, đúng 1 POST, 0 PATCH/DELETE**, không retry tự động hoặc theo redirect.

| Bước | Endpoint và tham số chính | HTTP | Phản hồi thật |
| --- | --- | ---: | --- |
| Token preflight | `GET /onboarding/token` | 200 | `code=200`, `status=true`; toàn response token được che |
| Hồ sơ doanh nghiệp | `GET /middleware-api/enterprises/profile` | 401 | `NOT_VERIFIED_USER` |
| Sản phẩm CSV | `GET /middleware-api/products/detail/8931234567892` | 401 | `NOT_VERIFIED_USER` |
| Tìm A002 | `GET /middleware-api/batch-lots/list?page=1&rows=100&GTIN=8931234567892&search=A002` | 401 | `NOT_VERIFIED_USER` |
| Tra toàn URL pilot | `GET /middleware-api/serials/find?serial=<toàn URL1, URL-encoded>` | 401 | `NOT_VERIFIED_USER` |
| Tra bằng metadata CSV | `GET /middleware-api/serials/find?GTIN=8931234567892&serialNumber=BN8joxxKwx` | 400 | `Either GTIN and Serial Number or Serial is required` |
| Danh sách mã RANDOM | `GET /middleware-api/serials/list?gtin=8931234567892&type=RANDOM&page=1&rows=100` | 401 | `NOT_VERIFIED_USER` |
| Token chẩn đoán chỉ đọc | `GET /onboarding/token` | 200 | `code=200`, `status=true`; kiểm prefix/header bằng boolean |
| Token pilot | `GET /onboarding/token` | 200 | `code=200`, `status=true`; toàn response token được che |
| Kích hoạt pilot 1 URL | `POST /middleware-api/CTEs/active` | 400 | `Provide either serialNumbers or fromIndex and toIndex, not both` |
| Hậu kiểm toàn URL pilot | `GET /middleware-api/serials/find?serial=<toàn URL1, URL-encoded>` | 401 | `NOT_VERIFIED_USER` |
| Hậu kiểm metadata | `GET /middleware-api/serials/find?GTIN=8931234567892&serialNumber=BN8joxxKwx` | 400 | `Either GTIN and Serial Number or Serial is required` |
| Hậu kiểm lô A002 | `GET /middleware-api/batch-lots/list?page=1&rows=100&GTIN=8931234567892&search=A002` | 401 | `NOT_VERIFIED_USER` |

Token thực có tiền tố `accessToken=`, không có tiền tố `Bearer` sẵn; harness dựng `Authorization: Bearer accessToken=<JWT>` đúng PDF. API key và client ID không có khoảng trắng ngoài. Không lưu token để chứng minh điều này; chỉ lưu boolean trong `auth-shape-diagnostic.json`. Credential được người dùng chỉ định cho ASTA HEALTHCARE, taxCode `4401062392`; profile401 nên **chưa xác minh được tên/taxCode bằng API doanh nghiệp**, không suy token200 là có đúng quyền doanh nghiệp.

Request nghiệp vụ được lưu theo dạng đã che bí mật:

```http
GET https://beta-api-eco.checkee.vn/middleware-api/batch-lots/list?page=1&rows=100&GTIN=8931234567892&search=A002
Authorization: [REDACTED]
x-api-key: [REDACTED]
Accept: application/json
```

Response nghiệp vụ đại diện:

```json
{
  "code": 401,
  "status": false,
  "message": "NOT_VERIFIED_USER",
  "data": null
}
```

Tên `GTIN`/`serialNumber` của lần tra metadata theo PDF, nhưng API thực tế trả validation 400. Không coi kết quả này là mã không tồn tại; cũng không thay payload hoặc cắt URL để ép API chấp nhận.

Chứng cứ HTTP từng request: `token-preflight.json`, `pre-profile.json`, `pre-product.json`, `pre-batches.json`, `pre-pilot-url.json`, `pre-pilot-metadata.json`, `pre-serials-page-1.json`, `token-diagnostic.json`, `token-pilot.json`, `pilot-response.json`, `post-pilot-url.json`, `post-pilot-metadata.json`, `post-batches.json`.

## 5. Pilot thật và phản hồi

Mục 13.1 của PDF quy định `POST /middleware-api/CTEs/active`, JSON có `GTIN`, `batchCode`, `type` và một trong hai lựa chọn `serialNumbers` hoặc `fromIndex`+`toIndex`. Tài liệu không công bố max batch hoặc bảo đảm idempotency. Phản hồi thành công có `data.activeSerials`; không được suy số chấp nhận chỉ từ HTTP200.

```json
{
  "GTIN": "8931234567892",
  "batchCode": "A002",
  "type": "RANDOM",
  "serialNumbers": [
    "https://beta-elink.checkee.vn/01/8931234567892/21/BN8joxxKwx"
  ]
}
```

Payload trên ở `pilot-payload.json`, khớp body đã gửi trong `pilot-response.json`; **không có `fromIndex` hoặc `toIndex`**. Sau khi nhận checkpoint preflight 401, CEO chỉ định rõ chạy đúng một POST theo quyền người dùng đã cấp, không yêu cầu GET thành công và không cho gửi 999 còn lại kể cả pilot 2xx. Căn cứ này được ghi trung thực trong `pilot-authorized.json`: `authorization_basis`, `remoteReadUnverified=true`, các cờ xác minh remote vẫn false. Không giả mạo kết quả preflight.

Response activation thực tế:

```json
{
  "code": 400,
  "status": false,
  "message": "Provide either serialNumbers or fromIndex and toIndex, not both",
  "data": null
}
```

Đã tạo marker một lần `pilot-attempt.json` trước POST; không xóa marker để gửi lại. HTTP400/status=false là từ chối rõ nên dừng mutation ngay. GET hậu kiểm vẫn bị 401 và validation 400 như bảng trên; **chưa đối soát được trạng thái thực tế của mã/lô trên remote**. Không tuyên bố đã kích hoạt, không đổi URL sang serial thuần và không gửi mã thứ hai.

## 6. Số đếm và đối soát

| Chỉ tiêu trong đợt 26/11 | Số lượng |
| --- | ---: |
| Dòng dữ liệu CSV | 1.000 |
| URL1 chuẩn hóa duy nhất | 1.000 |
| Mã có sẵn trong PO thật, khớp chính xác | 1.000 |
| Mã nhập thêm | 0 |
| Request kích hoạt đã gửi | 1 |
| Mã đã gửi kích hoạt | 1 |
| Mã được API xác nhận chấp nhận trong đợt này | 0 |
| Mã bị API activation từ chối trong đợt này | 1 |
| Mã chưa gửi kích hoạt | 999 |
| Mã chưa xác minh trạng thái từ xa | 1.000 |
| Lỗi HTTP chỉ đọc | 9 request: 7×401, 2×400 |
| Lỗi HTTP kích hoạt | 1 request: HTTP400 |

Số chấp nhận 0 là số **xác nhận trong đợt này**, không có nghĩa toàn bộ hệ thống eTrace đang có 0 mã kích hoạt. Không đọc được remote nên không suy trạng thái remote từ trường Pending/Activated cục bộ. Không đánh dấu mã Sent, Activated hoặc Scanned, không tạo Pass giả.

## 7. So với kiểm tra ngày 25/11

| Nội dung | 25/11 | 26/11 |
| --- | --- | --- |
| Lấy token | Thành công | Vẫn thành công |
| Quyền middleware | Profile/find trả 401 `NOT_VERIFIED_USER` | Profile/product/batch/findURL/list vẫn trả 401 cùng lỗi |
| Kích hoạt | Hai POST cùng serial thuần trả 400 `Provide either serialNumbers or fromIndex and toIndex, not both` | Đúng 1 POST toàn URL1, chỉ serialNumbers, vẫn trả 400 cùng thông báo |
| CodeContent được yêu cầu hiện tại | Báo cáo cũ dùng serial thuần | Toàn URL1 chuẩn hóa theo chỉ đạo mới; đã thử đúng 1 URL, không fallback |
| PO local | Không phải chứng cứ activation | Đã đối chiếu đủ 1.000 mã A002, không sửa |

## 8. Bước tiếp tục và giới hạn

1. eTrace cần làm rõ/cấp đúng quyền middleware của credential doanh nghiệp trên UAT. Token thành công chưa chứng minh quyền xem sản phẩm, lô hoặc mã. Đồng thời cần làm rõ tên tham số tra metadata đang không khớp hành vi thực tế.
2. eTrace cần xử lý lỗi validation ở activation: request chỉ có `serialNumbers` vẫn bị báo như đồng thời có index. Không sửa code sản phẩm hoặc tự thêm/xóa trường khác để thay hợp đồng khi chưa có căn cứ.
3. Nếu có chỉ đạo tiếp tục sau khi sửa, dùng cùng executor và giữ marker/evidence lần này; chạy lại GET để đối soát mã đã thử trước mọi lần mutation tiếp. Không tự retry pilot hoặc gửi 999 mã còn lại; quyền của đợt thực thi này chỉ cho đúng một POST. Chỉ sau envelope chấp nhận rõ và GET đối soát mới xem xét phần tiếp theo theo chỉ đạo mới. Không đổi CodeContent hoặc fallback serial.
4. Runner hiện chỉ có `preflight`, `pilot`, `reconcile`, chưa triển khai gửi 999 mã còn lại. Nếu tiếp tục được, phải thêm thực thi từng batch có marker và dừng/đối soát đúng phản hồi thật. Có thể chọn 100 mã/request như giới hạn thận trọng của harness/cấu hình hiện hữu; **không gọi đây là max batch theo PDF**.

Runner `.tmp-etrace-po-a002/etrace_a002.py` đã kiểm cú pháp, guard SHA-256/tập local và cơ chế che bí mật với dữ liệu mô phỏng; kiểm tra mô phỏng không gọi mạng. Không dùng wrapper sản phẩm để tuyên bố kết quả, vì wrapper có tự retry401 và fallback HTTP200 khi đọc body không được. Hướng dẫn resume ở `.tmp-etrace-po-a002/README.md`.

**Kết luận:** PO A002 local đã đúng đủ 1.000 link số 1; không phát sinh nhập mới. Đã gửi thật đúng 1 URL, bị API từ chối 400 với lỗi validation giống ngày 25/11; **lỗi vẫn tái hiện trong lần kiểm tra này**. Số được xác nhận chấp nhận 0; bị từ chối 1; chưa gửi 999. UAT vẫn có lỗi quyền `NOT_VERIFIED_USER`; chưa đối soát được trạng thái remote. Không commit, deploy, đổi nhánh hoặc sửa code UI/cấu hình sản phẩm.
