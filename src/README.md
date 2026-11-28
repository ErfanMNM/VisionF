# .NET libraries

Các thư viện dùng chung của VisionF nằm trong thư mục này. Mở ../VisionF.slnx từ root repository để tải giải pháp.

- Tạo thư viện dùng chung mới tại src/ClassLibraryMT.<Module>/.
- Dùng ProjectReference tương đối giữa các project trong src/.
- Test, tool và host độc lập được giữ trong ../tools/.
- Khi thêm project mới, cập nhật solution root, đường dẫn build/phát hành và docs.
