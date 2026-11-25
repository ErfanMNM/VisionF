# Kế hoạch tự bù buffer và phục hồi mã in G10

Ngày lập: 25/11/2026. Trạng thái: chờ duyệt kế hoạch và xác minh trên máy G10 thật.

## 1. Mục tiêu

1. Khi tự động gửi được bật, PO đang chạy, máy in đang in và buffer giảm xuống dưới 10 mã, tự gửi mã `Pending` vào buffer đến mức mục tiêu mà không vượt sức chứa cấu hình.
2. Khi mã đã được máy nhận nhưng chưa in bị mất khỏi buffer vì người dùng xóa buffer hoặc máy tắt/restart, đưa đúng các mã chưa in về hàng đợi để gửi lại.
3. Không gửi trùng mã đã được xác nhận bằng `BACK`; không thay đổi `ActivatedUtc`, không sinh lại mã và không tự bật máy in.

## 2. Hành vi hiện tại

| Phần | Hiện trạng | Ảnh hưởng |
|---|---|---|
| Nguồn mã | `PrintQueueStore.ClaimNextAsync` lấy mã chưa active có trạng thái `Pending`; trong transaction ghi `Sent` trước khi truyền | Có thể tái sử dụng claim an toàn, nhưng `Sent` cần biết rõ máy đã nhận hay chưa |
| Gửi thủ công | `PrintService.SendNextBatchAsync` đọc `MFDLEN`, tính chỗ trống theo `BatchSize`/`BufferCapacity`, gửi tuần tự | Chưa có luồng tự bù khi buffer xuống thấp |
| Theo dõi | `PrinterMonitorService` đọc trạng thái mỗi 3 giây và chỉ cập nhật UI | Có thể đặt điều phối tự bù ở worker này hoặc worker chuyên trách; không gửi trực tiếp trong ViewModel |
| Nhận mã | `MFD|OK` xác nhận máy nhận mã vào buffer; `BACK` không chứa mã, app ghép với mã `Sent` lâu nhất theo FIFO | Không thể đối chiếu mã trong buffer bằng ID từ giao thức hiện tại |
| Mất kết nối | Trạng thái mã được lưu trong SQLite theo PO; các mã `Sent` không tự gửi lại | Giữ được trạng thái qua lần chạy app, nhưng chưa có quy trình phục hồi buffer đã mất |
| Lệnh xóa | `CLR` có thể gửi từ app; thao tác trên HMI/RDP không được báo cho TCP client | App có thể quản lý `CLR` từ nút riêng; thao tác remote chỉ phát hiện gián tiếp qua mất kết nối/thay đổi buffer |
| Legacy | `Print/ucG10Printer.cs` tự nạp mã khi `MFDLEN < MaxCount` và `IsPrinting`; app mới chưa port hành vi này | Tái sử dụng nguyên tắc chỉ bù khi máy đang in, nhưng dùng ngưỡng thấp hơn theo yêu cầu |

Nguồn chính: `ClassLibraryMT.Print/PrintService.cs`, `PrintQueueStore.cs`, `G10PrinterClient.cs`, `TanTienQR/Services/PrinterMonitorService.cs`, `Print/ucG10Printer.cs`, `docs/printing.md`.

## 3. Tự động bù buffer

### Quy tắc đề xuất

- Tính năng có công tắc bật/tắt riêng và mặc định tắt sau khi cập nhật, để người vận hành chủ động nghiệm thu trước khi áp dụng vào chuyền.
- Dùng ngưỡng thấp cố định `BufferLength < 10`. Khi chạm ngưỡng, bù một lần lên `min(BufferCapacity, BatchSize)` hoặc hết mã `Pending`. Giá trị đích không mặc định là 100 nếu cấu hình máy đặt sức chứa thấp hơn.
- Chỉ bù khi license/module còn hiệu lực, có PO đang chạy, `STATUS` xác nhận máy đang in, TCP đang kết nối, `MFDLEN` hợp lệ, không có thao tác gửi lô/CLR/phục hồi khác đang chạy và không có sự cố cần đối soát.
- Không tự gửi `START`. Nếu máy đang dừng hoặc `STATUS` không đọc được, giữ hàng đợi và hiển thị lý do tạm dừng tự bù.
- Dùng một khóa điều phối cho máy in để poll 3 giây không thể kích hoạt nhiều lần nạp chồng lên nhau. Tính số cần nạp từ buffer mới nhất; claim tối đa phần còn trống, `BatchSize` và số mã `Pending`.
- Giữ cách ghi trạng thái trước khi gửi và tuần tự hóa giao tiếp TCP. ACK `MFD|OK` được ghi nhận tách biệt với trạng thái đã in; lỗi sau khi ghi nhưng trước khi biết ACK được coi là mơ hồ và không tự gửi lại.
- Nếu PO đổi, module/license bị tắt, máy ngừng in hoặc mất kết nối trong khi bù, dừng batch mới. Các mã đã claim nhưng chưa xác định được phản hồi không được đưa về `Pending` ngay.

### Cấu hình và giao diện dự kiến

Thêm `Print.AutoSend.Enabled` (mặc định `false`), `LowWatermark` (mặc định `10`) và có thể dùng cấu hình sức chứa/lô hiện tại làm mức nạp đích. Chỉ thêm `TargetBufferLevel` nếu người vận hành cần khác `min(BufferCapacity, BatchSize)`. Kiểm tra `0 <= LowWatermark < TargetBufferLevel <= BufferCapacity` khi lưu cấu hình.

Tab Máy in hiển thị trạng thái tự bù: tắt/bật, đang nạp, chờ máy in chạy, hết mã, tạm dừng do mất kết nối hoặc cần đối soát. Thêm nút dừng tự bù tức thời; không làm thay đổi trạng thái bật/tắt máy in.

## 4. Phục hồi sau khi buffer bị xóa hoặc máy restart

### Ý nghĩa trạng thái

`Sent` hiện gom chung mã đã claim, mã đã gửi nhưng chưa nhận phản hồi và mã máy đã nhận nhưng chưa có `BACK`. Đợt triển khai cần phân biệt ít nhất `SendUnknown`, `Accepted/Queued`, `Printed`, `RecoveryRequired` và `Pending`, hoặc bổ sung bảng lịch sử lần gửi để giữ các lần thử cũ. `NoResponse` do mã đã active phải tiếp tục là trạng thái cần đối soát riêng, không tự gửi lại.

Mỗi lần phục hồi lưu sự kiện bền vững gồm PO, printer, lý do (`CLR`, offline/restart, buffer giảm bất thường), buffer trước/sau, thời điểm, danh sách mã liên quan, kết quả quyết định và người/luồng khởi tạo. Ghi lịch sử từng lần gửi để khi mã được gửi lại không mất bằng chứng lần trước.

### `CLR` do app gửi

1. Tạm khóa tự bù và thao tác gửi lô; chụp trạng thái buffer cùng danh sách mã đã được nhận nhưng chưa có `BACK`.
2. Gửi `CLR`, yêu cầu ACK thành công, rồi đọc lại `MFDLEN` để xác nhận buffer đã rỗng.
3. Nếu firmware xác nhận `CLR` xóa toàn bộ mã chờ và máy không thể phát `BACK` muộn cho các mã đó, chuyển đúng nhóm mã chưa in về `Pending`, lưu lịch sử thu hồi, rồi cho phép tự bù tiếp.
4. Nếu ACK/đọc lại thất bại, buffer không về 0, hoặc có `BACK` đến trong cửa sổ thao tác, giữ mã liên quan ở `RecoveryRequired`, khóa tự bù và yêu cầu đối soát; không tự đoán mã nào đã in.

Không tự động quản lý thao tác `CLR` phát ra từ RDP/HMI trong giai đoạn đầu vì lệnh đó không đi qua `PrintService`. Khi phát hiện buffer giảm bất thường, xử lý theo nhánh phục hồi mơ hồ bên dưới.

### Tắt máy, mất điện, restart hoặc `CLR` ngoài app

Khi TCP mất kết nối, giữ các mã đã gửi ở trạng thái cần đối soát; không trả ngay về `Pending`. Sau khi kết nối lại, đọc `STATUS` và `MFDLEN`, chờ một khoảng xác nhận ngắn để nhận các `BACK` đang đến muộn, rồi so sánh với backlog đã lưu.

- Nếu buffer còn mã, tiếp tục theo dõi và không gửi lại backlog đó.
- Nếu máy xác nhận buffer bị xóa, chỉ tự thu hồi nhóm `Accepted/Queued` khi đã chứng minh qua firmware rằng các mã chưa có `BACK` không thể đã in xong trong khoảng mất kết nối và `BACK` không bị mất.
- Nếu không chứng minh được điều trên, tạo `RecoveryRequired`, dừng tự bù và yêu cầu đối chiếu; sau xác nhận của người vận hành, có thể đưa nhóm được chọn về `Pending`.

Giao thức hiện không gửi ID mã trong `BACK`, không cho đọc danh sách mã còn nằm trong buffer và `MFDLEN=0` không phân biệt hàng đợi vừa bị xóa với hàng đợi đã in hết khi mất kết nối. Vì vậy tự động gửi lại toàn bộ `Sent` sau mọi lần reconnect có thể in trùng. Muốn tự phục hồi hoàn toàn sau mất điện cần ít nhất một trong các căn cứ: firmware lưu và trả log/ID mã đã nhận/chưa in; bộ đếm `CSTATUS` có semantics đã kiểm chứng kết hợp với bộ đếm in bền vững; hoặc tín hiệu xác nhận rõ ràng rằng máy đã khởi động lại và đã bỏ queue. Nếu firmware không cung cấp căn cứ này, phục hồi hoàn toàn tự động sau mất điện chưa thể bảo đảm không trùng.

## 5. Trình tự triển khai

### Bước 1 — Xác minh giao thức trên G10 thật

- Đo quan hệ giữa `MFDLEN`, số mã `MFD|OK`, `BACK` và mã đang in.
- Kiểm tra `STATUS` khi máy chạy, dừng, rỗng buffer, vừa nhận mã và đang in mã cuối.
- Thử `CLR` khi đang chạy và đang dừng; xác minh số `BACK` có thể đến sau ACK `CLR` không.
- Tắt nguồn và bật lại khi buffer có mã; xác minh queue có được lưu, `MFDLEN`/`CSTATUS` thay đổi thế nào và có thể phân biệt với queue đã in xong không.
- Xác minh thứ tự `BACK`, khả năng mất `BACK` khi TCP rớt và ý nghĩa từng trường `CSTATUS`.

**Điều kiện qua bước:** thống nhất được điều kiện nào chứng minh mã đã in, còn nằm trong queue, hoặc chắc chắn bị mất. Nếu không đạt, chỉ triển khai tự bù và phục hồi có xác nhận người vận hành; không bật auto-retry sau restart.

### Bước 2 — Mô hình lưu và điều phối

- Mở rộng state/persistence theo hướng migration tương thích DB PO cũ; thêm lịch sử lần gửi/phục hồi và truy vấn backlog theo printer/PO.
- Đảm bảo nhiều cửa sổ/lệnh không claim trùng; một worker duy nhất điều phối auto-fill, manual-send, `CLR` và recovery.
- Phân biệt `MFD|OK`, timeout sau write, từ chối, `BACK`, mã đã active và mã cần đối soát.
- Đặt cửa sổ chờ `BACK` muộn và quy tắc resume sau app restart rõ ràng.

### Bước 3 — Tự bù và thao tác phục hồi

- Thêm options/schema/validation, trạng thái UI và công tắc auto-send.
- Bật tự bù khi dưới 10, nạp đến mức đích cấu hình trong giới hạn buffer, chỉ khi PO đang chạy và máy in báo đang in.
- Quản lý `CLR` từ app theo chu trình khóa, snapshot, ACK, xác minh buffer rỗng và thu hồi mã chưa in.
- Tạm dừng tự bù khi reconnect/recovery mơ hồ; cho phép người vận hành xem danh sách mã và xác nhận đưa lại vào hàng đợi.

### Bước 4 — Kiểm thử và rollout

- Kiểm thử ngưỡng 9/10/11, buffer 0/đầy, lô nhỏ hơn chỗ trống, hết mã Pending, đổi PO, dừng máy, mất license/module và poll đồng thời.
- Mô phỏng ACK thành công/thất bại/timeout sau write, `BACK` đến trễ, ngắt TCP giữa batch, app tắt sau claim, reconnect với buffer còn/0/không đọc được.
- Kiểm tra `CLR` thành công, bị từ chối, timeout, buffer không rỗng, `BACK` đồng thời và thao tác `CLR` qua HMI không quan sát được.
- Kiểm tra migration DB cũ, lịch sử lần gửi, idempotency khi app khởi động lại giữa bước phục hồi và không claim lại mã `Printed`/`NoResponse`.
- Chạy `dotnet run --project tools/PrintTests -c Debug`, kiểm thử cấu hình/UI liên quan, `dotnet build TanTienQR/TanTienQR.csproj -c Debug`, sau đó nghiệm thu trên G10 với auto-send tắt trước, bật có giám sát sau.

## 6. Tệp dự kiến thay đổi

| Nhóm | Tệp dự kiến |
|---|---|
| Options/schema | `ClassLibraryMT.Print/PrintModels.cs`, `TanTienQR/Services/AppConfigModels.cs`, `TanTienQR/appsettings.json`, metadata/validation cấu hình và `ConfigTests` |
| Store/state | `ClassLibraryMT.Print/PrintQueueStore.cs`; migration SQLite cho state/lịch sử lần gửi/phục hồi |
| Điều phối | `ClassLibraryMT.Print/PrintService.cs` hoặc service mới chuyên trách auto refill/recovery; `TanTienQR/Services/PrinterMonitorService.cs` chỉ cung cấp trạng thái, không chứa policy gửi nếu tách worker |
| Giao thức | `ClassLibraryMT.Print/G10PrinterClient.cs` nếu cần lệnh đọc bổ sung như `CSTATUS`; cập nhật `PrintTests` bằng TCP loopback |
| UI/tài liệu | `TanTienQR/ViewModels/PrintViewModel.cs`, `TanTienQR/Views/PrintView.axaml`, `docs/printing.md` |

## 7. Quyết định cần duyệt

1. Tự bù mặc định tắt; khi bật, nạp từ ngưỡng `< 10` lên mức `min(BufferCapacity, BatchSize)`, không tự `START`.
2. `CLR` do nút app gửi được phục hồi tự động chỉ sau ACK và xác minh buffer rỗng; thao tác từ HMI/RDP chưa tự thu hồi.
3. Sau tắt máy/restart, chỉ tự thu hồi khi kiểm thử firmware chứng minh được nhóm chưa in. Trường hợp mơ hồ sẽ dừng tự bù và hiện danh sách cần đối soát để tránh in trùng.
4. Cần máy G10 thật trước khi chốt tự động phục hồi sau power-cycle; `MFDLEN`/`BACK` hiện tại chưa đủ phân biệt queue mất với ACK bị mất.
