# Muse

Ứng dụng cá nhân để chat, đọc dự án, sửa tệp và chạy kiểm tra qua tài khoản Muse.

## Mở ứng dụng

Mở **`dist\Muse\Muse.exe`** (hiện tại **0.8.4**). Bản này đã kèm runtime; không cần cài SDK hay mở terminal.
Nếu đang mở bản cũ, đóng cửa sổ cũ trước khi mở bản mới.

Ngay trong ứng dụng:

1. Bấm **Chọn dự án** và chọn thư mục trên máy.
2. Bấm **Kết nối Muse**, đăng nhập trên trang Muse; kết nối được lưu tự động.
3. Nhập điều bạn muốn làm, hoặc chọn **Tìm hiểu dự án**, **Tìm và sửa lỗi**, **Thêm tính năng**, **Kiểm tra chất lượng**.

Muse tự chuẩn bị kết nối và tự quản lý mã truy cập nội bộ. Người dùng không
cần lấy/nhập API key, admin key, chọn cổng, chạy server hay nạp cookie thủ công.

## Không gian làm việc

- Hội thoại là màn hình chính; Enter để gửi, Shift+Enter để xuống dòng.
- Không tự dừng sau 30 bước. Muse tiếp tục đến khi hoàn thành, cần bạn trả lời,
  bạn bấm Dừng, hoặc gặp lỗi/vòng lặp không tiến triển. Chi tiết thao tác ở Terminal;
  chat chỉ có trạng thái hiện tại và câu trả lời cuối.
- Khi web đã trả lời xong, app nhận phản hồi hoàn tất. Nếu web mất trạng thái,
  báo lỗi hoặc đứng sau khi có nội dung mà không đổi trong 90 giây, app dừng
  chờ và báo rõ; giữ cuộc chat hiện tại, không tự gửi lại hay tạo chat thay thế.
- Dán ảnh chụp màn hình bằng **Ctrl+V**, **Shift+Insert**, chuột phải → Dán
  hoặc nút **Dán ảnh**. Có ảnh xem trước và nút bỏ ảnh trước khi gửi; có thể
  gửi ảnh không kèm chữ. Tối đa 4 ảnh, 8 MiB mỗi ảnh, tổng 16 MiB mỗi lượt.
- Tin nhắn **BẠN** nằm bên phải, nền tím nhẹ; **MUSE · AI** nằm bên trái.
  Mỗi tin nhắn có nút Sao chép; hoạt động công cụ xem trong bảng Chi tiết.
- Phong cách Mesh Gradient: nền xanh đen, tím/cyan/hồng, nút chuyển sắc,
  khối bo góc và biểu tượng vector. Có chế độ sáng và tối; giữ tiếng Việt.
  Gradient được lưu đệm, không chạy hiệu ứng chuyển động nền.
- Lịch sử ở thanh bên, lưu riêng theo từng dự án.
- **Tệp dự án** mở trình xem/sửa tệp khi cần.
- **Thay đổi** hiển thị kết quả và cho phép hoàn tác tệp.
- Trước khi ghi tệp, Muse hiển thị nội dung trước/sau để bạn duyệt.
- **Dừng** hủy yêu cầu đang chạy; các bước đã hoàn tất vẫn được giữ lại.
- **Terminal** mở nhật ký và ô chạy lệnh, có lựa chọn PowerShell/CMD/Bash; tuỳ chọn nâng cao nằm trong **Cài đặt**.
- **Chạy nền** giữ ứng dụng hoặc máy chủ hoạt động trong lúc kiểm thử; **Dừng nền** kết thúc các tiến trình Muse đã mở.
- **Màn hình** chọn cửa sổ để Muse đọc giao diện, chụp ảnh, nhập dữ liệu, bấm nút và kiểm tra trạng thái. Khi cần, Muse tự mở hộp chọn cửa sổ; Esc dừng điều khiển.

Dữ liệu cá nhân được lưu dưới `%LOCALAPPDATA%\Muse2Api.WinForms`.
Ứng dụng dùng gateway riêng trên địa chỉ loopback với cổng tự chọn, không thay
đổi hoặc dừng gateway khác đang mở trên máy. Lệnh shell được duyệt sẽ chạy với
quyền người dùng; chúng không bị sandbox theo thư mục.

## Dành cho người phát triển

```powershell
dotnet build Muse2Api.sln -c Release
dotnet test Muse2Api.sln -c Release
# Chạy UI từ source:
dotnet run --project src/Muse2Api.WinForms -c Release
# Đóng gói bản Windows không cần SDK/runtime bên ngoài:
dotnet publish src/Muse2Api.WinForms -c Release -r win-x64 --self-contained true -o dist/Muse
```

Backend OpenAI-compatible vẫn chạy độc lập bằng `dotnet run --project src/Muse2Api`.
Chế độ backend riêng giữ cơ chế API/admin key và cấu hình `MUSE2API_*` trong
`Settings.cs`. UI cá nhân tự xử lý toàn bộ phần này ở bên trong.

Xem [WORKSPACE.md](WORKSPACE.md) cho hành vi của desktop và [TESTING.md](TESTING.md)
cho hướng dẫn kiểm chứng. Bản port gốc từ [www222fff/muse2api](https://github.com/www222fff/muse2api).

Bản 0.7 dùng một định dạng kế hoạch JSON riêng cho Muse web: model sinh dữ liệu
`actions`, ứng dụng chuyển sang công cụ cục bộ có kiểm tra và duyệt. Model không
cần giả lập công cụ của trang web hay tự nhận đã chạy trên Windows.

Agent phải đọc phiên bản hiện tại trước khi sửa, đọc đủ các trang trước khi
ghi đè toàn bộ tệp, giữ BOM/encoding/xuống dòng, kiểm tra lại sau khi lưu và
không tính một lần ghi không đổi là sửa thật. Terminal có tiến trình nền,
stdin, mã thoát, đầu ra cuối khi log dài và chờ tiến trình con thoát khi dừng.

Đăng nhập nằm trong dữ liệu người dùng, dùng lại qua các bản build. Cookie và
local storage của trình duyệt chat được lưu; tài khoản đang chờ lượt không bị
coi là đăng xuất. Mở lại cửa sổ đăng nhập không tự xóa cookie; chỉ **Tài khoản
khác** mới xóa phiên WebView. Cookie phải được Muse xác nhận trước khi lưu.

**203 test gateway/agent/phiên đăng nhập** đã qua, gồm kiểm tra DOM bằng Chrome
headless. Bản 0.7.2 giữ escape của code qua khối JSON, gửi lại phản hồi sai
định dạng và không chạy một phần kế hoạch lỗi. Gói mới:
`dist/Muse/Muse.exe` (chi tiết trong TESTING.md).
Bản 0.8.2 hoàn thiện dán PNG/bitmap/file ảnh, preview/bỏ ảnh và truyền ảnh
qua HTTP tới agent. Ảnh không bị upload lại ở mỗi bước công cụ; dán lại cùng
ảnh ở lượt mới vẫn gửi được. JSON lịch sử chỉ giữ ghi chú về ảnh đã gửi,
không lưu base64 ảnh đầy đủ.

Khi phát triển bản tiếp theo, chạy `scripts/Publish-Desktop.ps1`. Quy trình
kiểm thử, publish vào thư mục tạm và kiểm tra gói rồi mới thay `dist/Muse`,
dọn các bản cũ. Nếu app đang chạy hoặc bản mới lỗi thì giữ các bản cũ.
Không dọn hồ sơ đăng nhập/lịch sử trong LocalAppData.
Bản 0.8.1 sửa chữ vẽ tràn/chồng giữa các nút, co giãn Form theo DPI và Cài
đặt dùng nội dung tự co giãn/cuộn, giữ nút Xong trong khung. Mỗi cuộc chat
trong ứng dụng dùng một thread Muse xuyên suốt các bước và qua restart;
chỉ gửi quan sát mới, không gửi lại toàn bộ lịch sử. Gặp rate limit thì dừng,
không tự tạo chat thay thế hoặc gửi lại qua tài khoản khác.
Bản 0.7.3 làm gọn chat: một dòng trạng thái đang suy nghĩ/đọc/sửa/chạy kiểm
tra, rồi một câu trả lời cuối. Nút **Chi tiết** mở toàn bộ hoạt động trong
Terminal. Mở lại lịch sử cũng không dựng lại các thẻ hoạt động riêng lẻ.
Các thao tác Windows đã được kiểm tra riêng trên ứng dụng mẫu; bộ native có
2 bài chính sách chạy nền và 3 bài mở cửa sổ opt-in. Hiện chỉ tiếp tục kiểm thử
nền theo yêu cầu người dùng. Kiểm tra thật qua Muse: sửa lỗi phép cộng C#,
`dotnet run` in `ADD_TEST_OK`, khởi động lại và chat bằng phiên cũ, không đăng
nhập/lấy mã mới. Bản 0.7.2 cũng đã sửa code chứa tiếng Việt, dấu nháy và đường
dẫn Windows qua Muse thật, chạy `dotnet run` ra `QUOTE_TEST_OK`, exit 0.
Chuỗi UI qua Muse chưa được xác minh đầy đủ sau khi dừng mở
cửa sổ; xem [TESTING.md](TESTING.md) cho phạm vi đã kiểm chứng.
