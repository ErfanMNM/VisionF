# Tan Tien License Portal

Web cap license chi dung Cloudflare — **khong ton 1 dong server nao**.

| Thanh phan | Cloudflare | Phi gia (free tier) |
|---|---|---|
| Web UI + API | Workers (Static Assets) | 100k request/ngay |
| Du lieu | D1 (SQLite serverless) | 5M row doc / 100k row ghi/ngay |
| Private key | Workers Secrets | Khong gioi han |
| TLS + CDN | Cloudflare Edge | Mien phi |

> Free tier Worker: 100.000 request/ngay. 1 user cap ~20 license/ngay la du cho mo hieu van hanh
> thuong (moi request cap license goi 1 lan D1 write).

---

## 1. Kien truc

```
┌──────────────┐      HTTPS      ┌──────────────────────────────┐
│  Nhan vien    │ ──────────────> │  Cloudflare Worker           │
│  (trinh duyet)│                 │  ├─ Static Assets  (UI)      │
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
                                   LICENSE_PRIVATE_KEY_PKCS8  (KHONG bao gio commit)
                                   LICENSE_KEY_ID
                                   ADMIN_USERNAME
                                   ADMIN_PASSWORD_HASH
                                   SESSION_SECRET
```

---

## 2. Vao app lay HWID

Trong app `TanTienQR`: **Quan tri → License** — copy ma may 32 ky tu hex
(dang `XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX`).

---

## 3. Setup (chay 1 lan)

```bash
cd license-portal
npm install
node scripts/setup.mjs
```

Script se:
1. Tao D1 database `tantien-license` (neu chua co) va ghi `database_id` vao `wrangler.jsonc`
2. Sinh cap khoa **RSA 2048 moi**
3. Tinh hash mat khau admin (PBKDF2-SHA256, 100.000 iterations)
4. Dat 5 Worker secrets
5. Ap dung migration D1
6. In ra **public key** va **key id** de dan vao app

Bien moi truong (tuy chon):
```bash
ADMIN_USERNAME=admin ADMIN_PASSWORD=matkhaucuaban DEFAULT_PRODUCT=DemoQR node scripts/setup.mjs
```

> **Luon luon:** `node scripts/setup.mjs` sinh cap khoa MOI. Chay lai se lam key cu
> trong app khong con khop. Neu chi doi mat khau, dung `node scripts/hash-password.cjs <matkhau>`
> roi `npx wrangler secret put ADMIN_PASSWORD_HASH`.

---

## 4. Dua public key vao app

Mo `ClassLibraryMT/Licensing/LicenseKeys.cs`, them public key moi vao danh sach `All`:

```csharp
public const string CurrentKeyId = "<KEY_ID>";
public const string CurrentPublicKey = "<PUBLIC_KEY_BASE64>";

public static IReadOnlyDictionary<string, string> All { get; } =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [CurrentKeyId] = CurrentPublicKey,
        // ... giu key cu de license da cap van chay
    };
```

Sau do build lai app:
```bash
dotnet build TanTienQR\TanTienQR.csproj -c Release
```

---

## 5. Deploy

```bash
cd license-portal
npx wrangler deploy
```

URL se la: `https://tantien-license-portal.<account>.workers.dev`

### Dung ten domain rieng (mien phi)

```bash
npx wrangler deploy
# Vao Cloudflare Dashboard -> Workers & Pages -> tantien-license-portal -> Settings
# -> Custom Domains -> them "license.tantien.vn"
```

---

## 6. Local dev

```bash
cd license-portal
npm run dev
```

File `.dev.vars` (da co san sau setup) chua secrets cho local. File nay **khong duoc commit**.

---

## 7. API

| Method | Duong dan | Mo ta |
|---|---|---|
| GET | `/api/health` | Kiem tra dich vu (cong khai) |
| GET | `/api/session` | Trang thai dang nhap (cong khai) |
| POST | `/api/login` | `{ username, password }` |
| POST | `/api/logout` | Xoa session |
| GET | `/api/licenses` | Danh sach (`?q=&product=&status=active|expired|revoked|all&limit=&offset=`) |
| POST | `/api/licenses` | Cap license `{ customer, hwid, daysValid, product, note }` |
| GET | `/api/licenses/download?key=` | Tai file `.lic` |
| POST | `/api/licenses/revoke` | Thu hoi `{ licenseKey, reason }` |
| POST | `/api/licenses/renew` | Gia han `{ licenseKey, daysValid }` |
| POST | `/api/licenses/verify` | Kiem tra chu ky `{ licenseKey }` |
| GET | `/api/products` | Danh sach san pham |
| GET | `/api/audit` | Nhat ky 100 dong gan nhat |
| GET | `/api/public-key` | Public key dang active |

Moi API (tru health/session/login) **yeu cau cookie session**.

---

## 8. File license `.lic`

Dung format **giong het** app `ClassLibraryMT/Licensing/`:

```json
{ "v": 1, "data": "<base64 JSON payload>", "sig": "<base64 chu ky RSA>" }
```

Payload (giai ma `data`):
```json
{
  "hwid": "544836E495094B7605B1048E74D7912D",
  "customer": "Cong ty ABC",
  "issued": "2026-11-11T07:46:56.000Z",
  "expires": "2026-12-11T07:46:56.000Z",
  "product": "DemoQR",
  "kid": "4D088A7B"
}
```

### Thuat toan

| Server (Worker) | App (.NET) |
|---|---|
| `RSASSA-PKCS1-v1_5` + `SHA-256` | `RSASignaturePadding.Pkcs1` + `HashAlgorithmName.SHA256` |

> **Luu y:** Cloudflare Workers **chi ho tro** `RSASSA-PKCS1-v1_5` cho signing
> (khong ho tro `RSASSA-PSS`). `.NET` verify bang PKCS1-v1.5 nen khop 100%,
> khong can sua app.

`kid` = 8 ky tu hex dau cu `SHA256(public key SPKI)` — cung cong thuc voi
`LicenseKeys.ComputeKeyId`.

---

## 9. Kiem tra chu ky bang chinh code app

```bash
dotnet run --project tools/LicenseVerifyTest -- duong-dan-license.lic DemoQR
```

Ket qua:
```
Ket qua  : HOP LE
Thong bao: OK
```

---

## 10. Bao mat

| Nguyen tac | Thuc hien |
|---|---|
| Private key khong bao gio trong repo | Chi luu trong Worker Secret |
| Public key trong app | Chi public, an toan |
| Mat khau admin | PBKDF2-SHA256 100k iterations, hash trong Secret |
| Session | HMAC-SHA256, cookie `HttpOnly; Secure; SameSite=Strict`, 8 gio |
| Login sai | Van tinh hash de tranh leak thoi gian |
| Moi cap license | Ghi `audit_log` |

### Xoay vong khoa (khuyen nghi 6–12 thang)

1. `node scripts/setup.mjs` — sinh key moi
2. Them public key moi vao `LicenseKeys.All` (giu key cu)
3. Build + phat lai app cho khach
4. Sau khi key cu het han, go bo khoi danh sach

---

## 11. Quotas free tier

| Tai nguyen | Gioi han |
|---|---|
| Worker requests | 100.000 / ngay |
| D1 rows doc | 5.000.000 / ngay |
| D1 rows ghi | 100.000 / ngay |
| D1 dung luong | 500 MB |
| D1 Time Travel | 7 ngay |
| Workers Secrets | Khong gioi han |

> Luu DB: `npx wrangler d1 export tantien-license --remote --output=backup.sql`
