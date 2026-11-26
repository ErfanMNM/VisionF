# Nhật ký review nghiệm thu PO

- Task: `PO-03-REVIEW`; parent collaboration: `/root`; agent thực: `/root/po_acceptance_review`.
- Thread riêng xác minh bằng `CODEX_THREAD_ID` và rollout của bộ đếm: `01a2175f-66a2-7b70-9236-d2d8848855da`; không dùng ID CEO.
- Journal-key do parent cấp: `po-acceptance-review`; theo phân công chỉ ghi file `LOG.md` này, không tạo budget/ACK hoặc sửa hồ sơ tổng.
- Project root: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`; branch `master`; HEAD `b66dce690674bebbbe395f47cd052cbc2777fe47`.
- Phạm vi: review chỉ đọc diff/source/callers/test, không sửa source, không build khi PO-01 đang triển khai, không commit/deploy/gọi production.
- Budget 2026-11-26T17:21:56+07:00: `normal`, 4/100, exact=true, còn 96; đã chạy bộ đếm chung không ghi ra file ngoài quyền được giao.

## 2026-11-26 — Nhận việc và checkpoint đầu

- Đã đọc `session-worker`, `session-ops`, giao thức và BRIEF/REGISTRY/TASKS/kế hoạch; xác minh CEO hiện tại từ registry là `01a2175d-8c03-7cb0-8b6c-0fd6c8e9dcf9`.
- Snapshot có sáu source tracked đang đổi, `ClassLibraryMT.Po/PoCodeParser.cs` và `docs/po-demo/` mới; backend/download/static/tests còn đang được PO-01 triển khai. Tám file `docs/plans/*.md` deleted và `.playwright-cli/` giữ nguyên.
- Contract review: planned `orderQty` phải khớp số record input trước lọc; giữ raw `CodeContent`; đúng một GTIN checksum; response theo từng mã loại; receive và download dùng cùng luật; static embedded GET cố định chỉ bypass auth trên loopback; `/api/po` giữ `x-api-key`.
- Tiếp tục: đọc handler/parser/store/download/callers/test; báo finding với vị trí source và ghi rõ pending của snapshot, không đánh đồng phần chưa triển khai với lỗi hồi quy đã nghiệm thu.

## 2026-11-26T17:25:09+07:00 — Kết quả snapshot review

**Chưa đủ điều kiện nghiệm thu.** Đây là review trong lúc PO-01/PO-02 đang sửa, không phải báo cáo một bản hoàn tất. Chưa chạy build/test/HTTP/browser; các vị trí dưới đây được xác minh bằng đọc source/diff. Chỉ bài tái hiện regex dùng .NET trong PowerShell đã được thực thi, không ghi source hoặc gọi mạng.

### Findings theo mức độ

1. **[P1 — đang chờ triển khai] Planned quantity bị thay bằng accepted/actual khi lưu.** `ClassLibraryMT.PoServer/PoHttpServer.cs:740-746` gán `OrderQty = existing TotalCodes + codes.Count`; `ClassLibraryMT.Po/PoStore.cs:435-436` lưu giá trị này, `:617` lại trả `OrderQty` từ `totalCodes` nếu có mã. Download cũng gán actual ở `ClassLibraryMT.Po/ProductionOrderService.cs:223,237-239`. Vì vậy `plannedCount` response không đủ bảo toàn `orderQty` của caller trong DB/reload. Tái hiện cần chạy sau khi source compile: gửi bốn record gồm một mã hợp lệ GTIN A, một bản lặp của nó, một mã sai cấu trúc và một mã GTIN B, `orderQty=4`; response có thể là accepted=1/planned=4 nhưng source hiện lưu/đọc OrderQty=1. Ảnh hưởng: mất dấu partial receive và sai số planned trong PO local. Cần tách planned và actual xuyên suốt store/list/summary/download, định nghĩa rõ append/replay không cộng planned thành actual hoặc tăng hai lần.

2. **[P1 — đang chờ triển khai] Download chưa dùng luật GTIN/cấu trúc và chưa báo các mã loại.** `ClassLibraryMT.Po/ProductionOrderService.cs:213-239` chỉ lọc chuỗi rỗng và duplicate trong snapshot, không kiểm checksum/GTIN metadata/GTIN mismatch, không kiểm planned với record input, rồi ghi những mã còn lại. `:34` và `:257` cho thấy `PoSyncResult` chỉ có ba số đếm tổng. `TanTienQR/ViewModels/ProductionOrdersViewModel.cs:219-229` chỉ báo tải thành công cùng tổng PO/mã mới. Tái hiện theo source: fake `IPoClient` trả PO planned=4/GTIN A cùng bốn record ở finding 1; mã sai và GTIN B vẫn đi vào store, không có reject details cho caller. Cần kiểm parity receive/download và giữ lỗi network/HTTP/JSON khác kết quả validation/duplicate.

3. **[P1 — snapshot source chưa ghép xong] Guard một GTIN chưa có persistence round-trip và đang tham chiếu member thiếu.** `ClassLibraryMT.PoServer/PoHttpServer.cs:665-666,744` dùng `LocalPoInfo.Gtin`, nhưng `ClassLibraryMT.Po/PoStore.cs:1282-1328` chưa định nghĩa member này; đọc/lưu metadata tại `:435-448,549-626` cũng chưa round-trip top-level `Gtin`. Hiện `additional["gtin"]` được ghi ở handler `:735` nhưng không được đưa lại vào property dùng để guard. Chưa chạy compiler nên không có build result để báo; source snapshot có lỗi member theo kiểm tra tĩnh. Cần hoàn thiện mapping và test append GTIN B sau reload một PO đã gắn GTIN A. Với PO legacy có GTIN rỗng, chú ý `existingOrder?.Gtin ?? gtin` không fallback nếu chuỗi là `""`; cần chính sách migrate/từ chối rõ ràng thay vì âm thầm cho trộn.

4. **[P1 — compatibility] Caller MES hiện hữu không thể cung cấp GTIN bắt buộc.** `ClassLibraryMT.MesSimulator/MesSimulatorClient.cs:78-84` chỉ thêm `poName`, `orderQty`, `productName`; options/worker không có đường cấp GTIN. Trong khi `ClassLibraryMT.PoServer/PoHttpServer.cs:612-623` trả 422 khi metadata GTIN thiếu. Sau khi backend build được, cả `UploadTestOrderAsync` và `UploadExistingFileAsync` hiện sẽ bị từ chối dù file đúng. Cần mở đường truyền GTIN cấu hình/tham số, giữ alias `poName`, cập nhật worker và test. Console download server cũng chưa khớp: `tools/PoServer/PoConsoleServer.cs:105-112` bỏ qua metadata.Gtin; `tools/PoServer/PoServerStore.cs:178,198-206` lưu actual và không trả GTIN. Nếu đây là nguồn test chính thức cho luồng download, cần đồng bộ hoặc ghi rõ contract fixture mới để không vô tình nghiệm thu bằng mock lệch.

5. **[P2 — tái hiện được] Regex URL nuốt GTIN đầu vào prefix và chỉ chọn GTIN cuối.** `ClassLibraryMT.Po/PoCodeParser.cs:9` cho phép prefix path tùy ý bằng quantifier lặp nhiều segment trước `/01/`; `:23-31` lấy group `gtin` duy nhất mà không phát hiện AI lặp. Đã dùng cùng regex .NET tái hiện chuỗi `https://example.invalid/01/00012345678905/21/A/01/09506000134352/21/B`: regex thành công, trả GTIN `09506000134352`, serial `B`; cả hai GTIN có checksum đúng. Nếu metadata là GTIN cuối, receive coi chuỗi chứa GTIN đầu khác là hợp lệ. Cần giới hạn grammar/prefix và từ chối repeated primary identifiers hoặc GTIN mâu thuẫn; giữ nguyên raw code khi trả reject. Đây là finding cụ thể ngoài các phần pending đã biết.

### Phần đã xác minh đúng trong snapshot

- `PoUploadHandler` chấp nhận `orderNo` và canonical alias legacy `poName`/`po`/`lot`; duplicate metadata alias qua query/multipart bị phát hiện bởi cùng canonical key. Một request chỉ có một file; không đổi raw `CodeContent` trước đưa vào handler.
- `PoCodeParser.IsValidGtin` dùng chữ số ASCII, độ dài 8/12/13/14 và checksum; receive kiểm metadata trước lưu. Mã hợp lệ được lưu bằng `new PoCode { CodeContent = raw }`; invalid/duplicate response giữ raw. Dedupe dùng `StringComparer.Ordinal`, không cắt URL/serial/case.
- `accepted`/`ignoredDuplicates`/`poName` response cũ còn hiện diện cùng fields mới. Khối xử lý mới phân loại `invalidStructure` trước `gtinMismatch`, rồi duplicate exact-string trong file/PO đích; all duplicate -> 200 accepted=0; all invalid -> 422 không ghi DB.
- API vẫn qua `CheckApiKey` trước dispatch (`PoHttpServer.cs:420-432,502-511`). Chưa thấy bypass auth mới trong snapshot.
- File parser CSV/JSON/XLSX giữ nguyên text; schema sai/empty explicit cell bị reject toàn file theo parser hiện hữu. Đây là lỗi định dạng file khác mã không hợp GS1; cần giữ phân biệt này trong tài liệu/test để tránh thay đổi ngầm.

### Pending và khoảng trống test

- Chưa có route `/po-demo/` hoặc `EmbeddedResource` trong `ClassLibraryMT.PoServer.csproj`; static chưa thể nghiệm thu. UI `docs/po-demo/index.html:158,185-187` vẫn khóa gửi/preview; có password input không `name`, nhưng chưa có `fetch`/FormData/map response. Đây là tiến độ PO-01/PO-02, không phải kết luận một route đang lộ auth.
- Bộ `tools/PoServerTests/Program.cs:32-45,199-204,242-246` vẫn gửi thiếu GTIN, dùng các opaque fixtures như `Case`, và mong 200. `tools/PoServerTests/e2e/Program.cs:175-178` còn coi metadata GTIN là không được hỗ trợ. `tools/PoFlowTests/Program.cs:167-168` khẳng định OrderQty bằng actual thay vì planned. Chưa thấy test mới checksum/mixed-GTIN/planned/rejected/static-auth trong diff.
- `docs/MES-PO-API.md:38,97-104,182` vẫn mô tả opaque codes không cần GTIN, cấm metadata GTIN và reject toàn file cho mọi record sai. Cần cập nhật sau khi contract/source hoàn tất; không sửa ở review này.
- Chưa xác minh publish/Native AOT còn embedded resource, chưa chạy HTTP listener, browser smoke hoặc test suite. Không được dùng log review này làm bằng chứng build/test pass.

### Ma trận nghiệm thu đề xuất cho CEO/owner

1. Build các project bị ảnh hưởng sau khi PO-01 xác nhận source sẵn sàng; chạy `tools/PoServerTests`, `tools/PoServerTests/e2e`, `tools/PoFlowTests`, `tools/PoTests`, `tools/MesSimulatorTests`, `tools/PoServer/Tests` phù hợp phạm vi. Test fixture GS1 cần checksum đúng; không chỉ sửa expected status để che fixture cũ.
2. Receive CSV/JSON/XLSX: cùng bộ bốn record ở finding 1 -> input=planned=4, accepted=1, invalidStructure=1, gtinMismatch=1, duplicate=1; raw từng record trong response/DB phải khớp ordinal; reload store giữ planned=4, actual=1, GTIN A.
3. Metadata GTIN thiếu/checksum sai/list GTIN -> 4xx và không tạo/thay DB; `orderNo` và `poName` cho cùng hành vi; gửi cả hai, metadata lặp, nhiều file hoặc schema sai -> 400 nguyên tử. Planned thấp/cao hơn raw record count -> 400 không mutation.
4. All invalid -> 422 accepted=0 và không DB mới; all duplicate -> 200 accepted=0 cùng danh sách duplicate; replay/append/concurrent merge không tăng actual sai hoặc làm mất planned/metadata và lịch sử scan.
5. GTIN A đã lưu, restart/reload, append GTIN B -> 4xx không mutation. Legacy GTIN rỗng hoặc có mã trộn phải có policy cụ thể; không chỉ kiểm property khi non-empty rồi cho nhập.
6. Parser parity: URL, dạng `(01)...(21)...`, element string/GS nếu chủ định hỗ trợ; checksum/serial length sai, repeated AI/GTIN mâu thuẫn, newline/space/control không hợp grammar -> invalidStructure. Metadata GTIN ngắn và padded GTIN-14 cần policy equality rõ, không sửa raw để né so sánh.
7. Download fake client + HTTP fixture: lọc và trả cùng categories/counts với receive; planned/raw record count trước lọc; duplicate trong snapshot và đã có trong PO; finished/running guards; legacy metadata policy; network/HTTP timeout/JSON lỗi vẫn là lỗi vận chuyển, không báo thành validation hay thành công rỗng.
8. `/po-demo/`: GET fixed resource 200 HTML đúng assembly; alias chỉ nếu explicit; POST/PUT/HEAD/path lạ không trả static; traversal `..`, `%2e%2e`, slash/backslash encode không đọc disk/config/log. Xác minh embedding cả publish/single-file/AOT nếu thuộc gói phát hành.
9. Static auth matrix: bind loopback có configured key -> GET HTML 200 thiếu key, API thiếu/sai key -> 401 không DB, đúng key -> response hợp đồng. Bind wildcard/LAN -> static không được bypass auth; request remote qua listener bind rộng không được hưởng exception loopback.
10. Browser từ cùng origin `/po-demo/`: gửi đúng `FormData` tới relative `/api/po`, không tự đặt multipart Content-Type; map counts/danh sách raw với `textContent`, hiển thị partial/all-invalid/401/409/network lỗi; không báo thành công khi request chưa gửi hoặc HTTP lỗi.
11. Key chỉ nằm trong password input/bộ nhớ tạm/header: không query/body/storage/log/HTML/summary; kiểm key xóa theo chính sách UI và không bị log bởi debug; không ghi giá trị thật vào test artifact.
12. Kiểm diff cuối giữ tám plan deleted có sẵn và `.playwright-cli/`; chỉ owner được ghi vùng source. Chỉ đóng outcomes kế hoạch sau build/test/browser có bằng chứng.

### Bàn giao

- Đã gửi cảnh báo sớm cho CEO qua collaboration về planned/GTIN/download và parser/caller compatibility; CEO là đầu mối chuyển tới PO-01/PO-02.
- Chỉ thay đổi file nhật ký này; không sửa source, test, bảng task, plan, registry hoặc artifact của agent khác. Không chạy build/test endpoint để tránh xung đột owner.
- Budget cuối nhóm review: 2026-11-26T17:25:09+07:00, `normal`, 15/100 exact=true, còn 85.
- Bước tiếp theo: PO-01 hoàn thiện các phần pending và sửa finding cụ thể; CEO cần review lại diff cuối cùng rồi chạy kiểm tra/browser. Reviewer có thể nhận lượt review lại khi có snapshot sẵn sàng, không tự chờ vô hạn hoặc nhận quyền source.

## 2026-11-26T18:03:00+07:00 — Review delta backend/callers

### Phạm vi và kết luận

- CEO giao review delta cuối sau khi A/B/C hoàn thiện phần lõi; chỉ đọc source/test, không chạy lại build/suite, không thực hiện thao tác Git hoặc sửa source. UI successor đang nối fetch nên UI/browser vẫn pending riêng.
- Đã đọc các quyết định mới: GTIN canonical14 chỉ để so/lưu metadata, rawordinal dùng định danh; planned merge max; receive raw==planned; download accepted-only raw<=planned giữ planned. Quyết định này thay gate download raw==planned trong ma trận review ban đầu.
- **PASS có điều kiện cho source backend/callers đang đọc:** năm nhóm finding cũ đã có phần triển khai tương ứng. Cần CEO chạy/ghi bằng chứng test sau guard static cuối và DI fix mới, hoàn tất cập nhật tài liệu simulator còn cũ, rồi nghiệm thu UI/browser từ assembly build mới. Không dùng kết luận này thay bằng chứng chạy suite hoặc browser.

### Đối chiếu năm finding cũ

| Nhóm | Kết quả delta | Vị trí xác minh |
| --- | --- | --- |
| Planned bị đổi thành actual | Đã sửa: receive lưu payload.OrderQty; store cập nhật max, reload giữ planned; summary có TotalCodes riêng, tiến độ/lifecycle dùng actual. | `PoHttpServer.cs:792-804`; `PoStore.cs:436-448,674-687`; `ProductionOrderService.cs:296-310,338-356,441-450,717-724,1181-1188`; `ProductionOrdersViewModel.cs:645-651`; `ProductionInfoViewModel.cs:219-226,258-265`. |
| Download thiếu validation/details | Đã sửa: xác thực metadata GTIN/planned, raw<=planned; guard local GTIN và toàn bộ mã cũ; lọc từng raw theo cùng lý do; outcomes additive, lỗi transport không bị catch thành validation. | `ProductionOrderService.cs:34-55,231-318`; `PoDownloadContractChecks.cs` được gọi từ `tools/PoFlowTests/Program.cs:245`; caller mô tả result tại `ProductionOrdersViewModel.cs:253-269`. |
| GTIN không round-trip/member thiếu | Đã sửa: MetaGtin và LocalPoInfo.Gtin hiện hữu, canonical14; fallback additional/legacy chỉ suy khi raw đồng nhất; receive/download còn kiểm lại từng mã cũ, chặn legacy opaque/mixed dù metadata có giá trị. | `PoStore.cs:451-465,617-640,677-678,1351-1352`; `PoHttpServer.cs:716-732`; `ProductionOrderService.cs:249-269`; `PoMetadataContractChecks.cs`, `PoContractChecks.cs`, `PoDownloadContractChecks.cs` có ca legacy tương ứng. |
| Caller MES/console chưa gửi/lưu GTIN | Client/options/worker/console đã được nối; DI thiếu hai field mới đã được reviewer phát hiện và CEO sửa ngay trong lượt này. Đọc lại xác nhận Gtin và CustomOrderNo được bind ở DI. | `MesSimulatorClient.cs:25-26,41,46-53,79-96`; `MesSimulatorWorker.cs` gọi UploadExistingFileAsync kèm options.Gtin; `MesSimulatorServiceCollectionExtensions.cs:25,29` sau sửa 18:02:55; `PoConsoleServer.cs:105-113`; `PoServerStore.cs:136-206,241-279`. |
| Regex nuốt GTIN đầu | Đã sửa mẫu cụ thể: prefix không được nuốt segment AI số; query AI 01/21 lặp bị chặn; serial dạng ngoặc chứa 01/21 lặp bị chặn. | `PoCodeParser.cs:9,24-31,35-49`; `PoMetadataContractChecks.cs` có mẫu AI lặp/checksum/serial. |

Đã chạy lại **chỉ regex .NET đọc trực tiếp pattern từ file**, không build/suite: mẫu cũ `https://example.invalid/01/00012345678905/21/A/01/09506000134352/21/B` hiện `RegexMatches=false`; URL prefix hợp lệ `https://example.invalid/products/01/04006381333931/21/AbC-1?batch=A` vẫn match và trả đúng GTIN. Đây là kiểm chứng độc lập riêng cho finding parser.

### Phát hiện trong lượt delta và trạng thái

1. **[P1 — đã sửa theo source trong lượt] DI bỏ qua GTIN/CustomOrderNo cấu hình.** Lần đọc 18:01, `MesSimulatorServiceCollectionExtensions.cs:18-29` Configure thủ công thiếu hai field mới dù options/client/worker dùng chúng. Tái hiện tối thiểu: cấu hình `MesSimulator:Gtin` hợp lệ và `Enabled=true`, đăng ký qua `AddMesSimulator(configuration)`, options.Gtin vẫn rỗng, worker ném ArgumentException tại ResolveGtin trước HTTP. CEO đã nhận báo và sửa; lần đọc lại xác nhận `:25` đọc Gtin, `:29` đọc CustomOrderNo. Test suite trước đó tạo Options trực tiếp, nên cần một test cấu hình/DI thật và rerun phù hợp cho phần vừa sửa. Reviewer không chạy lại để tránh suite trùng.
2. **[P2 — tài liệu còn phải đồng bộ] `docs/MES-SIMULATOR.md:15-26,30-49,61-69` vẫn dạy contract cũ.** Văn bản nói không gửi GTIN, JSON config không có Gtin, ví dụ CodeContent opaque và lời gọi C# không chỉ ra nguồn GTIN. Làm theo hướng dẫn sẽ bị client chặn thiếu GTIN hoặc API loại invalidStructure. Cần dùng orderNo/alias rõ, thêm Gtin có checksum, mã GS1 hợp lệ và thông tin partial/planned; đối chiếu API guide mới. Đã báo CEO, không tự sửa tài liệu.
3. **[Chính sách edge — không coi blocker nếu CEO chấp nhận] All-duplicate không cập nhật planned.** `PoHttpServer.cs:760-779` và `tools/PoServer/PoServerStore.cs:209-210` trả sớm khi không có accepted mới; incoming planned cao hơn không persist. Ví dụ planned1/raw A sẵn có, gửi [A,A,A,A]/planned4 -> response200/planned4, DB planned1. `docs/MES-PO-API.md:53` đã ghi explicit exception: chỉ append có mã mới mới merge max, duplicate-only không ghi target/metadata. Vì vậy không kết luận đây là lỗi code so với tài liệu; DECISIONS mô tả merge max chưa nêu exception, CEO cần đồng bộ/khẳng định chính sách và nên có test bảo đảm no-mutation. Download all-duplicate vẫn persist max; cần giữ khác biệt này chủ ý, tránh mơ hồ.

### Static/auth sau guard cuối

- Source mới chụp `_boundToLoopback` từ prefix thực đã Start tại `PoHttpServer.cs:224-230`, rồi dùng field này tại `:540`; thay options sau bind không thể mở bypass cho listener wildcard.
- Chỉ GET và RawUrl exact `/po-demo/`, `/po-demo/validation.js` ánh xạ tên resource cố định, không đọc file disk (`:542-564`). API-key gate vẫn nằm trước API routes (`:440-452`). Listener bind rộng chỉ phục vụ static sau CheckApiKey; HEAD trả status/body rỗng riêng.
- `PoContractChecks.cs` hiện được compile include và gọi từ Program; source test có HTML/resource equality, API 401, method/path/traversal, wildcard auth và thay options sau bind. Chưa tự chạy suite sau thay guard; gate thuộc CEO.

### Bằng chứng kiểm tra và phần còn chờ

- Reviewer chỉ dùng Get-Content/rg để kiểm source/test và một bài regex tách biệt; không build, không chạy listener/HTTP/browser, không thao tác Git. Không sửa gì ngoài LOG này.
- CEO cung cấp số suite trước review: core142/63, server117/e2e31, MES28/console37, TanTienQR build0error với AVLN3001 ngoài scope; AcceptanceRelease154 pass/0 lỗi trước guard cuối. Đây là **số CEO báo**, reviewer không chạy hoặc chứng nhận lại. Kết quả cuối sau DI/static/UI phải do owner/CEO bổ sung.
- Các cổng còn chờ: test DI config Gtin/CustomOrderNo và test policy duplicate-only nếu chốt; sửa docs simulator; rerun test cần thiết sau source cuối; build lại resource khi UI xong; browser same-origin success/partial/all-invalid/401/409/network, key không storage/log/DOM và phản hồi raw an toàn.
- Budget riêng: 2026-11-26T18:02:46+07:00, `normal`, 27/100 exact=true, còn 73. Tiếp tục giữ slot nghỉ sau khi gửi kết quả delta; nhận review tiếp nếu CEO gọi, không tự triển khai source.

### 2026-11-26T18:05:29+07:00 — Xác nhận sửa tiếp ngay trước bàn giao delta

- CEO/owner đã cập nhật source trong khi review; đọc lại xác nhận `tools/MesSimulatorTests/Program.cs:145-173` thêm test `ConfigurationBuilder` + `ServiceCollection` + `AddMesSimulator`, kiểm options và chạy worker qua DI thật, kiểm GTIN/planned/actual cùng ưu tiên CustomOrderNo. Reviewer chỉ đọc test, chưa chạy; gate chạy test vừa thêm vẫn thuộc CEO.
- `docs/MES-SIMULATOR.md:15-28,40-55,63-76` đã được sửa: metadata GTIN bắt buộc, mã GS1 có checksum, config Gtin/CustomOrderNo, override GTIN và giải thích planned/raw. **Finding P2 tài liệu phía trên đã khép theo lần đọc mới.**
- Kết luận cuối của lượt delta: **PASS có điều kiện cho backend/callers; không còn finding code chặn đã biết trong phạm vi vừa review.** Các điều kiện còn lại là thực thi/ghi bằng chứng test sau DI/static guard mới, đồng bộ quyết định exception duplicate-only với API guide, và UI/browser cuối sau khi build lại embedded resources. Đây không phải chứng nhận UI hoặc khẳng định suite đã pass do reviewer chạy.
