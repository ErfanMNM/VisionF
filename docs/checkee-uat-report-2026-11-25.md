# Báo cáo tích hợp Checkee Middleware UAT

**Ngày kiểm tra:** 25/11/2026
**Môi trường:** UAT - `https://beta-api-eco.checkee.vn`
**Tài liệu tham chiếu:** Checkee Middleware API Integration Specification v1.1.1

## Tóm tắt

Credential được cung cấp lấy được access token trên UAT. Trường `name` trong phản hồi token khớp tên doanh nghiệp ASTA HEALTHCARE. Các API Middleware chưa xác thực được quyền doanh nghiệp: API xem chi tiết serial trả HTTP 401 `NOT_VERIFIED_USER`. API xem hồ sơ doanh nghiệp đã thử nhưng cũng trả HTTP 401.

Hai lần thử dùng cùng một payload và cùng một serial trả HTTP 400 với thông báo `Provide either serialNumbers or fromIndex and toIndex, not both`, dù payload chỉ chứa `serialNumbers`. Lần thứ hai chỉ để lấy nội dung JSON lỗi chi tiết. Chưa có phản hồi thành công xác nhận serial được kích hoạt; không thử serial khác.

## Phạm vi và kết quả

| Bước | Endpoint | Kết quả |
|---|---|---|
| Lấy access token | `GET /onboarding/token` | Thành công; `status=true`; tên trong token khớp ASTA HEALTHCARE. |
| Xem hồ sơ doanh nghiệp | `GET /middleware-api/enterprises/profile` | HTTP 401; chưa đối chiếu được `taxCode` từ API. |
| Tra chi tiết serial | `GET /middleware-api/serials/find?serial=BN8joxxKwx` | HTTP 401 `NOT_VERIFIED_USER`. |
| Kích hoạt thử một serial | `POST /middleware-api/CTEs/active` (2 lần cùng payload/serial) | Cả hai lần HTTP 400 `Provide either serialNumbers or fromIndex and toIndex, not both`. Lần hai xác nhận nội dung JSON chi tiết. |
| Kiểm tra production host | `GET /onboarding/token` tại `https://api-eco.checkee.vn` | HTTP 404; chưa dùng production. |

## Request kích hoạt đã thử

```http
POST https://beta-api-eco.checkee.vn/middleware-api/CTEs/active
Authorization: Bearer <token trả về từ endpoint onboarding>
x-api-key: <API key do Checkee cấp>
Content-Type: application/json
```

```json
{
  "GTIN": "8931234567892",
  "batchCode": "A002",
  "type": "RANDOM",
  "serialNumbers": ["BN8joxxKwx"]
}
```

Phản hồi HTTP 400:

```json
{
  "code": 400,
  "status": false,
  "message": "Provide either serialNumbers or fromIndex and toIndex, not both",
  "data": null
}
```

## Đối chiếu đặc tả

Mục 13.1 của v1.1.1 mô tả `POST /middleware-api/CTEs/active` với `GTIN`, `batchCode`, `type` và một trong hai lựa chọn: `serialNumbers` hoặc `fromIndex` cùng `toIndex`. Request trên chỉ gửi `serialNumbers`, đúng theo lựa chọn thứ nhất. Mục 2.2 và 2.3 quy định token lấy bằng `x-client-id` và `x-api-key`; các API Middleware dùng `Authorization: Bearer ...` cùng `x-api-key`.
