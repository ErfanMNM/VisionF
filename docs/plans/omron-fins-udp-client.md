# Kế hoạch viết client đọc biến D Omron qua FINS/UDP

> Nguồn: plan Cursor `.cursor/plans/omron_fins_udp_client_78115e4f.plan.md`.
> File này nằm trong `docs/` để commit lên git cho cả team cùng theo dõi và code tiếp.

## 1. Vì sao viết lại thay vì copy file đính kèm

- File `OmronPLC.cs` đính kèm là WinForms `Component` (`InitializeComponent()` nằm ở file designer không được gửi), dùng `BackgroundWorker` + `Thread.Sleep` vòng lặp ghi heartbeat, giữ `OmronFinsUdp` của HslCommunication.
- HslCommunication là dependency nặng, gần như chắc chắn không trim/AOT-safe, sẽ phá `IsAotCompatible=true` của `ClassLibraryMT/ClassLibraryMT.csproj`.
- FINS/UDP đủ đơn giản để tự triển khai bằng `System.Net.Sockets.UdpClient` thuần, không reflection, hợp với AOT.

## 2. Khung FINS/UDP sẽ triển khai

- Transport UDP tới `IP:Port` (mặc định 9600 như `PLC_PORT` trong file cũ), gọi đồng bộ `Send`/`Receive` với timeout (mặc định 1000ms).
- Header 10 byte mỗi request: `ICF=0x80, RSV=0x00, GCT=0x02, DNA=0x00, DA1=node PLC, DA2=0x00, SNA=0x00, SA1=node PC, SA2=0x00, SID=tăng dần`.
- Giữ mặc định tương đương file cũ (`DA1=0, SA1=1, GCT=2`) nhưng cho chỉnh qua constructor/options vì node PLC thực tế thường là octet cuối IP.
- Lệnh Memory Area Read `MRC=0x01 SRC=0x01` cho vùng D word: `area=0x82`, địa chỉ 2 byte big-endian + byte `0x00`, số word 2 byte big-endian. Ví dụ đọc 1 word D100 gửi sau header: `01 01 82 00 64 00 00 01`.
- Lệnh Memory Area Write `MRC=0x01 SRC=0x02` cùng cấu trúc, kèm dữ liệu mỗi word 2 byte big-endian.
- Response: khớp `SID` và echo `MRC/SRC`, đọc end code 2 byte (`00 00` là thành công), dữ liệu word về đổi big-endian sang số bằng `BinaryPrimitives`.
- Timeout mỗi request + retry có giới hạn, map end code hay gặp (`1101` sai area, `1103` địa chỉ vượt phạm vi, `0401` lệnh không hỗ trợ, `0001` service bị hủy) thành mã lỗi trong result.
- Không làm heartbeat/Ready: bỏ hoàn toàn vòng lặp `WK_Update` cũ, không ghi cyclic D16.

## 3. API mới (đồng bộ, khai báo class rồi gọi)

- Project mới `ClassLibraryMT.Plc` (net10.0, không package ngoài, AOT-safe), solution `VisionF.slnx` tham chiếu khi cần. `ClassLibraryMT` hiện tại không đụng tới để giữ nguyên AOT.
- Cách dùng đúng như yêu cầu:

```csharp
var plc = new OmronPlc("192.168.1.10", 9600);
PlcResult rs = plc.WriteInt32("D12", 1);
PlcResult<int> rr = plc.ReadInt32("D12");
if (rr.IsSuccess) { int v = rr.Value; }
Console.WriteLine($"mat {rr.ProcessTime.TotalMilliseconds:F1} ms");
```

- `OmronPlc` là class thường (không `Component`, không DI bắt buộc): constructor nhận `ip`, `port`, `remoteNode`, `localNode`, `timeoutMs`, `retries`.
- Result kiểu Hsl-like, cả hai đều có `ProcessTime`:
  - `PlcResult { bool IsSuccess, string Message, string ErrorCode, TimeSpan ProcessTime }` cho Write.
  - `PlcResult<T> { bool IsSuccess, string Message, string ErrorCode, T? Value, TimeSpan ProcessTime }` cho Read.
  - `ErrorCode` là end code FINS dạng hex (`0000`, `1101`, `1103`...) hoặc `TIMEOUT` khi không có response, hoặc `ARG` khi sai định dạng địa chỉ.
  - `ProcessTime` đo bằng `Stopwatch` từ lúc bắt đầu gọi tới khi có kết quả cuối (gồm cả retry), kể cả khi lỗi vẫn có giá trị để biết lệnh treo bao lâu.
- Đủ kiểu đọc/ghi vùng D, thứ tự word CDAB như `ByteTransform.DataFormat` trong file cũ:
  - Bit/word: `ReadBool("D12.05")` / `WriteBool("D12.05", true)` (đọc word D12 rồi tách bit 5; ghi bằng read-modify-write một word).
  - Số nguyên: `ReadInt16`/`WriteInt16` (`short`, 1 word), `ReadUInt16`/`WriteUInt16` (`ushort`, 1 word), `ReadInt32`/`WriteInt32` (`int`, 2 word), `ReadUInt32`/`WriteUInt32` (`uint`, 2 word), `ReadInt64`/`WriteInt64` (`long`, 4 word), `ReadUInt64`/`WriteUInt64` (`ulong`, 4 word).
  - Số thực: `ReadFloat`/`WriteFloat` (`float`, 2 word), `ReadDouble`/`WriteDouble` (`double`, 4 word).
  - Chuỗi: `ReadString("D100", length)` / `WriteString("D100", value, length)` (ASCII, 2 ký tự/word, byte đảo theo kiểu `IsStringReverseByteWord=true` của file cũ, tự pad `0x00` và cắt đúng độ dài).
- Đủ mảng kiểu Hsl (đọc/ghi nhiều phần tử liên tiếp từ một địa chỉ bắt đầu):
  - `ReadInt16Array` / `WriteInt16Array` (`short[]`), `ReadUInt16Array` / `WriteUInt16Array` (`ushort[]`).
  - `ReadInt32Array` / `WriteInt32Array` (`int[]`, mỗi phần tử 2 word), `ReadUInt32Array` / `WriteUInt32Array` (`uint[]`).
  - `ReadInt64Array` / `WriteInt64Array` (`long[]`, mỗi phần tử 4 word), `ReadFloatArray` / `WriteFloatArray` (`float[]`, 2 word/phần tử), `ReadDoubleArray` / `WriteDoubleArray` (`double[]`, 4 word/phần tử).
  - `ReadBoolArray("D100.00", length)` / `WriteBoolArray("D100.00", bool[])` (đọc word bao phủ rồi tách/gộp bit).
  - Mảng word thô: `ReadWords("D100", wordCount)` / `WriteWords("D100", short[])` cho trường hợp đặc biệt.
  - Quy ước như Hsl: `length` là số phần tử (không phải số word); tự chia nhỏ request khi vượt giới hạn word/frame FINS rồi gộp kết quả, `ProcessTime` đo toàn bộ; tràn vùng D hoặc `length <= 0` trả `IsSuccess=false` mã `ARG`.
- Parse địa chỉ chấp nhận `D16`, `DM16`, `D0016`, cộng `D12.00`–`D12.15` cho bool, kiểm tra 0-32767 và kiểm tra tràn khi đọc nhiều word, sai định dạng trả `IsSuccess=false` thay vì throw.
- Không event trạng thái, không thread nền, không heartbeat.

## 4. Kiểm tra

- Build `ClassLibraryMT.Plc` Debug và Release, xác nhận không warning `IL2026`/`IL3050`.
- Test không cần PLC thật: viết fake UDP responder trả frame FINS mẫu để kiểm tra parse request/response, end code lỗi, kiểm tra `IsSuccess`/`ErrorCode`/`Value`/`ProcessTime` cho từng kiểu (bool, int16/uint16, int32/uint32, int64, float/double, string, array).
- Test tay với PLC thật: `ReadInt32("D16")` so với giá trị trên CX-Programmer, `WriteInt32` một DM test rồi đọc lại, đối chiếu `ProcessTime` với timeout đã cấu hình.
- `DemoQR` và `T8T` chưa đấu nối trong bước này, chỉ xong thư viện dùng được cho solution đang mở.

## 5. Checklist theo dõi tiến độ (đánh dấu khi xong)

- [ ] 5.1 Tạo project `ClassLibraryMT.Plc` (net10.0, `IsAotCompatible=true`, không package ngoài)
- [ ] 5.2 Thêm project vào `VisionF.slnx`
- [ ] 5.3 Viết `PlcResult` / `PlcResult<T>` gồm `IsSuccess`, `Message`, `ErrorCode`, `Value`, `ProcessTime`
- [ ] 5.4 Viết parse địa chỉ D (`D16`, `DM16`, `D0016`, `D12.05`), chặn 0-32767 và tràn vùng
- [ ] 5.5 Viết FINS header 10 byte (ICF/GCT/DNA/DA1/SNA/SA1/SID tăng dần)
- [ ] 5.6 Viết lệnh Read `01 01` và Write `01 02` vùng D word (`0x82`, big-endian)
- [ ] 5.7 Viết UDP transport đồng bộ `Send`/`Receive` + timeout 1000ms + retry
- [ ] 5.8 Kiểm tra response: khớp `SID`, echo `MRC/SRC`, đọc end code (`0000`, `1101`, `1103`, `0401`, `0001`, `TIMEOUT`, `ARG`)
- [ ] 5.9 Viết Read/Write đơn: `Bool`, `Int16`/`UInt16`, `Int32`/`UInt32`, `Int64`/`UInt64`, `Float`/`Double`, `String` (CDAB + đảo byte chuỗi)
- [ ] 5.10 Viết Read/Write mảng kiểu Hsl: `Int16Array`, `UInt16Array`, `Int32Array`, `UInt32Array`, `Int64Array`, `FloatArray`, `DoubleArray`, `BoolArray`, `Words`
- [ ] 5.11 Xử lý chia nhỏ request mảng khi vượt giới hạn word/frame, gộp kết quả, `ProcessTime` đo toàn bộ
- [ ] 5.12 Build Debug + Release, xác nhận không warning `IL2026`/`IL3050`
- [ ] 5.13 Test fake UDP responder: đúng/sai `SID`, end code lỗi, đủ kiểu dữ liệu + mảng
- [ ] 5.14 Test tay PLC thật: đọc `D16` đối chiếu CX-Programmer, ghi/đọc lại 1 DM test
- [ ] 5.15 Xác nhận `DemoQR`/`T8T` chưa đấu nối, thư viện độc lập dùng được cho solution đang mở
