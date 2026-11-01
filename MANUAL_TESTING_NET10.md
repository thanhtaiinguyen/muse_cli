# Kiểm tra tay Muse 0.8.11 — bản tích hợp .NET 10

Bản này đã tích hợp nhánh `upgrade/net10-security-ci-release` tại commit
`d0c505464457ccccc4ed6e0bc6a9fb0a9f2b4849`, giữ các sửa lỗi chat/cuộn/tool call
0.8.6 đang có trên máy và vá hai lỗi đã phát hiện khi review.

Người dùng đã báo kiểm tra tay **0.8.7** OK ngày 31/10/2026 và yêu cầu push lên GitHub.
Đây là xác nhận thủ công của người dùng; không thay cho các bài cần
cửa sổ thật của bản mới. Bản tại máy vẫn giữ hồ sơ preview, không tự ghi
hoặc chuyển đổi dữ liệu của hồ sơ chính.

0.8.8 sửa lỗi ép chạy lệnh khi chỉ trao đổi về chạy/test, giữ phản hồi thiếu thao tác
trong lượt tiếp theo, cải thiện tương phản chữ tin nhắn sáng/tối, kết thúc con trỏ
nhả chữ và bỏ dữ liệu CLIXML khỏi kết quả PowerShell. Bản này cần kiểm tra tay lại.

0.8.9 thêm nén ngữ cảnh và tìm lại đoạn liên quan từ lịch sử cục bộ, gửi kèm
vào cùng cuộc chat web. Xem [CONTEXT_MEMORY.md](CONTEXT_MEMORY.md) về giới hạn.

0.8.10 thêm thông báo cập nhật và kiểm tra tay trong Cài đặt. Xem [UPDATES.md](UPDATES.md).

0.8.11 sửa luồng điều khiển cửa sổ, thêm tìm kiếm/phân trang UIA, chuột phải/đúp,
kéo thả/cuộn và nhập theo focus. Xem [SCREEN_CONTROL.md](SCREEN_CONTROL.md).

## Mở bản kiểm thử

Mở `dist\Muse\Muse.exe`. Tiêu đề phải có **0.8.11 — Kiểm thử .NET 10 · hồ sơ riêng**.
Không cần chạy server hoặc nhập admin key.

- Hồ sơ thử: `%LOCALAPPDATA%\Muse2Api.WinForms-Preview-Net10`.
- Hồ sơ gốc `%LOCALAPPDATA%\Muse2Api.WinForms` được giữ nguyên. Không xóa hoặc ghi đè hồ sơ gốc.
- Đã sao chép riêng dữ liệu đăng nhập đã lưu sang hồ sơ thử. Nếu phiên còn hiệu lực, app có thể kết nối lại mà không lấy OTP mới. Không sao chép cache trình duyệt lớn.
- Lịch sử của bản thử là lịch sử riêng; lịch sử cũ vẫn nằm ở hồ sơ gốc.
- Để thử đọc/sửa tệp mẫu, chọn thư mục `artifacts\manual-test-net10`. Bản cập nhật giữ nguyên dự án bạn đang chọn, không tự chuyển thư mục.

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
   - Trong Turbo, lệnh chạy thẳng; Terminal có `MUSE_NET10_OK` và `exit_code=0`, không mở hộp duyệt.
   - Đổi về Hỏi trước khi thay đổi: thử Từ chối, lệnh không được chạy. Yêu cầu lại và Cho phép để chạy.
   - Chế độ Chỉ xem chặn chạy lệnh. Tệp bí mật, khóa và AGENTS.md vẫn cần duyệt trong Turbo; cấu hình build được tự duyệt.

4. **Dán ảnh**
   - Chụp màn hình, Ctrl+V vào chat: phải có ảnh xem trước và nút bỏ ảnh.
   - Gửi yêu cầu mô tả ảnh, kiểm tra ảnh có được hiểu đúng.

5. **Dừng và mở lại**
   - Thử Dừng một yêu cầu đang chạy: app phải thoát trạng thái bận và cho gửi tiếp.
   - Đóng rồi mở lại Muse: kết nối đã lưu và lịch sử tạo trong bản thử phải được khôi phục.

6. **Màn hình, nếu bạn dùng chức năng này**
   - Chọn một cửa sổ thử không chứa dữ liệu nhạy cảm và kiểm tra đọc giao diện, nhập/bấm, chụp ảnh.
   - Kiểm tra Esc dừng điều khiển. Các bài cần cửa sổ thật được để cho bạn kiểm tra tay; agent không mở chúng.
   - Hộp chọn mới có tìm tên ứng dụng/tiêu đề; xóa bộ lọc không tự chọn nhầm cửa sổ.
   - Thử trên dữ liệu mẫu: nhập vào ô tìm kiếm → bấm nút → xác minh nhãn kết quả.
   - Với app có menu/drag/scroll, yêu cầu riêng chuột phải, bấm đúp, kéo thả hoặc cuộn;
     mỗi thao tác tọa độ cần ảnh mới, không tái dùng ảnh sau khi UI thay đổi.
   - Bấm Dừng trong lúc thao tác: kết thúc quyền và cho phép chat tiếp; không tự chọn app khác.

7. **Trao đổi sau khi đã làm việc trên dự án**
   - Sau một lượt đọc tệp hoặc chạy test, gửi: `Khi chạy test thì ứng dụng hay bị chậm`.
   - Muse trả lời đúng lời trao đổi, không báo thiếu thao tác chỉ vì có chữ chạy/test, không tự gửi lại liên tục.
   - Gửi `Đừng chạy test` rồi `Chạy CMD lệnh echo MUSE_CHAT_OK giúp tôi`.
   - Lượt đầu không bắt chạy lệnh; lượt sau phải xin duyệt và có `exit_code=0` khi được cho phép.
   - Dùng cùng cuộc trò chuyện; kiểm tra không nhảy về câu trả lời cũ.

8. **Màu chữ và trạng thái hoàn tất**
   - Chuyển sáng/tối: tin của bạn phải đọc rõ trong cả hai chế độ.
   - Đổi theme khi Muse đang hiển thị câu trả lời: không lặp chữ; khi xong không còn con trỏ đen.
   - Nếu có lỗi thật, dòng suy nghĩ thu gọn ngắn; thông báo đầy đủ chỉ có một khung riêng, chi tiết ở Terminal.

9. **Nhớ lựa chọn và lời dặn trong chat dài**
   - Trong một chat thử, nói: `Giữ tiếng Việt, chỉ kiểm thử nền. Chọn SQLite; mã phương án AMBER_731.`
   - Sau các lượt đọc tệp/trao đổi khác, hỏi: `Nhắc lại phương án AMBER_731 và các lời dặn đã thống nhất.`
   - Đóng/mở lại app, chọn đúng chat rồi hỏi lại: vẫn phải lấy được trích đoạn liên quan.
   - Nói `Đổi sang PostgreSQL cho phương án AMBER_731; chỉ trao đổi, chưa sửa file.` Sau đó hỏi lại lựa chọn mới:
     không dùng SQLite làm lựa chọn hiện tại, không tự chạy lại công việc trong lịch sử.
   - Sau khi nén, Terminal có ghi chú; chat không thêm nhiều thẻ hay tạo chat web thay thế.

10. **Kiểm tra cập nhật**
    - Mở Cài đặt → Kiểm tra cập nhật: nút tạm khóa khi đang chờ, rồi có kết quả tiếng Việt.
    - Khi chưa có Release mới hoặc đang chạy bản cao hơn GitHub, không báo cập nhật giả.
    - Mất mạng không làm treo/mất cuộc chat. Nếu có bản mới thật, banner không che chat;
      nút Xem bản mới chỉ mở trang GitHub khi bấm. Ẩn phiên bản hiện tại không chặn thông báo bản cao hơn.
    - Bản `dotnet run` hiển thị kênh mã nguồn và hướng dẫn pull/build; bản `dist/Muse` là kênh Release.

Sau khi thử, báo bước nào đạt/chưa đạt cùng thông báo Terminal hoặc ảnh lỗi.
Không gửi cookie, API key, OTP hoặc file hồ sơ cá nhân. Việc chuyển sang hồ sơ
chính là thao tác riêng, không được thực hiện tự động chỉ vì đã test OK.

## Đã xác minh tự động

- Build các project desktop .NET 10: 0 lỗi, 0 cảnh báo.
- Core/agent/browser: 349/349 đạt, không bỏ qua; chỉ dùng fixture, không gửi chat Muse thật.
- Cập nhật: so phiên bản số, cache/ETag qua restart, ẩn đúng một phiên bản, giới hạn GitHub, nguồn source, URL không hợp lệ, mất mạng, hủy/kiểm tra trùng đều đạt với HTTP giả. Native ngoài màn hình kiểm tra banner ở cửa sổ tối thiểu/bình thường và nút kiểm tra thủ công; không mở trình duyệt. Workflow tag đã chuẩn bị trong source, chưa chạy/phát hành trên GitHub trong lượt này.
- Bộ nhớ: giữ mục tiêu/lời dặn qua nhiều lần nén, khôi phục phiên, tìm lại đoạn cũ và sửa đổi mới; không xuất thân file/ảnh vào bản nhớ, không lấy kết quả cũ thay chứng cứ hiện tại. Trình duyệt giả xác nhận gửi bản nhớ trong cùng chat sau khởi động lại, không tăng số lượt gửi.
- Hồi quy lời trao đổi/yêu cầu chạy lệnh, giữ execution_repair, đọc/sửa/kiểm chứng thao tác và đầu ra PowerShell nền/đồng bộ đều đạt.
- WinForms ngoài màn hình: tách hồ sơ preview, đường dẫn gateway/workspace, tương phản chữ người dùng sáng/tối >= 4,5:1, kết thúc con trỏ/timer, không lặp chữ khi đổi theme, thông báo lỗi gọn và giữ cuộn ở lượt thứ hai/khi đọc lịch sử đều đạt.
- Computer tests: 42 đạt (có UIA thật trên cửa sổ hoàn toàn ẩn và kiểm tra phạm vi owned dialog), 4 cần cửa sổ hiển thị/chuột-phím thật được bỏ qua có chủ đích.
- Kiểm tra gói cài đặt đạt. Chỉ giữ bản phát hành mới trong `dist\Muse`.

SDK build cục bộ nằm trong `artifacts\dotnet10` (không thay SDK .NET 8 hoặc PATH
toàn máy). Khi cần tự build, dùng `artifacts\dotnet10\dotnet.exe` hoặc script
`scripts\Publish-Desktop.ps1` đã tự nhận SDK này.

Muốn đóng gói lại cùng chế độ thử: `scripts\Publish-Desktop.ps1 -Preview`.
Không bỏ `-Preview` và dùng hồ sơ gốc trước khi thống nhất chuyển sang bản chính.
