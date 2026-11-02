# Kiểm tra Muse 0.9.2

Mở `dist/Muse/Muse.exe`, kiểm tra phiên bản **0.9.2**. Giữ nguyên dữ liệu đăng
nhập và hội thoại; không xóa LocalAppData hay lấy mã đăng nhập mới để xử lý lỗi đọc chat.

## Cửa sổ CMD bật mỗi phút

Nguyên nhân đã xác định là tác vụ Windows `MuseSwapRelease` cũ gọi trực tiếp
PowerShell mỗi phút và không tự gỡ khi hết việc. Tác vụ thừa trên máy phát triển
đã được gỡ sau khi xác nhận không có bản cập nhật chờ.

Bộ đóng gói mới dùng launcher Windows không có console. Khi app đang chạy,
bản đã kiểm chứng vẫn chờ an toàn; sau khi đóng app và đổi bản xong, tác vụ tự
gỡ. Nếu không còn bản chờ, tác vụ cũng tự gỡ. Không đóng cưỡng bức Muse và
không xóa các tác vụ không thuộc thư mục dự án này.

## Tiếp tục chat cũ

1. Bật hai tài khoản Muse, dùng một tài khoản chat rồi đóng/mở lại app.
   Nhắn tiếp trong chat đó: app phải dùng đúng tài khoản gốc, giữ URL chat,
   không chọn tài khoản khác chỉ vì đang rảnh hoặc ít dùng hơn.
2. Nếu tài khoản gốc bị tắt/xóa/hết phiên đăng nhập: app báo đúng nguyên nhân,
   giữ lịch sử; không tự gửi qua tài khoản khác. Bật/khôi phục tài khoản gốc
   rồi tiếp tục. Tài khoản gốc bận thì chờ tài khoản đó, không chuyển chat.
3. Trong chat có đoạn code/lời giải thích chứa `Connecting...`, `usage limit`
   hoặc `out of credits`: những chữ này không được làm app hiểu là mất kết nối
   hoặc hết lượt. HTTP 429 và thông báo giới hạn thật vẫn được tôn trọng.
4. Nếu web vẫn đang tạo phản hồi trước, app không gửi thêm tin chen vào. Đợi
   web xong rồi nhắn lại một lần. App không tự gửi lại, đổi tài khoản hoặc mở
   cuộc trò chuyện mới khi gặp lỗi.
5. Câu trả lời phải gắn với mã yêu cầu hiện tại; tin cũ bị sắp xếp lại không
   được coi là câu trả lời mới. Hỗ trợ thêm dấu vai trò user/assistant khi web
   đổi cách bố trí, giữ nguyên yêu cầu xác định nguồn phản hồi.

Không thể bảo đảm khôi phục một chat đã bị xóa hoặc mất quyền trên Muse web;
app sẽ báo riêng tình huống đó thay vì gộp thành lỗi mạng. Chi tiết kỹ thuật
vẫn nằm trong Terminal. Các bài kiểm thử dùng tài khoản/trang web giả lập và
Chromium ẩn, không gửi lượt chat lên tài khoản Muse thật.
