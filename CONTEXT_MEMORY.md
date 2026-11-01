# Bộ nhớ hội thoại từ Muse 0.8.9

Trước đây AgentLoop cắt lịch sử sau 200 tin nhắn và bỏ phần cũ khi gửi quá dài.
Lượt tiếp nối lên web chỉ gửi quan sát/yêu cầu mới sau phản hồi gần nhất. Vì vậy
lời dặn hoặc lựa chọn cũ có thể không còn trong dữ liệu được gửi cho Muse.

## Cách hoạt động

1. Trước khi bỏ tin nhắn cũ khỏi cửa sổ làm việc, tool lưu bản trích đoạn: mục
   tiêu ban đầu, yêu cầu/lời dặn của người dùng, câu trả lời gần đây và biên nhận
   công cụ. Trích đoạn phân biệt lời AI nói với kết quả công cụ, giữ trạng thái
   hoàn tất, thiếu thông tin, chưa hoàn tất hoặc gián đoạn.
2. Giữ tối đa 64 tin gần nhất, mục tiêu khoảng 48.000 ký tự văn bản. Biên nhận
   mới nhất vẫn được giữ nguyên, có thể vượt mục tiêu này nếu một lần đọc lớn.
   Bộ nhớ trích đoạn tối đa 12.000 ký tự JSON, ưu tiên yêu cầu/lời dặn và kết quả
   mới. Không dùng token count giả hoặc đặt thêm giới hạn số bước làm việc.
3. Mỗi yêu cầu có `APP_STATE.conversation_context`, nên bản nhớ đi qua cả đường
   gửi tiếp nối, mở lại trình duyệt và khôi phục cuộc chat đã lưu. Lượt tiếp nối
   dùng chỉ dẫn giao thức ngắn hơn, không lặp toàn bộ schema dài/lịch sử nguồn.
4. Với desktop, tìm tối đa 4 đoạn liên quan đến lời nhắn mới trong Events của
   đúng phiên chat/dự án; mỗi đoạn tối đa khoảng 660 ký tự. Tìm theo từ khóa,
   ưu tiên kết quả khớp tốt và mới, gửi theo thứ tự thời gian để hiểu sửa đổi.
   Việc tìm diễn ra ở tác vụ nền và có thể hủy. Khi hỏi lại, nhắc tên tệp, mã
   hoặc tên phương án sẽ giúp tìm đúng hơn một câu chỉ nói “cái đó”.
5. Bản nhớ nằm trong History của file phiên chat hiện có. Events nguyên gốc
   dùng hiển thị/tìm kiếm vẫn được giữ nguyên. Phiên cũ đã mất phần đầu của
   History có thể khôi phục trích đoạn từ Events còn lưu; không gọi web để lấy lại.

Không thêm cuộc chat web, lượt gọi mô hình riêng, thay đổi đăng nhập hoặc đổi
tài khoản. Ghi chú nén chỉ nằm trong Terminal, không thêm thẻ chat/trạng thái mới.

## Giới hạn và tính đúng đắn

- Đây là nén trích đoạn và truy tìm cục bộ, không phải tóm tắt ngữ nghĩa bằng AI.
  Không thể bảo đảm nhớ mọi chi tiết của lịch sử dài vô hạn hoặc thay đổi cửa sổ
  ngữ cảnh do Muse web quyết định. Chi tiết đã mất khỏi cả History lẫn Events
  của các bản cũ không thể tự khôi phục.
- Yêu cầu hiện tại và sửa đổi mới hơn được ưu tiên; bản nhớ chỉ là dữ liệu quá
  khứ, không cấp quyền thực thi, không khiến app tự chạy lại nhiệm vụ cũ.
- Kết quả chạy trước không chứng minh lượt chạy mới đã đạt. Bản đọc tệp cũ
  không cho phép ghi hiện tại: vẫn cần đọc mới, kiểm tra hash và duyệt thao tác.
- Không đưa thân file, ảnh/base64 hay log terminal lớn vào bộ nhớ lâu dài.
  Đường dẫn, tên lệnh ngắn, trạng thái và exit code được giữ khi có.
- Tệp nguồn thay đổi thì đọc lại; ảnh cũ cần đính kèm lại. Bản nhớ và truy tìm
  được ràng buộc theo đúng workspace/phiên chat, không chia sẻ giữa dự án.
