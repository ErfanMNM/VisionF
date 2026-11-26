# Nhật ký khảo sát PO-00

- Vai trò: nhân viên khảo sát chỉ đọc; agent: `/root/po_code_survey`; journal-key do parent cấp: `po-code-survey`.
- Dự án: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`; branch `master`; HEAD lúc khảo sát `b66dce6`.
- Phạm vi được giao: ánh xạ hiện trạng tạo/nhận PO, nhập mã/GTIN, download, API, HTML và test; không sửa source hay hồ sơ cấp dự án. Chỉ ghi nhật ký riêng này.
- Budget: không có thread UUID thật đã xác minh cho agent, nên chưa thể chạy bộ đếm session; không giả định số request. Executor: các lệnh đọc đã kết thúc, không còn tiến trình kiểm thử/chỉnh sửa do tôi khởi chạy.

## Kết quả khảo sát

### Mô hình PO và receive

- `ClassLibraryMT.Po/ProductionOrder.cs`: `ProductionOrder` có `OrderNo`, `OrderQty`, `CreatedUtc`, `Additional`; chưa có thuộc tính GTIN.
- `ClassLibraryMT.Po/PoContracts.cs`: `PoCode.CodeContent` là identity toàn chuỗi. `Gtin`/`Serial` là property tương thích cũ, bị `[JsonIgnore]` và obsolete. `PoCreateRequest` dùng `OrderNo`/`OrderQty`.
- `ClassLibraryMT.PoServer/PoServerContracts.cs`: `PoPushRequest` wire-format dùng `poName` và `orderQty`, cộng `batchCode`, `productName`, `customer`. Response `PoPushResponse` chỉ trả `success`, `message`, `poName`, `accepted`, `ignoredDuplicates`.
- `TanTienQR/Services/TanTienAppServices.cs` đăng ký PO server; `TanTienQR/Services/PoServerWorker.cs` khởi động/dừng worker; `ClassLibraryMT.PoServer/PoHttpServer.cs` nhận `GET /api/health` và `POST /api/po`.
- `PoHttpServer.HandlePushAsync` gọi `PoUploadHandler.ReadContentAsync`; handler stream file vào vùng tạm rồi `PoCodeFileParser` đọc CSV/JSON/XLSX một cột `CodeContent`. `CanonicalMetadataField` trong `PoUploadHandler.cs` không nhận `gtin`/`serial` hay inline `codes`/`csv`.
- `PoHttpServer.SavePoAsync`: trim/check tên PO; hiện bắt `orderQty` đúng bằng số dòng file trước lọc trùng; mã rỗng làm hỏng toàn request. Chỉ lọc trùng `CodeContent` nguyên vẹn bằng `StringComparer.Ordinal`, cả trong file lẫn trong chính PO. Response có số lượng đã nhận và số trùng, không trả nội dung mã bị bỏ. Lưu DB theo transaction; PO đang chạy/kết thúc chặn import.
- Chưa có kiểm định GS1/AI hay kiểm tra một GTIN trên một PO trong receive. `Additional` linh hoạt nhưng route hiện tại chủ động loại metadata `gtin`/`serial`.

### Cấu trúc barcode và GTIN

- `TanTienQR/Services/QrParser.cs`: `HasGs1Structure` là helper `internal`, chỉ trả boolean; regex nhận Digital Link AI 01/21, dạng `(01)...(21)...`, hoặc element string có symbology prefix/GS. GTIN Digital Link hiện nhận 8/12–14 chữ số; hai dạng còn lại yêu cầu 14 số. Serial 1–20 ký tự theo tập đã cài; không kiểm checksum. Hàm không expose GTIN đã parse.
- `TanTienQR/Services/CameraSignalWorker.cs` gọi kiểm tra cấu trúc ở luồng scan. `CodeContent` vẫn giữ nguyên chuỗi đầy đủ. `QrParser` nằm ở app project nên `ClassLibraryMT.PoServer` chưa thể dùng trực tiếp; muốn thống nhất luật upload/download cần đưa parser/extractor vào assembly dùng chung hoặc tạo contract riêng có test.
- Ngữ nghĩa mới cần chốt: GTIN là metadata do client gửi hay trích từ mỗi CodeContent; chính sách với GTIN hợp lệ nhưng khác GTIN PO; `orderQty` là số dòng gốc, số dòng sau lọc, hay số mã nhận cuối; client cần trả danh sách mã lỗi/trùng hay chỉ số lượng/thông điệp.

### Download xuống ứng dụng

- `ClassLibraryMT.Po/IPoClient.cs` + `PoClient.cs`: upstream client lấy PO qua `GET api/po[?includeCompleted=true]`, mã qua `GET api/po/{orderNo}/codes`, kích hoạt qua `POST api/activate`; `CreateOrderAsync` là đường JSON POST tùy chọn, không phải upload-file receive.
- `ClassLibraryMT.Po/ProductionOrderService.cs`, `SyncFromServerAsync`: đọc cả PO đã hoàn tất, bỏ qua PO local đã kết thúc, lấy code, bỏ chuỗi rỗng và duplicate theo exact full `CodeContent`, rồi merge vào store. Chưa bỏ mã sai GS1/GTIN. `PoSyncResult` chỉ có count; `TanTienQR/ViewModels/ProductionOrdersViewModel.cs` (`DownloadOrdersAsync`) chỉ báo số PO/mã mới, không có danh sách loại bỏ.
- `docs/MES-PO-API.md`, `docs/production-order.md`, `docs/contract.md` mô tả hợp đồng hiện tại: raw `CodeContent`, exact identity, chỉ bỏ duplicate trong receive/sync, file schema sai thì từ chối nguyên request. Tài liệu cần đồng bộ nếu đổi sang lọc dòng sai và trả báo cáo.

### HTML PO hiện hữu

- `tools/PoServer/www/index.html`: form `poName`, `orderQty`, upload file; fetch tương đối `POST /api/po`, đọc danh sách bằng `GET /api/po?includeCompleted=true`; hiển thị message server. Không có GTIN, preview/phân tích mã hay API key/base URL.
- Trang được copy output theo `tools/PoServer/PoServer.csproj`. `tools/PoServer/PoConsoleServer.cs` là máy chủ kiểm thử riêng: phục vụ trang ở root, có GET list/codes và POST upload vào store riêng. Không phải `TanTienQR` runtime.
- `PoHttpServer` thật không phục vụ HTML/root, không có GET list/download route, cũng không thấy xử lý CORS. Vì vậy page hiện tại không thể dùng nguyên trạng để tạo PO trong TanTienQR; mở page cùng origin với `PoConsoleServer` chỉ tạo dữ liệu trong DB test. Nếu host HTML khác origin để POST vào app cần endpoint/hosting, CORS/preflight và xử lý API key phù hợp; hiện page cũng không gửi header key.

## Test và vùng file dự kiến

- `tools/PoServerTests/Program.cs`: test receive HTTP, response, orderQty, duplicate, atomicity, auth, giới hạn body, lifecycle, concurrency. `tools/PoServerTests/CodeContentParserChecks.cs`: kiểm parser CSV/JSON/XLSX, đang chấp nhận nhiều chuỗi opaque/plain không phải GS1 và từ chối cả file có cell rỗng/schema sai.
- `tools/PoServerTests/e2e/Program.cs`: E2E `PoHttpServer`, upload, metadata và lỗi; hiện có case `gtin` bị từ chối.
- `tools/PoTests/Program.cs`, `CodeContentLifecycleChecks.cs`: local store/PO lifecycle; `tools/PoFlowTests/Program.cs`: `SyncFromServerAsync`, merge code exact identity/duplicate. Chưa thấy test HTML/browser.
- Vùng PO-01 ít va chạm: model/contracts, `ClassLibraryMT.PoServer/PoUploadHandler.cs`, `PoHttpServer.cs`, shared barcode parser/extractor, `ProductionOrderService.SyncFromServerAsync`, PO tests/docs API. Cần phối hợp response semantics trước khi sửa response type.
- Vùng PO-02 riêng: trang/asset HTML và hướng dẫn chạy. Tích hợp trực tiếp với TanTienQR còn phụ thuộc thiết kế hosting/CORS/API key và hợp đồng PO-01; không nên nhầm `tools/PoServer` test server là target production.

## Working tree và xác minh

- Trạng thái Git mới nhất được đọc bằng `git status --porcelain=v1 --untracked-files=all`: các xóa trong `docs/plans/` (omron-fins-udp-client.md, plc-result-and-heartbeat-to-demoqr.md, po-activation-worker-local-guard.md, printer-buffer-autosend-recovery.md, printer-tab-remote-16-9.md, production-reconciliation-connectivity.md, ui-audit-fix-checklist.md, ui-audit-offscreen.md); file chưa track gồm hồ sơ `.session-org/visionf/...` của tổ chức và `docs/plans/nhiemvu.md`. Không có source đang sửa trong snapshot Git cuối cùng. Tránh sửa/xóa/chụp đè các thay đổi trên.
- Có snapshot Git sớm hơn trong lượt đầu cho thấy file Checkee đang sửa; ở lần kiểm tra porcelain cuối không còn các M đó. Chỉ dùng trạng thái cuối làm cơ sở hiện tại; nếu task triển khai kéo dài hãy đọc lại `git status` trước khi sửa.
- Đã đọc (không chạy test): `docs/plans/nhiemvu.md`; `ClassLibraryMT.Po/{ProductionOrder.cs,IPoClient.cs,PoContracts.cs,PoClient.cs,ProductionOrderService.cs}`; `ClassLibraryMT.PoServer/{PoServerContracts.cs,PoHttpServer.cs,PoUploadHandler.cs,PoCodeFileParser.cs}`; `TanTienQR/Services/{TanTienAppServices.cs,PoServerWorker.cs,QrParser.cs}`; `TanTienQR/ViewModels/ProductionOrdersViewModel.cs`; `tools/PoServer/{PoConsoleServer.cs,Program.cs,PoServer.csproj,www/index.html}`; `tools/PoServerTests/{Program.cs,CodeContentParserChecks.cs,PoServerTests.csproj,e2e/Program.cs}`; `tools/PoTests`, `tools/PoFlowTests`; docs API/contract/production order; các hồ sơ PO department để xác nhận phạm vi.
- Không sửa source, không chạy test/build. Lệnh đọc Git cuối là `git status --porcelain=v1 --untracked-files=all`, branch `master`, HEAD `b66dce6`.
