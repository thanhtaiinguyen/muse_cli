# Điều khiển cửa sổ Windows

Lớp `DesktopComputer` triển khai `IComputerTools` của agent, dùng UI Automation
cho cấu trúc/giá trị/control patterns và Win32 SendInput cho chuột/phím.
Mọi lời gọi UIA chạy trên MTA worker ngoài luồng UI, có timeout và kiểm tra
generation để thao tác cũ không tiếp tục sau khi quyền bị thu hồi.

Frontend xin phép chọn một cửa sổ cho mỗi lượt. Chỉ handle và process đã chọn
được dùng; PID của chính host Muse bị loại để không điều khiển hộp duyệt của
mình. Đăng ký Esc toàn cục trong lượt, hủy và bỏ quyền khi kết thúc hoặc đổi
dự án. Không thao tác password controls. Screenshot dùng vùng cửa sổ đang ở
foreground, giữ snapshot id/bounds/scale; click từ ảnh cũ, ngoài ảnh hoặc sau
khi cửa sổ di chuyển bị từ chối. UIA ids hết hạn sau 180 giây; ảnh sau 120 giây.

Kết quả action chỉ xác nhận đã gửi thao tác. `computer_assert` quan sát tên,
giá trị, checkbox hoặc trạng thái chọn/enabled với thời gian chờ hữu hạn.
Agent xóa bằng chứng assert/ảnh khi có thao tác thay đổi UI tiếp theo, nên
không thể dùng kiểm tra trước thao tác làm bằng chứng cho kết quả sau đó.
Ảnh chỉ được gửi cho model trong lượt, không ghi ảnh vào chat history.

Browser Muse nhận JSON cấu hình `actions` (ui_tree/ui_fill/ui_press/ui_assert…);
parser chuyển thành công cụ cục bộ. Backend không giả lập native function
calling hay yêu cầu model tự nhận đã chạy trên Windows.

Tham khảo chính thức: [Control patterns](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-controlpatternsoverview),
[UIA threading](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading),
[SendInput và giới hạn integrity level](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
Lưu phiên trình duyệt dựa trên [Playwright authentication state](https://playwright.dev/dotnet/docs/auth).

Kiểm thử native có opt-in riêng. Hiện giữ `MUSE_COMPUTER_TESTS=0` theo yêu cầu
không mở thêm cửa sổ. Xem TESTING.md ở thư mục gốc để biết bằng chứng và giới hạn.
