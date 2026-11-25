# Quy trình Production Order với CodeContent

TanTienQR lưu dữ liệu sản xuất theo từng PO trên máy. Mã được nhận diện bằng **toàn bộ `CodeContent`**, so sánh ordinal và phân biệt hoa thường. Hai URL cùng hậu tố, hai mã khác chữ hoa/thường hoặc khác khoảng trắng có ý nghĩa vẫn là các mã riêng. Luồng sản xuất không tách GTIN/serial và không dùng cửa sổ chống trùng theo giây.

Hợp đồng dữ liệu nằm ở [contract.md](contract.md); định dạng upload và ví dụ HTTP nằm ở [MES-PO-API.md](MES-PO-API.md). Hướng dẫn bộ đếm, reset PLC và tra cứu nằm ở [application-codecontent.md](application-codecontent.md).

## Nhận PO và tải PO

Có hai đường đưa PO vào cơ sở dữ liệu local:

| Đường dữ liệu | Cách dùng |
|---|---|
| MES đẩy file vào ứng dụng | Bật `PoServer`; gửi `POST /api/po` tới listener của TanTienQR, mặc định cổng 8085. |
| Ứng dụng tải từ server PO | Cấu hình `Po:BaseUrl`; bấm **Tải PO từ server** để lấy danh sách và mã qua GET rồi merge vào DB local. |

Listener `ClassLibraryMT.PoServer` nhận file và cung cấp health; nó không phục vụ API tải danh sách/mã hay nhận kích hoạt. Server tại `Po:BaseUrl` phải cung cấp các API đó. Công cụ `tools/PoServer` là server thử nghiệm riêng, mặc định cổng 5080. `ClassLibraryMT.MesSimulator` là client gửi file tới listener; xem [MES-SIMULATOR.md](MES-SIMULATOR.md).

File nhập chỉ có đúng một cột/thuộc tính `CodeContent`: CSV có header, JSON là mảng object, hoặc XLSX một cột văn bản. Metadata `poName`, `orderQty` và mô tả tùy chọn đi riêng qua query hoặc field multipart. `orderQty` bằng số bản ghi của lần upload **trước lọc trùng**. Ví dụ đầy đủ và các trường hợp bị từ chối có trong hướng dẫn API.

Nhập lại vào PO chưa bắt đầu chỉ bổ sung mã mới theo nguyên chuỗi; mã và trạng thái đã lưu được giữ nguyên. Cùng CodeContent trong một PO khác vẫn được lưu độc lập. Khi còn PO đang chạy hoặc dở dang, thao tác nhận/tải PO mới bị chặn. PO hoàn tất, hủy hoặc đóng không được mở lại bằng upload.

## Bắt đầu và đối chiếu mã

Danh sách và tiến độ trên giao diện được đọc từ DB local. Khi bắt đầu, service nạp một `Dictionary<string, state>` cho đúng PO đang chạy; key là CodeContent đầy đủ, value chứa trạng thái đã quét, đã kích hoạt hoặc Pass đang chờ đối soát. Khi PO kết thúc, cache đang chạy được giải phóng.

| Trường hợp | Kết quả sản xuất |
|---|---|
| Mã thuộc PO, chưa quét và chưa kích hoạt | Thử ghi Pass xuống PLC; chỉ xác nhận Pass sau khi trạng thái PO đã commit. |
| Mã đã quét **hoặc đã kích hoạt** trong PO | `Duplicate`, gửi Fail. Kể cả mã có `ActivatedUtc` nhưng chưa có `ScannedUtc`. |
| Mã không thuộc PO đang chạy | `InvalidForOrder`, gửi Fail. |
| Tín hiệu `NoRead` hoặc `FAIL`, không phân biệt hoa thường | `NoRead`, gửi Fail khi luồng nhận đang hoạt động. |
| Mã thông thường khi chưa chạy PO | `NoProductionOrder`, không ghi kết quả PLC. |
| Service đang halt hoặc cổng nhận bị khóa | Không tiếp tục phát Pass; cần xử lý nguyên nhân được ghi trên giao diện/log. |

Địa chỉ ghi kết quả lấy từ `Plc:ResultAddress`, mặc định `D12`: Pass là Int16=1, Fail là Int16=0. Những địa chỉ mặc định phải khớp mapping của máy thực tế. CodeContent được lưu vào lịch sử cùng PO của lần xử lý, kể cả khi mã cuối vừa làm PO tự hoàn tất.

## PLC ACK và lưu trạng thái

Luồng camera dùng `ProductionOrderService.ProcessCodeAsync` giữ khóa thay đổi PO xuyên suốt đối chiếu → PLC ACK → commit. Trước khi phát Pass, DB lưu `PendingPassUtc` để khởi động lại vẫn nhận biết một thao tác chưa xác định kết quả.

- PLC từ chối chắc chắn hoặc thao tác không được gửi: mã chưa được tiêu thụ, số đã quét PO không tăng; ý định Pass được xóa khi xác nhận được việc xóa.
- PLC xác nhận thành công: `ScannedUtc` được commit, cờ pending được xóa, rồi cache và số đã quét PO được cập nhật. Quét lại mã đó là Duplicate, kể cả sau khởi động lại.
- Mất ACK hoặc phản hồi không xác định sau khi có thể đã gửi: giữ pending và halt để tránh phát Pass lại. Không tự gửi lại Pass để thử lấy ACK.
- PLC đã nhận Pass nhưng commit trạng thái quét lỗi: giữ dấu đối soát, khóa nhận mã và Ready=0. Khởi động lại không tự mở mã đó để chạy tiếp.

Nếu **mã cuối đã Pass và commit thành công nhưng bước hoàn tất PO lỗi**, kết quả quét của mã vẫn là Pass. Service ghi halt bền vững, ứng dụng đóng nhận mã và đặt Ready=0 để đối soát lỗi hoàn tất riêng. Không đổi mã đã commit thành Fail hoặc phát lại Pass. Không tự xóa dấu halt/pending bằng cách khởi động lại hay đổi cấu hình; cần đối soát PLC và DB trước khi phục hồi vận hành.

## Hoàn tất, hủy và khôi phục

Khi số mã đã quét đạt tổng số mã của PO, service tự hoàn tất và giữ thông tin lần chạy cuối cho giao diện. Nếu hủy trước khi đủ, PO được đánh dấu `Cancelled`; các dòng mã và lịch sử đã lưu vẫn được giữ. Hủy một PO khác không xóa cache của PO đang chạy.

Tiến độ PO tính mỗi mã đã quét hoặc đã kích hoạt một lần, kể cả mã được nhập với trạng thái kích hoạt sẵn; không tự tạo thời điểm quét cho mã chỉ có trạng thái kích hoạt. Nếu toàn bộ mã đã được xử lý khi bắt đầu hoặc khôi phục, PO hoàn tất ngay mà không ghi Pass xuống PLC. Dấu pending/halt vẫn ưu tiên chặn hoàn tất tự động để đối soát.

Khi khởi động, ứng dụng tìm lần chạy dở dang và nạp lại trạng thái từ DB. PO có pending Pass hoặc dấu halt không được tự tiếp tục sản xuất. Trạng thái đã quét và kích hoạt không phụ thuộc bộ nhớ của phiên ứng dụng trước.

Các số lớn trên màn Thông tin lấy từ PLC. Tổng mã/đã quét/tiến độ của PO lấy từ bản ghi trong DB từng PO; reset bộ đếm PLC không xóa trạng thái mã PO. Xem [hướng dẫn bộ đếm PLC](application-codecontent.md).

## Kích hoạt sau Pass

Worker chỉ gửi các CodeContent có `ScannedUtc` và chưa có `ActivatedUtc`. Trạng thái chờ nằm trong DB, nên worker tiếp tục quét các PO local sau khi khởi động lại, **kể cả PO đã hoàn tất hoặc hủy**. Mã cuối không bị bỏ lại chỉ vì PO tự hoàn tất trước tick gửi tiếp theo.

Lỗi mạng hoặc server từ chối giữ mã ở trạng thái chờ để thử ở chu kỳ sau. Khi server xác nhận, ACK được ghi vào đúng DB của PO nguồn. Nếu PO đang chạy đã đổi trong lúc chờ mạng, ACK không được áp vào cache PO mới. Kích hoạt của một PO không chặn mã giống nội dung trong PO khác.

```json
{
  "Po": {
    "BaseUrl": "http://127.0.0.1:5080",
    "DbRoot": "logs/po",
    "ActivationIntervalMs": 2500,
    "AutoActivate": true
  }
}
```

`ActivationIntervalMs` phải lớn hơn 2000. Khi `AutoActivate=false`, worker không gửi định kỳ. Việc bật/tắt listener nhận file `PoServer` không quyết định worker gửi tới server tại `Po:BaseUrl`; các endpoint này có vai trò riêng. Cần cấu hình đúng server và khởi động lại sau thay đổi. `Po:BaseUrl` phải trỏ tới server hỗ trợ tải/kích hoạt, không trỏ vào listener chỉ nhận file ở cổng 8085.

## Dữ liệu và kiểm thử

`Po:DbRoot` tương đối được tính từ thư mục ứng dụng. Mỗi PO có file `{DbRoot}/{yyyy-MM}/{OrderNo}.db`; lịch sử chạy nằm ở `{DbRoot}/po-history.db`. Bảng `PoCodes` giữ cột vật lý `Code`, `Gtin`, `Serial` để tương thích DB cũ: `Code` lưu nguyên CodeContent; các dòng mới để trống hai cột legacy. `ScannedUtc`, `ActivatedUtc`, `PendingPassUtc` và metadata halt/trạng thái PO phục vụ khôi phục. Lịch sử quét toàn ứng dụng ở DB được cấu hình bởi `Scans:DbPath`.

Các harness dùng DB tạm và thiết bị giả; chạy tuần tự để tránh ghi đồng thời vào cùng thư mục build:

```powershell
dotnet run --project tools/PoTests/PoTests.csproj
dotnet run --project tools/PoFlowTests/PoFlowTests.csproj
dotnet run --project tools/PoServerTests/PoServerTests.csproj
dotnet run --project tools/PoServerTests/e2e/PoServerE2E.csproj
dotnet run --project tools/CameraHotApplyTests/CameraHotApplyTests.csproj
```

Việc kiểm thử bằng PLC giả xác minh thứ tự lệnh và trạng thái phần mềm. Mapping thanh ghi và hành vi ladder của dây chuyền cần được đối chiếu trên thiết bị thực tế.
