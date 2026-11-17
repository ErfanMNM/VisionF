# Tài liệu tích hợp API Nhận PO từ MES

> **Dành cho:** Đội phát triển MES muốn đẩy Lệnh sản xuất (Production Order) + tập mã
> xuống máy chạy ứng dụng **TanTienQR** (VisionF).
>
> **Phiên bản tài liệu:** 1.2 — bổ sung hỗ trợ **upload file thật** (`.csv`, `.json`, `.xlsx`, `.xls`).
>
> **Ngày cập nhật:** 2026-11-18

---

## 📑 Mục lục

1. [Tổng quan nhanh](#1-tổng-quan-nhanh)
2. [Cấu hình phía máy chạy](#2-cấu-hình-phía-máy-chạy)
3. [Base URL / Cổng / Xác thực](#3-base-url--cổng--xác-thực)
4. [Endpoint `GET /api/health`](#4-endpoint-get-apihealth)
5. [Endpoint `POST /api/po`](#5-endpoint-post-apipo)
6. [Cách gửi tập mã](#6-cách-gửi-tập-mã)
7. [Đặc tả định dạng file](#7-đặc-tả-định-dạng-file)
8. [Quy tắc trùng lặp](#8-quy-tắc-trùng-lặp)
9. [Mã lỗi (HTTP status codes)](#9-mã-lỗi-http-status-codes)
10. [Giới hạn & thời gian chờ](#10-giới-hạn--thời-gian-chờ)
11. [Firewall & URL ACL (Windows)](#11-firewall--url-acl-windows)
12. [Checklist xử lý sự cố](#12-checklist-xử-lý-sự-cố)
13. [Changelog](#13-changelog)

---

## 1. Tổng quan nhanh

Ứng dụng TanTienQR chạy trên máy chủ production. Ứng dụng này mở một **HTTP server nhỏ**
(`System.Net.HttpListener`) lắng nghe trên một cổng cố định để **MES chủ động đẩy PO xuống máy**.

Luồng tổng thể:

```
   ┌──────────┐   POST /api/po        ┌──────────────┐   ghi DB PO local
   │   MES    │ ────────────────────► │  TanTienQR   │ ──────────────────► logs/po/yyyy-MM/PO-xxx.db
   └──────────┘   (JSON hoặc file)   └──────────────┘
```

PO nhận được **ghi thẳng vào DB PO local** của máy, nên nó xuất hiện ngay trong tab
*"Truy xuất Sản xuất (Local)"* và trong danh sách chọn PO ở tab *"Thông tin sản xuất"* —
giống hệt PO cào từ server.

### 1.1. English summary

The machine running TanTienQR exposes a small HTTP API so that an MES system can **push**
Production Orders (and their barcode/serial lists) down to the machine.

| Item | Value |
|---|---|
| Default base URL | `http://<machine-ip>:8085` |
| Auth header | `x-api-key: <key>` (only when `PoServer:ApiKey` is set) |
| Endpoints | `GET /api/health`, `POST /api/po` |
| Payload modes | JSON body (`codes` / `csv`), raw file upload (`text/csv`, `application/json`, `application/vnd.ms-excel`, `...spreadsheetml.sheet`), or `multipart/form-data` file field |
| Accepted file formats | `.csv` (one code per line), `.json` (array of strings **or** array of `{code}` / `{serial}` objects), `.xlsx` / `.xls` (first sheet, first column) |
| Limits | `MaxBodyMB` (body), `MaxFileMB` (single file), `RequestTimeoutSeconds`, `MaxConcurrentRequests` |
| Error codes | `200`, `400`, `401`, `404`, `408`, `409`, `413`, `500` |
| Implementation notes | Files are **streamed to a temp file on disk** (never fully loaded into RAM); all I/O is async and non-blocking; **no reflection** (the app publishes with Native AOT, so Excel parsing is done by a built-in OOXML/BIFF reader instead of ClosedXML/NPOI) |

---

## 2. Cấu hình phía máy chạy

Section `PoServer` trong `appsettings.json` (và `appsettings.<Env>.json`):

```jsonc
{
  "PoServer": {
    "Enabled": false,                    // Bật server nhận PO từ MES
    "Port": 8085,                        // Cổng TCP (1-65535)
    "BindAddress": "127.0.0.1",         // 127.0.0.1 = chỉ local; 0.0.0.0 = mọi interface
    "ApiKey": "",                        // Rỗng = không xác thực; có giá trị = bắt buộc x-api-key
    "MaxBodyMB": 64,                     // Giới hạn kích thước body của 1 request
    "MaxFileMB": 64,                     // Giới hạn kích thước của 1 FILE tải lên
    "UploadTempDirectory": "",           // Rỗng = %TEMP% của Windows
    "RequestTimeoutSeconds": 120,        // Thời gian chờ tối đa cho 1 request
    "MaxConcurrentRequests": 4           // Số request xử lý song song
  }
}
```

| Khóa | Kiểu | Mặc định | Ý nghĩa |
|---|---|---|---|
| `Enabled` | bool | `false` | `true` = mở listener. Lỗi bind **không** làm crash app — trạng thái hiển thị ở tab *Server*. |
| `Port` | int | `8085` | Cổng lắng nghe. |
| `BindAddress` | string | `127.0.0.1` | `127.0.0.1` chỉ máy cục bộ. `0.0.0.0` / `+` / IP LAN = mọi máy trong mạng (cần URL ACL + firewall). |
| `ApiKey` | string | `""` | Rỗng = **không** xác thực (chỉ nên dùng trong mạng nội bộ khi cài đặt). Có giá trị = mọi request phải có header `x-api-key` khớp, sai thì trả `401`. |
| `MaxBodyMB` | int | `64` | Body request vượt → `413`. |
| `MaxFileMB` | int | `64` | File tải lên vượt → `413`. |
| `UploadTempDirectory` | string | `""` | Nơi chứa file tạm khi nhận file. File **được xoá ngay** sau khi xử lý. |
| `RequestTimeoutSeconds` | int | `120` | Request quá hạn → `408`. Nên tăng lên cho PO rất lớn (vài chục nghìn mã). |
| `MaxConcurrentRequests` | int | `4` | Số request xử lý đồng thời; request vượt sẽ **chờ** (không bị từ chối). |

> ⚠️ **Lưu ý bảo mật:** `HttpListener` là HTTP thuần (**không mã hoá TLS**).
> Hãy dùng trong mạng nội bộ (VLAN/PLC network) hoặc đặt sau reverse proxy có TLS
> (nginx / IIS / HAProxy) nếu cần truy cập từ ngoài mạng công nghiệp.

---

## 3. Base URL / Cổng / Xác thực

### 3.1. Base URL

```
http://<IP-máy-chạy>:<Port>
```

| Tình huống | Base URL ví dụ |
|---|---|
| MES chạy **cùng máy** với TanTienQR | `http://127.0.0.1:8085` |
| MES ở máy khác, `BindAddress = "0.0.0.0"` | `http://192.168.1.50:8085` |
| Cổng đã đổi | `http://192.168.1.50:9000` |

> Endpoint đầy đủ luôn là: **`{base}/api/po`** và **`{base}/api/health`**.

### 3.2. Xác thực — header `x-api-key`

Nếu `PoServer:ApiKey` **rỗng** → server **không** yêu cầu xác thực.

Nếu `PoServer:ApiKey` **có giá trị** → mọi request (kể cả `GET /api/health`) phải kèm:

```
x-api-key: <giá trị trong appsettings.json>
```

- Thiếu header → `401`
- Sai giá trị → `401`

```bash
curl -H "x-api-key: secret-key-123" http://192.168.1.50:8085/api/health
```

```powershell
curl.exe -H "x-api-key: secret-key-123" http://192.168.1.50:8085/api/health
```

```csharp
using var client = new HttpClient { BaseAddress = new Uri("http://192.168.1.50:8085") };
client.DefaultRequestHeaders.Add("x-api-key", "secret-key-123");
```

> 🔐 Header là so khớp **chuỗi tuyệt đối** (phân biệt hoa/thường), không phải hàm băm.
> Khuyến nghị dùng chuỗi ngẫu nhiên dài ≥ 32 ký tự.

---

## 4. Endpoint `GET /api/health`

Kiểm tra server còn sống.

**Request**

```http
GET /api/health HTTP/1.1
Host: 192.168.1.50:8085
x-api-key: secret-key-123
```

**Response `200 OK`**

```json
{
  "success": true,
  "message": "HTTP server nhận PO đang hoạt động.",
  "prefix": "http://192.168.1.50:8085/",
  "received": 12,
  "startedUtc": "2026-11-18T08:00:00.0000000Z"
}
```

| Trường | Ý nghĩa |
|---|---|
| `success` | `true` nếu server đang chạy. |
| `prefix` | Prefix server đang lắng nghe. |
| `received` | Số PO đã nhận được từ lúc bật server. |
| `startedUtc` | Thời điểm khởi động server (UTC). |

**Ví dụ**

```bash
curl -s -H "x-api-key: secret-key-123" http://192.168.1.50:8085/api/health
```

```powershell
curl.exe -s -H "x-api-key: secret-key-123" http://192.168.1.50:8085/api/health
```

```csharp
var resp = await client.GetAsync("/api/health");
Console.WriteLine(await resp.Content.ReadAsStringAsync());
```

---

## 5. Endpoint `POST /api/po`

Đẩy một PO + tập mã xuống máy.

**Request**

```http
POST /api/po?poName=PO-001&orderQty=100&ltin=08934567890123 HTTP/1.1
Host: 192.168.1.50:8085
x-api-key: secret-key-123
Content-Type: <xem mục 6>
```

### 5.1. Tham số metadata

| Tham số | Vị trí | Bắt buộc | Ý nghĩa |
|---|---|---|---|
| `poName` | query string **hoặc** field multipart **hoặc** JSON | ✅ | Tên/mã lệnh sản xuất. Duy nhất — trùng PO đã có → `409`. Tối đa 200 ký tự. |
| `orderQty` | query string **hoặc** field multipart **hoặc** JSON | ✅ | Số lượng mã của PO. Phải `> 0`. |
| `gtin` | query string / field / JSON | ⬜ | GTIN (mã sản phẩm). Dùng cho **mã trần** (serial không kèm GTIN) và lưu vào trường mở rộng. |
| `batchCode` | field multipart / JSON | ⬜ | Mã lô. |
| `productName` | field multipart / JSON | ⬜ | Tên sản phẩm. |
| `customer` | field multipart / JSON | ⬜ | Tên khách hàng. |
| `additional` | JSON | ⬜ | Object `key → value` ghi thêm vào trường mở rộng của PO. |

### 5.2. Response thành công — `200 OK`

```json
{
  "success": true,
  "message": "Đã nhận PO PO-001 với 100 mã.",
  "poName": "PO-001",
  "accepted": 100
}
```

`accepted` = số mã **thực sự được chấp nhận** (đã de-dup, đã loại mã không hợp lệ).
Nếu `accepted < orderQty` → server trả `400` (xem [mục 9](#9-mã-lỗi-http-status-codes)).

### 5.3. Ví dụ gửi JSON thuần

```bash
curl -X POST "http://192.168.1.50:8085/api/po" \
  -H "x-api-key: secret-key-123" \
  -H "Content-Type: application/json" \
  -d '{
        "poName": "PO-001",
        "orderQty": 3,
        "gtin": "08934567890123",
        "batchCode": "LOT-2026-01",
        "codes": [
          "https://digital-link.txng.vn/01/08934567890123/21/SER001",
          "https://digital-link.txng.vn/01/08934567890123/21/SER002",
          "https://digital-link.txng.vn/01/08934567890123/21/SER003"
        ]
      }'
```

```powershell
$body = @'
{
  "poName": "PO-001",
  "orderQty": 3,
  "gtin": "08934567890123",
  "codes": ["SER001", "SER002", "SER003"]
}
'@
Invoke-RestMethod -Uri "http://192.168.1.50:8085/api/po" `
  -Method Post `
  -Headers @{ "x-api-key" = "secret-key-123" } `
  -ContentType "application/json" `
  -Body $body
```

```csharp
var payload = new
{
    poName = "PO-001",
    orderQty = 3,
    gtin = "08934567890123",
    codes = new[] { "SER001", "SER002", "SER003" },
};

var req = new HttpRequestMessage(HttpMethod.Post, "/api/po")
{
    Content = JsonContent.Create(payload),
};
req.Headers.Add("x-api-key", "secret-key-123");

var resp = await client.SendAsync(req);
Console.WriteLine(await resp.Content.ReadAsStringAsync());
```

---

## 6. Cách gửi tập mã

Có **3 cách**. Server tự nhận diện dựa trên `Content-Type` + header `Content-Disposition`.

### 6.1. Cách A — JSON thuần (body)

`Content-Type: application/json`, body là object PO với `codes` (mảng chuỗi) hoặc `csv` (chuỗi).

> Nếu body JSON là **mảng** (`[...]`) thì server hiểu là **file danh sách mã JSON**
> và metadata lấy từ query string — xem [mục 6.3](#63-cách-c--file-thuần-kèm-tên-file).

### 6.2. Cách B — `multipart/form-data` (khuyến nghị cho file)

Đây là cách **phổ biến nhất** để gửi file thật từ MES.

```bash
curl -X POST "http://192.168.1.50:8085/api/po" \
  -H "x-api-key: secret-key-123" \
  -F "poName=PO-002" \
  -F "orderQty=3" \
  -F "gtin=08934567890123" \
  -F "file=@codes.csv"
```

```powershell
curl.exe -X POST "http://192.168.1.50:8085/api/po" `
  -H "x-api-key: secret-key-123" `
  -F "poName=PO-002" `
  -F "orderQty=3" `
  -F "gtin=08934567890123" `
  -F "file=@codes.csv"
```

```csharp
// Upload file .xlsx / .csv / .json bằng HttpClient
using var form = new MultipartFormDataContent();
form.Add(new StringContent("PO-002"), "poName");
form.Add(new StringContent("3"), "orderQty");
form.Add(new StringContent("08934567890123"), "gtin");

var fileBytes = await File.ReadAllBytesAsync(@"C:\data\codes.xlsx");
form.Add(new ByteArrayContent(fileBytes), "file", "codes.xlsx");

var req = new HttpRequestMessage(HttpMethod.Post, "/api/po") { Content = form };
req.Headers.Add("x-api-key", "secret-key-123");

var resp = await client.SendAsync(req);
Console.WriteLine(await resp.Content.ReadAsStringAsync());
```

**Quy tắc multipart:**

- Tên field chứa file **không bắt buộc** phải là `file` — server tìm **field nào có `filename`**.
- Phần metadata là field **không có `filename`**; tên field (không phân biệt hoa thường):
  `poName` (hoặc `po`), `orderQty` (hoặc `qty`), `gtin`, `batchCode`, `productName`, `customer`.
- Nếu có **nhiều field file** → server dùng field file **đầu tiên** có phần mở rộng được hỗ trợ.
- Nếu multipart **không có file** → `400`.

### 6.3. Cách C — File thuần kèm tên file

Gửi **nội dung file làm body**, kèm header `Content-Disposition` để server biết định dạng.

| Content-Type | Header bắt buộc | Server hiểu là |
|---|---|---|
| `text/csv` hoặc `application/csv` | `Content-Disposition: attachment; filename="codes.csv"` | file CSV |
| `application/json` | `Content-Disposition: attachment; filename="codes.json"` | file JSON (mảng) |
| `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` | `Content-Disposition: attachment; filename="codes.xlsx"` | file Excel `.xlsx` |
| `application/vnd.ms-excel` | `Content-Disposition: attachment; filename="codes.xls"` | file Excel `.xls` |

Metadata PO gửi kèm trong **query string**:
`?poName=PO-003&orderQty=100&ltin=089...`

```bash
# CSV
curl -X POST "http://192.168.1.50:8085/api/po?poName=PO-003&orderQty=2&gtin=08934567890123" \
  -H "x-api-key: secret-key-123" \
  -H "Content-Type: text/csv" \
  -H 'Content-Disposition: attachment; filename="codes.csv"' \
  --data-binary @codes.csv

# Excel .xlsx
curl -X POST "http://192.168.1.50:8085/api/po?poName=PO-004&orderQty=5&gtin=08934567890123" \
  -H "x-api-key: secret-key-123" \
  -H "Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" \
  -H 'Content-Disposition: attachment; filename="codes.xlsx"' \
  --data-binary @codes.xlsx
```

```powershell
# Excel .xlsx (PowerShell)
$uri = "http://192.168.1.50:8085/api/po?poName=PO-004&orderQty=5&gtin=08934567890123"
curl.exe -X POST $uri `
  -H "x-api-key: secret-key-123" `
  -H "Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" `
  -H 'Content-Disposition: attachment; filename="codes.xlsx"' `
  --data-binary "@codes.xlsx"
```

```csharp
// Upload file .xlsx — C# HttpClient
var xlsxPath = @"C:\data\codes.xlsx";

using var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(xlsxPath));
fileContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue(
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
fileContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = Path.GetFileName(xlsxPath),
    };

var uri = "/api/po?poName=PO-004&orderQty=5&gtin=08934567890123";
var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = fileContent };
req.Headers.Add("x-api-key", "secret-key-123");

var resp = await client.SendAsync(req);
Console.WriteLine($"HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
```

> 💡 **Mẹo tương thích:** Nếu MES cũ gửi `Content-Type: text/csv` mà **không** có header
> `Content-Disposition`, server vẫn hiểu đó là file CSV (tên file mặc định `codes.csv`).

---

## 7. Đặc tả định dạng file

Tất cả các định dạng đều tuân theo cùng một nguyên tắc:

- Đọc **cột đầu tiên** / **phần tử đầu tiên**.
- **Dòng tiêu đề đầu tiên là tuỳ chọn** — nếu dòng đầu tiên *không chứa chữ số* **và** khớp
  một tên cột thường gặp (`serial`, `serials`, `serialnumber`, `serialno`, `code`, `codes`,
  `ma`, `mas`, `barcode`, `gtin`, `product`, `productname`, `customer`, `batch`, `batchcode`, `lot`)
  thì dòng đó **được bỏ qua**.
- Dòng trống / khoảng trắng thừa **được bỏ qua**.
- Mã trùng nhau trong cùng file **được de-dup** (giữ lần đầu).
- File UTF-8 có **BOM** vẫn đọc được (BOM được bỏ qua).
- Server chấp nhận 3 dạng mã:
  - Link Digital Link TXNG — `https://digital-link.txng.vn/01/{gtin}/21/{serial}`
  - GS1 có ngoặc — `(01)08934567890123(21)SER001`
  - **Mã trần** — chỉ serial; GTIN lấy từ tham số `gtin` của request.

### 7.1. CSV (`.csv`)

Mỗi dòng = một mã.

```csv
serial
SER001
SER002
SER003
```

```csv
# Không có tiêu đề — hoàn toàn hợp lệ
SER001
SER002
SER003
```

> ⚠️ Chỉ hỗ trợ **một cột**; nếu file có nhiều cột, **cột đầu tiên** được lấy
> (phần còn lại bị bỏ qua). Dùng `;` hoặc `,` làm dấu phân cách đều được — server coi
> **toàn bộ dòng** là một mã, nên tránh dùng dấu phẩy trong file.

### 7.2. JSON (`.json`)

**Dạng 1 — mảng chuỗi:**

```json
["SER001", "SER002", "SER003"]
```

**Dạng 2 — mảng đối tượng** (tên field không phân biệt hoa thường):
`code`, `codes`, `serial`, `serials`, `serialNumber`, `serialNo`, `barcode`, `ma`, `value`, `gtin`.

```json
[
  { "code": "SER001" },
  { "serial": "SER002" },
  { "Code": "SER003" }
]
```

Nếu một đối tượng **không có** field nào ở danh sách trên → server lấy **field đầu tiên**
có giá trị chuỗi.

### 7.3. Excel (`.xlsx` / `.xls`)

| Quy tắc | Mô tả |
|---|---|
| Sheet | Lấy **sheet đầu tiên** theo thứ tự khai báo trong `xl/workbook.xml` (thường là sheet đầu tiên trong file). |
| Cột | Lấy **cột đầu tiên** (cột A). |
| Dòng tiêu đề | Bỏ qua nếu dòng đầu **không chứa chữ số** và khớp tên cột thường gặp (xem mục 7). |
| Kiểu ô | Hỗ trợ: shared string (`t="s"`), inline string (`t="inlineStr"`), chuỗi (`t="str"`), số (mặc định / `t="n"`). |
| Ô số | `123` → `"123"`; `1234.5` → `"1234.5"`; dấu phẩy nghìn `1,234` → `"1234"`. |
| Định dạng | `.xlsx` (OOXML) và `.xls` (BIFF8/BIFF5) đều đọc được — **không cần cài thêm thư viện**. |

> 💡 **Ghi chú kỹ thuật:** Ứng dụng được publish dưới dạng **Native AOT** (1 file `.exe`,
> không cần cài .NET Runtime). Vì vậy server **không** dùng ClosedXML / NPOI / EPPlus
> (các thư viện này dùng reflection và sẽ làm hỏng bản AOT). Thay vào đó, ứng dụng có sẵn
> bộ đọc OOXML/BIFF tự viết, dựa trên `System.IO.Compression` + `System.Xml.XmlReader`
> (100% AOT-safe).

**Ví dụ file Excel hợp lệ:**

| | A | B | C |
|---|---|---|---|
| **1** | serial | note | |
| **2** | SER001 | ghi chú 1 | |
| **3** | SER002 | ghi chú 2 | |
| **4** | SER003 | | |

→ Server lấy `SER001`, `SER002`, `SER003`.

---

## 8. Quy tắc trùng lặp

Server **từ chối** (không ghi đè) PO nếu gặp một trong hai trường hợp. Cả hai đều trả `409`.

### 8.1. Trùng tên PO

`poName` đã tồn tại trên máy (kể cả PO tải từ server trước đó, không chỉ PO do MES đẩy).

```json
{
  "success": false,
  "message": "PO 'PO-001' đã tồn tại trên máy — không thể nhận lại.",
  "poName": "PO-001",
  "accepted": 0
}
```

> PO đã **huỷ** cũng vẫn bị từ chối — trạng thái `Cancelled` được giữ nguyên, không bị ghi đè.

### 8.2. Trùng mã với PO khác

Bất kỳ mã nào trong tập mã đã tồn tại trong **PO khác** trên máy → `409`, **PO mới không được tạo**.

```json
{
  "success": false,
  "message": "PO bị từ chối: 1 mã đã tồn tại trong PO 'PO-MES-001' (ví dụ: 08934567890123|SER001). Không thể nhận PO có mã trùng.",
  "poName": "PO-002",
  "accepted": 0
}
```

**Cách so khớp mã (quan trọng):**

- Khoá so sánh là **`GTIN|Serial`** — giống hệt cách dùng khi quét mã trên máy.
- Nhờ vậy, **mã trần** `SER001` và **Digital Link** `https://digital-link.txng.vn/01/08934567890123/21/SER001`
  (cùng GTIN) được coi là **trùng nhau**.
- Thông điệp lỗi luôn nêu rõ: **số mã trùng**, **tên PO chứa mã trùng**, và **ví dụ mã** (tối đa 5).

### 8.3. De-dup trong cùng một file

Mã trùng nhau **trong cùng file/JSON** bị loại bỏ (giữ lần đầu) và **không** gây lỗi.

---

## 9. Mã lỗi (HTTP status codes)

Mọi response đều có cùng một shape JSON:

```json
{ "success": false, "message": "<thông điệp tiếng Việt>", "poName": "...", "accepted": 0 }
```

| Mã | Ý nghĩa | Thông điệp mẫu | MES nên làm gì |
|---|---|---|---|
| **200** | Thành công | `Đã nhận PO PO-001 với 100 mã.` | Ghi nhận, có thể xoá file tạm. |
| **400** | Request sai định dạng / thiếu dữ liệu | `Thiếu trường poName (tên/mã lệnh sản xuất).`<br>`Thiếu tập mã: cần gửi "codes" (mảng JSON), "csv" (nội dung CSV), hoặc tải file .csv/.json/.xlsx/.xls lên.`<br>`Số mã gửi lên (2) ít hơn orderQty (3).`<br>`Định dạng file không được hỗ trợ: 'codes.txt'. Chỉ chấp nhận .csv, .json, .xlsx, .xls.`<br>`Tên file không hợp lệ: Tên file không được chứa đường dẫn.` | Sửa payload rồi gửi lại. **Đây là lỗi của MES.** |
| **401** | Thiếu / sai API key | `Thiếu hoặc sai API key (header x-api-key).` | Kiểm tra lại header `x-api-key` và giá trị trong `appsettings.json`. |
| **404** | Endpoint không tồn tại | `Endpoint không tồn tại: GET /api/po. Dùng POST /api/po hoặc GET /api/health.` | Kiểm tra lại URL/method. |
| **408** | Quá thời gian chờ | `Yêu cầu bị hủy do quá thời gian chờ.` | File quá lớn hoặc mạng chậm → tăng `RequestTimeoutSeconds` / chia nhỏ PO. |
| **409** | Trùng PO hoặc trùng mã | `PO 'PO-001' đã tồn tại trên máy — không thể nhận lại.`<br>`PO bị từ chối: 3 mã đã tồn tại trong PO 'PO-MES-001' (ví dụ: ...).` | **Không gửi lại.** PO đã có trên máy → cần operator xử lý (huỷ PO cũ hoặc sinh mã mới). |
| **413** | Vượt giới hạn kích thước | `Body vượt quá dung lượng cho phép (64 MB).`<br>`File vượt quá dung lượng cho phép (64 MB).` | Tăng `MaxBodyMB` / `MaxFileMB` phía máy, hoặc chia nhỏ file. |
| **500** | Lỗi máy chủ | `Lỗi khi lưu PO: ...` | Ghi log lỗi, báo admin. **Không** gửi lại liên tục (có thể gây trùng). |

### 9.1. Ví dụ response đầy đủ từng mã

**400 — thiếu `poName`**

```json
{
  "success": false,
  "message": "Thiếu trường poName (tên/mã lệnh sản xuất).",
  "poName": "",
  "accepted": 0
}
```

**401 — sai API key**

```json
{
  "success": false,
  "message": "Thiếu hoặc sai API key (header x-api-key).",
  "poName": "",
  "accepted": 0
}
```

**404 — endpoint sai**

```json
{
  "success": false,
  "message": "Endpoint không tồn tại: GET /api/po. Dùng POST /api/po hoặc GET /api/health.",
  "poName": "",
  "accepted": 0
}
```

**408 — timeout**

```json
{
  "success": false,
  "message": "Yêu cầu bị hủy do quá thời gian chờ.",
  "poName": "",
  "accepted": 0
}
```

**409 — trùng PO**

```json
{
  "success": false,
  "message": "PO 'PO-001' đã tồn tại trên máy — không thể nhận lại.",
  "poName": "PO-001",
  "accepted": 0
}
```

**413 — quá lớn**

```json
{
  "success": false,
  "message": "File vượt quá dung lượng cho phép (64 MB).",
  "poName": "",
  "accepted": 0
}
```

**500 — lỗi server**

```json
{
  "success": false,
  "message": "Lỗi khi lưu PO: <chi tiết lỗi>",
  "poName": "PO-001",
  "accepted": 0
}
```

---

## 10. Giới hạn & thời gian chờ

| Hạng mục | Mặc định | Cấu hình | Ghi chú |
|---|---|---|---|
| Kích thước body request | 64 MB | `MaxBodyMB` | Toàn bộ body (kể cả multipart). |
| Kích thước 1 file tải lên | 64 MB | `MaxFileMB` | Áp dụng cho file CSV/JSON/Excel. |
| Thời gian chờ 1 request | 120 s | `RequestTimeoutSeconds` | Kể từ lúc bắt đầu nhận body. |
| Số request song song | 4 | `MaxConcurrentRequests` | Request vượt sẽ **xếp hàng chờ**, không bị từ chối. |
| Số ký tự `poName` | 200 | — | Dài hơn → `400`. |

### 10.1. Về mặt kỹ thuật (quan trọng cho máy công nghiệp)

- **Không nạp trọn file vào RAM.** Body/file được ghi **tăng dần** (streaming) xuống file tạm
  trên đĩa, rồi parser đọc lại theo luồng. RAM chỉ tăng **O(1)** bất kể file 1 KB hay 500 MB.
  → Máy công nghiệp cấu hình yếu vẫn chịu được PO rất lớn.
- **File tạm được xoá ngay** sau khi xử lý xong (kể cả khi lỗi). Khi app khởi động lại,
  các file tạm sót lại (> 6 giờ) sẽ được dọn tự động.
- **Mọi I/O đều async / không block** luồng UI. Trong lúc nhận PO, giao diện vẫn phản hồi.
- **Không dùng reflection** — tương thích Native AOT.

### 10.2. Khuyến nghị cho PO lớn

| Số mã | Gợi ý |
|---|---|
| ≤ 10.000 | Gửi 1 request bình thường. |
| 10.000 – 100.000 | Tăng `RequestTimeoutSeconds` lên `300`. Có thể chia thành nhiều PO nhỏ. |
| > 100.000 | Cân nhắc chia PO thành nhiều PO nhỏ hơn, hoặc nâng `MaxFileMB`. |

---

## 11. Firewall & URL ACL (Windows)

### 11.1. Điều kiện để MES truy cập được

1. `PoServer:Enabled = true`
2. `PoServer:BindAddress` = `0.0.0.0` (hoặc IP LAN của máy) — nếu để `127.0.0.1`
   thì **chỉ máy cục bộ** truy cập được.
3. **URL ACL**: `HttpListener` trên Windows cần quyền đăng ký prefix.
4. **Firewall**: mở cổng TCP cho MES.

### 11.2. Đăng ký URL ACL (chạy PowerShell **quyền Administrator**)

```powershell
netsh http add urlacl url=http://+:8085/ user=Everyone
```

Hoặc giới hạn theo tài khoản dịch vụ:

```powershell
netsh http add urlacl url=http://+:8085/ user=DOMAIN\svc-tantien
```

Xoá lại:

```powershell
netsh http delete urlacl url=http://+:8085/
```

> 💡 **Mẹo:** Có thể **không cần** đăng ký URL ACL nếu chạy ứng dụng với **quyền
> Administrator** — nhưng khi đó phải chạy app bằng tài khoản quản trị, không khuyến nghị
> cho máy production. Nên dùng URL ACL.

### 11.3. Mở Firewall

```powershell
# Mở cho mọi mạng (nhanh, để test)
New-NetFirewallRule -DisplayName "TanTienQR PO Server 8085" `
  -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8085

# Giới hạn theo IP của MES (khuyến nghị)
New-NetFirewallRule -DisplayName "TanTienQR PO Server 8085 (from MES)" `
  -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8085 `
  -RemoteAddress 192.168.1.20
```

Xoá:

```powershell
Remove-NetFirewallRule -DisplayName "TanTienQR PO Server 8085"
```

### 11.4. Kiểm tra nhanh

```powershell
# 1) Server còn sống?
curl.exe -s http://127.0.0.1:8085/api/health

# 2) Từ máy MES, port có mở?
Test-NetConnection 192.168.1.50 -Port 8085
```

### 11.5. Lỗi bind thường gặp

| Lỗi Windows | Nguyên nhân | Cách sửa |
|---|---|---|
| `Access is denied` (mã 5) | Thiếu URL ACL | `netsh http add urlacl ...` hoặc chạy app bằng Administrator. |
| `Prefix already reserved` (mã 183) | Prefix đã được đăng ký bởi app khác | `netsh http delete urlacl ...` hoặc đổi `Port`. |
| `Address already in use` (mã 10048) | Cổng đang bị chiếm | Đổi `PoServer:Port` hoặc tắt ứng dụng khác. |

Trạng thái lỗi bind được hiển thị ở tab **Server** trong ứng dụng (chữ đỏ), **không** làm crash app.

---

## 12. Checklist xử lý sự cố

### 12.1. Không kết nối được

- [ ] `PoServer:Enabled = true`?
- [ ] `PoServer:BindAddress` khác `127.0.0.1` nếu MES ở máy khác?
- [ ] Đã đăng ký URL ACL? (`netsh http list urlacl`)
- [ ] Đã mở firewall cho cổng?
- [ ] `Test-NetConnection <ip> -Port <port>` từ máy MES?
- [ ] `curl http://127.0.0.1:8085/api/health` từ chính máy chạy — nếu cũng không được thì là lỗi cấu hình/bind.

### 12.2. Trả `401`

- [ ] Đã gửi header `x-api-key` chưa?
- [ ] Giá trị có **khớp chính xác** `PoServer:ApiKey` (phân biệt hoa/thường, không có khoảng trắng thừa) không?
- [ ] Header có đúng tên `x-api-key` (không phải `X-Api-Key` — tuy HTTP không phân biệt, nhưng hãy dùng đúng tên trong tài liệu này) không?

### 12.3. Trả `400`

- [ ] Đã gửi `poName` và `orderQty > 0` chưa?
- [ ] Số mã trong file có **>= `orderQty`** không?
- [ ] File có đúng phần mở rộng `.csv` / `.json` / `.xlsx` / `.xls` không?
- [ ] File có **nội dung** (không rỗng) không?
- [ ] Tên file không chứa đường dẫn (`..\..\evil.csv` bị từ chối)?

### 12.4. Trả `409`

- [ ] PO đã có trên máy chưa? → Kiểm tra tab *"Truy xuất Sản xuất (Local)"*.
- [ ] Mã trong file đã nằm trong PO khác chưa? → Đổi tên PO **không** giải quyết được;
      phải dùng mã mới hoặc huỷ PO cũ (nếu PO cũ đã huỷ thì vẫn bị từ chối — hãy đổi tên file/PO và dùng mã mới).

### 12.5. Trả `413` / `408`

- [ ] File có vượt `MaxFileMB` không? → Tăng `MaxFileMB` hoặc chia nhỏ file.
- [ ] Mạng có chậm không? → Tăng `RequestTimeoutSeconds`.
- [ ] MES có timeout client ngắn hơn server không? → Đặt timeout client ≥ 120 s.

### 12.6. File tải lên không ra mã

- [ ] Mã có nằm ở **cột đầu tiên** / **phần tử đầu tiên** chưa?
- [ ] Dòng đầu tiên là **tiêu đề** không nhận diện được (VD `Ma Serial`) → dòng đó bị coi là **mã** và
      sẽ gây lỗi "không tìm được mã nào hợp lệ"? → Sửa tiêu đề thành `serial` hoặc xoá dòng tiêu đề.
- [ ] File Excel có **nhiều sheet**? → Dữ liệu phải nằm ở **sheet đầu tiên**.
- [ ] File `.xlsx` có phải ZIP hỏng không? → Thử mở lại bằng Excel và lưu lại.

### 12.7. PO đã nhận nhưng không thấy trên UI

- [ ] UI có tự tải lại danh sách không? (Ứng dụng phát event tự động khi nhận PO.)
- [ ] Đang xem đúng tab (*"Truy xuất Sản xuất (Local)"* / *"Thông tin sản xuất"*) chưa?
- [ ] PO có bị từ chối một phần không? → Xem log ứng dụng.

### 12.8. Xem log

Log ứng dụng nằm trong thư mục `logs/` cạnh file `.exe`:

| File | Nội dung |
|---|---|
| `logs/app.log` | Log chung (kèm dòng *"Đã nhận PO ... từ MES"*). |
| `logs/po/yyyy-MM/PO-xxx.db` | DB SQLite của từng PO (mã + trạng thái). |

---

## 13. Changelog

### v1.2 — 2026-11-18

**Tính năng mới**

- ✅ Hỗ trợ **upload file thật** từ MES:
  - `.csv` — một mã mỗi dòng, tiêu đề tuỳ chọn.
  - `.json` — mảng chuỗi **hoặc** mảng đối tượng `{code}` / `{serial}`.
  - `.xlsx` / `.xls` — **sheet đầu tiên**, **cột đầu tiên**, tiêu đề tuỳ chọn.
- ✅ Ba cách gửi: JSON thuần, `multipart/form-data`, file thuần kèm `Content-Disposition`.
- ✅ Bộ đọc Excel **tự viết, AOT-safe** (OOXML qua `ZipArchive` + `XmlReader`;
  BIFF8/BIFF5 tự parse) — **không** phụ thuộc ClosedXML/NPOI/EPPlus.
- ✅ Kiểm tra phần mở rộng file, tên file (chặn path traversal), kích thước file.
- ✅ Thêm cấu hình `PoServer:MaxFileMB` và `PoServer:UploadTempDirectory`.
- ✅ File được **stream xuống đĩa tạm** (không nạp trọn vào RAM) và tự xoá sau khi xử lý.
- ✅ Dọn file tạm sót lại khi khởi động server.

**Tương thích ngược**

- ✅ `POST /api/po` với `codes` / `csv` trong JSON thuần — **không đổi**.
- ✅ `Content-Type: text/csv` không kèm `Content-Disposition` — vẫn hoạt động.
- ✅ `multipart/form-data` với field `file` — **không đổi** (nay hỗ trợ thêm `.xlsx`/`.xls`).
- ✅ Tất cả mã trạng thái HTTP và shape response **không đổi**.

### v1.1 — 2026-11-17

- ✅ Từ chối PO trùng **mã** với PO khác (`409`), có thông điệp nêu số mã trùng,
  tên PO chứa mã trùng và ví dụ mã.
- ✅ So khớp mã theo khoá `GTIN|Serial` → mã trần và Digital Link cùng mã được coi là trùng.
- ✅ Kiểm tra PO trùng ở **tháng khác** trong `logs/po/yyyy-MM/`.
- ✅ PO đã huỷ giữ nguyên trạng thái `Cancelled` khi MES gửi lại.

### v1.0 — 2026-11-16

- ✅ Phát hành lần đầu: `POST /api/po`, `GET /api/health`, xác thực `x-api-key`,
  giới hạn body/timeout/số request đồng thời, ghi PO vào DB PO local.

---

## Phụ lục — Lệnh kiểm tra nhanh (copy-paste)

```bash
# Health
curl -s -H "x-api-key: secret-key-123" http://192.168.1.50:8085/api/health

# JSON thuần
curl -s -X POST "http://192.168.1.50:8085/api/po" \
  -H "x-api-key: secret-key-123" -H "Content-Type: application/json" \
  -d '{"poName":"PO-001","orderQty":2,"gtin":"08934567890123","codes":["S1","S2"]}'

# Upload CSV (multipart)
curl -s -X POST "http://192.168.1.50:8085/api/po" \
  -H "x-api-key: secret-key-123" \
  -F "poName=PO-002" -F "orderQty=2" -F "gtin=08934567890123" -F "file=@codes.csv"

# Upload XLSX (multipart)
curl -s -X POST "http://192.168.1.50:8085/api/po" \
  -H "x-api-key: secret-key-123" \
  -F "poName=PO-003" -F "orderQty=5" -F "gtin=08934567890123" -F "file=@codes.xlsx"

# Upload XLSX (file thuần + filename)
curl -s -X POST "http://192.168.1.50:8085/api/po?poName=PO-004&orderQty=5&gtin=08934567890123" \
  -H "x-api-key: secret-key-123" \
  -H "Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" \
  -H 'Content-Disposition: attachment; filename="codes.xlsx"' \
  --data-binary @codes.xlsx
```

```powershell
# PowerShell — upload CSV bằng Invoke-RestMethod (Form tự tạo)
$form = @{
  poName   = "PO-005"
  orderQty = "2"
  gtin     = "08934567890123"
  file     = Get-Item "C:\data\codes.csv"
}
Invoke-RestMethod -Uri "http://192.168.1.50:8085/api/po" `
  -Method Post -Headers @{ "x-api-key" = "secret-key-123" } -Form $form
```

```csharp
// C# — upload .xlsx tổng quát
static async Task<(int Status, string Body)> PushPoAsync(
    HttpClient client, string apiKey, string poName, int qty, string filePath)
{
    using var form = new MultipartFormDataContent();
    form.Add(new StringContent(poName), "poName");
    form.Add(new StringContent(qty.ToString()), "orderQty");

    var bytes = await File.ReadAllBytesAsync(filePath);
    var file = new ByteArrayContent(bytes);
    file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
        "application/octet-stream");
    form.Add(file, "file", Path.GetFileName(filePath));

    using var req = new HttpRequestMessage(HttpMethod.Post, "/api/po") { Content = form };
    req.Headers.Add("x-api-key", apiKey);

    var resp = await client.SendAsync(req);
    return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
}
```
