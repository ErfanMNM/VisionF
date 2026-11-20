# Phát hành và phục hồi TanTienQR qua Cloudflare

Hệ thống phân phối các gói Native AOT `win-x64` và `linux-x64` qua Worker của cổng license, D1 và bucket R2 riêng tư. TanTienQR kiểm tra chữ ký license cùng mã máy tại chỗ; Worker đối chiếu đúng chứng thư đã cấp, hạn dùng và trạng thái thu hồi trong D1. License cấp bằng công cụ offline cần được cấp lại qua cổng để tải cập nhật. Đây là xác thực quyền của chứng thư tại server; file `.lic` vẫn có thể sao chép.

Ngày 20/11/2026 đã tạo bucket riêng tư `tantien-updates`, áp dụng migration `0005_license_update_signature_index.sql` và deploy Worker tại [cổng TanTienQR](https://tantien-license-portal.okeynhat.workers.dev), phiên bản Worker `eab2ff0f-f145-408a-bfd3-df00492bf725`. Smoke test xác nhận health/trang portal HTTP 200 và yêu cầu cập nhật bằng license giả HTTP 403 với `no-store`. Chưa công bố gói hoặc con trỏ `pilot`/`stable`; khóa phát hành và bootstrap phải được chuẩn bị theo các bước bên dưới.

## Chuẩn bị hạ tầng và khóa ký

Chạy các lệnh trong tài liệu từ thư mục gốc repository với PowerShell 7.4 trở lên. Máy phát hành cần .NET 10 SDK, C++ Build Tools cho Windows Native AOT, Docker đang dùng Linux containers và AWS CLI v2 có `s3api put-object --if-match/--if-none-match`. Build Linux hiện dùng image .NET SDK Debian; cần thử trên bản Linux đích trước khi đưa lên `stable`.

1. Tạo bucket R2 cho cập nhật trong tài khoản Cloudflare, ví dụ `tantien-updates`. Giữ bucket riêng tư, tắt cả `r2.dev` và public custom domain. Gắn binding `UPDATE_BUCKET` của Worker với bucket đó theo cấu hình `license-portal/wrangler.jsonc`.
2. Tạo R2 S3 API token giới hạn đúng bucket. Cấu hình một AWS profile riêng bằng `aws configure --profile tantien-release`, nhập access key/secret ở prompt của CLI và đặt region `auto`. Sau đó chọn `$env:AWS_PROFILE = 'tantien-release'`. Không đặt secret vào tham số lệnh, Git hoặc tài liệu. [Hướng dẫn AWS CLI của Cloudflare](https://developers.cloudflare.com/r2/examples/aws/aws-cli/)
3. Giữ một khóa RSA dành riêng cho cập nhật, tối thiểu 3072 bit, dạng PEM, **ngoài repository**. Không dùng lại khóa cấp license. Build nhúng khóa công khai SPKI vào app và launcher bằng MSBuild property `UpdatePublicKey`; khóa riêng chỉ dùng trên máy phát hành. Sao lưu khóa ở nơi kiểm soát truy cập. V1 đổi khóa cần cài lại bootstrap trên các máy đang dùng khóa cũ.
4. Đăng nhập Wrangler và kiểm tra `npx wrangler whoami` trong `license-portal` trước khi deploy Worker. Tạo bucket trước khi bật binding. Việc deploy và tạo tài nguyên Cloudflare là thao tác vận hành riêng; chạy test địa phương không công bố bản mới.

Bucket có cấu trúc:

```text
releases/1.2.3/win-x64/package.zip
releases/1.2.3/win-x64/manifest.json
releases/1.2.3/linux-x64/package.zip
releases/1.2.3/linux-x64/manifest.json
channels/pilot.json       # {"version":"1.2.3"}
channels/stable.json      # {"version":"1.2.3"}, hoặc {} khi rút bản
```

Worker dùng R2 binding để đọc và truyền gói; không gửi S3 credential về máy khách. Manifest có envelope `{data,sig}`, cả hai là base64 chuẩn. `data` là UTF-8 JSON chứa `version`, `rid`, `size`, `sha256`, `notes` và danh sách `files` với `path`, `sha256`, `size`, `executable`. Chữ ký RSA SHA-256 PKCS#1 v1.5 áp dụng lên chính byte `data`, không serialize lại để xác minh. [Tài liệu R2 Workers API](https://developers.cloudflare.com/r2/api/workers/workers-api-reference/)

## Build từ một Git tag sạch

Commit toàn bộ thay đổi đã duyệt rồi tạo Git tag `vX.Y.Z` tại HEAD. Thư mục làm việc phải sạch. Mỗi thành phần phiên bản là số 0–65534; không dùng prerelease hoặc hậu tố build trong v1. Cập nhật DB/cấu hình phải tương thích với phiên bản trước để rollback chương trình giữ nguyên dữ liệu vẫn chạy được.

Ví dụ build phiên bản `1.2.3` với ghi chú lưu ngoài hoặc trong repository:

```powershell
pwsh -NoProfile -File deploy/update/build-release.ps1 `
  -Version 1.2.3 `
  -SigningKeyPath 'D:/TanTienReleaseKeys/update-private.pem' `
  -NotesPath 'D:/TanTienReleaseNotes/1.2.3.md'
```

Script xuất snapshot từ đúng Git tag, build app và launcher của hai nền tảng vào thư mục tạm mới, ký và tự kiểm tra manifest. Mặc định kết quả nằm trong `publish/updates/1.2.3`. Nếu thư mục đích đã tồn tại, script dừng; lưu bộ kết quả hoàn chỉnh làm bản phát hành chuẩn, không ghi đè hoặc ký lại cùng một phiên bản.

Mỗi nền tảng có `package.zip`, `manifest.json` và `bootstrap.zip`. Gói cập nhật chỉ chứa chương trình chính và các thư viện native ở thư mục gốc. Bộ bootstrap thêm `TanTienLauncher(.exe)`. Cả hai loại đều loại bỏ cấu hình, license, database và symbols. `release.json` ghi commit cùng SHA-256 của bootstrap; `public-key.spki` chứa khóa công khai. Các thư mục runtime con bất ngờ làm script dừng để người phát hành kiểm tra quy tắc đóng gói.

Giới hạn v1: ZIP cập nhật 256 MiB, tổng tệp giải nén 512 MiB, tối đa 128 tệp, envelope manifest 64 KiB và ghi chú 8 KiB. Khi build lỗi, thư mục tạm được dọn; thư mục đầu ra chưa hoàn chỉnh được giữ để kiểm tra. Không phát hành bộ đầu ra đó.

## Công bố pilot và stable

`Upload` tải hai gói lên R2, tải lại để kiểm tra chữ ký/kích thước/SHA-256 và đối chiếu chính xác với bộ kết quả địa phương trước khi đổi con trỏ `pilot`. Upload có thể chạy lại sau lỗi: các đối tượng trùng byte được giữ, khác byte làm script dừng. Put mới dùng điều kiện `If-None-Match: *` để tránh ghi đè cùng phiên bản. [Điều kiện ghi S3 của R2](https://developers.cloudflare.com/r2/api/s3/api/)

```powershell
$env:AWS_PROFILE = 'tantien-release'
$releaseArgs = @{
  Version = '1.2.3'
  AccountId = '<32 ký tự hex của Cloudflare account>'
  Bucket = 'tantien-updates'
  ReleaseDirectory = 'publish/updates/1.2.3'
  PublicKeyPath = 'publish/updates/1.2.3/public-key.spki'
}
& ./deploy/update/r2-release.ps1 -Action Upload @releaseArgs
```

Chọn ít nhất một máy Windows và một máy Linux thí điểm. Đặt biến môi trường `TANTIEN_Updates__Channel=pilot` cho tiến trình của máy đó rồi mở lại qua launcher. Mặc định là `stable`. URL cập nhật dùng cấu hình `Updates:BaseUrl`, nếu thiếu sẽ dùng `LicensePortal:BaseUrl` hoặc URL cổng mặc định theo ứng dụng. Có thể đổi URL bằng `TANTIEN_Updates__BaseUrl=https://...`.

Sau khi ghi nhận kết quả thử đạt trên cả hai nền tảng:

```powershell
& ./deploy/update/r2-release.ps1 -Action Promote @releaseArgs -PilotVerified
```

`Promote` yêu cầu pilot đang trỏ đúng phiên bản và kiểm tra lại cả hai gói trên R2 khớp bộ địa phương. Script chỉ đổi con trỏ `stable`; không build hoặc ký lại. `-PilotVerified` là xác nhận của kỹ thuật viên, không thay thế thử nghiệm. Con trỏ được ghi với ETag hiện tại; nếu người khác vừa sửa kênh, lệnh dừng thay vì ghi đè thay đổi đó.

## Cài bootstrap một lần và thử rollback

Máy cũ chưa có updater phải được kỹ thuật viên đưa lên bản bootstrap một lần khi dây chuyền đã dừng. Sao lưu thư mục ứng dụng và dữ liệu trước bước chuyển đổi ban đầu. Giải nén `bootstrap.zip` đúng nền tảng vào thư mục tạm; đối chiếu SHA-256 với `release.json` lấy qua kênh nội bộ tin cậy. Thoát TanTienQR, sao chép các tệp chương trình vào đúng thư mục hiện tại, giữ nguyên `appsettings.json`, `license.lic`, `logs/` và các đường dẫn DB riêng. Đổi shortcut/menu/autostart để mở `TanTienLauncher.exe` hoặc `TanTienLauncher`.

Trên Linux, đặt quyền thực thi cho cả `TanTienLauncher` và `TanTienQR` nếu công cụ giải nén chưa giữ mode ZIP. Tài khoản chạy ứng dụng cần quyền ghi thư mục cài đặt; dùng ổ đĩa cục bộ. V1 hỗ trợ desktop, chưa tích hợp supervisor/systemd/kiosk tự khởi động lại tiến trình. Khi dùng launcher phải tránh để một cơ chế khác đồng thời mở `TanTienQR` trực tiếp.

Trong **Thông tin → Cập nhật**, quản trị viên chọn kiểm tra và cài khi dây chuyền rảnh. Gói chỉ được áp dụng sau khi xác minh xong, đã khóa bắt đầu lệnh/lượt quét mới, chờ các việc đang chạy và ghi DB xong, rồi dừng app. App vẫn hoạt động với bản hiện tại khi không truy cập được dịch vụ cập nhật.

Launcher sao lưu binary và ghi journal trước khi thay tệp. Nếu bản mới không mở được giao diện và dữ liệu cục bộ trong 60 giây, launcher khôi phục binary trước đó. Nếu giao dịch bị ngắt hoặc mất điện khi thay tệp, lần mở launcher tiếp theo phục hồi giao dịch dang dở. Backup/journal nằm trong `.tantien-updates`; không xóa thư mục này khi giao dịch chưa kết thúc. Rollback không phục hồi dữ liệu cũ hoặc khôi phục ngược migration không tương thích.

Trước `stable`, ghi nhận các kết quả sau trên Windows và Linux:

- Tải/cài thành công, mở đúng phiên bản mới; cấu hình, license, người dùng, lịch sử quét và PO giữ nguyên.
- License hết hạn, thu hồi hoặc chưa có trong D1 bị từ chối; mất mạng không ảnh hưởng bản đang chạy.
- Sai chữ ký/SHA-256/nền tảng bị chặn trước cài; không được đóng app để áp dụng gói chưa xác minh.
- Đang chạy PO hoặc xuất hiện lệnh mới đồng thời thì updater từ chối; quét đang xử lý được ghi xong trước bàn giao.
- Bản mới cố ý không báo khởi động thành công trên **máy lab** được tự phục hồi sau 60 giây.
- Ngắt launcher hoặc tắt VM giữa các bước thay binary trên **máy lab**, rồi mở launcher để kiểm tra phục hồi. Kiểm tra lỗi hết dung lượng, không có quyền ghi và tệp bị khóa cũng giữ hoặc trả về bản trước.

Không mô phỏng mất điện hoặc phá gói trên máy đang vận hành sản xuất. Máy lab không có PLC cần cấu hình riêng `TANTIEN_Plc__Enabled=false`; không dùng cấu hình đó cho dây chuyền thật.

## Rút bản lỗi và theo dõi

```powershell
pwsh -NoProfile -File deploy/update/r2-release.ps1 `
  -Action Withdraw -Version 1.2.3 `
  -AccountId '<32 ký tự hex của Cloudflare account>' `
  -Bucket tantien-updates -Channel stable
```

Lệnh chỉ rút nếu con trỏ vẫn là phiên bản đã chỉ định. Nó ghi con trỏ `{}` có điều kiện ETag, không xóa gói hay backup. Worker xem con trỏ rỗng là chưa có bản được công bố. Máy chưa tải xong sẽ không được cấp gói từ kênh đó; tải lại yêu cầu kiểm tra license và phiên bản được phép. Máy đã cài thành công cần nhận bản sửa có số cao hơn. Việc rút kênh không phải lệnh ép hạ phiên bản từ xa.

Theo dõi log cập nhật tại máy và Workers Observability để phát hiện lỗi quyền tải, gói thiếu và lỗi R2/D1. Không ghi request body, file `.lic`, chữ ký license hoặc credential vào log. Chỉ đưa một bản lên stable sau khi xác nhận pilot và kết quả rollback; thay đổi mã nguồn trong repository chưa đồng nghĩa với đã deploy Worker hoặc phát hành cho khách.

Kiểm tra tooling địa phương bằng `pwsh -NoProfile -File deploy/update/test-release.ps1`. Bài kiểm tra tạo khóa tạm, dữ liệu giả và fake S3 tại máy; không truy cập Cloudflare, không dùng khóa thật. Bài này xác nhận đóng gói/ký và thứ tự công bố, không thay thế bài cài/rollback thực trên hai hệ điều hành.

Các kiểm tra mã nguồn liên quan:

```powershell
dotnet run --project tools/UpdateTests -c Release
dotnet run --project tools/UpdateSafetyTests -c Release
dotnet run --project tools/UpdateSendTests -c Release
dotnet run --project tools/PoFlowTests -c Release
dotnet run --project tools/PoServerTests -c Release
dotnet run --project tools/ConfigUiTests -c Release
```

Trong `license-portal`, chạy `npm run check` và `npm test` để kiểm tra API với Worker, D1 và R2 cục bộ.
