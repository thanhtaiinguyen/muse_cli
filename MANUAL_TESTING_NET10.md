# Kiểm tra tay Muse 0.8.7 — bản tích hợp .NET 10

Bản này đã tích hợp nhánh `upgrade/net10-security-ci-release` tại commit
`d0c505464457ccccc4ed6e0bc6a9fb0a9f2b4849`, giữ các sửa lỗi chat/cuộn/tool call
0.8.6 đang có trên máy và vá hai lỗi đã phát hiện khi review.

Người dùng đã báo kiểm tra tay OK ngày 31/10/2026 và yêu cầu push lên GitHub.
Đây là xác nhận thủ công của người dùng; không đổi kết quả 3 bài tự động cần
cửa sổ thật đang được bỏ qua. Bản tại máy vẫn giữ hồ sơ preview, không tự ghi
hoặc chuyển đổi dữ liệu của hồ sơ chính.

## Mở bản kiểm thử

Mở `dist\Muse\Muse.exe`. Tiêu đề phải có **0.8.7 — Kiểm thử .NET 10 · hồ sơ riêng**.
Không cần chạy server hoặc nhập admin key.

- Hồ sơ thử: `%LOCALAPPDATA%\Muse2Api.WinForms-Preview-Net10`.
- Hồ sơ gốc `%LOCALAPPDATA%\Muse2Api.WinForms` được giữ nguyên. Không xóa hoặc ghi đè hồ sơ gốc.
- Đã sao chép riêng dữ liệu đăng nhập đã lưu sang hồ sơ thử. Nếu phiên còn hiệu lực, app có thể kết nối lại mà không lấy OTP mới. Không sao chép cache trình duyệt lớn.
- Lịch sử của bản thử là lịch sử riêng; lịch sử cũ vẫn nằm ở hồ sơ gốc.
- App được chọn sẵn thư mục mẫu `artifacts\manual-test-net10`. Nếu chưa thấy, bấm Chọn dự án và chọn thư mục này.

## Các bước cần thử

1. **Hai lượt đọc liên tiếp trong cùng một cuộc trò chuyện**
   - Gửi: `Đọc first.txt và in toàn bộ nội dung để tôi kiểm tra.`
   - Chờ hoàn tất rồi gửi: `Bây giờ chỉ đọc second.txt và trả lời mã đánh dấu trong tệp này.`
   - Lượt sau phải trả `MUSE_SECOND_FILE_PROOF`, không lấy lại `MUSE_FIRST_FILE_PROOF`.
   - Khi thêm thinking/tool cards, khung chat không nhảy về đầu. Khi tự cuộn lên đọc lịch sử, cập nhật mới không kéo bạn đi.

2. **Sửa tệp thường bằng Turbo**
   - Bật Turbo trong Cài đặt.
   - Gửi: `Đọc sample.txt rồi đổi Xin chào Muse thành MUSE_EDIT_OK.`
   - App đọc rồi sửa được tệp; mở Tệp/Thay đổi để xác nhận. Tệp thường được tự duyệt.

3. **Chạy lệnh và kiểm tra bước duyệt**
   - Gửi: `Chạy CMD lệnh echo MUSE_NET10_OK.`
   - Turbo mới vẫn phải hỏi trước khi chạy lệnh. Thử Từ chối một lần: lệnh không được chạy.
   - Yêu cầu lại và chọn Cho phép: Terminal phải có `MUSE_NET10_OK` và `exit_code=0`.
   - Tệp nhạy cảm như `.env`, khóa và cấu hình build cũng vẫn yêu cầu duyệt.

4. **Dán ảnh**
   - Chụp màn hình, Ctrl+V vào chat: phải có ảnh xem trước và nút bỏ ảnh.
   - Gửi yêu cầu mô tả ảnh, kiểm tra ảnh có được hiểu đúng.

5. **Dừng và mở lại**
   - Thử Dừng một yêu cầu đang chạy: app phải thoát trạng thái bận và cho gửi tiếp.
   - Đóng rồi mở lại Muse: kết nối đã lưu và lịch sử tạo trong bản thử phải được khôi phục.

6. **Màn hình, nếu bạn dùng chức năng này**
   - Chọn một cửa sổ thử không chứa dữ liệu nhạy cảm và kiểm tra đọc giao diện, nhập/bấm, chụp ảnh.
   - Kiểm tra Esc dừng điều khiển. Các bài cần cửa sổ thật được để cho bạn kiểm tra tay; agent không mở chúng.

Sau khi thử, báo bước nào đạt/chưa đạt cùng thông báo Terminal hoặc ảnh lỗi.
Không gửi cookie, API key, OTP hoặc file hồ sơ cá nhân. Việc chuyển sang hồ sơ
chính là thao tác riêng, không được thực hiện tự động chỉ vì đã test OK.

## Đã xác minh tự động

- Build solution .NET 10: 0 lỗi, 0 cảnh báo.
- Core/agent/browser: 266/266 đạt, không bỏ qua; chỉ dùng fixture, không gửi chat Muse thật.
- WinForms ngoài màn hình: tách hồ sơ preview, đường dẫn gateway/workspace, giữ cuộn ở lượt thứ hai và khi đọc lịch sử đều đạt.
- Computer tests: 2 đạt, 3 cần cửa sổ thật được bỏ qua có chủ đích.
- Kiểm tra gói cài đặt đạt. Chỉ giữ bản phát hành mới trong `dist\Muse`.

SDK build cục bộ nằm trong `artifacts\dotnet10` (không thay SDK .NET 8 hoặc PATH
toàn máy). Khi cần tự build, dùng `artifacts\dotnet10\dotnet.exe` hoặc script
`scripts\Publish-Desktop.ps1` đã tự nhận SDK này.

Muốn đóng gói lại cùng chế độ thử: `scripts\Publish-Desktop.ps1 -Preview`.
Không bỏ `-Preview` và dùng hồ sơ gốc trước khi thống nhất chuyển sang bản chính.
