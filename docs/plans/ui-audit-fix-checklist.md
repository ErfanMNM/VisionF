# TanTienQR UI Audit — Phê duyệt sửa lỗi (checklist)

> Cách dùng: tick `[x]` vào ô của mục bạn **đồng ý sửa**. Chỉ những mục được tick mới được sửa.
> Sau khi sửa xong từng mục sẽ chạy lại harness để chụp ảnh trước/sau đối chiếu.
>
> Chi tiết đầy đủ + ảnh render + số đo: [docs/ui-audit-report.md](../ui-audit-report.md)
> Ảnh và cây trực quan: [docs/ui-audit-shots/](../ui-audit-shots/)

**Tiến độ duyệt:** 18 / 18

> ✅ Đã sửa xong cả 18 mục (UI-001 … UI-018).
> Build: `dotnet build TanTienQR\TanTienQR.csproj` → **0 warning / 0 error**.
> Harness off-screen đã chạy lại (`tools\UiAuditHarness-VF`) → 17/17 ảnh render mới trong `docs\ui-audit-shots\`.
> DB thật trong `TanTienQR\bin\Debug\net10.0\logs\` **không đổi** (cùng size + cùng `LastWriteTimeUtc` trước/sau).

---

## Nhóm 1 — Placeholder bị mask / bị cắt dấu (ưu tiên cao nhất, sửa nhỏ, thấy ngay)

- [x] **UI-009 (Major)** — Placeholder của ô mật khẩu bị mask thành dấu `•`, mất hoàn toàn chữ gợi ý; ô thứ 3 của `PasswordChangeWindow` trống hẳn.
  Vị trí: `TanTienQR/Views/PasswordChangeWindow.axaml`, `RecipeDeleteConfirmWindow.axaml`, `UserEditWindow.axaml`.
  Bằng chứng: `PasswordChangeWindow.png`, `RecipeDeleteConfirmWindow.png`, `UserEditWindow.png`.
  → Đã thêm `VerticalContentAlignment="Center"` cho 6 ô mật khẩu. Render sau: placeholder hiện đủ chữ ("Nhập mật khẩu hiện tại", "Tối thiểu 6 ký tự", "Nhập lại mật khẩu mới", "Nhập mật khẩu để xác nhận") — `DesiredW == W`, không còn dấu `•`.

- [x] **UI-010 (Major)** — Placeholder trong `ComboBox` ("Chọn PO để bắt đầu sản xuất...") bị cắt **12px**, mất dấu nặng/dấu huyền.
  Vị trí: `TanTienQR/Views/ProductionOrdersView.axaml`, `TanTienQR/Views/InfoView.axaml` (ô chọn PO).
  Bằng chứng: `MainWindow__ProductionOrders.png` + `MainWindow__ProductionOrders.tree.json` (Desired 31px / Bounds 19px).
  → Đã sửa ở style dùng chung `ComboBox.field` (`App.axaml`): `VerticalContentAlignment="Center"`, template `ContentPresenter#PART_ContentPresenter` cao hết phần con + canh giữa, `TextBlock#PART_PlaceholderPresenter` cỡ 13px; 2 ComboBox PO nâng cao 48px. Render sau: placeholder `DesiredH=17 / Bounds=17` (không còn cắt 12px).

---

## Nhóm 2 — Resource thiếu (rủi ro runtime, sửa 1 dòng)

- [x] **UI-001 (Major)** — `TextSecondary` không tồn tại trong `App.axaml` nhưng được dùng ở 2 chỗ → `NullReferenceException` lúc runtime (build vẫn pass, 0 warning).
  Vị trí: `TanTienQR/Views/InfoView.axaml:335` (khung "MÁY ĐÃ DỪNG"), `TanTienQR/Views/CodeLookupView.axaml:212` ("NỘI DUNG GỐC CAMERA").
  Cách sửa: thêm `TextSecondary` vào `App.axaml` **hoặc** đổi 2 chỗ sang `TextMuted`.
  → Đã thêm `<SolidColorBrush x:Key="TextSecondary">#3D5A68</SolidColorBrush>` vào `App.axaml` (giữ nguyên 2 chỗ dùng, không đổi thiết kế).

---

## Nhóm 3 — Lệch cột bảng + cắt nhãn tiêu đề (sửa kiến trúc, ảnh hưởng 5 view)

- [x] **UI-002 (Major)** — Tiêu đề cột và dữ liệu dùng 2 hệ toạ độ khác nhau: tiêu đề hụt **20px mỗi bên** (682px vs 722px), các cột `*` lệch tích luỹ.
  Vị trí: `InfoView.axaml` + cùng mẫu ở `AccountsView.axaml`, `HistoryView.axaml`, `ProductionOrdersView.axaml`, `CodeLookupView.axaml`.
  Bằng chứng: `MainWindow__Info.tree.json` (header kết thúc x=951, dữ liệu kết thúc x=971).
  → Đã thống nhất header + dữ liệu về cùng một hệ toạ độ (cùng `x` và cùng `w`) ở `InfoView`, `AccountsView`, `HistoryView`, `ProductionOrdersView`, `PlcSettingsView`; thêm `HorizontalScrollBarVisibility="Disabled"` và dành sẵn 16px khe thanh cuộn cho cả header lẫn dữ liệu. Đo lại sau sửa: Info 914/914, Accounts 1278/1278, History 1282/1282, ProductionOrders 1282/1282, PlcSettings 400/400 — **khớp tuyệt đối**.

- [x] **UI-013 (Minor)** — Hậu quả trực quan của UI-002: tiêu đề cột "GỬI PLC" bị cắt mất chữ "C" (hiển thị `GỬI PL`).
  Vị trí: `TanTienQR/Views/InfoView.axaml` (bảng "LỊCH SỰ MÃ QUÉT"). Sửa chung với UI-002.
  → Render sau: `GỬI PLC` hiện đủ (`DesiredW=42`, `W=100`), không còn mất chữ "C".

---

## Nhóm 4 — Ép chiều cao dưới mức thiết kế ở `PlcSettingsView` (8 vị trí)

- [x] **UI-007 (Major)** — `Height="28/30/32"` đè lên `MinHeight=38` của style chung (`Button.soft`, `Button.whitebtn`, `TextBox.field`) ⇒ nội dung bị cắt.
  Vị trí: `TanTienQR/Views/PlcSettingsView.axaml` dòng 358, 365, 416, 542, 562, 968, 988, 1008, 1059, 1078.
  → Đã bỏ hết các `Height` cố định 28/30/32; nút/ô nhỏ dùng style riêng `smallbtn` / `smallfield` (34px) để vẫn nhỏ gọn nhưng không cắt nội dung.

- [x] **UI-014 (Major)** — Bằng chứng render độc lập xác nhận UI-007: 8 lỗi `fixed-height-too-small` (thiếu 6–10px), đo từ `MainWindow__PlcSettings.tree.json`.
  Sửa chung với UI-007 (bỏ `Height` cố định, để `MinHeight=38` có hiệu lực, hoặc tạo style nút nhỏ riêng).
  → Render sau: **0** phát hiện `fixed-height-too-small` / `text-truncated` trên `PlcSettingsView`.

- [x] **UI-008 (Major)** — Dấu tiếng Việt viết HOA ("CƠ SỞ", "TRUY CẬP", "KIỂU") bị cắt chiều cao (Desired 24px / Bounds 14px).
  Vị trí: `TanTienQR/Views/PlcSettingsView.axaml`. Sửa chung đợt với UI-007 (cùng nguyên nhân ép chiều cao).
  → Đã đặt `LineHeight` rõ ràng cho style `overline` (17) và `fieldLabel` (15) trong `App.axaml`, và style `th` trong `PlcSettingsView`. Render sau: "TÊN" / "GIÁ TRỊ" / "NHẬP" / "Ô NHỚ PLC" đều `DesiredH == H` (17 / 17 / 17 / 19).

---

## Nhóm 5 — Khối KPI / số liệu trùng ở `InfoView` (sửa 1 file)

- [x] **UI-003 (Minor)** — `OrderQtyText` hiển thị 2 lần cạnh nhau (" / 100" size 20 xám + "100" size 22 đậm dưới nhãn "SỐ LƯỢNG ĐƠN") trong khi nhãn ghi "SỐ LƯỢNG ĐƠN" nhưng giá trị là *tổng số mã của PO*.
  Vị trí: `TanTienQR/Views/InfoView.axaml:372` và `:379`.
  → Đã bỏ hậu tố `/ {0}`; khối "ĐÃ QUÉT / TỔNG" dùng `ProductionInfo.ProgressText`; khối bên phải đổi nhãn thành "TỔNG MÃ PO" và hiển thị `OrderQtyText` (đúng nghĩa).

- [x] **UI-017 (Minor)** — Bằng chứng render xác nhận UI-003: hai con số `0` (36px và 22px) cùng một giá trị. Sửa chung với UI-003.
  → Render sau: mỗi giá trị chỉ hiện **một** lần, 2 nhãn khác nghĩa ("ĐÃ QUÉT / TỔNG" và "TỔNG MÃ PO").

- [x] **UI-011 (Minor)** — Ký hiệu `%` ở thẻ "TỶ LỆ ĐẠT" bị cắt vòng tròn trên (Desired 25px / Bounds 19px, thiếu 6px).
  Vị trí: `TanTienQR/Views/InfoView.axaml`. Làm chung đợt `InfoView`.
  → Đã giảm cỡ chữ `%` 16→14 và thêm `VerticalAlignment="Center"`. Render sau: `%` có `DesiredH=17 / Bounds=17` — không còn cắt.

- [x] **UI-012 (Minor)** — Hậu tố `" / 0"` bị cắt 6px phía dưới và lệch baseline so với số lớn (đáy `0` lớn y=355, hậu tố bị cắt ở y=349).
  Vị trí: `TanTienQR/Views/InfoView.axaml:379` (`Margin="0,0,0,6"`). Làm chung đợt `InfoView`.
  → Đã bỏ `Margin="0,0,0,6"`, dùng `VerticalAlignment="Center"`. Render sau: `0%` có `DesiredH=17 / Bounds=17` — không còn cắt, baseline thống nhất.

---

## Nhóm 6 — Chuẩn hoá cỡ chữ tiêu đề + chiều cao nút toàn app (sửa diện rộng, làm gộp)

- [x] **UI-004 (Minor)** — Cỡ chữ tiêu đề trang không nhất quán: 24 (`Info`, `Config`, `ProductionOrders`) / 21 (`CodeLookup`, `Dev`, `History`, `Accounts`) / 20 (`PlcSettings`).
  Cách sửa: chốt 1 cỡ chuẩn (đề xuất 24) hoặc đưa vào style dùng chung `TextBlock.pageTitle`.
  → Đã tạo style `TextBlock.pageTitle` (`FontSize=24`, `FontWeight=Bold`) trong `App.axaml` và áp dụng cho cả 8 tab: Info, Config, ProductionOrders, CodeLookup, Dev, History, Accounts, PlcSettings.

- [x] **UI-006 (Minor)** — Ô nhập cao 44px, 2 nút cao 36px trong cùng hàng tìm kiếm (`CodeLookupView.axaml`) ⇒ viền dưới lệch 8px; toàn app có 11 mức chiều cao nút (28→56) ở 12 file.
  Cách sửa: chốt 2–3 chiều cao chuẩn + `VerticalAlignment="Center"` trong hàng.
  → Đã chốt 3 mức: **44px** (nút hành động chính), **38px** (nút thanh công cụ + ô nhập), **34px** (nút/ô nhỏ trong `PlcSettingsView`). Thêm `VerticalContentAlignment="Center"` cho các nút trong hàng.

- [x] **UI-015 (Minor)** — Bằng chứng render xác nhận UI-006 (`MainWindow__CodeLookup.tree.json`): ô nhập 44px vs nút 38px (khai báo 36, bị `MinHeight=38` đè) ⇒ lệch 3px trên / 6px dưới.
  Sửa chung với UI-006.
  → Hàng tra cứu của `CodeLookupView` nay đồng nhất 38px cho ô nhập + 2 nút (`VerticalAlignment="Center"` trên Grid), viền dưới thẳng hàng.

---

## Nhóm 7 — Cosmetic

- [x] **UI-005 (Cosmetic)** — `LetterSpacing` của style `overline` (0.12) bị override thành 0.1 ở `InfoView.axaml` và `ProductionOrdersView.axaml`.
  Cách sửa: bỏ override, dùng giá trị style chung.
  → Đã bỏ toàn bộ override `LetterSpacing="0.1"` trong các view; giữ style chung `overline` với `LetterSpacing=0.12`.

---

## Nhóm 8 — Trải nghiệm bảng trống / bố cục Config

- [x] **UI-016 (Minor)** — Bảng trống không có thông báo trạng thái rỗng (`ProductionOrdersView` trống 1322×513px, `InfoView`, `PlcSettingsView`).
  Cách sửa: thêm `EmptyView`/overlay "Chưa có dữ liệu" cho `ItemsControl`.
  → Đã thêm overlay trạng thái rỗng cho `ProductionOrdersView` ("Chưa có dữ liệu"), `InfoView` (bảng mã quét + danh sách PO), `PlcSettingsView` ("Chưa có ô nhớ nào", sửa luôn binding bị đảo `!IsTableEmpty` → `IsTableEmpty`), `AccountsView` ("Chưa có tài khoản nào", thêm `AccountsViewModel.IsEmpty`), `HistoryView` và `CodeLookupView`.

- [x] **UI-018 (Cosmetic)** — Tab Cấu hình: cột phải ngắn hơn cột trái nhiều ⇒ vùng trắng trống ~340×460px (x≈856–1318), thanh cuộn không có manh mối trực quan.
  Vị trí: `TanTienQR/Views/ConfigView.axaml:50-51`. Cách sửa: cân bằng 2 cột, đảm bảo thanh cuộn hiện khi vượt khung.
  → Đã đảo bố cục 2×2 cho cân bằng (hàng 1: CAMERA | VÙNG NHỚ D; hàng 2: PLC | WEB · CHECKEE) và đổi `ScrollViewer` sang `VerticalScrollBarVisibility="Visible"`. Đo lại: 2 card cột cao bằng nhau (`w=656`, `h=646`), thanh cuộn dọc hiện rõ (`ScrollBar w=16 h=646`), vùng trắng trống giảm từ ~340×460px xuống còn vài chục px.

---

## Ghi chú thực hiện (áp dụng cho mọi mục được duyệt)

- Chỉ sửa đúng mục đã tick; không sửa thêm ngoài danh sách.
- Chạy trong sandbox/off-screen như lúc audit: không hiện cửa sổ, không đụng DB thật (`TanTienQR/bin/Debug/net10.0/logs/`), không chạm PLC/camera/PO server thật.
- Sau mỗi mục (hoặc mỗi nhóm) chạy lại `tools/UiAuditHarness-VF/` để chụp ảnh trước/sau đối chiếu.
- Không audit lại nội dung web trong `WebView` (headless chỉ ra `about:blank`; app đã có placeholder đúng thiết kế).
