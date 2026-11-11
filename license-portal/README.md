# TanTienQR — License Portal

Web cấp license chỉ dùng Cloudflare — **không tốn 1 đồng server nào**.

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

```bash
cd license-portal
npx wrangler deploy
```

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

---

## 7. API

| Method | Đường dẫn | Mô tả |
|---|---|---|
| GET | `/api/health` | Kiểm tra dịch vụ (công khai) |
| GET | `/api/session` | Trạng thái đăng nhập (công khai) |
| POST | `/api/login` | `{ username, password }` |
| POST | `/api/logout` | Xoá session |
| GET | `/api/licenses` | Danh sách (`?q=&product=&status=active\|expired\|revoked\|all&limit=&offset=`) |
| POST | `/api/licenses` | Cấp license `{ customer, hwid, daysValid, product, note }` |
| GET | `/api/licenses/download?key=` | Tải file `.lic` |
| POST | `/api/licenses/revoke` | Thu hồi `{ licenseKey, reason }` |
| POST | `/api/licenses/renew` | Gia hạn `{ licenseKey, daysValid }` |
| POST | `/api/licenses/verify` | Kiểm tra chữ ký `{ licenseKey }` |
| GET | `/api/products` | Danh sách sản phẩm |
| GET | `/api/audit` | Nhật ký 100 dòng gần nhất |
| GET | `/api/public-key` | Public key đang active |

Mọi API (trừ health/session/login) **yêu cầu cookie session**.

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
