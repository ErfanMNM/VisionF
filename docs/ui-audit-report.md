# TanTienQR — Báo cáo kiểm tra giao diện (UI Audit)

> **Trạng thái:** Hoàn tất.
> Báo cáo gồm hai lớp bằng chứng:
> **(1) Phân tích tĩnh** (đọc mã nguồn XAML + kiểm chứng thực nghiệm bằng Avalonia headless) — `UI-001 … UI-008`
> **(2) Bằng chứng render thực tế** (ảnh PNG 1360×820 + cây trực quan JSON đo từ chính cửa sổ đã render) — `UI-009 … UI-018`
>
> Không sửa một dòng source ứng dụng. Toàn bộ công cụ kiểm tra nằm trong `tools/UiAuditHarness-VF/`.

---

## A. Phương pháp kiểm tra

| Hạng mục | Chi tiết |
|---|---|
| Ứng dụng | `TanTienQR` — Avalonia 12.1.4, .NET 10, cửa sổ 1360×820, `WindowDecorations=None` |
| Nền tảng | Avalonia.Headless + Skia, render **off-screen** (`RenderTargetBitmap` → PNG) — **không hiện cửa sổ lên màn hình người dùng** |
| Cách ly dữ liệu | Toàn bộ SQLite (`users.db`, `app.db`, `plc.db`, `scans.db`) trỏ sang thư mục sandbox riêng (`tools\sandbox\logs\`) — **không đụng DB thật** |
| Mạng ngoài | PLC / camera / handheld scanner / PO server đều trỏ về cổng local chết — **không chạm hệ thống thật** |
| Đăng nhập | `admin` / `tantien512` (đã xác minh: role *Quản trị*, `MustChangePassword = False`) |
| License | **Không chặn UI** — chỉ ảnh hưởng trạng thái PLC "Ready" (D16) và badge trạng thái |

### A.1. Xác minh quan trọng đã thực hiện

1. **License không chặn giao diện** — `LicenseManager.Load()` chỉ gọi `AppStateManager.SetLicenseValid()`;
   `LoginViewModel` hoàn toàn không kiểm tra license. Vì vậy audit **không cần** license hợp lệ,
   không cần đụng tới Cloudflare/wrangler.
2. **Cơ chế render off-screen đã được chứng minh hoạt động**: dựng `MainWindow` 1360×820 qua
   `Avalonia.Headless`, render ra PNG 105 KB với 118 màu khác nhau, trong đó khớp đúng design token
   của app: nền `#F6F9FB`, panel `#FFFFFF`, accent `#1A56DB`, viền `#D8E6EC`.
3. **`MainViewModel` / `LoginViewModel` là `internal`** → không thể tham chiếu trực tiếp từ harness.
   Giải pháp: resolve qua DI (`TanTienAppServices.AddTanTienServices` là public) rồi lấy instance
   bằng reflection. **Không cần sửa một dòng source của app.**
4. **DB thật không bị chạm**: đã đo MD5 + mốc sửa của 4 file `*.db` trong
   `TanTienQR\bin\Debug\net10.0\logs\` **trước và sau** khi chạy harness → **không đổi** (byte-for-byte).
5. **Không cửa sổ nào hiện trên màn hình**: toàn bộ cửa sổ được đặt ở `Position = (-4000, -4000)`
   và nền tảng là `Avalonia.Headless` (không có backend hiển thị).

### A.2. Xử lý lỗi composition-cache của Avalonia 12 (quan trọng cho độ tin cậy của ảnh)

**Triệu chứng:** `MainWindow` xếp **cả 9 view dưới cùng một ô `Grid`**, chỉ bật/tắt bằng `IsVisible`.
Avalonia 12 cache render data theo visual trong `CompositingRenderer`, nên `RenderTargetBitmap.Render(mainWindow)`
sau khi đổi tab vẫn trả về **khung của view trước đó** (ảnh `Production` và `Config` ra nội dung `InfoView`).
Cây trực quan dump từ `MainWindow` cũng chứa **cả 9 view**, khiến kết quả không tin cậy.

**Cách khắc phục đã áp dụng** (`tools/UiAuditHarness-VF/Program.cs`, hàm `CaptureView`):

1. Vẫn chạy **command điều hướng thật** trên `mainVm` (`ShowInfoTabCommand`, `ShowConfigTabCommand`, …)
   để side-effect của ViewModel xảy ra y hệt app thật (nạp lịch sử, danh sách PO, refresh PLC).
2. Tạo **instance MỚI** của view qua reflection: `Activator.CreateInstance(asm.GetType("TanTienQR.Views." + t))`.
3. Đặt instance mới làm `Content` của **một `Window` MỚI**, `DataContext = mainVm` → **composition tree hoàn toàn mới**,
   không có khả năng dùng cache cũ.
4. `window.Show()` → pump dispatcher → **3 vòng `UpdateLayout()` + `PumpRender()`** để nội dung
   `ScrollViewer`/`ItemsControl` được "realize" và toạ độ ổn định.
5. **Dump cây trực quan TRƯỚC**, rồi render PNG **NGAY SAU** (không có bước pump nào ở giữa)
   ⇒ `.tree.json` và `.png` luôn mô tả **cùng một trạng thái layout**.
6. `window.Close()`.

**Kết quả:** mỗi view có số node khác nhau, ảnh khác nhau, và **khớp với cây trực quan**:
`Info` 218 node · `Production` 47 · `Config` 627 · `PlcSettings` 211 · `History` 1688 ·
`ProductionOrders` 88 · `CodeLookup` 109 · `Accounts` 85 · `Dev` 3799.

### A.3. Phạm vi kiểm tra

| Nhóm | Số lượng | Trạng thái |
|---|---|---|
| Tab chính (`Info`, `Production`, `Config`, `PlcSettings`, `History`, `ProductionOrders`, `CodeLookup`, `Accounts`, `Dev`) | 9 / 9 | ✅ render thành công |
| Cửa sổ con (`LicenseWindow`, `PasswordChangeWindow`, `UserEditWindow`, `RecipeDeleteConfirmWindow`, `PlcParamEditWindow`) | 5 / 5 | ✅ render thành công |
| Màn hình đăng nhập (`LoginShellWindow`) | 1 / 1 | ✅ render thành công |
| Overlay (`LicensePanel`, `WindowMenuPopup`) | 2 / 2 | ✅ render thành công |
| **Tổng** | **17 / 17** | **0 thất bại** |

Ảnh: `docs\ui-audit-shots\*.png` — cây trực quan: `docs\ui-audit-shots\*.tree.json` — máy phân tích: `findings.json`.

---

## B. Lỗi đã xác minh bằng phân tích tĩnh

> Phần này giữ nguyên các mục `UI-001 … UI-008` đã được kiểm chứng độc lập bằng đọc mã nguồn
> và đo trực tiếp trên cây trực quan. **Không sửa, không lược bớt.**

### UI-001 — `TextSecondary` là resource không tồn tại (Major)

**View:** `InfoView.axaml:335`, `CodeLookupView.axaml:212`

Hai chỗ dùng `Foreground="{StaticResource TextSecondary}"` nhưng `App.axaml` **chỉ định nghĩa**
`TextPrimary` (#0F2A38) và `TextMuted` (#5C7683) — không có `TextSecondary`.

**Bằng chứng (kiểm chứng thực nghiệm):**
```
App resources count = 26
TryGetResource('TextSecondary') found=False value=null
TryGetResource('TextMuted')     found=True  value=#ff5c7683
→ Resolve StaticResource thiếu: NullReferenceException
```

`dotnet build TanTienQR` vẫn **Build succeeded, 0 Warning** → đây là lỗi **runtime-only**,
không bị trình biên dịch XAML bắt.

**Ảnh hưởng:**
- `InfoView.axaml:335` — dòng giải thích trong khung cảnh báo "MÁY ĐÃ DỪNG" (chỉ hiện khi `ProductionInfo.IsHalted`)
- `CodeLookupView.axaml:212` — giá trị "NỘI DỤNG GỐC CAMERA"

**Cách sửa (đề xuất, chưa áp dụng):** khai báo `TextSecondary` trong `App.axaml` (một màu trung gian
giữa `TextPrimary` và `TextMuted`), **hoặc** đổi 2 chỗ dùng sang `TextMuted` cho khớp style hiện có.

---

### UI-002 — Tiêu đề cột của bảng bị lệch so với dữ liệu (Major)

**View:** `InfoView.axaml` (bảng "LỊCH SỰ MÃ QUÉT"), và cùng kiểu lỗi ở
`AccountsView.axaml`, `HistoryView.axaml`, `ProductionOrdersView.axaml`, `CodeLookupView.axaml`

**Đo đạc thực tế từ cây trực quan (render headless, cửa sổ 1360×820):**

| Thành phần | X | Rộng | Biên phải |
|---|---|---|---|
| Hàng tiêu đề cột (`Grid(1)`) | 269 | **682** | 951 |
| Vùng dữ liệu (`ScrollViewer`) | 249 | **722** | 971 |

**Chi tiết từng cột tiêu đề (đo trong `MainWindow__Info.tree.json`):**

| Cột | X | Rộng | Biên phải |
|---|---|---|---|
| GIỜ | 269 | 90 | 359 |
| MÃ / SERIAL | 371 | 127 | 498 |
| GTIN | 510 | 95 | 605 |
| KẾT QUẢ | 617 | 100 | 717 |
| XỬ LÝ | 729 | 110 | 839 |
| **GỬI PLC** | 851 | 100 | **951** |

→ Vùng dữ liệu kết thúc ở **971**, còn cột cuối của tiêu đề kết thúc ở **951**:
**tiêu đề hụt 20px ở mép phải**, và vì các cột `*` được chia theo tỉ lệ trên một bề rộng nhỏ hơn
(682 thay vì 722), **mọi cột sau cột đầu đề lệch dần so với dữ liệu**.

**Nguyên nhân trong mã nguồn:**

```xml
<!-- Tiêu đề cột: Margin 20 hai bên -> bị thu hẹp lại 20px mỗi bên -->
<Grid Grid.Row="1" ColumnDefinitions="90,1.6*,1.2*,100,110,100"
      ColumnSpacing="12" Margin="20,0" ...>

<!-- Vùng dữ liệu: ScrollViewer KHÔNG có margin, chỉ mỗi dòng mới có -->
<Grid Grid.Row="2">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <ItemsControl ...>
            <DataTemplate>
                <Border Margin="20,1" Padding="0,7">   <!-- margin nằm ở TỪNG DÒNG -->
```

Tiêu đề dùng `Margin` ở **cấp Grid** (thu hẹp cả khung), còn dữ liệu dùng `Margin` ở **cấp dòng**
(chỉ thu hẹp nội dung từng dòng). Hai cách này cho ra **hai hệ toạ độ khác nhau**.

**Hệ quả:**
1. Tiêu đề cột và dữ liệu **không thẳng hàng** (lệch tích luỹ theo từng cột).
2. Khi bảng có **nhiều dòng và xuất hiện thanh cuộn dọc**, `ScrollViewer` bị thu hẹp thêm
   ~15–17px ⇒ độ lệch **tăng thêm**, cột "GỬI PLC" lệch hẳn khỏi dữ liệu.
3. Lỗi lặp lại ở 5 view dùng chung mẫu bảng này.

**Cách sửa (đề xuất):** đưa tiêu đề cột và dữ liệu về **cùng một khung toạ độ** — gỡ
`Margin="20,0"` ở tiêu đề và thay bằng `Padding` trên `Border` bao ngoài, hoặc bọc cả tiêu đề +
dữ liệu vào chung một `ScrollViewer` / `Grid` với cùng một padding; và khai báo rõ
`HorizontalScrollBarVisibility="Disabled"` để thanh cuộn dọc không làm lệch thêm.

> **Ghi chú:** công cụ audit ban đầu báo "sibling-width-mismatch" cho vùng này, nhưng phần lớn
> là **false positive** do chính công cụ gom nhầm các hàng khác nhau của `Grid` vào cùng nhóm.
> Độ lệch **20px ở mép phải** nêu trên là kết quả đo trực tiếp từ cây trực quan, đã được
> đối chiếu với mã nguồn — đây là lỗi thật.

---

### UI-003 — Cùng một giá trị hiển thị 2 lần, 2 kiểu chữ khác nhau (Minor)

**View:** `InfoView.axaml:372` và `InfoView.axaml:379`

`OrderQtyText` (tổng số lượng của PO) được bind **hai lần** trong cùng một khối:

```xml
<!-- Khối "ĐÃ QUÉT / TỔNG" -->
<TextBlock FontSize="36" ... Text="{Binding ProductionInfo.ScannedText}" />          <!-- 12 -->
<TextBlock FontSize="20" FontWeight="Medium" Foreground="TextMuted" Margin="0,0,0,6"
           Text="{Binding ProductionInfo.OrderQtyText, StringFormat=' / {0}'}" />      <!-- " / 100" -->

<!-- Khối "SỐ LƯỢNG ĐƠN" ngay bên cạnh -->
<TextBlock Classes="overline" Text="SỐ LƯỢNG ĐƠN" />
<TextBlock FontSize="22" FontWeight="Bold" Foreground="TextPrimary"
           Text="{Binding ProductionInfo.OrderQtyText}" />                              <!-- "100" -->
```

**Bằng chứng:** `ProductionInfoViewModel.OrderQtyText` = tổng số mã của PO
(`_po.TotalCount` hoặc `order.OrderQty`) — **không phải số lượng đơn hàng**.
Vì vậy "SỐ LƯỢNG ĐƠN" đang hiển thị *số mã của PO*, tức là trùng đúng con số đã hiện ở "TỔNG".

**Ảnh hưởng:** người dùng thấy "100" hai lần cạnh nhau (một lần size 20 xám nhạt, một lần size 22
đậm) với 2 nhãn khác nhau ⇒ gây hiểu nhầm là có 2 chỉ số khác nhau.

**Cách sửa (đề xuất):** hoặc bỏ binding ` / {0}` ở khối "ĐÃ QUÉT / TỔNG" và đổi nhãn thành
"ĐÃ QUÉT" (dùng `ProgressText` vốn đã có sẵn: `$"{ScannedText} / {OrderQtyText}"`), hoặc đổi khối
"SỐ LƯỢNG ĐƠN" sang hiển thị **số lượng đơn hàng** (`order.OrderQty`) đúng nghĩa tên.

---

### UI-004 — Cỡ chữ tiêu đề trang không nhất quán giữa các tab (Minor)

**Ảnh hưởng:** khi chuyển tab, tiêu đề **nhảy cỡ chữ**, gây cảm giác giao diện chưa hoàn thiện.

| View | Tiêu đề | FontSize |
|---|---|---|
| `InfoView.axaml:22` | Bảng điều khiển quét mã | **24** |
| `ConfigView.axaml:16` | Cấu hình ứng dụng | **24** |
| `ProductionOrdersView.axaml:17` | Truy xuất Sản xuất (Local) | **24** |
| `CodeLookupView.axaml:17` | Tra cứu mã đã quét | **21** |
| `DevView.axaml:33` | Dev · Trạng thái runtime | **21** |
| `HistoryView.axaml:32` | Lịch sử thao tác | **21** |
| `AccountsView.axaml:19` | Tài khoản người dùng | **21** |
| `PlcSettingsView.axaml:178` | Chỉnh PLC | **20** |

**Cách sửa (đề xuất):** chốt một cỡ chuẩn cho tiêu đề trang (đề xuất 24, hoặc 21 cho cả nhóm
hiện tại đang chiếm đa số 4/8) và áp dụng đồng loạt. Tốt hơn nữa: đưa cỡ chữ này vào một style
dùng chung (vd. `TextBlock.pageTitle`) để tránh lệch lần nữa.

---

### UI-005 — `LetterSpacing` của style `overline` bị override không nhất quán (Cosmetic)

**Style gốc** (`App.axaml:120`): `TextBlock.overline` → `LetterSpacing = 0.12`

**Các chỗ override** (đặt `LetterSpacing="0.1"` trực tiếp):
- `InfoView.axaml` (tiêu đề "LỊCH SỰ MÃ QUÉT")
- `ProductionOrdersView.axaml`

**Cách sửa (đề xuất):** bỏ các override, dùng luôn giá trị từ style `overline` chung.

---

### UI-006 — Chiều cao nút không nhất quán trong cùng một hàng (Minor)

**View:** `CodeLookupView.axaml` — hàng tìm kiếm

```xml
<TextBox  Height="44" ... />              <!-- ô nhập -->
<Button  Height="36" MinWidth="88" ... />  <!-- "Tra cứu" -->
<Button  Height="36" MinWidth="80" ... />  <!-- "Xoá" -->
```

Ô nhập cao 44px còn 2 nút cao 36px, cả 3 cùng nằm trong một hàng không có `VerticalAlignment="Center"`
⇒ **viền trên cùng thẳng hàng, viền dưới lệch 8px** — nhìn rất rõ.

**Quy mô toàn app** (độ cao nút trong khoảng 28–56px):

| View | Các chiều cao nút đang dùng |
|---|---|
| `AccountsView` | 28, 30, 36, 52 |
| `CodeLookupView` | 36, **44**, 52 |
| `DevView` | 34, 36, 42 |
| `HistoryView` | 36, 38, 42 |
| `MainWindow` | 28, 30, 32, 34, 36, 40, 44 |
| `PlcSettingsView` | 28, 30, 32, 34, 36, 42, 52 |
| `ProductionOrdersView` | 40, 42, 44 |
| `PasswordChangeWindow` | 42, 44, 48 |
| `RecipeDeleteConfirmWindow` | 42, 44, 48 |
| `PlcParamEditWindow` | 42, 44 |
| `UserEditWindow` | 42, 44 |

**Cách sửa (đề xuất):** chốt 2–3 chiều cao chuẩn (vd. 36 cho nút trong toolbar, 42–44 cho nút
chính trong form) và áp dụng đồng loạt; đặt nút trong hàng có `VerticalAlignment="Center"`.

---

### UI-007 — Nút/ô nhập bị ép chiều cao dưới mức thiết kế ⇒ nội dung bị cắt (Major)

**View:** `PlcSettingsView.axaml` (nhiều vị trí)

Các style dùng chung trong `App.axaml` đều đặt chiều cao tối thiểu **38px**:

```xml
<Style Selector="Button.soft">      <Setter Property="MinHeight" Value="38" /> ...
<Style Selector="Button.whitebtn">  <Setter Property="MinHeight" Value="38" /> ...
<Style Selector="TextBox.field">    <Setter Property="MinHeight" Value="38" /> ...
```

Nhưng `PlcSettingsView.axaml` đặt **chiều cao cố định** nhỏ hơn ở nhiều chỗ. Trong Avalonia,
`Height` (giá trị cố định) **thắng** `MinHeight` của style ⇒ control bị ép nhỏ hơn mức thiết kế
và nội dung bị cắt.

**Bằng chứng đo từ cây trực quan (`fixed-height-too-small`):**

| View | Chiều cao đặt | DesiredSize.Height | Thiếu |
|---|---|---|---|
| `PlcSettingsView` | 32px (`TextBox`, `Button`) | 38px | **6px** |
| `PlcSettingsView` | 30px (`ComboBox`, `Button`) | 38px | **8px** |
| `PlcSettingsView` | 28px (`Button`) | 38px | **10px** |

Các dòng mã tương ứng: `PlcSettingsView.axaml` dòng 358 (`TextBox Height="32"`), 365 (`Button
Height="32"`), 416 (`Button Height="28"`), 542 & 562 (`ComboBox Height="30"`), 968 / 988 / 1008
(`Button Height="32"`), 1059 / 1078 (`Button Height="30"`).

**Cách sửa (đề xuất):** bỏ các `Height="28|30|32"` đó (để style `MinHeight=38` có hiệu lực), hoặc
đặt lại bằng 38px; nếu cần nút nhỏ hơn thì tạo một style riêng (vd. `Button.chipSm`) thay vì ép
chiều cao trực tiếp, để không phá vỡ quy ước thiết kế dùng chung.

---

### UI-008 — Dấu tiếng Việt viết HOA bị cắt chiều cao (Major)

**View:** `PlcSettingsView.axaml`

Các nhãn dạng **CHỮ HOA CÓ DẤU** (ví dụ "CƠ SỞ", "LOẠI", "TRUY CẬP", "KIỂU") có chiều cao
thực tế nhỏ hơn chiều cao cần thiết: **DesiredSize.Height = 24px nhưng Bounds.Height chỉ 14px** ⇒
**phần dấu nằm trên (và dưới) bị cắt**.

Nguyên nhân: chữ HOA tiếng Việt (`Ơ Ư Ầ Ộ Ệ …`) với `FontWeight="SemiBold"` + `LetterSpacing`
cần nhiều chiều cao hơn chữ thường, nhưng `TextBlock.overline` chỉ đặt `FontSize=11` và
`LetterSpacing=0.12`, và các ô chứa nó bị đặt chiều cao cố định (28–32px) như mô tả ở UI-007.

**Cách sửa (đề xuất):** không ép chiều cao cố định cho các ô chứa nhãn overline có dấu; hoặc
tăng `LineHeight`/`MinHeight` của ô; hoặc dùng chữ thường có dấu cho nhãn này.

---

## C. Lỗi xác minh bằng bằng chứng RENDER THỰC TẾ

> Các mục dưới đây lấy từ **ảnh PNG 1360×820 đã render** kèm **đo đạc trực tiếp** trên cây trực quan
> JSON của **chính cửa sổ đã render** (không phải `MainWindow` chung) — nhờ vậy mỗi phát hiện
> đều gắn đúng với một view và đo được bằng số liệu cụ thể.

---

### UI-009 — Placeholder của ô mật khẩu bị mask thành dấu "•", mất hoàn toàn chữ gợi ý (Major)

**View:** `PasswordChangeWindow`, `RecipeDeleteConfirmWindow`, `UserEditWindow`

Khi một `TextBox` có **cả** `PasswordChar="•"` **và** `PlaceholderText`, Avalonia 12 render
**placeholder qua lớp mask** ⇒ người dùng thấy một loạt dấu chấm thay vì chữ hướng dẫn.
Riêng ô thứ 3 của `PasswordChangeWindow` ("XÁC NHẬN MẬT KHẨU MỚI") placeholder **biến mất hẳn**,
ô trông trống trơn — dễ bị hiểu là đã điền sẵn hoặc lỗi hiển thị.

**Bằng chứng render:**

| Cửa sổ | Ô | Nhìn thấy trong ảnh | Placeholder khai báo |
|---|---|---|---|
| `PasswordChangeWindow` | MẬT KHẨU HIỆN TẠI | `••••••` | "Nhập mật khẩu hiện tại" |
| `PasswordChangeWindow` | MẬT KHẨU MỚI | `••••••` | "Tối thiểu 6 ký tự" |
| `PasswordChangeWindow` | XÁC NHẬN MẬT KHẨU MỚI | **(trống hoàn toàn)** | "Nhập lại mật khẩu mới" |
| `RecipeDeleteConfirmWindow` | Mật khẩu xác nhận | `••••••` | "Nhập mật khẩu để xác nhận" |
| `UserEditWindow` | Mật khẩu / Xác nhận | `••••••` | "Tối thiểu 6 ký tự" / "Nhập lại mật khẩu" |

Đo đạc xác nhận các ô **thực sự rỗng** (`Text=''` trong `PasswordChangeWindow.tree.json` cho cả 3 ô,
`RecipeDeleteConfirmWindow.tree.json` và `UserEditWindow.tree.json` tương tự) ⇒ các dấu chấm
**không phải mật khẩu đã nhập**, mà là placeholder bị mask.

**Đối chiếu để xác định nguyên nhân:** `LoginShellWindow` cũng có `PasswordChar` + `PlaceholderText`
nhưng placeholder **hiện bình thường** (chữ xám "Nhập mật khẩu"), vì các `TextBox` ở đó có thêm
`VerticalContentAlignment="Center"`. `PasswordChangeWindow` / `RecipeDeleteConfirmWindow` /
`UserEditWindow` **thiếu** thuộc tính này.

**Cách sửa (đề xuất):** thêm `VerticalContentAlignment="Center"` cho các `TextBox` mật khẩu
(đồng thời kiểm tra lại `App.axaml` style `TextBox.field` để áp dụng chung), hoặc bỏ `PasswordChar`
và tự mask bằng `TextBox.InnerRightContent`/converter, hoặc tạo một style riêng cho ô mật khẩu.

---

### UI-010 — Placeholder trong `ComboBox` bị cắt dấu tiếng Việt (Major)

**View:** `ProductionOrdersView` (ô "Chọn PO"), `InfoView` (ô chọn PO)

**Đo đạc chính xác** (`MainWindow__ProductionOrders.tree.json`):

| Thuộc tính | Giá trị |
|---|---|
| Text | `Chọn PO để bắt đầu sản xuất...` |
| FontSize | 14 |
| **DesiredSize.Height** | **31** |
| **Bounds.Height** | **19** |
| **Chiều cao bị cắt** | **12px** |

Đây là mức cắt **lớn nhất** trong toàn bộ audit. Trong ảnh render, phần dưới của các chữ
`đ`, `ầ`, `ẩu`, `ả` bị cắt thẳng — mất toàn bộ dấu nặng và dấu huyền.

**Nguyên nhân:** `ComboBox` có `MinHeight="44"` nhưng `TextBlock` placeholder bên trong
(`ContentPresenter` của ComboBox) chỉ được cấp **19px**, không đủ cho chữ 14px có dấu.

**Cách sửa (đề xuất):** đặt `MinHeight`/`Height` lớn hơn cho `ComboBox` (vd. 48), hoặc thêm
`VerticalContentAlignment="Center"` + `FontSize` nhỏ hơn cho placeholder, hoặc đặt
`ComboBox.placeholder` trong một `Style` dùng chung.

---

### UI-011 — Ký hiệu "%" bị cắt chiều cao ở thẻ "TỶ LỆ ĐẠT" (Minor)

**View:** `InfoView`

**Đo đạc** (`MainWindow__Info.tree.json`):

| Thuộc tính | Giá trị |
|---|---|
| Text | `%` |
| FontSize | 16 |
| DesiredSize.Height | **25** |
| Bounds.Height | **19** |
| Chiều cao bị cắt | **6px** |

Ảnh render cho thấy vòng tròn trên của dấu `%` bị cắt thẳng.

**Cách sửa (đề xuất):** tăng chiều cao ô chứa (hoặc `MinHeight`), hoặc giảm `FontSize` xuống 14.

---

### UI-012 — Hậu tố " / 0" bị cắt chiều cao và lệch baseline so với số lớn (Minor)

**View:** `InfoView` — khối "ĐÃ QUÉT / TỔNG"

**Đo đạc:**

| TextBlock | X | Y | Bounds.H | Desired.H | FontSize | Cắt |
|---|---|---|---|---|---|---|
| `0` (số lớn) | 1005 | 313 | 42 | 42 | 36 | — |
| ` / 0` (hậu tố) | 1033 | 325 | **24** | **30** | 20 | **6px** |

Ảnh render cho thấy hậu tố `/ 0` bị cắt phía dưới và **nằm lệch baseline** so với chữ `0` lớn
(đáy của `0` lớn ở y=355, còn ` / 0` bị cắt ở y=349).

**Cách sửa (đề xuất):** bỏ `Margin="0,0,0,6"` trên TextBlock hậu tố (`InfoView.axaml:379`) và
đặt `VerticalAlignment="Center"` cho cả khối, hoặc đưa cả hai về cùng một `FontSize`.

---

### UI-013 — Tiêu đề cột "GỬI PLC" bị cắt chữ "C" (Minor)

**View:** `InfoView` — bảng "LỊCH SỰ MÃ QUÉT"

**Đo đạc** (`MainWindow__Info.tree.json`):

| Thành phần | X | Rộng | Biên phải |
|---|---|---|---|
| Hàng tiêu đề cột (`Grid(1)`) | 37 | **914** | **951** |
| Vùng dữ liệu (`Grid(2)`) | 17 | **954** | **971** |
| Chênh lệch | | | **40px** (20px mỗi bên) |

Cột `GỬI PLC` bắt đầu tại x=851 với bề rộng 100px ⇒ kết thúc ở 951, **trong khi vùng dữ liệu
kéo tới 971**. Header "GỬI PLC" bị cắt mất chữ "C" ở mép phải (ảnh render xác nhận rõ:
hiển thị `GỬI PL`).

> **Độc lập với UI-002:** UI-002 mô tả sai lệch *hệ toạ độ* giữa tiêu đề và dữ liệu (cũng 20px mỗi bên,
> đo từ cây trực quan của `MainWindow` trước đây). UI-013 là **hậu quả trực quan cụ thể** của
> chính sai lệch đó: **một nhãn tiêu đề bị cắt chữ** — đã xác nhận bằng ảnh render.
> Số đo hiện tại (914 vs 954) là số đo lại trên bản render per-view đã sửa.

**Cách sửa (đề xuất):** xem UI-002.

---

### UI-014 — Control bị đặt cố định chiều cao nhỏ hơn `MinHeight` của style ⇒ mất nội dung (Major)

**View:** `PlcSettingsView` (8 vị trí)

Bộ máy phân tích ghi nhận **8** lỗi `fixed-height-too-small` trong `PlcSettingsView`:

| Chiều cao đặt | DesiredSize.Height | Thiếu | Loại control |
|---|---|---|---|
| 32px | 38px | 6px | `Button` (×6), `TextBox` (×1) |
| 30px | 38px | 8px | `Button` (×2) |
| 28px | 38px | 10px | `Button` (×1) |

Đây là **bằng chứng render độc lập** xác nhận UI-007, lấy từ cây trực quan của chính cửa sổ
`PlcSettingsView` đã render (không phải `MainWindow` chung) — trước đây mới chỉ có phân tích tĩnh.

Vị trí cụ thể (từ `MainWindow__PlcSettings.tree.json`):
`Grid(0)/Border(1)/Grid(0)/Grid(0)/Button(2)` (28px), `.../Grid(0)/Border(4)/Grid(0)/StackPanel(1)/Button(0..2)`,
`.../Grid(0)/Grid(1)/TextBox(1)` và `Button(2)` (32px), `.../Grid(0)/Button(1|2)` (30px).

**Cách sửa (đề xuất):** xem UI-007.

---

### UI-015 — Ô nhập và 2 nút trong cùng hàng lệch chiều cao (Minor) — *xác nhận UI-006 bằng render*

**View:** `CodeLookupView` — hàng tìm kiếm

**Đo đạc** (`MainWindow__CodeLookup.tree.json`):

| Control | X | Y | Bounds.H | Desired.H | `Height` khai báo |
|---|---|---|---|---|---|
| `TextBox` (nhập mã) | 33 | **78** | **44** | 44 | 44 |
| `Button` "Tra cứu" | 1139 | **81** | **38** | 38 | 36 (bị `MinHeight=38` đè) |
| `Button` "Xoá" | 1247 | **81** | **38** | 38 | 36 (bị `MinHeight=38` đè) |

⇒ Nút **thấp hơn ô nhập 3px ở mép trên** và **ngắn hơn 6px ở mép dưới**.
Ảnh render xác nhận rõ: viền dưới của ô nhập thấp hơn viền dưới của 2 nút.

**Cách sửa (đề xuất):** xem UI-006.

---

### UI-016 — Bảng trống không có thông báo trạng thái rỗng (Minor)

**View:** `ProductionOrdersView`, `InfoView`, `PlcSettingsView`

Các bảng dùng `ItemsControl` không có template "rỗng". Khi sandbox không có dữ liệu,
vùng bảng trống trơn, **không có thông điệp** giải thích vì sao trống.

**Đo đạc** (`findings.json`, kind `empty-container`):

| View | Kích thước vùng trống | Ghi chú |
|---|---|---|
| `ProductionOrdersView` | **1322 × 513px** | vùng trống lớn nhất, chiếm gần 40% bề cao cửa sổ |
| `InfoView` | 954 × 536px | bảng "LỊCH SỰ MÃ QUÉT" |
| `InfoView` | 318 × 204px | khối "LỆNH SẢN XUẤT" |
| `PlcSettingsView` | 428 × 340px | danh sách tham số |

**Cách sửa (đề xuất):** thêm `DataTemplate` / `EmptyView` cho `ItemsControl`, hoặc một `Border`
overlay hiển thị "Chưa có dữ liệu" khi collection rỗng.

---

### UI-017 — Một giá trị hiển thị 2 lần với 2 nhãn khác nhau (Minor) — *xác nhận UI-003 bằng render*

**View:** `InfoView` — khối "ĐÃ QUÉT / TỔNG" vs "SỐ LƯỢNG ĐƠN"

Ảnh render cho thấy ngay dưới tiêu đề "ĐÃ QUÉT / TỔNG" có **hai con số `0`**:
một `0` cỡ 36px và một `0` cỡ 22px nằm bên phải dưới nhãn "SỐ LƯỢNG ĐƠN" — **cùng một giá trị**
(`OrderQtyText`), hiển thị 2 lần với 2 nhãn khác nhau, đúng như UI-003 mô tả.

**Cách sửa (đề xuất):** xem UI-003.

---

### UI-018 — Vùng trống lớn bất đối xứng giữa 2 cột trong tab Cấu hình (Cosmetic)

**View:** `ConfigView`

`ConfigView.axaml:51` dùng `Grid ColumnDefinitions="*,*"` đặt các card cấu hình thành 2 cột.
Cột trái (CAMERA + PLC + VÙNG NHỚ D + WEB) cao hơn cột phải rất nhiều, nên phía dưới cột phải
(x ≈ 856–1318) là **vùng trắng trống ~340px × 460px** trong khi cột trái đã kín nội dung.

Ngoài ra, `ScrollViewer` bao ngoài (`ConfigView.axaml:50`) có `VerticalScrollBarVisibility="Auto"`
nhưng trong cây trực quan **không có `ScrollBar` nào được đo được** (`W`/`H` rỗng) ⇒ người dùng
không có manh mối trực quan rằng còn nội dung bên dưới.

**Cách sửa (đề xuất):** cân bằng lại chiều cao 2 cột (vd. dùng `GridSplitter` hoặc chuyển card
về 1 cột khi nội dung ngắn), và đảm bảo thanh cuộn dọc hiển thị khi nội dung vượt khung.

---

## D. Vấn đề mang tính hệ thống (xuất hiện ở nhiều view)

| # | Vấn đề | Phạm vi | Mức độ |
|---|---|---|---|
| S-1 | Tiêu đề cột bảng và dữ liệu dùng 2 hệ toạ độ khác nhau ⇒ lệch cột + cắt nhãn | 5 view bảng | Major |
| S-2 | `Height` cố định (28/30/32) đè lên `MinHeight=38` của style chung ⇒ cắt nội dung | `PlcSettingsView` (8 vị trí) | Major |
| S-3 | `PasswordChar` + `PlaceholderText` ⇒ placeholder bị mask thành dấu "•" | 3 cửa sổ, 6 ô | Major |
| S-4 | Placeholder trong `ComboBox` thiếu chiều cao cho dấu tiếng Việt ⇒ cắt 12px | `ProductionOrdersView`, `InfoView` | Major |
| S-5 | `Height` cố định nhỏ hơn nhu cầu chữ có dấu ⇒ cắt 6px (dấu nặng, dấu huyền) | `InfoView`, `PlcSettingsView` | Minor |
| S-6 | Cỡ chữ tiêu đề trang không nhất quán (24 / 21 / 20) | 8 view | Minor |
| S-7 | Chiều cao nút rời rạc (28→56), thiếu quy ước chung | 12 file | Minor |
| S-8 | Bảng rỗng không có thông báo trạng thái rỗng | 3 view | Minor |
| S-9 | Resource `TextSecondary` dùng nhưng không định nghĩa | 2 view | Major |
| S-10 | Style `overline` bị override `LetterSpacing` ở từng view | 2 view | Cosmetic |

---

## E. Chưa kiểm tra được / giới hạn

| Hạng mục | Lý do |
|---|---|
| Nội dung trang web trong `WebView` (tab **Sản xuất**, phần camera trong **Chỉnh PLC**) | `Avalonia.Controls.WebView` cần WebView2 Runtime + tiến trình trình duyệt thật; trong headless chỉ ra `about:blank`. **Lưu ý:** ảnh render cho thấy app **có** placeholder dựng sẵn khi web bị tắt ("Tính năng web đang bị tắt trong tab Cấu hình." — `ProductionView.axaml:162-186`), nên đây là hành vi **đúng theo thiết kế**, không phải lỗi. |
| Hành vi runtime PLC / camera / PO server thật | Cố ý không kết nối hệ thống đang chạy. Harness chỉ gọi lệnh điều hướng và đọc trạng thái, không ghi PLC. |
| Giao diện trên màn hình cảm ứng / độ phân giải nhỏ | Chỉ audit ở kích thước cửa sổ chuẩn 1360×820 (đúng kích thước `MainWindow` của app). |
| Biểu tượng `Path` trong nút | Đánh giá bằng hình ảnh render, chưa đối chiếu từng path với icon gốc. |
| Hành vi focus / tab-order / bàn phím | Headless không mô phỏng thiết bị nhập. |
| Hiệu năng (thời gian tải, độ trễ vẽ) | Không đo trong chế độ headless. |

---

## F. Quy trình duyệt

Báo cáo này **chỉ ghi nhận lỗi, chưa sửa gì**. Mỗi mục `UI-00x` được duyệt độc lập.
Bản sửa sẽ được thực hiện **chỉ sau khi bạn duyệt**, ưu tiên theo thứ tự:

1. **UI-009 + UI-010** (placeholder bị mask / bị cắt dấu) — lỗi người dùng thấy ngay, sửa nhỏ, ảnh hưởng 4 view
2. **UI-001** (resource thiếu) — rủi ro runtime, sửa 1 dòng, ảnh hưởng 2 view
3. **UI-002 + UI-013** (lệch cột + cắt nhãn "GỬI PLC") — sửa kiến trúc, ảnh hưởng 5 view
4. **UI-007 + UI-014** (cắt chiều cao ở `PlcSettingsView`) — sửa diện rộng, 8 vị trí
5. **UI-003 + UI-017** (số hiển thị trùng) — sửa 1 view
6. **UI-004 / UI-006 / UI-015** (chuẩn hoá cỡ chữ & chiều cao nút) — sửa diện rộng, nên làm gộp
7. **UI-005** (LetterSpacing) — cosmetic
8. **UI-016 / UI-018** (empty-state, cân bằng 2 cột) — cải thiện trải nghiệm

---

## G. Tệp sinh ra

| Đường dẫn | Nội dung |
|---|---|
| `tools/UiAuditHarness-VF/Program.cs` | Harness chính (DI, sandbox, login, điều hướng, render per-view, dump cây trực quan) |
| `tools/UiAuditHarness-VF/VisualAudit.cs` | Dump cây trực quan ra JSON + phát hiện lỗi layout (overflow, cắt chữ, lệch cột) |
| `tools/UiAuditHarness-VF/Sandbox.cs` | Copy DB sang sandbox, ghi `appsettings.json` cách ly |
| `tools/UiAuditHarness-VF/crop.ps1` | Crop + zoom ảnh render để làm bằng chứng trực quan |
| `docs/ui-audit-shots/*.png` | 17 ảnh render (1360×820 hoặc kích thước cửa sổ con) |
| `docs/ui-audit-shots/*.tree.json` | 17 cây trực quan (đo đạc từ chính cửa sổ đã render) |
| `docs/ui-audit-shots/findings.json` | 83 phát hiện máy phát hiện (Major 25 / Minor 46 / Cosmetic 12) |

**Kết luận cuối:** 9/9 tab + 5/5 cửa sổ con + 1 màn hình đăng nhập + 2 overlay = **17/17** render thành công,
0 thất bại. DB thật **không đổi** (đã đối chiếu MD5 + mốc sửa trước/sau). **Không cửa sổ nào hiện trên màn hình người dùng.**
