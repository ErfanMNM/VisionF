# Counters — nhiệm vụ 1

- Owner: `/root/trace_plc_sources`, session `01a21820-dcea-7ad3-945f-156690c941eb`.
- 2026-11-26 22:34 +07:00: SOURCE FROZEN, BUILD SLOT RELEASED. Hai suite Release đã exit 0; báo CEO, không có dotnet process của phòng còn chạy.
- Response checkpoint: 54 unique response_id, đúng session log `C:/Users/TH/.codex/sessions/2026/11/26/rollout-2026-11-26T20-52-16-01a21820-dcea-7ad3-945f-156690c941eb.jsonl`.
- Contract: địa chỉ riêng tùy chọn cho Total/Pass/Fail/Timeout, fallback CounterAddress cũ; FAIL mặc định vẫn cộng Timeout. Nguồn hiển thị PLC/App. Reset counter và Clear lỗi là lệnh riêng, cần EditConfig và thao tác chủ động.
- Source: AppConfigModels, ConfigValidation, PlcOptions, PlcWriter, RuntimeCounters, TanTienAppServices phần PLC, appsettings; MainViewModel chỉ counters kiểu long + Scans refresh, InfoView nhãn nguồn + nút Clear. Không sửa phần UI V2/Checkee.
- Tests source: ConfigTests thêm validation địa chỉ/nguồn/diff/JSON cũ+mới. PlcHotApplyTests thêm startup binding, custom D, fallback, contract Fail, lỗi snapshot giữa các D, App snapshot không tăng khi refresh/switch, Clear auth/gate/disabled, heartbeat không tự Clear/reset.
- Docs: docs/plc-counters.md. Read-only check: git diff --check trên toàn phạm vi Counters exit 0 (chỉ line-ending warning, không có lỗi whitespace trong phạm vi).
- Kiểm chứng mới nhất (sau Settings V2 sửa compile):
  - `dotnet run --project tools/ConfigTests -c Release -p:PublishAot=false -p:SkipPrinterRemoteHelper=true`: **208 pass / 0 fail**, exit 0. Log `counters-config-tests-release.log`.
  - `dotnet run --project tools/PlcHotApplyTests -c Release -p:PublishAot=false -p:SkipPrinterRemoteHelper=true`: **142 pass / 0 fail**, exit 0. Log `counters-plc-hotapply-release.log`. Chỉ UDP localhost.
  - Không có source delta trong phạm vi Counters giữa source checkpoint và test. Có Avalonia AVLN3001 cảnh báo constructor của ConnectionConfigurationConfirmWindow, đã có trước, không chặn suite.
- Lần compile trước đó thất bại (không cộng vào số suite pass): log `counters-config-tests.log`, SettingsV2ViewModel.cs:168 CS9273/CS1061 ngoài ownership, đã báo đúng owner/CEO và chờ cấp lượt mới.
- Kế tiếp thuộc CEO: ApplicationUiTests + integration/reviewer; PLC production và AOT publish chưa kiểm. Không nhận source sửa mới khi chưa mở freeze.
- Không commit/push/deploy. Giữ nguyên docs/plans/nhiemvu.md của user.

## Review độc lập Checkee (chỉ đọc, đã báo CEO)

- P1: Begin intent đặt record Failed trong HTTP. UI lấy ID lỗi ngoài worker lock, queue resend; HTTP cũ có thể Sent trước khi queued resend reset ID vô điều kiện về Pending và gửi lại. Bằng chứng: ScanSendStatusStore.History.cs:48–50; CodeLookupViewModel.cs:371; CheckeeSendWorker.cs:151,175–180; ScanSendStatusStore.cs:250–254. Cần conditional reset và ngăn candidate đang có unfinished intent.
- P2: Overlay chi tiết đang mở từ InProgress không reload khi history list đổi sang final; CodeLookupViewModel.SendHistory.cs:44–49 cập nhật list, không refetch SelectedSendAttempt/SendDetailRows.
- Điểm đã đối chiếu: snapshot items bền vững trước HTTP, complete transaction một lần, unknown không markOK, cancellation complete Unknown độc lập caller token, FK cascade/prune60.
- Counters SOURCE FREEZE tạm theo CEO, đang chờ SettingsV2 release slot. Không sửa Checkee/V2.
