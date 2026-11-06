# Lib ghi log SQLite (`ClassLibraryMT.Logging`)

Thư viện log dùng chung cho các ứng dụng .NET trong repo. Tách làm 2 loại log:

| Loại | Bảng | Ghi bằng | Mục đích |
|---|---|---|---|
| **Log kỹ thuật** | `AppLogs` | `ILogger<T>` (tự động) | Stack trace, lỗi kết nối, trạng thái camera/PLC |
| **Thao tác người dùng** | `UserActions` | `IUserActionLogger` (API riêng) | "Ai đã làm gì" — chuyển tab, lưu cấu hình, nhập license |

Tại sao tách 2 loại? Log kỹ thuật có **mức độ ưu tiên thấp** (ghi được nhiều, xoá được nhanh). Thao tác người dùng là **sự kiện nghiệp vụ** — người dùng cần xem lại chính xác họ đã bấm gì, không cần stack trace.

> **Hiện trạng:** lib đã tích hợp vào `TanTienQR`. `DemoQR` / `T8T` chưa dùng.

---

## 1. Cài đặt

```xml
<ProjectReference Include="..\ClassLibraryMT.Logging\ClassLibraryMT.Logging.csproj" />
```

Yêu cầu: .NET 10, `IsAotCompatible=true` (không dùng reflection lúc runtime).

---

## 2. Đăng ký vào DI

```csharp
builder.Services.AddSqliteLogging(builder.Configuration);
```

Extension này làm 4 việc:

1. Bind `SqliteLoggingOptions` + `LoggingHistoryOptions` (thủ công, AOT-safe — không dùng `ConfigurationBinder.GetValue<T>`).
2. Đăng ký `SqliteLoggerProvider` vào pipeline `ILogger` hiện có → **mọi** `ILogger<T>` tự động chảy vào SQLite.
3. Đăng ký `IUserActionLogger` (API ghi thao tác người dùng).
4. Đăng ký `ILogQueryService` (API đọc log cho trang Lịch sử).

Các option được `ValidateOnStart()` → app không khởi động được nếu cấu hình sai.

### Thứ tự đăng ký quan trọng

```csharp
// Đúng: đăng ký logging TRƯỚC khi worker/logger khác được tạo.
builder.Services.AddSqliteLogging(builder.Configuration);
builder.Services.AddTanTienServices(builder.Configuration);

// Sai: worker đã log trước khi provider có mặt -> mất vài log đầu.
builder.Services.AddTanTienServices(builder.Configuration);
builder.Services.AddSqliteLogging(builder.Configuration);
```

---

## 3. Cấu hình (`appsettings.json`)

```json
{
  "Logging": {
    "Sqlite": {
      "Path": "logs/app.db",
      "MinLevel": "Information",
      "RetentionDays": 30,
      "EnableUserActions": true,
      "BatchSize": 100,
      "FlushIntervalMs": 1000,
      "PruneIntervalMs": 3600000
    },
    "History": {
      "Enabled": true,
      "PageSize": 200
    }
  }
}
```

| Khoá | Mặc định | Ý nghĩa |
|---|---|---|
| `Logging:Sqlite:Path` | `logs/app.db` | Đường dẫn file DB. Tương đối → tính từ thư mục ứng dụng. |
| `Logging:Sqlite:MinLevel` | `Information` | Mức log tối thiểu ghi xuống SQLite. |
| `Logging:Sqlite:RetentionDays` | `30` | Số ngày giữ log. `0` = giữ vĩnh viễn. |
| `Logging:Sqlite:EnableUserActions` | `true` | Có ghi bảng `UserActions` hay không. |
| `Logging:Sqlite:BatchSize` | `100` | Số bản ghi gom trước khi insert. |
| `Logging:Sqlite:FlushIntervalMs` | `1000` | Chu kỳ flush (ms) khi batch chưa đủ. |
| `Logging:Sqlite:PruneIntervalMs` | `3600000` | Chu kỳ prune (ms) — mặc định 1 giờ. |
| `Logging:History:Enabled` | `true` | Hiện/ẩn mục nhà phát triển (tab Lịch sử). |
| `Logging:History:PageSize` | `200` | Số bản ghi mỗi trang. |

> **Lưu ý:** khi bấm "Lưu cấu hình" ở tab Cấu hình, phần `Logging` được **giữ nguyên** từ file cũ (form không có ô nhập cho log).

---

## 4. Ghi log

### 4.1. Log kỹ thuật — không cần sửa callsite

Vì `SqliteLoggerProvider` đã nối vào pipeline `ILogger`, mọi log sẵn có tự động được ghi:

```csharp
public class CameraSignalWorker(ILogger<CameraSignalWorker> logger)
{
    public void OnScan()
    {
        logger.LogInformation("Doc ma thanh cong, gui PLC D12 = 1");
    }
}
```

Không cần đổi gì ở nơi gọi — chỉ cần `ILogger<T>` đã được inject.

### 4.2. Thao tác người dùng — API riêng

```csharp
public class MainViewModel(IUserActionLogger actionLog)
{
    [RelayCommand]
    private void Save()
    {
        if (!SaveOk)
        {
            actionLog.Log("Config.Save", "Config", "Khong ghi duoc file", UserActionResults.Failed);
            return;
        }

        actionLog.Log("Config.Save", "Config", "Luu cau hinh", UserActionResults.Success);
    }
}
```

**Chữ ký:**

```csharp
void Log(string action, string? target = null, string? detail = null, string? result = null);

Task LogAsync(
    string action,
    string? target = null,
    string? detail = null,
    string? result = null,
    CancellationToken cancellationToken = default);
```

| Tham số | Ví dụ | Ghi chú |
|---|---|---|
| `action` | `Config.Save` | Tên hành động. Dùng dạng `Phạm vi.Hành động`. |
| `target` | `Config` | Đối tượng bị tác động. |
| `detail` | `Nhap file abc.lic` | Chi tiết. Văn bản thường → tự bọc JSON. |
| `result` | `Success` | `UserActionResults.Success` / `.Failed` / `.Cancelled`. |

`LogAsync` chờ ghi xong xuống DB (flush) — dùng khi cần chắc chắn dữ liệu đã nằm trong file (ví dụ trước khi tắt app).

### 4.3. Không log dữ liệu nhạy cảm

Chỉ log **tên hành động + kết quả**. Không log:
- Nội dung license / payload chữ ký số
- API key, mật khẩu
- Nội dung file license đầy đủ

---

## 5. Đọc log

```csharp
public class HistoryViewModel(ILogQueryService query)
{
    public async Task LoadAsync()
    {
        var filter = new UserActionFilter
        {
            FromUtc = DateTime.UtcNow.AddDays(-7),
            ToUtc = DateTime.UtcNow,
            ActionContains = "Config",
            Result = UserActionResults.Failed,
        };

        var page = await query.GetUserActionsAsync(filter, page: 1, pageSize: 200);

        foreach (var item in page.Items)
        {
            Console.WriteLine($"{item.TimestampUtc:u} {item.Action} {item.Result}");
        }
    }
}
```

**API:**

```csharp
Task<PagedResult<UserActionEntry>> GetUserActionsAsync(
    UserActionFilter? filter = null, int page = 1, int pageSize = 200, CancellationToken ct = default);

Task<PagedResult<AppLogEntry>> GetAppLogsAsync(
    AppLogFilter? filter = null, int page = 1, int pageSize = 200, CancellationToken ct = default);

Task<Dictionary<string, long>> GetUserActionCountsAsync(CancellationToken ct = default);
```

`PagedResult<T>` có: `Items`, `TotalCount`, `Page`, `PageSize`, `TotalPages`, `HasNextPage`, `HasPreviousPage`.

### Bộ lọc

| Filter | Trường | Kiểu |
|---|---|---|
| `UserActionFilter` | `FromUtc`, `ToUtc` | `DateTime?` (UTC) |
| | `ActionContains` | `string?` — LIKE `%...%` |
| | `Result` | `string?` — khớp chính xác |
| `AppLogFilter` | `FromUtc`, `ToUtc` | `DateTime?` (UTC) |
| | `MinLevel` | `LogLevel?` — lấy mức đó **trở lên** |
| | `CategoryContains` | `string?` |
| | `MessageContains` | `string?` |

---

## 6. Schema SQLite

```sql
CREATE TABLE IF NOT EXISTS SchemaVersion (
    Version     INTEGER NOT NULL PRIMARY KEY,
    AppliedUtc  TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS AppLogs (
    Id             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    TimestampUtc   TEXT    NOT NULL,     -- ISO-8601 ( ví dụ: 2026-11-06T09:15:30.1234567Z )
    Level          TEXT    NOT NULL,     -- Trace | Debug | Information | Warning | Error | Critical
    Category       TEXT    NOT NULL DEFAULT '',
    EventId        INTEGER NOT NULL DEFAULT 0,
    Message        TEXT    NOT NULL DEFAULT '',
    Exception      TEXT    NULL,
    PropertiesJson TEXT    NULL
);

CREATE TABLE IF NOT EXISTS UserActions (
    Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    TimestampUtc TEXT    NOT NULL,
    Action       TEXT    NOT NULL,
    Target       TEXT    NOT NULL DEFAULT '',
    DetailJson   TEXT    NULL,
    Result       TEXT    NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS IX_UserActions_TimestampUtc ON UserActions (TimestampUtc DESC);
CREATE INDEX IF NOT EXISTS IX_UserActions_Action       ON UserActions (Action);
CREATE INDEX IF NOT EXISTS IX_AppLogs_TimestampUtc     ON AppLogs (TimestampUtc DESC);
CREATE INDEX IF NOT EXISTS IX_AppLogs_Level            ON AppLogs (Level);
```

Thời gian lưu dạng **TEXT ISO-8601 UTC** → so sánh được bằng `ORDER BY` / `WHERE` theo thứ tự chuỗi (đúng thứ tự thời gian), không cần cột `REAL` hay kiểu ngày giờ.

### Cấu hình runtime

Mỗi connection đều bật:

```sql
PRAGMA journal_mode = WAL;      -- đọc song song với ghi
PRAGMA busy_timeout = 5000;     -- chờ 5s thay vì lỗi "database is locked"
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
```

---

## 7. Ví dụ truy vấn SQLite trực tiếp

Mở file `.db` bằng [DB Browser for SQLite](https://sqlitebrowser.org/) hoặc `sqlite3`:

```sql
-- 10 thao tac gan nhat
SELECT TimestampUtc, Action, Target, Result
FROM UserActions
ORDER BY TimestampUtc DESC, Id DESC
LIMIT 10;

-- Cac thao tac that bai trong 7 ngay qua
SELECT TimestampUtc, Action, DetailJson
FROM UserActions
WHERE Result = 'Failed'
  AND TimestampUtc >= datetime('now', '-7 days')
ORDER BY TimestampUtc DESC;

-- Dem theo hanh dong
SELECT Action, COUNT(*) AS SoLan
FROM UserActions
GROUP BY Action
ORDER BY SoLan DESC;

-- Log loi ky thuat
SELECT TimestampUtc, Category, Message, Exception
FROM AppLogs
WHERE Level IN ('Error', 'Critical')
ORDER BY TimestampUtc DESC
LIMIT 50;

-- Xoa ban ghi cu hon 90 ngay (neu RetentionDays = 0)
DELETE FROM AppLogs     WHERE TimestampUtc < datetime('now', '-90 days');
DELETE FROM UserActions WHERE TimestampUtc < datetime('now', '-90 days');
```

---

## 8. Trang Lịch sử trong TanTienQR

- **Vào trang:** tab "Lịch sử" trên thanh header, hoặc sidebar → nhóm **NHÀ PHÁT TRIỂN** → "Lịch sử thao tác".
- **Bộ lọc:** từ ngày, đến ngày, tên hành động (lọc con), kết quả (Success/Failed/Cancelled).
- **Phân trang:** theo `Logging:History:PageSize`, mặc định 200 bản ghi/trang.
- **Xuất JSON:** `user-actions-yyyyMMdd-HHmmss.json` — gồm metadata + toàn bộ bản ghi khớp bộ lọc.
- **Xuất CSV:** `user-actions-yyyyMMdd-HHmmss.csv` — có header, escape dấu ngoặc kép theo RFC 4180.
- **Ẩn/hiện:** đặt `Logging:History:Enabled = false` → mục nhà phát triển biến mất khỏi sidebar.

Thao tác xuất file **cũng được ghi** vào `UserActions` (hành động `History.Export`).

### Bỏ ghi log thao tác

Đặt `Logging:Sqlite:EnableUserActions = false` → bảng `UserActions` không còn được ghi nữa (log kỹ thuật vẫn hoạt động).

---

## 9. Retention (tự dọn log cũ)

- Worker chạy prune mỗi `PruneIntervalMs` (mặc định 1 giờ).
- Xoá bản ghi có `TimestampUtc` cũ hơn `RetentionDays` ngày.
- `RetentionDays = 0` → **giữ vĩnh viễn**, không prune.
- Prune chạy trên **cả hai bảng**, mỗi bảng một lệnh riêng (để lỗi ở bảng này không chặn bảng kia).

---

## 10. Ghi chú về Native AOT

| Điểm | Cách xử lý |
|---|---|
| Bind cấu hình | `LogConfigReader` tự parse — **không** dùng `ConfigurationBinder.GetValue<T>` (IL2026/IL3050). |
| Serialize JSON | `LogJsonContext` / `HistoryJsonContext` (source-generated). |
| Query DB | ADO.NET thuần (`Microsoft.Data.Sqlite`), **không** EF Core / Dapper. |
| `Path` property | `SqliteLoggingOptions.Path` che khuất `System.IO.Path` → phải gọi `System.IO.Path.*` tường minh. |

Kiểm tra publish AOT:

```bash
dotnet publish TanTienQR/TanTienQR.csproj -c Release -r win-x64
```

---

## 11. Vòng đời ghi log (không mất dữ liệu khi tắt app)

1. `Log()` / `LogAsync()` → `enqueue` vào `Channel` (không block UI thread).
2. Worker nền gom batch → insert trong **một transaction**.
3. Flush khi: đủ `BatchSize` **hoặc** hết `FlushIntervalMs`.
4. `Dispose()` (khi DI container dispose) → đóng channel → worker flush nốt phần đang dở → thoát.

`FlushAsync` dùng mẫu **request/response** (`FlushRequest` + `TaskCompletionSource`): worker chỉ báo hoàn thành *sau khi* đã insert xong. Nếu chỉ chờ "channel rỗng" thì sẽ bị race — worker đã gom vào list cục bộ nhưng chưa kịp ghi xuống file.

---

## 12. Chạy test

```bash
dotnet run --project ClassLibraryMT.Logging.Tests
```

7 nhóm test: ghi/đọc log · ghi thao tác người dùng · lọc & phân trang · flush khi đóng app · prune retention · escape CSV · schema & WAL.

---

## 13. Ràng buộc đã biết

- **Một tiến trình ghi, nhiều tiến trình đọc** — WAL hỗ trợ đọc song song, nhưng nếu chạy **2 bản app cùng lúc trên cùng file `.db`** thì hai writer sẽ tranh nhau (SQLite chỉ cho 1 writer). Mỗi máy chỉ nên chạy 1 bản app.
- `Channel` có giới hạn 20.000 bản ghi, `DropOldest` — nếu log ồ ạt hơn tốc độ ghi, bản ghi cũ nhất bị bỏ (ưu tiên giữ app không bị treo).
- Mức `MinLevel` của SQLite **độc lập** với mức của console logger — có thể ghi console `Information` nhưng SQLite `Debug` để xem thêm chi tiết.
