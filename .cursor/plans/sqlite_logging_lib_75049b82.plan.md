---
name: sqlite logging lib
overview: Tạo lib log SQLite dùng chung (log kỹ thuật qua ILogger + log thao tác người dùng qua API riêng), tích hợp trước vào TanTienQR kèm trang Lịch sử thao tác cho nhà phát triển (xem + xuất JSON/CSV), kèm tài liệu .md và XML doc.
todos:
  - id: new-proj
    content: Tạo project ClassLibraryMT.Logging (net10.0, AOT-compatible, XML doc) + thêm vào VisionF.slnx
    status: pending
  - id: core-writer
    content: Viết SQLite writer bất đồng bộ (Channel + batch), schema 3 bảng, WAL, retention prune
    status: pending
  - id: provider-di
    content: Viết SQLiteLoggerProvider + IUserActionLogger + ILogQueryService + AddSqliteLogging (bind ConfigReader, ValidateOnStart)
    status: pending
  - id: history-page
    content: "Thêm trang Lịch sử thao tác (mục nhà phát triển): AppSection + tab + HistoryView/ViewModel, lọc + xuất JSON/CSV"
    status: pending
  - id: docs-tests
    content: Viết docs/logging-sqlite.md + XML doc, thêm unit test, build và chạy thử kiểm tra file .db
    status: pending
isProject: false
---

# Kế hoạch lib log SQLite cho TanTienQR

## Hiện trạng
- Chưa có SQLite/EF/Dapper trong repo (grep toàn repo không có kết quả).
- Logging hiện tại chỉ là `ILogger<T>` ra console (`builder.Logging.AddConsole()` trong `App.axaml.cs`), không lưu trữ.
- DI theo pattern: `Host.CreateApplicationBuilder` + extension `AddTanTienServices` trong [TanTienQR/Services/TanTienAppServices.cs](d:/DuAn/VisionF/TanTienQR/Services/TanTienAppServices.cs), bind config thủ công qua `ConfigReader` để tương thích Native AOT.
- Lib dùng chung là [ClassLibraryMT/ClassLibraryMT.csproj](d:/DuAn/VisionF/ClassLibraryMT/ClassLibraryMT.csproj) (`net10.0`, `IsAotCompatible=true`, đã bật `GenerateDocumentationFile`).
- Các thao tác người dùng cần log nằm ở [TanTienQR/ViewModels/MainViewModel.cs](d:/DuAn/VisionF/TanTienQR/ViewModels/MainViewModel.cs) (chuyển tab, mở license window, import/revoke license) và [TanTienQR/ViewModels/ConfigEditorViewModel.cs](d:/DuAn/VisionF/TanTienQR/ViewModels/ConfigEditorViewModel.cs) (Save/Reset cấu hình).
- Điều hướng tab hiện tại: enum [TanTienQR/Models/AppSection.cs](d:/DuAn/VisionF/TanTienQR/Models/AppSection.cs) (Info/Production/Configuration) + partial [TanTienQR/ViewModels/MainViewModel.Tabs.cs](d:/DuAn/VisionF/TanTienQR/ViewModels/MainViewModel.Tabs.cs) + segmented control header trong `MainWindow.axaml`.
- File cấu hình mẫu: [TanTienQR/appsettings.json](d:/DuAn/VisionF/TanTienQR/appsettings.json); solution: [VisionF.slnx](d:/DuAn/VisionF/VisionF.slnx).

## Thiết kế lib
- Tạo project mới `ClassLibraryMT.Logging/ClassLibraryMT.Logging.csproj` (`net10.0`, `Nullable`, `IsAotCompatible=true`, `GenerateDocumentationFile=true`), package duy nhất `Microsoft.Data.Sqlite` (dùng ADO.NET thuần, không EF/Dapper để giữ AOT và nhẹ).
- Ghi bất đồng bộ qua `Channel<T>` + worker nền: API log chỉ enqueue, worker batch insert (mỗi 1s hoặc 100 bản ghi), flush khi shutdown. Tránh block UI thread.
- SQLite: WAL mode, `busy_timeout`, tạo bảng `IF NOT EXISTS` + bảng `SchemaVersion` lúc khởi động. File mặc định `logs/app.db` dưới `AppContext.BaseDirectory`.
- Tích hợp `Microsoft.Extensions.Logging`: `SQLiteLoggerProvider : ILoggerProvider` để mọi `ILogger<T>` hiện có tự chảy vào SQLite, không phải sửa callsite.
- API riêng cho thao tác người dùng: `IUserActionLogger.LogAsync(action, target, detail, result)` vì đây là sự kiện nghiệp vụ, không phải log kỹ thuật.
- API đọc cho trang Lịch sử: `ILogQueryService` với `GetUserActionsAsync(filter, paging)` và `GetAppLogsAsync(...)` trả về DTO (lib không phụ thuộc UI, ViewModel tự serialize JSON/CSV để giữ AOT-safe bằng source-generated `JsonContext`).

## Schema SQLite
- `AppLogs`: Id, TimestampUtc, Level, Category, EventId, Message, Exception, PropertiesJson.
- `UserActions`: Id, TimestampUtc, Action, Target, DetailJson, Result.
- `SchemaVersion`: Version applied timestamp, để nâng cấp schema sau này.
- Index trên `UserActions(TimestampUtc)` và `UserActions(Action)` để trang Lịch sử lọc nhanh.
- Cấu hình retention: `Logging:Sqlite:RetentionDays` (worker prune mỗi giờ, mặc định 30 ngày, 0 = giữ vĩnh viễn).

## Cấu hình (section `Logging` trong appsettings.json)
- `Logging:Sqlite:Path` (mặc định `logs/app.db`), `MinLevel` (mặc định Information), `RetentionDays` (mặc định 30), `EnableUserActions` (mặc định true).
- `Logging:History:Enabled` (mặc định true) để ẩn/hiện mục nhà phát triển, `Logging:History:PageSize` (mặc định 200).
- Bind thủ công qua `ConfigReader` + `ValidateOnStart`, theo đúng pattern `Camera`/`Plc` hiện có.

## Tích hợp TanTienQR (chỉ app này)
- Thêm `services.AddSqliteLogging(configuration)` trong `AddTanTienServices`, trước khi đăng ký worker. Đăng ký thêm `HistoryViewModel` singleton.
- Hook log thao tác người dùng tại các RelayCommand hiện có: chuyển tab (kể cả tab Lịch sử), Save/Reset config (kèm kết quả thành công/thất bại), Import/Revoke license, mở/đóng license window, Xuất JSON/CSV.
- Không log nội dung nhạy cảm (ApiKey, license payload) — chỉ log tên hành động + kết quả.

## Trang Lịch sử thao tác (mục nhà phát triển)
- Thêm `AppSection.History = 3` trong [TanTienQR/Models/AppSection.cs](d:/DuAn/VisionF/TanTienQR/Models/AppSection.cs); cập nhật [TanTienQR/ViewModels/MainViewModel.Tabs.cs](d:/DuAn/VisionF/TanTienQR/ViewModels/MainViewModel.Tabs.cs): `NotifyPropertyChangedFor(nameof(IsHistoryVisible))`, `IsHistoryVisible`, `PageTitle`/`PageSubtitle` cho "Lịch sử", `ShowHistoryTabCommand`.
- Header: thêm nút segtab thứ 4 "Lịch sử" vào segmented control trong `MainWindow.axaml` (giữ `Width=140` cố định để không giật layout như lần sửa trước); vùng nội dung thêm `<views:HistoryView IsVisible="{Binding IsHistoryVisible}" />`.
- Tạo `Views/HistoryView.axaml` + `ViewModels/HistoryViewModel.cs`: bộ lọc (từ ngày/đến ngày, hành động, kết quả), bảng hiển thị (thời gian, hành động, đối tượng, chi tiết, kết quả), phân trang theo `PageSize`, nút Làm mới.
- Xuất file qua `StorageProvider` (SaveFilePicker): nút "Xuất JSON" và "Xuất CSV" (tên file `user-actions-yyyyMMdd-HHmmss.json/csv`); CSV có header + escape dấu ngoặc kép; JSON serialize bằng source-generated context (AOT-safe).
- Mục nhà phát triển: nhóm trang Lịch sử dưới nhãn "NHÀ PHÁT TRIỂN" (sidebar MỤC LỤC + ConfigView nếu cần), ẩn/hiện bằng `Logging:History:Enabled`; bản thân hành động xuất file cũng được ghi vào `UserActions`.

## Tài liệu
- `docs/logging-sqlite.md`: cách cấu hình, schema, API, ví dụ query SQLite, hướng dẫn dùng trang Lịch sử + xuất JSON/CSV, lưu ý AOT và retention.
- XML doc trên toàn bộ public API của lib (project đã bật `GenerateDocumentationFile`).

## Kiểm thử và nghiệm thu
- Thêm test theo mẫu [ClassLibraryMT.Plc.Tests](d:/DuAn/VisionF/ClassLibraryMT.Plc.Tests): ghi log, đọc lại từ SQLite qua `ILogQueryService`, kiểm tra prune retention, kiểm tra xuất CSV escape đúng.
- Build `TanTienQR` Debug thành công, chạy thử: thao tác trên UI (chuyển tab, lưu cấu hình) rồi mở trang Lịch sử kiểm tra bản ghi hiển thị, bấm Xuất JSON/CSV và mở file kiểm tra nội dung.
- Tiêu chí xong: app chạy không chậm UI, tắt app không mất log (flush), trang Lịch sử lọc/xuất đúng, không commit file `.db`/log vào git (bổ sung `.gitignore`).
