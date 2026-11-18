# Plugin giả lập MES gửi PO

`ClassLibraryMT.MesSimulator` sinh một Production Order mẫu và gửi bằng
`multipart/form-data` tới HTTP server nhận PO có sẵn (`POST /api/po`). Client
upload file CSV với các field `poName`, `orderQty`, `gtin`, `productName` và
`file`, tương tự một MES bên ngoài.

Mặc định plugin tắt. Cấu hình trong `TanTienQR/appsettings.json`:

```json
"PoServer": {
  "Enabled": true,
  "Port": 8085,
  "BindAddress": "127.0.0.1",
  "ApiKey": ""
},
"MesSimulator": {
  "Enabled": true,
  "TargetUrl": "http://127.0.0.1:8085",
  "ApiKey": "",
  "IntervalSeconds": 30,
  "OrderQty": 10,
  "Gtin": "08934567890123",
  "PoPrefix": "MES-TEST",
  "CustomFilePath": "",
  "CustomPoName": "",
  "TimeoutSeconds": 30
}
```

Bật cả server nhận PO và simulator, sau đó khởi động lại ứng dụng. Simulator
gửi một PO ngay khi khởi động và tiếp tục gửi theo `IntervalSeconds`. Tên PO
và serial được sinh mới ở mỗi lần gửi. Tắt `MesSimulator:Enabled` để ngừng
phát sinh PO. Nếu cấu hình `PoServer:ApiKey`, đặt cùng giá trị ở
`MesSimulator:ApiKey`.

Để upload file PO có sẵn, đặt `CustomFilePath` tới file `.csv`, `.json`,
`.xlsx` hoặc `.xls`. Đường dẫn tương đối tính từ thư mục chạy ứng dụng; có
thể điền `CustomPoName` riêng, nếu để trống tên PO lấy từ tên file. `OrderQty`
phải khớp hoặc nhỏ hơn số mã đọc được trong file, và `Gtin` được dùng cho các
serial không kèm GTIN. File custom chỉ được upload một lần khi ứng dụng khởi
động để tránh gửi lặp cùng PO. Xoá `CustomFilePath` để quay về chế độ tự sinh
PO định kỳ.

Thư viện có thể đăng ký trong host .NET khác bằng
`services.AddMesSimulator(configuration)`. `MesSimulatorWorker.SendOneAsync`
và `MesSimulatorClient.UploadTestOrderAsync` cũng có thể gọi trực tiếp khi cần
kiểm thử theo yêu cầu thay vì chạy theo chu kỳ.

Phía TanTienQR cho phép gọi upload nhiều lần vào PO chưa bắt đầu: chỉ nhận
mã mới và trả số mã bỏ qua trong `ignoredDuplicates`. Upload lại file cũ
trả `200` với `accepted: 0`. PO đã huỷ/hoàn tất/đóng bị từ chối `409`;
nếu máy còn bất kỳ PO đang chạy hoặc dở dang thì mọi lượt upload đều bị chặn.
Chế độ file custom của worker vẫn gửi một lần lúc khởi động; có thể gọi
`UploadExistingFileAsync` nhiều lần khi cần thử lại hoặc bổ sung mã.
