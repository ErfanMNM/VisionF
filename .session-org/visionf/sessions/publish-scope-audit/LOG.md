# Review phạm vi publish PO
- Task: PO-05-PUBLISH-AUDIT; parent `/root`, CEO `01a21787-5ed2-7ac2-a73d-80c07b4996d0`.
- Agent: `/root/publish_scope_audit`; thread thật qua CODEX_THREAD_ID: `01a21789-fd90-7fa0-ad39-bd14a4cf4607`.
- Root: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`; nhánh `codex/po-gtin-demo`; chưa commit tại snapshot.
- Chỉ đọc source/test/docs/Git và chỉ ghi LOG này theo phạm vi parent. Không build/test/host, không stage/commit/push và không sửa bảng tổng.
- Đã đọc session-worker, session-ops và protocol. Budget riêng lúc bắt đầu normal 2/100, exact=true; không ghi budget ngoài phạm vi.
- Mục tiêu: kiểm phạm vi publish, tệp sinh ra/bí mật vô tình và tài liệu contract cuối. Không lặp functional/backend review đã có.

## Kết quả audit 2026-11-26 18:10 (Asia/Saigon)

### Phạm vi và chứng cứ
- Đã đọc `git status --short --branch`, `git diff --stat`, danh sách tệp untracked, README UI, API guide, MES guide, README acceptance, csproj embedded resources và phần hợp đồng trong production-order.
- Quét 71 tệp text dự kiến thuộc source/test/docs/Markdown với mẫu private key, GitHub token, OpenAI key, AWS key và URL chứa credential: **không có candidate độ tin cậy cao**. Quét generic credential literal chỉ thấy 6 giá trị test/placeholder được nhận diện; không có candidate cần mở khóa thật. Đây là kiểm tra tĩnh theo mẫu, không phải bảo đảm tuyệt đối rằng mọi dạng bí mật đều được phát hiện.
- Key `po-demo-local-test-key` là key công khai dành riêng host test; các host `.invalid`/`.test` và GTIN mẫu không phải credential.
- Không build/test/browser/host; không sửa source, test, tài liệu đầu ra, Git index hoặc bảng tổng. `git diff --name-only --cached` rỗng ở snapshot audit, do đó chưa review được staged diff cuối.

### Tài liệu contract cuối
- `docs/MES-PO-API.md:53,161` hiện ghi rõ all-duplicate persist planned max; không còn exception bỏ qua cập nhật target. `docs/MES-SIMULATOR.md` cũng có ví dụ planned1 → upload bốn bản trùng → actual1/planned4 và DI Gtin/CustomOrderNo.
- `docs/po-demo/README.md:8,13,31-38,49,57` mô tả đúng same-origin `/api/po`, hai route GET embedded, rebuild/restart, raw File, counts/rejections và key transient. `ClassLibraryMT.PoServer.csproj` chỉ embed `index.html`, `validation.js`; không embed README/tests/output.
- GTIN canonical14 và raw ordinal nhất quán giữa ba tài liệu mới đã đọc. Không lặp backend functional review hoặc coi report lịch sử là source cuối.

### Delta tài liệu nhỏ gửi CEO
1. `docs/production-order.md:18` là tài liệu cũ ngoài diff, vẫn mô tả metadata `poName`/`orderQty`, chưa nói GTIN bắt buộc và chỉ nói trước lọc trùng; MES guide mới đang liên kết tới tài liệu này. Nên sửa đoạn hẹp để nêu `orderNo`/alias, planned/raw count, một GTIN và các nhóm lọc, hoặc dẫn rõ API guide là contract mới. Không phải phát hiện code mới.
2. Dòng PO-05 cuối `.session-org/visionf/TASKS.md` có literal `` `n| `` làm hỏng row Markdown. CEO sửa khi cập nhật bảng tổng; reviewer không có quyền ghi bảng này.
3. `tools/PoAcceptanceTests/README.md:21` còn câu tương lai sẽ thêm assert `LocalPoInfo.Gtin` khi API hoàn tất; suite hiện kiểm GTIN qua metadata/guard như chính README nêu. Đây là giới hạn coverage đã công bố, không phải test pass giả hoặc blocker source; có thể bỏ lời hứa tương lai khi chốt hồ sơ.

### Loại khỏi stage
- Giữ nguyên, không stage tám deletion ngoài scope: `docs/plans/{omron-fins-udp-client,plc-result-and-heartbeat-to-demoqr,po-activation-worker-local-guard,printer-buffer-autosend-recovery,printer-tab-remote-16-9,production-reconciliation-connectivity,ui-audit-fix-checklist,ui-audit-offscreen}.md`.
- Toàn bộ `.playwright-cli/**` là output được giao bảo toàn, không commit/dọn.
- Không stage `.session-org/**/budget.json`, `*.log` output, script browser `browser-phase-a.js`, `browser-phase-b.js`, ảnh `desktop-partial.png`, `mobile-partial.png`, `network-error.png`, JSON `final-evidence.json`, `persistence.json` trong session UI. Chỉ chọn Markdown PO liên quan nếu CEO quyết định lưu hồ sơ trong commit.
- Không stage tempDB/build/bin/obj/artifacts hoặc cấu hình môi trường; phần lớn đã bị `.gitignore` loại. Không dùng `git add -A` vì tám deletion đang có sẵn.

### Kết luận
- Không phát hiện blocker publish do tệp sinh ra hoặc credential thật trong tập dự kiến đã đọc. Các delta tài liệu nhỏ ở trên đã báo parent; source/test và Git không bị reviewer thay đổi.
- CEO còn phải nghiệm thu browser sau rebuild backend delta, chạy AcceptanceRelease cuối, cập nhật outcomes/hồ sơ và inspect staged diff thực tế trước commit/push. Không dùng audit này thay cho các cổng đó.

## Audit staged snapshot 2026-11-26 18:14 (Asia/Saigon)
- Parent giao kiểm staged diff sau khi CEO stage source/test/docs; chỉ đọc index và tiếp tục chỉ ghi LOG riêng này. Không build/test hoặc thay index.
- Snapshot đã kiểm có **39 tệp staged: 13 A, 26 M, 0 D, 0 R**, tổng diff 2771 dòng thêm/400 dòng bỏ. Tất cả là source/test/docs PO thuộc scope; chưa có Markdown `.session-org` và hai docs đang được phòng PO-01 chốt ở snapshot này.
- Đã đọc manifest/stats, diff cached API/MES docs, csproj embedded/test compile và outcomes `docs/plans/nhiemvu.md`; quét trực tiếp blob **trong index** của 39 tệp cho private key/GitHub/OpenAI/AWS/credential URL và generic credential literals. **Không có candidate secret thật** theo các mẫu kiểm; key test công khai/placeholder vẫn được phân loại đúng.
- Không có `.playwright-cli`, `.session-org` budget/output, ảnh/JSON evidence, DB, DLL/EXE/PDB hoặc thư mục bin/obj trong 39 tệp. Không có tám deletion ngoài scope trong index. `git diff --cached --check` không có lỗi whitespace.
- `git diff --name-status` tại snapshot chỉ còn tám deletion ngoài scope và `docs/production-order.md` đang sửa; **không có source/test/UI staged nào bị bỏ sót delta working-tree** sau khi stage. Điều này hỗ trợ đối chiếu index với snapshot source cuối mà CEO đã báo test PASS, không thay chứng nhận test từ CEO/owner.
- API/MES docs staged đã có planned max toàn duplicate, raw ordinal, GTIN canonical14, alias orderNo/poName và runtime same-origin embedded. Outcomes staged ghi đúng các số cuối CEO cung cấp: core142/flow63/server121/e2e31/MES31/console40, AcceptanceRelease154/0lỗi và Node18/18; không tự nhận reviewer đã chạy các suite/browser.
- CEO đã sửa literal row PO-05 trong TASKS working-tree. Hai docs `docs/production-order.md` và `tools/PoAcceptanceTests/README.md` còn thuộc batch tài liệu tiếp theo, chưa được dùng làm chứng cứ staged trong audit39 này.
- **Kết luận staged39: PASS trong phạm vi audit publish.** CEO còn kiểm manifest cuối sau khi thêm hai docs và Markdown PO; chỉ `.md` trong `.session-org` nếu chọn lưu. Không suy ra quyền merge/deploy từ kết quả này.
- Budget riêng trước batch staged: normal **8/100**, exact=true. Không ghi budget JSON ngoài quyền; kết thúc mutation LOG tại checkpoint này.
