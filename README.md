# Muse

Ứng dụng cá nhân để chat, đọc dự án, sửa tệp và chạy kiểm tra qua tài khoản Muse.

## Mở ứng dụng

Mở **`dist\Muse\Muse.exe`** sau khi publish thành công. Phiên bản mã nguồn hiện tại là **0.9.0 (.NET 10)**; bản đã đóng gói kèm runtime nên không cần cài SDK hay mở terminal.
Người dùng đã xác nhận kiểm tra tay bản 0.8.7 ngày 31/10/2026. Bản 0.8.8 sửa chat, 0.8.9 thêm bộ nhớ, 0.8.10 thêm thông báo cập nhật; 0.8.11 hoàn thiện luồng điều khiển cửa sổ. 0.9.0 thêm đăng nhập nhiều tài khoản cùng lúc, xuất Base URL + API key chuẩn OpenAI và kỹ năng gọi bằng @alias (xem mục riêng bên dưới). Cần kiểm tra tay lại các tình huống mới; không dùng xác nhận của 0.8.7 thay cho kết quả mới. Bản đóng gói vẫn dùng `-Preview` và hồ sơ thử riêng. Xem [hướng dẫn kiểm tra tay](MANUAL_TESTING_NET10.md).
Nếu đang mở bản cũ, đóng cửa sổ cũ trước khi mở bản mới.

Ngay trong ứng dụng:

1. Bấm **Chọn dự án** và chọn thư mục trên máy.
2. Bấm **Kết nối Muse**, đăng nhập trên trang Muse; kết nối được lưu tự động.
3. Nhập điều bạn muốn làm, hoặc chọn **Tìm hiểu dự án**, **Tìm và sửa lỗi**, **Thêm tính năng**, **Kiểm tra chất lượng**.

Muse tự chuẩn bị kết nối và tự quản lý mã truy cập nội bộ. Người dùng không
cần lấy/nhập API key, admin key, chọn cổng, chạy server hay nạp cookie thủ công.

## Không gian làm việc

- Hội thoại là màn hình chính; Enter để gửi, Shift+Enter để xuống dòng.
- Từ 0.8.10, app tự kiểm tra cập nhật khi mở và định kỳ 6 giờ trong lúc đang
  chạy. Có bản mới sẽ hiện banner gọn, không ngắt chat. Có nút **Kiểm tra cập
  nhật** trong Cài đặt và **Ẩn thông báo** cho phiên bản đang được báo.
  Bản đóng gói kiểm tra GitHub Releases; bản chạy từ source kiểm tra phiên bản
  trên `main`. Xem [cập nhật và phát hành](UPDATES.md).
- Nhắc đến việc chạy/test trong lời trao đổi không tự tạo yêu cầu phải chạy lệnh.
  Yêu cầu thao tác thật vẫn cần kết quả công cụ; khi thiếu kết quả, app giữ phản hồi
  để Muse xử lý phần còn thiếu trong cùng cuộc chat.
- Không tự dừng sau 30 bước. Muse tiếp tục đến khi hoàn thành, cần bạn trả lời,
  bạn bấm Dừng, hoặc gặp lỗi/vòng lặp không tiến triển. Chi tiết thao tác ở Terminal;
  chat chỉ có trạng thái hiện tại và câu trả lời cuối.
- Khi web đã trả lời xong, app nhận phản hồi hoàn tất. Nếu web mất trạng thái,
  báo lỗi hoặc đứng sau khi có nội dung mà không đổi trong 90 giây, app dừng
  chờ và báo rõ; giữ cuộc chat hiện tại, không tự gửi lại hay tạo chat thay thế.
- Dán ảnh chụp màn hình bằng **Ctrl+V**, **Shift+Insert**, chuột phải → Dán
  hoặc nút **Dán ảnh**. Có ảnh xem trước và nút bỏ ảnh trước khi gửi; có thể
  gửi ảnh không kèm chữ. Tối đa 4 ảnh, 8 MiB mỗi ảnh, tổng 16 MiB mỗi lượt.
- Tin nhắn của bạn nằm bên phải, nền xanh; phản hồi Muse nằm bên trái, có
  nút Sao chép. Chi tiết công cụ nằm trong Terminal.
- Giao diện sáng/tối với khối bo góc, nút xanh và biểu tượng vector; giữ tiếng Việt.
  Câu trả lời hoàn tất dừng hiệu ứng nhả chữ và con trỏ.
- Lịch sử ở thanh bên, lưu riêng theo từng dự án.
- Chat dài được nén trích đoạn trên máy: giữ mục tiêu ban đầu, lời dặn, lựa chọn,
  trạng thái hoàn tất/dang dở và kết quả công cụ gần đây. Gói nhớ được gửi kèm
  yêu cầu hiện tại tới đúng cuộc chat Muse đang dùng; không tạo chat hoặc gọi
  AI riêng để tóm tắt. Các lượt tiếp theo dùng chỉ dẫn giao thức ngắn hơn.
- Khi hỏi lại chuyện cũ, tool tìm tối đa 4 đoạn liên quan trong lịch sử cục bộ.
  Bộ nhớ được lưu cùng phiên và khôi phục khi mở lại; lịch sử hiển thị không bị
  xóa bởi việc nén. Bộ nhớ có giới hạn và không giữ ảnh hay toàn bộ mã nguồn;
  cần đọc lại tệp trước khi sửa, đính kèm lại ảnh khi cần. Tool không điều khiển
  được giới hạn ngữ cảnh bên trong dịch vụ Muse. Xem [cơ chế bộ nhớ](CONTEXT_MEMORY.md).
- **Tệp dự án** mở trình xem/sửa tệp khi cần.
- **Thay đổi** hiển thị kết quả và cho phép hoàn tác tệp.
- Trước khi ghi tệp, Muse hiển thị nội dung trước/sau để bạn duyệt.
- **Dừng** hủy yêu cầu đang chạy; các bước đã hoàn tất vẫn được giữ lại.
- **Terminal** mở nhật ký và ô chạy lệnh, có lựa chọn PowerShell/CMD/Bash; tuỳ chọn nâng cao nằm trong **Cài đặt**.
- **Chạy nền** giữ ứng dụng hoặc máy chủ hoạt động trong lúc kiểm thử; **Dừng nền** kết thúc các tiến trình Muse đã mở.
- **Màn hình** có tìm kiếm cửa sổ theo tiêu đề/tên ứng dụng. Muse đọc/tìm phần tử,
  chụp ảnh, nhập liệu, bấm trái/phải/đúp, kéo thả, cuộn dọc/ngang và xác minh kết quả.
  Quyền chỉ trong cửa sổ đã chọn và hộp thoại/menu cùng tiến trình do nó mở, cho
  lượt hiện tại. Dừng/Esc thu hồi ngay; không điều khiển cửa sổ cấp quyền của chính Muse.
  Xem [hướng dẫn điều khiển](SCREEN_CONTROL.md), gồm giới hạn và phần cần kiểm tra tay.

## Nhiều tài khoản, kết nối OpenAI và kỹ năng (0.9.0)

- **Nhiều tài khoản cùng lúc:** trong **Cài đặt → Tài khoản Muse**, bấm
  **Thêm tài khoản** để đăng nhập thêm tài khoản Muse khác. Mọi tài khoản
  đang bật đều hoạt động đồng thời; gateway tự chia phiên, làm mát và
  chuyển phiên khi một tài khoản lỗi. Có thể bật/tắt hoặc xóa từng tài khoản.
- **Kết nối OpenAI (kiểu 9router):** **Cài đặt → Kết nối OpenAI (trên máy này)**
  hiển thị **Base URL** dạng `http://127.0.0.1:<cổng>/v1` và **API key** để
  dán vào ứng dụng khác tương thích OpenAI. Cổng được chọn ngẫu nhiên mỗi
  lần mở app, nên hãy sao chép lại link sau khi khởi động lại. Có thể tạo
  thêm key riêng, thu hồi hoặc xóa key; key mới chỉ hiển thị đầy đủ một lần.
- **Kỹ năng @alias:** **Cài đặt → Kỹ năng** cho phép cài skill từ file
  `.md`/`.txt` hoặc gói `.zip` chứa `SKILL.md` (frontmatter `name`,
  `description`, `alias`; không có frontmatter thì alias lấy từ tên file).
  Khi chat, viết `@tenskill` — ví dụ `@fe dựng layout này` — để áp dụng nội
  dung skill cho lượt đó. Skill được lưu ở
  `%LOCALAPPDATA%\Muse2Api.WinForms\Skills` và có thể bật/tắt hoặc xóa.

Dữ liệu cá nhân được lưu dưới `%LOCALAPPDATA%\Muse2Api.WinForms`.
Ứng dụng dùng gateway riêng trên địa chỉ loopback với cổng tự chọn, không thay
đổi hoặc dừng gateway khác đang mở trên máy. Lệnh shell được duyệt sẽ chạy với
quyền người dùng; chúng không bị sandbox theo thư mục.

## Kiểm thử branch .NET 10

Bản tích hợp 0.8.7 dùng .NET 10 và Microsoft.Playwright 1.64.1.
Hãy đọc [UPGRADE_TESTING.md](UPGRADE_TESTING.md) trước khi chạy: tài liệu
hướng dẫn pull branch, build/test, tạo profile Muse riêng và rollback về 0.8.5.

Trên Windows, gateway mã hóa API key, account và browser session bằng
DPAPI CurrentUser. Dùng `MUSE_DESKTOP_PROFILE_ROOT` để test riêng, không
làm đổi cookie hoặc lịch sử của bản cũ. Trên Linux, DPAPI không hoạt động.

**Turbo tự duyệt sửa mã/cấu hình build và chạy lệnh terminal, gồm tiến trình nền.**
Tệp bí mật và chính sách agent/CI vẫn có bước duyệt riêng. Bấm Dừng để ngắt;
Esc cũng dừng khi Muse nhận phím hoặc đang điều khiển cửa sổ đã chọn.
Nhập `/goal <mục tiêu>` để xây dựng, chạy test, vá lỗi và kiểm thử giao diện
đến khi có kết quả kiểm chứng. `/goal` tiếp tục mục tiêu đã lưu; `/goal status`
xem trạng thái; `/goal stop` tạm dừng. Xem [GOAL.md](GOAL.md).
Xem [SECURITY.md](SECURITY.md) và [COMMERCIAL-LICENSING.md](COMMERCIAL-LICENSING.md).
Bản 0.8.7 đã được người dùng kiểm tra tay và xác nhận OK; hồ sơ gốc vẫn được giữ nguyên. Không có giấy phép thương mại mới được cấp bởi bản cập nhật này.

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

**219 test gateway/agent/phiên đăng nhập** đã qua, gồm kiểm tra DOM bằng Chrome
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
