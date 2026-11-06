# Quản lý tài khoản, đăng nhập/đăng xuất & phân quyền

Tài liệu mô tả tính năng xác thực của ứng dụng **TanTienQR**, dựa trên thư viện
độc lập `ClassLibraryMT.Auth` (có thể dùng chung cho app khác trong repo).

---

## 1. Tổng quan

| Thành phần | Vị trí | Vai trò |
|---|---|---|
| Lib xác thực | `ClassLibraryMT.Auth/` | Băm mật khẩu, lưu trữ tài khoản, quản lý phiên, ma trận quyền |
| Lib ghi log | `ClassLibraryMT.Logging/` | Ghi tên người dùng đang đăng nhập vào nhật ký thao tác |
| Màn hình đăng nhập | `TanTienQR/Views/LoginWindow.axaml` | Cổng vào bắt buộc của ứng dụng |
| Tab quản lý tài khoản | `TanTienQR/Views/AccountsView.axaml` | CRUD tài khoản (chỉ Quản trị / Lập trình viên) |

**Nguyên tắc:** ứng dụng **bắt buộc đăng nhập** trước khi mở cửa sổ chính. Đóng
ứng dụng = kết thúc phiên (không có "ghi nhớ đăng nhập").

---

## 2. Vai trò & ma trận quyền

Có 4 vai trò, lưu trong database dưới dạng **số nguyên** (`int`) để sau này
thêm/bỏ vai trò không làm hỏng dữ liệu cũ:

| Giá trị | Vai trò | Tên hiển thị |
|---|---|---|
| 0 | `Worker` | Nhân viên |
| 1 | `Operator` | Vận hành |
| 2 | `Administrator` | Quản trị |
| 3 | `Developer` | Lập trình viên |

### Ma trận quyền cố định

Ma trận nằm trong code (`ClassLibraryMT.Auth/RolePermissions.cs`) — **không cấu
hình được** — để mọi app dùng chung lib đều có cùng hành vi bảo mật.

| Quyền | Worker | Operator | Administrator | Developer |
|---|:---:|:---:|:---:|:---:|
| `ViewInfo` — xem tab Thông tin | ✔ | ✔ | ✔ | ✔ |
| `ViewProduction` — xem tab Sản xuất | ✔ | ✔ | ✔ | ✔ |
| `EditConfig` — sửa cấu hình | ✖ | ✔ | ✔ | ✔ |
| `ManageLicense` — nhập/huỷ license | ✖ | ✔ | ✔ | ✔ |
| `ViewHistory` — xem lịch sử thao tác | ✖ | ✖ | ✔ | ✔ |
| `ManageUsers` — quản lý tài khoản | ✖ | ✖ | ✔ | ✔ |
| `ExportData` — xuất JSON/CSV | ✖ | ✔ | ✔ | ✔ |

Trên giao diện, các tab/nút không có quyền sẽ **bị ẩn** (không chỉ vô hiệu hoá),
nên người dùng không thấy chức năng mình không được phép dùng.

---

## 3. Bảo mật mật khẩu

### 3.1 Thuật toán băm

* **PBKDF2-HMAC-SHA256**, **210.000 vòng lặp** (mặc định, cấu hình được).
* **Salt ngẫu nhiên 16 byte** cho mỗi tài khoản → hai tài khoản cùng mật khẩu vẫn
  tạo ra hash khác nhau.
* **Hash 32 byte**.
* Chuỗi lưu trữ: `PBKDF2-SHA256$210000$<base64 salt>$<base64 hash>`.
* Xác minh dùng `CryptographicOperations.FixedTimeEquals` → **constant-time**,
  chống timing attack.

> ⚠️ Mật khẩu **không bao giờ** được lưu dạng plaintext. PBKDF2 210k vòng lặp mất
> khoảng 100–200 ms mỗi lần xác minh — chấp nhận được cho màn hình đăng nhập,
> nhưng **không** gọi trong vòng lặp render UI.

### 3.2 Khoá tài khoản

* Sai mật khẩu **5 lần liên tiếp** → khoá tài khoản **5 phút**.
* Hết thời gian khoá → lần đăng nhập kế tiếp tự động mở khoá và reset bộ đếm.
* Thông báo lỗi đăng nhập **không tiết lộ** tài khoản nào tồn tại (trừ trường
  hợp bị khoá, cần báo rõ để người dùng biết chờ bao lâu).

### 3.3 Đổi mật khẩu bắt buộc

Tài khoản seed `admin` được đánh dấu `MustChangePassword = 1` ngay từ đầu → lần
đăng nhập đầu tiên **bắt buộc** mở hộp thoại đổi mật khẩu, không vào được app
nếu huỷ.

Tương tự, khi quản trị **đặt lại mật khẩu** cho một tài khoản, tài khoản đó cũng
được đánh dấu phải đổi mật khẩu ở lần đăng nhập kế tiếp.

---

## 4. Tài khoản mặc định

Ở lần chạy đầu tiên (khi database chưa có tài khoản nào), hệ thống tự tạo:

| Tên đăng nhập | Mật khẩu | Vai trò | Bắt buộc đổi mật khẩu |
|---|---|---|---|
| `admin` | `admin` | Quản trị | ✔ |

> 🔒 **Phải đổi mật khẩu ngay ở lần đăng nhập đầu tiên.** Hệ thống sẽ không
> seed lại nếu database đã có tài khoản (nên việc đổi mật khẩu không bị ghi đè).

Có thể đổi qua section `Auth` trong `appsettings.json`:

```json
"Auth": {
  "DbPath": "logs/users.db",
  "MaxFailedAttempts": 5,
  "LockoutMinutes": 5,
  "Pbkdf2Iterations": 210000,
  "SeedAdminUser": "admin",
  "SeedAdminPassword": "admin"
}
```

---

## 5. Vị trí dữ liệu

| File | Nội dung | Ghi chú |
|---|---|---|
| `logs/users.db` | Bảng `Users` + `SchemaVersion` | **Riêng biệt** với `logs/app.db` |
| `logs/app.db` | Bảng `AppLogs` + `UserActions` | Nhật ký thao tác, đã có cột `Username`/`UserId` |

Bảng `Users`:

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `Id` | INTEGER PK AUTOINCREMENT | Khoá chính |
| `Username` | TEXT NOT NULL UNIQUE | Tên đăng nhập (không phân biệt hoa thường) |
| `DisplayName` | TEXT | Tên hiển thị |
| `PasswordHash` | TEXT NOT NULL | Chuỗi hash PBKDF2 |
| `Role` | INTEGER NOT NULL | Giá trị `UserRole` |
| `IsActive` | INTEGER NOT NULL | 1 = hoạt động, 0 = vô hiệu hoá |
| `MustChangePassword` | INTEGER NOT NULL | 1 = bắt buộc đổi mật khẩu |
| `FailedAttempts` | INTEGER NOT NULL | Số lần đăng nhập sai liên tiếp |
| `LockedUntilUtc` | TEXT NULL | Thời điểm hết khoá |
| `LastLoginUtc` | TEXT NULL | Đăng nhập thành công gần nhất |
| `CreatedUtc` / `UpdatedUtc` | TEXT NOT NULL | Mốc thời gian |

Cả hai database dùng **WAL mode** + `busy_timeout=5000` để đọc/ghi song song
không bị chặn.

**Migration an toàn:** bảng `UserActions` của `logs/app.db` cũ được bổ sung cột
`Username` / `UserId` bằng `ALTER TABLE` (kiểm tra `PRAGMA table_info` trước) —
**không xoá database**, giữ nguyên lịch sử.

---

## 6. Quy trình đăng nhập / đăng xuất

```
Mở app
  └─► Tạo tài khoản admin mặc định (nếu DB chưa có tài khoản nào)
      └─► Hiện LoginWindow  (bắt buộc)
          ├─► Sai mật khẩu 5 lần ──► khoá 5 phút ──► thử lại
          ├─► Đúng + MustChangePassword ──► PasswordChangeWindow ──┐
          ├─► Huỷ / đóng cửa sổ ──► TẮT APP                       │
          └─► Đúng ────────────────────────────────────────────────┘
              └─► Mở MainWindow (tab, sidebar theo quyền)
                  └─► Bấm "Đăng xuất" ──► SignOut() ──► quay lại LoginWindow
```

* `ShutdownMode` được chuyển sang `OnExplicitShutdown` trong lúc mở màn hình
  đăng nhập → đóng cửa sổ đăng nhập **không** tắt app (cần cho vòng lặp
  đăng xuất → đăng nhập lại).
* Đóng `MainWindow` bằng nút **X** (không phải nút Đăng xuất) → **tắt app**.
* Mỗi lần đăng nhập / đăng xuất đều được ghi vào nhật ký thao tác
  (`Auth.Login` / `Auth.Logout`) kèm tên người dùng.

---

## 7. Quản lý tài khoản trên giao diện

Tab **"Tài khoản"** (biểu tượng người) chỉ hiện với Quản trị / Lập trình viên.

| Thao tác | Mô tả |
|---|---|
| **+ Tài khoản** | Mở hộp thoại tạo mới: tên đăng nhập, tên hiển thị, mật khẩu, xác nhận, vai trò |
| **Sửa** | Đổi tên hiển thị và vai trò (không đổi được tên đăng nhập) |
| **Mật khẩu** | Đặt lại mật khẩu bằng mật khẩu tạm ngẫu nhiên (10 ký tự, hiển thị 1 lần) |
| **Khoá** | Vô hiệu hoá / kích hoạt tài khoản |
| **Xoá** | Xoá tài khoản khỏi database |

**Chống tự hại:**

* Không thể **xoá** hoặc **khoá** tài khoản đang đăng nhập.
* Tài khoản bị khoá do đăng nhập sai hiển thị trạng thái "Đang khoá".

**Badge vai trò:** Nhân viên = xám · Vận hành = xanh · Quản trị = teal ·
Lập trình viên = tím.

---

## 8. Tích hợp với lib ghi log

Lib `ClassLibraryMT.Logging` **không** tham chiếu lib `ClassLibraryMT.Auth`
(tránh phụ thuộc vòng). Thay vào đó:

* Lib Logging định nghĩa interface `IUserContext` (`Username`, `UserId`).
* Lib Auth cài đặt interface này cho `SessionContext`.
* `UserActionLogger` lấy `IUserContext` qua DI (nullable) và tự động ghi
  `Username` / `UserId` vào bảng `UserActions` khi có người dùng đăng nhập.

```csharp
// Trong lib Logging — không biết gì về lib Auth:
public interface IUserContext
{
    string? Username { get; }
    long? UserId { get; }
}
```

```csharp
// Trong lib Auth — cài đặt interface của lib Logging:
internal sealed class SessionContext : ISessionContext, IUserContext
{
    public string? Username => CurrentUser?.Username;
    public long? UserId => CurrentUser?.Id;
}
```

Khi ứng dụng **không** dùng lib Auth, các bản ghi sẽ có `Username` rỗng — app vẫn
chạy bình thường.

---

## 9. Dùng lib trong app khác

```csharp
builder.Services.AddAuth(builder.Configuration);

// Đảm bảo tài khoản quản trị mặc định tồn tại (chỉ tạo khi DB rỗng)
await app.Services.GetRequiredService<IUserService>().EnsureSeedAdminAsync();
```

Sau khi xác thực thành công, gọi `ISessionContext.SignIn(user)`; khi đăng xuất,
gọi `SignOut()`. Kiểm tra quyền bằng `session.Can(Permission.EditConfig)` hoặc
`RolePermissions.Can(role, permission)`.

---

## 10. Chạy test

```powershell
dotnet run --project ClassLibraryMT.Auth.Tests\ClassLibraryMT.Auth.Tests.csproj -c Debug
```

Test tự viết (không dùng xUnit), bao phủ:

1. Hash/verify đúng–sai, salt khác nhau giữa 2 tài khoản, chuỗi hash hỏng.
2. Ma trận quyền của cả 4 vai trò.
3. Seed admin tạo đúng 1 lần, không ghi đè nếu DB đã có.
4. Đăng nhập thành công / sai mật khẩu / user không tồn tại / user bị vô hiệu hoá.
5. Khoá sau 5 lần sai, tự mở khoá khi hết thời gian.
6. CRUD tài khoản, từ chối tạo trùng username, từ chối mật khẩu quá ngắn.
7. `SetPasswordAsync` sai mật khẩu cũ bị từ chối; đúng thì xoá cờ
   `MustChangePassword`.
8. Schema database: đủ bảng, index, cột, và hash lưu đúng định dạng.

---

## 11. Lưu ý vận hành

* **Đóng app = đăng xuất.** Không có token, không có cookie, không có phiên
  tồn tại qua các lần chạy.
* **File `logs/users.db` chứa dữ liệu nhạy cảm** — nên hạn chế quyền truy cập
  file (ACL) và không commit lên git.
* Nếu quên mật khẩu admin: xoá `logs/users.db` để seed lại tài khoản mặc định
  (chỉ an toàn khi chưa có tài khoản thật).
* **Native AOT:** toàn bộ lib dùng API BCL sẵn có, không reflection lúc runtime.
  Cấu hình được bind thủ công (không dùng `ConfigurationBinder.GetValue<T>`).
