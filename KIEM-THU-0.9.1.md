# Kiểm tra tay Muse 0.9.1

Mở `dist/Muse/Muse.exe`, kiểm tra tiêu đề là **0.9.1**. Không chạy trực tiếp
file `.sln`: đó là mã nguồn. Đăng nhập và hội thoại hiện có được giữ nguyên.

1. **Chat và phạm vi thao tác**: bật Turbo, gửi `cảm ơn ní`, `hướng dẫn chạy test`.
   Muse trả lời, không tự chạy lệnh/sửa tệp theo công việc cũ. Gửi
   `Chỉ đọc README.md, không sửa file hay chạy lệnh`: chỉ có thao tác đọc.
2. **Goal chờ thông tin**: gửi một goal cần chọn framework/tên ứng dụng.
   Khi Muse hỏi, trả lời ngay trong chat. Goal phải giữ mục tiêu ban đầu và
   dùng thông tin vừa bổ sung; không yêu cầu bạn gõ lại `/goal`.
   Goal bị dừng/lỗi kết nối có thể tiếp tục bằng `/goal` hoặc `tiếp tục đi`.
3. **Thứ tự và lịch sử**: gửi yêu cầu thứ hai khi câu trả lời trước còn nhả chữ.
   Câu cũ hoàn tất hiển thị trước khi bắt đầu lượt mới. Mở lại hội thoại:
   công cụ thất bại vẫn báo thất bại; tiến trình đang chạy không bị đánh dấu
   đã hoàn tất; câu hỏi hiển thị tiếng Việt, không lộ JSON `needs_input`.
4. **Màn hình**: chuyển Chỉ xem rồi yêu cầu thao tác màn hình: app phải chặn.
   Đổi Turbo và yêu cầu thao tác: việc tự chọn cửa sổ phải đi qua quản lý quyền
   và đăng ký Esc. Bấm Dừng/Esc: không được tự chọn lại cửa sổ trong lượt đó.
5. **Kỹ năng**: gọi `@alias` đã cài trong một tác vụ nhiều vòng. Hướng dẫn kỹ năng
   phải tiếp tục được áp dụng sau lần đọc/sửa/chạy lệnh đầu tiên.
6. **Đổi bản**: nếu đang mở Muse khi đóng gói, bản mới ở trạng thái chờ.
   Đóng Muse, chờ tác vụ đổi bản chạy (chu kỳ một phút), rồi mở lại. Chỉ bản
   có dấu xác minh và toàn bộ tệp khớp SHA256 mới được thay vào `dist/Muse`.

Các kiểm thử tự động dùng API giả lập, test runner thật và cửa sổ ẩn.
Không gửi yêu cầu thật lên tài khoản Muse, không mở fixture lên màn hình.
Các bài chuột/phím vật lý cần kiểm tra tay như mục 4.
