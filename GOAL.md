# Turbo và goal — Muse 0.9.1

1. Chọn dự án, vào Cài đặt và bật **Turbo**. Muse tự sửa mã, cập nhật cấu hình
   build, chạy lệnh và quản lý tiến trình nền. Tệp bí mật, AGENTS.md và CI còn
   bước duyệt riêng. Chế độ Chỉ xem vẫn chặn thay đổi và lệnh.
2. Gửi `/goal Hoàn thiện ứng dụng quản lý công việc, kiểm thử thêm/sửa/xóa và lưu dữ liệu`.
   Nêu rõ các chức năng và điều kiện đạt để Muse lập danh sách kiểm chứng phù hợp.
3. Muse đọc dự án, xây dựng, chạy các bộ test liên quan, vá lỗi và kiểm tra lại.
   Vòng xử lý không có giới hạn 30 bước mặc định. AI vẫn phải tuân theo quyền,
   kết quả thực tế và các điều kiện dừng, không được tự tuyên bố mọi lỗi đã hết.
4. Khi cần thử giao diện thật, Muse khởi chạy ứng dụng trong dự án. Cửa sổ
   kiểm thử được tự chọn qua giao diện quản lý quyền ở thao tác màn hình đầu tiên
   (foreground, hoặc trên cùng theo thứ tự Z-order), chỉ mở hộp chọn khi không
   tự chọn được. Chế độ Chỉ xem và phím dừng vẫn được kiểm tra trước thao tác.
   Quyền này dùng xuyên suốt lượt goal,
   gồm các hộp thoại thuộc cửa sổ đó. Muốn dùng cửa sổ khác với cửa sổ đã tự chọn, bấm **Màn hình** và chọn thủ công.
   Mật khẩu và ứng dụng yêu cầu quyền quản trị không được thao tác.
5. Tab **Màn hình** hiển thị ảnh tại máy tối đa khoảng mỗi 1,5 giây khi lượt
   đang chạy và tab đang mở. Không có hàng đợi ảnh hoặc lưu video. Ảnh xem
   trực tiếp không gửi lên web; AI chỉ nhận ảnh qua lệnh chụp màn hình.
   Nếu cửa sổ khác ở phía trước/che cửa sổ kiểm thử thì ảnh tạm dừng, không
   tự giành focus. Bỏ chọn Xem trực tiếp để ngừng ảnh xem trước.

Goal chỉ được đánh dấu hoàn tất khi có **test thực sự, exit_code=0**, không có
test lỗi/bỏ qua/hủy được phát hiện trong các lượt kiểm chứng, không còn lệnh
test đã chạy mà chưa xác minh, cùng thao tác giao diện, `computer_assert`
thành công và ảnh chụp sau thao tác cuối. Sau sửa mã phải kiểm chứng lại.
Chạy test có filter không thay thế bộ test đầy đủ. Kết quả đọc trong file,
log lịch sử, `echo`, tiến trình mới khởi chạy hoặc câu nói của AI không phải
bằng chứng đã chạy test. Bộ đọc nhận các tổng kết phổ biến của .NET, pytest,
Node/Jest/Vitest, Rust, Go và Maven. Runner tùy biến không có tổng kết nhận
diện được sẽ chưa được xác nhận, thay vì đoán là pass.

Muse còn phụ thuộc vào khả năng lập kế hoạch của mô hình: cổng kiểm chứng
không tự chứng minh đã tìm đủ mọi bộ test hay mọi lỗi của ứng dụng. Xem kết
quả cuối và danh sách chức năng đã thử trước khi phát hành.

- `/goal`: tiếp tục mục tiêu đã lưu, kiểm chứng lại từ đầu để tránh dùng kết quả cũ.
- Khi Muse hỏi thông tin còn thiếu, trả lời ngay trong chat để tiếp tục đúng goal;
  không cần gõ lại mục tiêu. Câu cảm ơn/hỏi cách dùng không tự kích hoạt việc cũ.
- Nếu goal đang tạm dừng, có thể gửi `tiếp tục đi`; goal đã hoàn tất sẽ không tự chạy lại.
- `/goal status`: xem mục tiêu và kết quả gần nhất tại máy, không gọi Muse web.
- `/goal stop`: tạm dừng mục tiêu, giữ thay đổi. Khi đang chạy hãy dùng **Dừng**.
- `/goal <mục tiêu mới>`: thay mục tiêu trong cuộc chat hiện tại.

Nút **Dừng** ngắt cả vòng goal và thu hồi quyền màn hình. **Esc** hoạt động
khi Muse nhận phím, hoặc toàn cục trong lúc điều khiển cửa sổ đã cho phép.
Nếu thiếu dữ liệu/quyền, web không phản hồi, rate limit hoặc thao tác lặp lại
không tiến triển, goal tạm dừng và giữ mục tiêu. Không tự spam chat, đăng nhập
lại hay đổi cuộc trò chuyện. Trả lời thông tin còn thiếu để tiếp tục goal đang chờ;
với lỗi kết nối hoặc bị dừng, gửi `/goal` khi muốn thử lại. Sau khởi động lại hoặc
lượt mới, quyền cửa sổ phải được cấp lại qua giao diện quản lý quyền; không lưu quyền
điều khiển màn hình vào lịch sử. Đăng nhập và dữ liệu cá nhân vẫn được giữ.

Ở CLI có `/goal`, `/goal status`, `/goal stop` và `/yes` để tự duyệt lệnh.
CLI chưa cung cấp quyền màn hình, nên goal có điều kiện thử giao diện sẽ cần
tiếp tục trong ứng dụng desktop; không báo hoàn tất chỉ với test terminal.
