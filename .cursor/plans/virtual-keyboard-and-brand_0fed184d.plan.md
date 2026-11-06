---
name: virtual-keyboard-and-brand
overview: Bổ sung bàn phím ảo (chữ tiếng Việt / số) hiện lên khi chạm vào TextBox trên máy cảm ứng, đồng thời áp logo công ty (Tân Tiễn) vào app và đổi bảng màu accent từ teal sang xanh dương theo logo.
todos:
  - id: logo-assets
    content: "Chuẩn bị asset logo: resize logo đính kèm -> tantien-logo.png (đầy đủ) + tantien-logo-mark.png (chỉ TT+gears) + sinh tantien-logo.ico đa kích thước"
    status: pending
  - id: recolor
    content: Đổi bảng màu accent sang xanh dương trong App.axaml (Accent, AccentSoft, LineAccent, primary hover/pressed)
    status: pending
  - id: logo-ui
    content: Thay chữ 'TT' bằng Image logo thật trong MainWindow.axaml + LoginShellWindow.axaml
    status: pending
  - id: kb-ui
    content: Tạo Controls/VirtualKeyboard.axaml — layout Alpha (QWERTY + hàng phụ dấu tiếng Việt) và Numeric
    status: pending
  - id: kb-vm
    content: Tạo VirtualKeyboardViewModel — chuyển layout, insert/delete tại CaretIndex, hỗ trợ PasswordChar
    status: pending
  - id: kb-service
    content: Tạo Services/KeyboardService — theo dõi GotFocus/LostFocus, chọn layout theo TextInputOptions.ContentType
    status: pending
  - id: kb-integration
    content: Gắn VirtualKeyboard vào MainWindow + LoginShellWindow (overlay đáy) và các Window con qua Popup
    status: pending
  - id: kb-di
    content: Đăng ký KeyboardService + VirtualKeyboardViewModel vào DI (TanTienAppServices)
    status: pending
  - id: verify
    content: Build Debug + publish Native AOT Release, chạy app và chụp màn hình xác minh bàn phím + logo + màu
    status: pending
isProject: false
---

## Bàn phím ảo cho máy cảm ứng + Rebrand logo Tân Tiễn

### Quyết định đã chốt
- Accent mới: **xanh dương** (theo chữ "HIGH TECHNOLOGY" trong logo) thay cho teal `#00A88E`.
- Bàn phím ảo: ô nhập **chữ** → bàn phím QWERTY + hàng phụ phím tổ hợp tiếng Việt (ă/â/ê/ô/đ + dấu thanh/huyền/hỏi/ngã); ô nhập **số** → bàn phím số thuần.
- Logo: dùng **logo đầy đủ** ở màn hình đăng nhập + header app; icon `.exe` cũng dùng logo đầy đủ.

---

### 1. Asset logo & bảng màu mới

**Logo** — file nguồn đã tìm thấy:
`C:\Users\TH\.cursor\projects\d-DuAn-VisionF\assets\..._images_TAN_TIEN_OFFICIAL_LOGO-...jpg`

- Copy và resize về `TanTienQR/Assets/tantien-logo.png` (nền trong suốt, giữ nguyên tỉ lệ, ≥512px) — hiện tại file `.png` cũ là logo placeholder "TT".
- Sinh `TanTienQR/Assets/tantien-logo.ico` (16/32/48/64/128/256) từ logo mới cho icon `.exe`.
- Logo có nền trắng → dùng trong header/panel trắng là OK, nhưng phần "HIGH TECHNOLOGY" chữ nhỏ sẽ khó đọc ở kích thước nhỏ → xem bước 2.

**Bảng màu** — sửa trong [App.axaml](d:\DuAn\VisionF\TanTienQR\App.axaml) (`Application.Resources`):
- `Accent`: `#00A88E` → `#1A56DB` (xanh dương theo logo)
- `AccentSoft`: `#D6F5EF` → `#DCE7FD`
- `LineAccent`: `#8FDDCB` → `#A9C4F5`
- `Button.primary:pointerover` `#00BF9F` → `#2563EB`; `:pressed` `#00937D` → `#1747B0`
- Giữ nguyên `Ok`/`Warn`/`Danger`/`Idle` (semantic, không đổi theo brand).

### 2. Thay chữ "TT" bằng logo thật

- [MainWindow.axaml](d:\DuAn\VisionF\TanTienQR\Views\MainWindow.axaml) dòng ~46: `Border` 36x36 chứa `TextBlock Text="TT"` → `Image Source="/Assets/tantien-logo.png"` (giữ `CornerRadius=11`, `Stretch=Uniform`).
- [LoginShellWindow.axaml](d:\DuAn\VisionF\TanTienQR\Views\LoginShellWindow.axaml) dòng ~186: `Border` 64x64 "TT" → `Image` logo đầy đủ, `Stretch=Uniform`.
- Logo đầy đủ có chữ "HIGH TECHNOLOGY" nhỏ → đặt ở cột branding login (rộng 480px) hiển thị rõ; ở header 36px chỉ hiện phần "TT" → dùng **2 asset**: `tantien-logo.png` (đầy đủ) + `tantien-logo-mark.png` (chỉ phần TT + gears, bo tròn) cho header/icon.

### 3. Bàn phím ảo — kiến trúc

Tạo thư mục `TanTienQR/Controls/` với:

**a. `VirtualKeyboard.axaml` (UserControl)** — bàn phím ảo, 2 layout:
- `Alpha`: 4 hàng QWERTY + hàng phụ dấu tiếng Việt (ă ê ô đ à á ả ã ạ ằ ắ ẳ ẵ ặ ầ ấ ẩ ẫ ậ ẹ ễ ệ ỉ ị ọ ỏ ố ổ ỗ ộ ờ ớ ở ỡ ợ ư ữ ự ừ ứ ử ữ ự + dấu phụ).
- `Numeric`: 3x4 (1-9, 0, ., ⌫) + hàng phụ (←, →, Enter, ␣).
- Phím Backspace (xoá 1 ký tự), Space, Enter, Shift (uppercase), Tab, Esc (đóng bàn phím).
- `IsVisible` bind theo thuộc tính của VM.

**b. `VirtualKeyboardViewModel`** — quản lý trạng thái:
- `IsVisible`, `Layout` (Alpha/Numeric), `IsShiftOn`, `Target` (TextBox đang focus).
- Phương thức nhận input từ bàn phím → ghi vào `Target`:
  - Chèn tại `CaretIndex` (`TextBox.CaretIndex` — API có sẵn, đã kiểm tra trong Avalonia 12.1.4).
  - Xoá đúng 1 ký tự trước caret (`TextBox.SelectedText` / `CaretIndex`).
  - Hỗ trợ `PasswordChar` (ô mật khẩu) — không hiện gợi ý chữ.

**c. `KeyboardService`** (đăng ký singleton trong [TanTienAppServices.cs](d:\DuAn\VisionF\TanTienQR\Services\TanTienAppServices.cs)):
- Hook `TextBox.GotFocus` / `LostFocus` ở cấp `TopLevel` (dùng `AddHandler(InputElement.GotFocusEvent, ..., RoutingStrategies.Bubble)` trên `Window`).
- Khi focus vào `TextBox`:
  - Xác định layout: **Numeric** nếu `TextBox` có `TextInputOptions.ContentType = Digits/Number/Pin` **hoặc** `MaxLength` nhỏ và toàn số → đơn giản & chính xác hơn: dùng `TextInputOptions.ContentType` (API có sẵn: `Alpha`, `Digits`, `Number`, `Password`, `Email`, `Url`, `Normal`).
  - Mặc định (không khai báo ContentType) → **Alpha**.
- Hiển thị bàn phím ở đáy cửa sổ (overlay), ẩn khi mất focus.

**d. Gắn bàn phím vào các cửa sổ:**
- [MainWindow.axaml](d:\DuAn\VisionF\TanTienQR\Views\MainWindow.axaml): thêm `Grid.Row` cuối chứa `<controls:VirtualKeyboard />` — nằm trên đè (Grid overlay, `VerticalAlignment=Bottom`, `IsHitTestVisible` chỉ khi visible).
- [LoginShellWindow.axaml](d:\DuAn\VisionF\TanTienQR\Views\LoginShellWindow.axaml): tương tự — cột phải (form đăng nhập).
- Các `Window` con (`PasswordChangeWindow`, `UserEditWindow`, `PlcParamEditWindow`, `LicenseWindow`): bàn phím ảo dạng **Popup/Window overlay** để không phải sửa layout — dùng chung `VirtualKeyboard` control trong `Popup` gắn `Placement=Bottom`, `PlacementTarget` = TextBox đang focus.

### 4. Điều chỉnh cho máy cảm ứng (bổ sung)
- Kích thước phím bàn phím ≥ 44px (theo khuyến nghị touch target).
- `Button.chip` / `Button.primary` hiện `MinHeight=44`/`38` → nâng về ≥44 cho các nút chính trong form.
- TextBox trong form nhập: `Height=42-44` → giữ, đã đủ.

### 5. Kiểm tra
- Build Debug (0 lỗi) + `dotnet publish -c Release -r win-x64 -p:PublishAot=true` (Native AOT — bản phát hành thật).
- Chạy app: bấm vào ô "Tên đăng nhập" → hiện bàn phím chữ (có dấu tiếng Việt); bấm ô "Port" → bàn phím số; gõ tiếng Việt có dấu vào ô tên hiển thị đúng.
- Ảnh chụp màn hình xác nhận.

### Các file chính sẽ sửa
| File | Nội dung |
|---|---|
| [App.axaml](d:\DuAn\VisionF\TanTienQR\App.axaml) | Bảng màu accent xanh dương + style phím bàn phím |
| [MainWindow.axaml](d:\DuAn\VisionF\TanTienQR\Views\MainWindow.axaml) | Logo thật + overlay bàn phím |
| [LoginShellWindow.axaml](d:\DuAn\VisionF\TanTienQR\Views\LoginShellWindow.axaml) | Logo đầy đủ + overlay bàn phím |
| [TanTienAppServices.cs](d:\DuAn\VisionF\TanTienQR\Services\TanTienAppServices.cs) | Đăng ký `KeyboardService`, `VirtualKeyboardViewModel` |
| `TanTienQR/Controls/VirtualKeyboard.axaml(.cs)` | **Mới** — UserControl bàn phím ảo |
| `TanTienQR/Controls/VirtualKeyboardViewModel.cs` | **Mới** — logic chuyển layout + nhập text |
| `TanTienQR/Services/KeyboardService.cs` | **Mới** — theo dõi focus, quyết định layout |
| `TanTienQR/Assets/tantien-logo.png` / `.ico` / `-mark.png` | **Mới** — logo từ file đính kèm |

### Rủi ro cần lưu ý
- **Native AOT**: không dùng reflection. Bàn phím viết bằng code thuần + XAML → an toàn.
- **`Icon="/Assets/tantien-logo.png"`**: Avalonia yêu cầu PNG cho `Window.Icon`; file `.ico` chỉ dùng cho icon `.exe` (thiết lập qua csproj `ApplicationIcon`).
- **Bàn phím chồng lên nội dung**: cần `ScrollViewer` cuộn được vùng nội dung phía trên khi bàn phím hiện (đã có `ScrollViewer` ở `ConfigView`/`DevView`; các tab khác cần kiểm tra).
- File logo `.jpg` gốc có nền trắng → cần xử lý nền trong suốt cho icon `.exe`/header.