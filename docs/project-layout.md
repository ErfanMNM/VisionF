# Cấu trúc dự án VisionF

## Thư mục nguồn

    VisionF.slnx                  # Solution tổng hợp của repository
    src/
      ClassLibraryMT/             # Mã dùng chung và các module nghiệp vụ đời đầu
      ClassLibraryMT.Auth/         # Tài khoản và phiên đăng nhập
      ClassLibraryMT.Checkee/      # Client và hợp đồng Checkee
      ClassLibraryMT.Logging/      # Audit log và logging
      ClassLibraryMT.MesSimulator/ # Client mô phỏng MES
      ClassLibraryMT.MultiConnect/ # Gateway đa kết nối
      ClassLibraryMT.Plc/          # Client PLC
      ClassLibraryMT.Po/           # Hợp đồng và nghiệp vụ production order
      ClassLibraryMT.PoServer/     # Receive API và nội dung PO demo nhúng
      ClassLibraryMT.Print/        # Trạng thái máy in và dữ liệu in
      ClassLibraryMT.Scans/        # Lịch sử quét và outbox
      ClassLibraryMT.Updates/      # Manifest ký số, staging, rollback
    TanTienQR/                     # Ứng dụng desktop Avalonia/Native AOT
    tools/                         # Launcher, test runners, CLI, harness và test host
    license-portal/                # Worker, D1, R2 API và portal Cloudflare
    deploy/
      linux/                       # Package Linux
      update/                      # Build, ký, kiểm chứng và phát hành package
    docs/                          # Contract, vận hành và lịch sử QA

ClassLibraryMT.Checkee được ứng dụng tham chiếu trực tiếp nhưng không được liệt kê riêng trong solution hiện tại. Nó vẫn được giữ trong src/, và được giải qua ProjectReference của TanTienQR.

## Build graph

Các lệnh từ root repository:

    dotnet sln VisionF.slnx list
    dotnet build TanTienQR/TanTienQR.csproj -c Release -p:PublishAot=false
    dotnet run --project tools/UpdateTests/UpdateTests.csproj -c Release

Project references giữa các thư viện trong src/ là tương đối với thư mục source mới. Tài nguyên PO demo nhúng của src/ClassLibraryMT.PoServer trỏ lên root docs/po-demo/. App desktop và các utility/test liên kết code từ TanTienQR/ vẫn giữ ở path gốc để giới hạn thay đổi.

## Thư mục cục bộ và dữ liệu gốc

- artifacts/, obj/, .vs/, .playwright-cli/, .tmp-* là output/cache hoặc dữ liệu phiên; giữ nguyên và không đưa vào sản phẩm.
- CodeContent-PO-1000.csv và Danh-sach-ma-khoi-tao (2).csv là file Git đang theo dõi ở root. Không di chuyển cho tới khi xác nhận contract dữ liệu.
- license.lic, appsettings.Development.json, DB/log và .dev.vars không thuộc gói. Không commit hoặc phát hành giá trị secrets.
- Print/ và Bruno/ là cây dữ liệu/source cũ bên ngoài solution. Giữ nguyên đến khi xác nhận owner và consumer trước khi chọn vị trí mới.
- Hồ sơ session nằm trong .session-org/; chúng là metadata vận hành, không nằm trong release artifact.

Khi thêm thư viện nghiệp vụ, đặt trong src/ và khai báo đúng path trong solution, mọi consumer/test project cùng docs. CLI và test runner độc lập thuộc tools/; Worker Cloudflare được giữ riêng trong license-portal/.
