# Phát hành và phục hồi TanTienQR qua GitHub Releases

TanTienQR kiểm tra license và mã máy tại chỗ; portal kiểm tra chứng thư đã cấp, hạn dùng và trạng thái thu hồi trong D1. Khi người dùng kiểm tra cập nhật, app vẫn gọi API HTTPS của portal. Worker kiểm tra license trước, sau đó đọc manifest và gói cài từ GitHub Releases của `ErfanMNM/VisionF`.

GitHub là nơi lưu artifact phát hành; Cloudflare Worker và D1 vẫn cung cấp API cập nhật có kiểm soát license. Không cần bucket R2 hoặc AWS CLI cho artifact. Repo hiện công khai, vì vậy asset trong Releases cũng tải công khai trực tiếp được. Worker chỉ kiểm soát luồng tải trong app; chữ ký RSA đã nhúng trong app/launcher mới là điều kiện để cài gói. Không dùng GitHub tag hoặc release notes làm bằng chứng tin cậy thay cho chữ ký manifest.

Worker cache metadata GitHub mặc định 300 giây qua `UPDATE_METADATA_CACHE_TTL_SECONDS`; kiểm tra license D1 vẫn chạy ở mỗi request, còn phản hồi cho app vẫn `no-store`. Việc rút bản có thể mất tối đa 5 phút để có hiệu lực ở mọi cache. Tăng TTL nếu lượng kiểm tra cao; GitHub giới hạn request API ẩn danh, xem [quota REST API](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api).

Các endpoint app đang dùng giữ nguyên `/api/updates/v1/check` và `/api/updates/v1/download`. App gửi license, RID, channel và phiên bản tải xuống cho Worker; không gửi license sang GitHub. Cấu hình mặc định vẫn lấy URL từ `LicensePortal:BaseUrl`, channel `stable`. Worker chỉ truy cập repo cố định `ErfanMNM/VisionF`; request app không thể chọn URL hoặc repository.

## Kênh và tên asset

Tag phát hành phải là `vX.Y.Z`, với mỗi phần số từ 0 đến 65534. Worker chọn phiên bản số cao nhất trong tối đa 100 release đã công bố cho kênh tương ứng:

- `stable`: release không phải prerelease.
- `pilot`: release prerelease.
- Release nháp không được chọn. Khi rút bản, chuyển release về nháp để Worker bỏ qua; các asset được giữ lại.

Mỗi release có các asset cố định cho hai nền tảng:

```text
manifest-win-x64.json
package-win-x64.zip
bootstrap-win-x64.zip
manifest-linux-x64.json
package-linux-x64.zip
bootstrap-linux-x64.zip
release.json
public-key.spki
```

Ba asset nền tảng được đổi tên từ `manifest.json`, `package.zip` và `bootstrap.zip` trong output build. Manifest giữ nguyên envelope đã ký; app kiểm tra chữ ký, RID, kích thước, SHA-256 và danh sách file trước khi cài. Worker trả nguyên manifest, rồi stream gói đúng phiên bản/RID. Nếu release đổi sau bước kiểm tra, download cũ bị từ chối để app kiểm tra lại.

## Chuẩn bị khóa và build

Chạy các lệnh từ repo với PowerShell 7.4 trở lên. Máy build chuẩn cần .NET 10 SDK, C++ Build Tools cho Windows Native AOT và Docker dùng Linux containers để build cả Windows x64 và Linux x64. GitHub CLI `gh` phải cài và đăng nhập bằng tài khoản có quyền tạo Release trong repo. Vì repo công khai, Worker không cần GitHub token; không đặt token trong app hoặc cấu hình Worker.

Giữ khóa RSA phát hành riêng, tối thiểu 3072 bit, dạng PEM, ngoài repo. Không dùng khóa cấp license. Khóa riêng ký manifest; khóa công khai `public-key.spki` được nhúng vào app và launcher lúc build. Sao lưu khóa riêng an toàn; đổi khóa yêu cầu cài bootstrap mới trên máy khách hiện tại.

Tạo tag tại source đã duyệt và build release sạch. Mỗi phiên bản phải tăng, không dùng lại tag hoặc ghi đè artifact:

```powershell
git tag -a v1.2.3 -m "TanTienQR 1.2.3"
pwsh -NoProfile -File deploy/update/build-release.ps1 `
  -Version 1.2.3 `
  -SigningKeyPath 'D:/TanTienReleaseKeys/update-private.pem' `
  -NotesPath 'D:/TanTienReleaseNotes/1.2.3.md'
```

Build script lấy snapshot từ đúng tag, kiểm tra tag trùng HEAD và working tree sạch, build hai RID, ký manifest và tự kiểm tra chữ ký/SHA. Output chuẩn là `publish/updates/1.2.3`, gồm `win-x64` và `linux-x64`, khóa công khai và metadata release. Nếu thư mục output đã có, script dừng; không ghi đè gói cũ.

Mỗi gói update chỉ chứa app và thư viện native. Bootstrap lần đầu chứa launcher cùng helper máy in Windows. Cả hai không chứa license, cấu hình máy, database hoặc symbols. Giữ `release.json` và `public-key.spki` cạnh hai thư mục platform.

## Upload pilot, promote stable và rút bản

Sau khi kiểm thử source, đẩy commit/tag đã duyệt lên `origin`. Script yêu cầu tag remote trỏ cùng commit HEAD, working tree sạch, repo đúng và public. Lệnh Upload xác minh lại chữ ký manifest và hash package, tạo release nháp, tải asset còn thiếu, so sánh SHA-256/kích thước với GitHub rồi mới công bố release dưới dạng prerelease cho kênh pilot:

```powershell
pwsh -NoProfile -File deploy/update/github-release.ps1 `
  -Action Upload -Version 1.2.3 `
  -ReleaseDirectory 'publish/updates/1.2.3'
```

`PublicKeyPath` mặc định là `public-key.spki` trong thư mục release. Có thể truyền `-NotesPath` nếu cần notes riêng. Script không ghi đè asset đã có: asset trùng tên nhưng sai hash làm thao tác dừng. Release nháp bị gián đoạn có thể chạy lại để upload phần còn thiếu; release đã công bố không được sửa asset, hãy build phiên bản mới.

Thử cập nhật trên máy pilot Windows và Linux khi dây chuyền rảnh. Sau khi xác nhận cài thành công, dữ liệu/config/license giữ nguyên và rollback hoạt động, promote chính release đó sang stable:

```powershell
pwsh -NoProfile -File deploy/update/github-release.ps1 `
  -Action Promote -Version 1.2.3 `
  -ReleaseDirectory 'publish/updates/1.2.3'
```

Promote kiểm tra lại bộ asset đã ký và yêu cầu phiên bản mới hơn stable hiện tại, rồi chuyển prerelease thành release ổn định. Để rút bản lỗi khỏi cả hai kênh:

```powershell
pwsh -NoProfile -File deploy/update/github-release.ps1 -Action Withdraw -Version 1.2.3
```

Withdraw chuyển release thành draft, giữ tag và asset để kiểm tra hoặc khôi phục. Release bị rút sẽ không được chọn cho các lần kiểm tra mới; thiết bị đã cài bản đó cần một bản sửa với số phiên bản cao hơn.

## Cài bootstrap lần đầu và rollback

Máy chưa có launcher phải được cài bootstrap khi dây chuyền dừng. Sao lưu thư mục app và dữ liệu trước. Tải `bootstrap-win-x64.zip` hoặc `bootstrap-linux-x64.zip` từ đúng GitHub Release; đối chiếu SHA-256 với `release.json` lấy qua kênh tin cậy. Giải nén vào thư mục cài đặt có quyền ghi, giữ `appsettings.json`, `license.lic`, log và database của máy, rồi đổi shortcut/autostart để mở `TanTienLauncher.exe` hoặc `TanTienLauncher`.

Trên Linux cần giữ quyền thực thi của launcher và app. Tài khoản chạy phải có quyền ghi thư mục cài đặt; dùng ổ đĩa cục bộ. V1 hỗ trợ desktop, chưa tích hợp supervisor/systemd/kiosk tự khởi động lại.

Trong **Thông tin → Cập nhật**, quản trị viên kiểm tra và cài lúc dây chuyền rảnh. App khóa intake, chờ lượt đang xử lý và persistence hoàn tất, sau đó mới thay binary. Launcher sao lưu file và ghi journal trước khi thay; nếu bản mới không xác nhận khởi động thành công trong 60 giây, launcher phục hồi binary cũ. Nếu giao dịch bị ngắt, lần chạy launcher kế tiếp khôi phục giao dịch dang dở. Thư mục `.tantien-updates` chứa journal/backup; không xóa khi giao dịch còn hoạt động.

Trước khi promote stable, thử trên máy lab: mất mạng, license hết hạn/thu hồi, manifest hoặc ZIP sai chữ ký/hash, sai RID, asset thiếu, update bị rút giữa hai request, app khởi động lỗi, tệp khóa hoặc không đủ quyền ghi. Không mô phỏng mất điện hoặc phá gói trên máy sản xuất. Rollback binary không khôi phục dữ liệu cũ và không đảo migration không tương thích.

## Kiểm tra local

Các kiểm tra Worker và release không gọi GitHub thật khi chạy bộ test:

```powershell
cd license-portal
npm run check
npm test
cd ..
pwsh -NoProfile -File deploy/update/test-release.ps1
dotnet run --project tools/UpdateTests -c Release
dotnet run --project tools/UpdateSafetyTests -c Release
dotnet run --project tools/UpdateSendTests -c Release
```

Test local không thay thế cài thử trên máy Windows/Linux, kiểm tra license thật qua Worker đã deploy hoặc thử rollback trên máy lab. Chỉ công bố release khi manifest và asset của cả hai nền tảng đã được xác minh.
