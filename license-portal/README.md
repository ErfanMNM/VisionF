# TanTienQR — License Portal

Web cấp license và phân phối bản cập nhật dùng Cloudflare Workers, D1 và R2. Chi phí phụ thuộc hạn mức và mức sử dụng của tài khoản.

Portal đang triển khai: https://tantien-license-portal.okeynhat.workers.dev

| Thành phần | Cloudflare | Phí (free tier) |
|---|---|---|
| Web UI + API | Workers (Static Assets) | 100k request/ngày |
| Dữ liệu | D1 (SQLite serverless) | 5M row đọc / 100k row ghi/ngày |
| Private key | Workers Secrets | Không giới hạn |
| TLS + CDN | Cloudflare Edge | Miễn phí |

> Free tier Worker: 100.000 request/ngày. 1 người dùng cấp ~20 license/ngày là đủ cho
> mô hình vận hành thường (mỗi request cấp license gọi 1 lần D1 write).

---

## 1. Kiến trúc

```
┌──────────────┐      HTTPS      ┌──────────────────────────────┐
│  Nhân viên   │ ──────────────> │  Cloudflare Worker           │
│  (trình duyệt)│                │  ├─ Static Assets  (UI)      │
└──────────────┘                 │  ├─ /api/login               │
                                 │  ├─ /api/licenses  CRUD      │
                                 │  └─ WebCrypto: RSASSA-        │
                                    PKCS1-v1_5 + SHA-256        │
                                          │                    │
                                          ▼                    │
                                 ┌──────────────────────────────┐
                                 │  D1: tantien-license         │
                                 │  ├─ licenses                 │
                                 │  ├─ products                 │
                                 │  ├─ admins                   │
                                 │  ├─ signing_keys             │
                                 │  └─ audit_log                │
                                 └──────────────────────────────┘

                                 Worker Secrets:
                                   LICENSE_PRIVATE_KEY_PKCS8  (KHÔNG bao giờ commit)
                                   LICENSE_KEY_ID
                                   ADMIN_USERNAME
                                   ADMIN_PASSWORD_HASH
                                   SESSION_SECRET
```

---

## 2. Vào app lấy HWID

Trong app `TanTienQR`: **Quản trị → License** — copy mã máy 32 ký tự hex
(dạng `XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX`).

### Gửi yêu cầu trực tiếp từ TanTienQR

Cấu hình địa chỉ portal trong `appsettings.json` của TanTienQR:

```json
"LicensePortal": {
  "BaseUrl": "https://tantien-license-portal.<account>.workers.dev"
}
```

Dùng URL Worker hoặc domain thực tế của bạn. Trong hộp thoại **Quản lý
license**, nhập thông tin khách hàng/liên hệ nếu cần và bấm **Gửi yêu cầu cấp
license**. App gửi HWID của chính máy đang chạy và sản phẩm `TanTienQR` tới
portal. Thao tác này tạo yêu cầu chờ xử lý, không tự cấp license và không cần
đưa tài khoản quản trị hay private key vào app.

Trên portal, mở tab **Yêu cầu cấp license** và chọn **Cấp license** ở yêu cầu
đang chờ. Biểu mẫu điền sẵn HWID, sản phẩm, khách hàng và ghi chú. Chọn
**Cấp quyền Máy in + gói bán kèm** nếu khách cần module máy in. HWID/sản phẩm
được giữ cố định khi đang xử lý yêu cầu; bấm **Bỏ chọn yêu cầu** để quay về
cấp thủ công. Sau khi cấp thành công, yêu cầu chuyển sang **Đã cấp**. Quản trị
viên tải file `.lic` rồi gửi khách nhập vào TanTienQR như trước.

Khi gia hạn license trong tab **Danh sách**, hộp thoại cho phép bật hoặc bỏ
quyền **Máy in + gói bán kèm**. Mặc định giữ lựa chọn đang có; gia hạn từ
license cơ bản có thể bật quyền máy in ngay tại đây.

Trong tab **Danh sách**, license **Đã thu hồi** có thêm nút **Xoá**. Portal
yêu cầu xác nhận trước khi xoá vĩnh viễn bản ghi; nhật ký thu hồi và xoá vẫn
được giữ. License chưa thu hồi không được xoá, kể cả khi đã hết hạn.

---

## 3. Setup (chạy 1 lần)

```bash
cd license-portal
npm install
node scripts/setup.mjs
```

Script sẽ:
1. Tạo D1 database `tantien-license` (nếu chưa có) và ghi `database_id` vào `wrangler.jsonc`
2. Sinh cặp khóa **RSA 2048 mới**
3. Tính hash mật khẩu admin (PBKDF2-SHA256, 100.000 iterations)
4. Đặt 5 Worker secrets
5. Áp dụng migration D1
6. In ra **public key** và **key id** để dán vào app

Biến môi trường (tuỳ chọn):
```bash
ADMIN_USERNAME=admin ADMIN_PASSWORD=matkhaucuaban DEFAULT_PRODUCT=TanTienQR node scripts/setup.mjs
```

> **Lưu ý:** `node scripts/setup.mjs` sinh cặp khóa **MỚI**. Chạy lại sẽ làm khóa cũ
> trong app không còn khớp. Nếu chỉ đổi mật khẩu, dùng `node scripts/hash-password.cjs <mật khẩu>`
> rồi `npx wrangler secret put ADMIN_PASSWORD_HASH`.

---

## 4. Đưa public key vào app

Mở `ClassLibraryMT/Licensing/LicenseKeys.cs`, thêm public key mới vào danh sách `All`:

```csharp
public const string CurrentKeyId = "<KEY_ID>";
public const string CurrentPublicKey = "<PUBLIC_KEY_BASE64>";

public static IReadOnlyDictionary<string, string> All { get; } =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [CurrentKeyId] = CurrentPublicKey,
        // ... giữ khóa cũ để license đã cấp vẫn chạy
    };
```

Sau đó build lại app:
```bash
dotnet build TanTienQR\TanTienQR.csproj -c Release
```

---

## 5. Deploy

Sau khi cập nhật mã nguồn, áp dụng migration mới trước khi deploy Worker:

```bash
cd license-portal
npx wrangler d1 migrations apply tantien-license --remote
npx wrangler deploy
```

Migration `0004_license_requests.sql` bổ sung hàng đợi yêu cầu cấp license.
Khi thử tại máy phát triển, dùng `--local` thay cho `--remote` để chỉ cập nhật
database local. Không chạy lại `scripts/setup.mjs` cho lần cập nhật này vì
script đó sinh khóa ký mới.

URL sẽ là: `https://tantien-license-portal.<account>.workers.dev`

### Dùng tên domain riêng (miễn phí)

```bash
npx wrangler deploy
# Vào Cloudflare Dashboard → Workers & Pages → tantien-license-portal → Settings
# → Custom Domains → thêm "license.tantien.vn"
```

---

## 6. Local dev

```bash
cd license-portal
npm run dev
```

File `.dev.vars` (đã có sẵn sau setup) chứa secrets cho local. File này **không được commit**.

### Kiểm thử bằng dữ liệu riêng

```bash
npm run check
npm test
npm run test:preview
```

Hai lệnh test tạo khóa RSA, tài khoản và D1 mới trong bộ nhớ, không đọc
`.dev.vars` và không kết nối D1 trên Cloudflare. Preview in URL localhost và
đường dẫn metadata chứa tài khoản kiểm thử. Có thể dùng URL đó để kiểm tra
client .NET từ thư mục gốc repository:

```bash
dotnet run --project tools/LicensePortalTests -- --live http://127.0.0.1:<port>
```

Không truyền URL production cho `--live`: lệnh này gửi hai yêu cầu kiểm thử.

---

## 7. API

| Method | Đường dẫn | Mô tả |
|---|---|---|
| GET | `/api/health` | Kiểm tra dịch vụ (công khai) |
| GET | `/api/session` | Trạng thái đăng nhập (công khai) |
| POST | `/api/login` | `{ username, password }` |
| POST | `/api/logout` | Xoá session |
| POST | `/api/license-requests` | Công khai: nhận `{ hwid, product, customer?, contact?, note? }` từ app |
| GET | `/api/license-requests` | Danh sách yêu cầu (`?status=pending\|issued\|all&limit=&offset=`), cần đăng nhập |
| GET | `/api/licenses` | Danh sách (`?q=&product=&status=active\|expired\|revoked\|all&limit=&offset=`) |
| POST | `/api/licenses` | Cấp license `{ customer, hwid, daysValid, product, note, requestId?, features? }` |
| DELETE | `/api/licenses` | Xoá license đã thu hồi `{ licenseKey }`; chưa thu hồi trả `409`, không tồn tại trả `404` |
| GET | `/api/licenses/download?key=` | Tải file `.lic` |
| POST | `/api/licenses/revoke` | Thu hồi `{ licenseKey, reason }` |
| POST | `/api/licenses/renew` | Gia hạn `{ licenseKey, daysValid, features? }`; nếu bỏ `features`, giữ quyền hiện có |
| POST | `/api/licenses/verify` | Kiểm tra chữ ký `{ licenseKey }` |
| GET | `/api/products` | Danh sách sản phẩm |
| GET | `/api/audit` | Nhật ký 100 dòng gần nhất |
| GET | `/api/public-key` | Public key đang active |

API quản trị **yêu cầu cookie session**. Health, session, login và
`POST /api/license-requests` là công khai; app không có quyền cấp/xoá license.

Ví dụ phản hồi tạo yêu cầu:

```json
{
  "ok": true,
  "requestId": "<UUID>",
  "status": "pending",
  "message": "Yêu cầu cấp license đã được tiếp nhận."
}
```

Gửi lặp cùng HWID/sản phẩm khi còn yêu cầu chờ sẽ trả lại mã yêu cầu đang có,
không tạo hàng loạt bản ghi. Yêu cầu giới hạn body 8 KB; HWID cần đủ 32 ký tự
hex, khách hàng/liên hệ tối đa 200 ký tự mỗi trường, ghi chú tối đa 1.000 ký
tự. Khi cấp bằng `requestId`, HWID và sản phẩm phải khớp yêu cầu; server kiểm
tra trạng thái để tránh cấp trùng nếu hai quản trị viên cùng xử lý.

---

## 8. File license `.lic`

Đúng format **giống hệt** app `ClassLibraryMT/Licensing/`:

```json
{ "v": 1, "data": "<base64 JSON payload>", "sig": "<base64 chữ ký RSA>" }
```

Payload (giải mã `data`):
```json
{
  "hwid": "544836E495094B7605B1048E74D7912D",
  "customer": "Công ty ABC",
  "issued": "2026-11-11T07:46:56.000Z",
  "expires": "2026-12-11T07:46:56.000Z",
  "product": "TanTienQR",
  "kid": "4D088A7B"
}
```

### Thuật toán

| Server (Worker) | App (.NET) |
|---|---|
| `RSASSA-PKCS1-v1_5` + `SHA-256` | `RSASignaturePadding.Pkcs1` + `HashAlgorithmName.SHA256` |

> **Lưu ý:** Cloudflare Workers **chỉ hỗ trợ** `RSASSA-PKCS1-v1_5` cho signing
> (không hỗ trợ `RSASSA-PSS`). .NET verify bằng PKCS1-v1.5 nên khớp 100%,
> không cần sửa app.

`kid` = 8 ký tự hex đầu của `SHA256(public key SPKI)` — cùng công thức với
`LicenseKeys.ComputeKeyId`.

---

## 9. Kiểm tra chữ ký bằng chính code app

```bash
dotnet run --project tools/LicenseVerifyTest -- đường-dẫn-license.lic TanTienQR
```

Kết quả:
```
Kết quả  : HỢP LỆ
Thông báo: OK
```

---

## 10. Bảo mật

| Nguyên tắc | Thực hiện |
|---|---|
| Private key không bao giờ trong repo | Chỉ lưu trong Worker Secret |
| Public key trong app | Chỉ public, an toàn |
| Mật khẩu admin | PBKDF2-SHA256 100k iterations, hash trong Secret |
| Session | HMAC-SHA256, cookie `HttpOnly; Secure; SameSite=Strict`, 8 giờ |
| Login sai | Vẫn tính hash để tránh leak thời gian |
| Mỗi cấp license | Ghi `audit_log` |

### Xoay vòng khóa (khuyến nghị 6–12 tháng)

1. `node scripts/setup.mjs` — sinh khóa mới
2. Thêm public key mới vào `LicenseKeys.All` (giữ khóa cũ)
3. Build + phát lại app cho khách
4. Sau khi khóa cũ hết hạn, gỡ bỏ khỏi danh sách

---

## 11. Quotas free tier

| Tài nguyên | Giới hạn |
|---|---|
| Worker requests | 100.000 / ngày |
| D1 rows đọc | 5.000.000 / ngày |
| D1 rows ghi | 100.000 / ngày |
| D1 dung lượng | 500 MB |
| D1 Time Travel | 7 ngày |
| Workers Secrets | Không giới hạn |

> Lưu DB: `npx wrangler d1 export tantien-license --remote --output=backup.sql`

## 12. API phân phối bản cập nhật

Worker bổ sung hai endpoint dùng HTTPS POST với `Content-Type: application/json`.
Chứng thư được gửi trong body, không gửi trong URL hay cookie quản trị:

```json
{
  "license": { "v": 1, "data": "<base64 payload>", "sig": "<base64 chữ ký>" },
  "rid": "win-x64",
  "channel": "stable"
}
```

- `/api/updates/v1/check`: trả nguyên envelope manifest đã ký; HTTP 204 nếu chưa có phiên bản trên kênh.
- `/api/updates/v1/download`: body như trên, thêm `"version": "1.2.3"`; trả ZIP theo nền tảng. Phiên bản phải còn là phiên bản hiện tại trên kênh, nếu khác trả HTTP 409 và ứng dụng kiểm tra lại.

Chỉ nhận `win-x64`/`linux-x64` và `pilot`/`stable`. Mọi phản hồi có `Cache-Control: no-store`.
Worker đối chiếu đúng `data` và `sig` đã cấp cho `TanTienQR` trong D1, kiểm tra ngày hết hạn bằng giờ server và trạng thái thu hồi ở mỗi lần check/tải.
Không có bản ghi, đã xoá, hết hạn hoặc thu hồi đều trả HTTP 403; license cấp offline cần cấp lại qua portal để tải.
TanTienQR tự kiểm tra chữ ký và HWID trên máy; API này không chứng minh phần cứng từ xa.
Lỗi kho hay gói chưa sẵn sàng trả HTTP 503, không ghi nội dung license vào log.

Bucket `tantien-updates` là riêng tư, chỉ truy cập qua binding `UPDATE_BUCKET`.
Không bật `r2.dev` hay gắn custom domain công khai cho bucket này.
Trước lần triển khai có R2 đầu tiên:

```powershell
npx wrangler whoami
npx wrangler r2 bucket create tantien-updates
npx wrangler d1 migrations apply tantien-license --remote
npm run deploy
```

Giữ cấu trúc đối tượng:

```text
channels/pilot.json                  # {"version":"1.2.3"}
channels/stable.json                 # {"version":"1.2.3"}
releases/1.2.3/win-x64/manifest.json  # {"data":"<base64>","sig":"<base64>"}
releases/1.2.3/win-x64/package.zip
releases/1.2.3/linux-x64/manifest.json
releases/1.2.3/linux-x64/package.zip
```

Đẩy đầy đủ cả hai gói và manifest trước khi cập nhật con trỏ kênh. Không ghi đè nội dung của phiên bản đã công bố.
Đặt con trỏ thành `{}` bằng conditional PUT để rút bản lỗi an toàn khi có người phát hành đồng thời; ZIP cũ dù còn trong R2 cũng bị API từ chối tải. Con trỏ chưa tồn tại cũng được coi là kênh chưa phát hành.
Kiểm thử API cục bộ bằng `npm run check` và `npm test`; fixture dùng khóa sinh tạm, D1/R2 cục bộ và không truy cập dữ liệu thật.
