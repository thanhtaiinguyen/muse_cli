# Dùng Muse

Mở `dist\Muse\Muse.exe`, chọn dự án rồi kết nối Muse. Không cần đọc tài liệu
hay thao tác kỹ thuật để hoàn thành bước thiết lập.

## Kết nối lần đầu

Ứng dụng tự khởi động phần kết nối trên máy và dùng Edge/Chrome có sẵn nếu phù
hợp. Nếu cần thành phần bổ sung, việc tải được thực hiện khi bạn bấm Kết nối
Muse và có trạng thái tiến trình trong ứng dụng.

Đăng nhập trực tiếp trên trang Muse trong cửa sổ mở ra. Khi đăng nhập xong,
ứng dụng tự lưu tài khoản và quay về hội thoại; không có bước đặt nhãn, tìm
cookie, nhập key hoặc bấm nạp tài khoản. Những lần sau dùng lại kết nối đã lưu.
Đổi tài khoản qua **Cài đặt** → **Đăng nhập / đổi tài khoản Muse**.

## Yêu cầu bằng lời thông thường

- “Giải thích dự án này đang làm gì và các phần chính liên hệ với nhau thế nào.”
- “Tìm lỗi trong luồng đăng nhập và đề xuất cách sửa.”
- “Thêm nút tìm kiếm, cho tôi xem thay đổi trước khi lưu.”
- “Kiểm tra những gì vừa sửa và giải thích kết quả.”

Các thẻ gợi ý trên màn hình giúp bắt đầu mà không cần biết tên tool hoặc lệnh.
Nếu chưa chọn thư mục mà đã bấm gửi, ứng dụng sẽ mở hộp chọn dự án.

## Đọc tệp và xem thay đổi

Hai nút **Tệp dự án** và **Thay đổi** mở bảng chi tiết bên phải, có thể đóng
để quay lại màn hình chat gọn. Tệp mã nguồn có thể đọc/sửa trong editor.
Trước khi agent ghi tệp, hộp duyệt hiển thị nội dung trước/sau. Bạn có thể cho
phép, giữ nguyên hoặc dừng yêu cầu.

Agent có thể đọc chi tiết kèm số dòng, tiếp tục các trang còn lại của tệp dài,
đọc nhiều tệp và sửa nhiều đoạn trong một lần duyệt. Ứng dụng giữ JSON khi đọc
phản hồi Muse, gửi kết quả thao tác cục bộ về model và yêu cầu thực hiện tiếp
nếu model chỉ trả lời bằng chữ. Khi chưa thực hiện được, chat báo chưa hoàn tất.

Trước khi agent sửa tệp đã có, ứng dụng yêu cầu một bản đọc trong lượt hiện
tại còn khớp với nội dung trên đĩa. Ghi đè toàn bộ chỉ được phép với tệp nhỏ
đã đọc đủ; tệp lớn hoặc thay một file bằng đoạn code ngắn phải dùng sửa có mục
tiêu để giữ nguyên phần không liên quan. Nếu
file đổi ngoài Muse hoặc `expected_hash` không khớp, thao tác bị từ chối và
agent phải đọc lại. Khi bạn từ chối duyệt, lượt thực hiện dừng, không báo thành công.

Hoàn tác khôi phục tệp về trước lần sửa đầu trong lần mở dự án hiện tại. Nếu
tệp đã bị sửa ngoài ứng dụng sau đó, Muse từ chối ghi đè. Hoàn tác chỉ theo dõi
việc ghi/sửa qua tool file và editor; không khôi phục thay đổi do lệnh shell.
Dùng Git để giữ lịch sử lâu dài.

## Lịch sử và dữ liệu

Cuộc trò chuyện lưu theo từng dự án, chọn ở thanh bên để tiếp tục. Nội dung
tệp/kết quả tool có thể nằm trong lịch sử và được gửi lại cho model khi cần.
Mã truy cập nội bộ không được đưa vào file hội thoại.

Chat hiển thị từng khung riêng: **BẠN** ở bên phải với nền xanh nhẹ,
**MUSE · AI** ở bên trái, **HOẠT ĐỘNG** và **THÔNG BÁO** tách riêng.
Bạn có thể chọn nội dung hoặc bấm **Sao chép** trên từng khung. Lịch sử mở lại
cũng dùng cách hiển thị này. Phản hồi chưa được kiểm tra giao thức không được
đưa lên chat như câu trả lời hoàn tất.

- Lịch sử: `%LOCALAPPDATA%\Muse2Api.WinForms\AgentSessions`.
- Kết nối và tài khoản: `%LOCALAPPDATA%\Muse2Api.WinForms\Gateway`.
- Phiên trình duyệt đăng nhập: `%LOCALAPPDATA%\Muse2Api.WinForms\WebView2`.
- Cookie/local storage của trình duyệt chat: `Gateway\browser-sessions` dưới cùng thư mục dữ liệu.

Tài khoản và trạng thái trình duyệt được dùng lại khi mở ứng dụng. Chờ lượt,
quota hay lỗi mạng không tự xóa đăng nhập. Cửa sổ đăng nhập khôi phục cookie
đã lưu khi cần và kiểm tra phiên trên Muse trước khi lưu tài khoản. Chỉ bấm
**Tài khoản khác** mới chủ động xóa cookie WebView. Nếu dịch vụ hết hạn hoặc
thu hồi phiên thì vẫn cần đăng nhập lại; ứng dụng không tự xin mã đăng nhập.
Các file phiên chứa thông tin đăng nhập và không được đưa vào gói phát hành.

## Kiểm thử giao diện Windows

Bấm **Màn hình**, chọn cửa sổ ứng dụng cần kiểm thử và cho phép. Nếu chưa chọn,
yêu cầu điều khiển đầu tiên sẽ mở hộp chọn. Quyền giới hạn trong cửa sổ đã chọn
và hết khi lượt làm việc kết thúc. **Esc** hoặc **Dừng** hủy thao tác đang chạy;
đóng cửa sổ đích khiến tác vụ báo chưa hoàn tất.

Muse có thể đọc cây giao diện, chụp vùng cửa sổ, điền ô nhập, bấm nút, chọn mục,
bật/tắt checkbox, cuộn và xác nhận trạng thái. Click tọa độ phải dựa trên ảnh
chụp còn hạn và cửa sổ chưa di chuyển/đổi kích thước. Kiểm thử cần bằng chứng
nhập/bấm/ảnh/assert phù hợp với yêu cầu; nhập vào một ô chưa có nghĩa đã kiểm
thử xong. Các điều khiển mật khẩu và cửa sổ của chính Muse không được thao tác.

Ảnh được gửi tới model trong lượt hiện tại, không nhúng ảnh vào lịch sử lưu
trên đĩa. Cửa sổ quyền admin/UAC, desktop bảo mật và ứng dụng có accessibility
không đầy đủ có thể không thao tác được; Muse báo lỗi thay vì coi là thành công.

Tài khoản từ cấu hình phiên bản cũ được sao chép khi lần đầu tạo hồ sơ mới;
ứng dụng không ghi đè file gốc. Gateway cá nhân dùng cổng riêng tự chọn, nên
không yêu cầu tìm key của gateway cũ và không chiếm/dừng dịch vụ bên ngoài.

## Tùy chọn dành cho người có kinh nghiệm

Cài đặt nâng cao có lựa chọn shell và thông tin chẩn đoán. Nút **Terminal**
mở bảng **Terminal / CMD**, có kết quả chi tiết, lựa chọn PowerShell/CMD/Bash
và ô chạy lệnh thủ công. Lệnh shell cần duyệt và chạy với
quyền của người dùng, có thể truy cập ngoài thư mục dự án.

Lệnh chạy trong dự án đang chọn, hỗ trợ đường dẫn có dấu/khoảng trắng và trả
mã thoát cùng stdout/stderr. Bật **Chạy nền** để giữ server/ứng dụng mở, xem
đầu ra tiếp tục và dùng **Dừng nền** khi xong. Agent có công cụ theo dõi tiến
trình, gửi stdin và kết thúc tiến trình. Đổi dự án/đóng app sẽ dừng tiến trình
nền do Muse quản lý. Khi dừng, ứng dụng chờ cả các tiến trình con đã tìm thấy.
Đây là trình chạy lệnh/script có stdin, chưa phải terminal PTY đầy đủ; trạng
thái shell như biến hoặc `cd` không được giữ giữa các lệnh độc lập.

Tool file giới hạn trong workspace, chặn link/junction; tệp văn bản tối đa
2 MiB. Bỏ qua thư mục sinh tự động khi duyệt/tìm kiếm. Các thay đổi giữ encoding
và BOM, kiểm tra tệp đổi ngoài ứng dụng trước khi ghi và ghi qua file tạm.

## Kiểm chứng hiện tại

219 test phủ gateway/streaming, vòng lặp đọc–duyệt–sửa, lệnh/cancellation,
hoàn tác, lưu dữ liệu và gateway desktop tự tạo key/cổng riêng/khôi phục tài khoản.
Kiểm tra giao diện bằng điều khiển cửa sổ đã bị dừng bằng Esc; lỗi lớp hiển thị
welcome phát hiện qua ảnh đã sửa trong source, chưa quan sát lại sau đó.
Đã kiểm chứng bằng tài khoản Muse thật: đọc tệp cục bộ, sửa tệp, chạy CMD
thành công và nhận trả lời kết thúc; yêu cầu sửa bằng lời thường ở lượt tiếp
theo cũng đã sửa đúng tệp fixture. Bản 0.7 đã sửa/chạy một project C# và chat
sau restart bằng phiên cũ. Kiểm thử đầu cuối UI qua Muse chưa được xác nhận
đầy đủ; sau yêu cầu không mở cửa sổ, chỉ chạy kiểm thử nền. Đăng nhập mới,
quota/media và nhiều cấu hình DPI vẫn cần kiểm chứng riêng.

Bản 0.7.1 sửa `search_files` nhận cả đường dẫn file và thư mục, tránh lỗi
Windows khi tìm thẳng trong `.cs`. 183 test qua; 2 bài browser bị đóng ngay
khi khởi động nên chưa xác nhận lại ở lần đó.

Bản 0.7.2 sửa truyền code có nháy/escape bằng JSON code fence, từ chối toàn
bộ kế hoạch lỗi và yêu cầu lại một action nhỏ tối đa 2 lần. 190/190 test qua
với Chrome headless, gồm 5 bài hồi phục JSON và kiểm tra DOM có code phức tạp.
Đã kiểm chứng qua Muse thật: đọc/sửa hàm trả chuỗi tiếng Việt có nháy và đường
dẫn Windows, `dotnet run` ra `QUOTE_TEST_OK`, exit 0, kiểm tra độc lập và nhận
phản hồi kết thúc.

Bản 0.7.3 gom tiến trình vào một dòng trên ô nhập; chat chỉ giữ yêu cầu và
kết quả cuối hoặc thông báo cần người dùng xử lý. Hoạt động đọc/sửa/chạy lệnh
nằm trong Terminal, mở từ Chi tiết. Cách hiển thị áp dụng cả lịch sử đã lưu.
Kiểm tra nền 150 thao tác và replay lịch sử qua, gồm câu hỏi/lỗi/hủy; ảnh
render không mở cửa sổ đã được kiểm tra.

Bản 0.8.0 áp dụng Mesh Gradient: palette tím/cyan/hồng, nút gradient, icon
vector, hero và composer bo góc, toolbar thích ứng cửa sổ nhỏ và bảng chi tiết
cùng theme. Giữ tiếng Việt, chế độ sáng/tối, dữ liệu đăng nhập và chat gọn.
190/190 test qua; kiểm tra UI nền xác nhận editor giữ BOM/CRLF, các nút/tab,
Ctrl+Tab, CMD, busy và lịch sử. Render 900×640/1280×820 sáng/tối đã được xem.
Không mở thêm cửa sổ test.

Bản 0.8.1 sửa paint clipping/translation và DPI; lỗi cũ được tái hiện bằng
DLL 0.8.0 (1040 pixel tràn), bản sửa 0 pixel. Kiểm tra Form ở DPI thật 120,
Cài đặt co giãn/cuộn và footer cố định; render sáng/tối đã xem. Một local
chat nay gắn với một URL thread Muse qua restart, tiếp tục quan sát mới,
không tạo thread ở mỗi bước hoặc sau 16 bubble. Rate limit và lỗi restore
dừng, không tự tạo chat thay thế/failover. 200/200 test qua; mô phỏng 20 bước
+ restart giữ 1 thread/1 tab. Không tạo chat Muse thật trong lần kiểm chứng
này. Gói mới: `dist/Muse-0.8.1/Muse.exe`.

Bản 0.8.2 hoàn thiện dán ảnh bằng native paste/shortcut, preview/bỏ ảnh và
gửi image-only. Có giới hạn 4 ảnh/16 MiB; không mất draft khi chưa kết nối.
ID riêng ngăn upload lại cùng ảnh trong mỗi bước, nhưng dán mới cùng pixels
vẫn được gửi. 203/203 test qua; kiểm tra WinForms/HTTP ảnh chạy nền qua.
Đã dọn 6 release cũ sau kiểm thử, chỉ giữ `dist/Muse/Muse.exe`, giảm ròng
khoảng 1,37 GiB. Từ nay publish qua `scripts/Publish-Desktop.ps1` để tự dọn
các bản cũ sau khi test/check-installation thành công; giữ dữ liệu cá nhân.

Bản 0.8.3 nâng cấp giao diện toàn diện theo phong cách Codex:
- Hiện Thinking: Thẻ `ThinkingCard` đếm thời gian thực và ghi nhận các bước suy
  nghĩ của Muse, hỗ trợ bấm thu gọn/mở rộng trực quan.
- Hội thoại Muse & Tool: Đưa các thao tác công cụ (đọc/sửa file, chạy lệnh, tìm kiếm,
  thao tác màn hình) lên thẳng hội thoại bằng thẻ `ToolCallCard` có huy hiệu trạng
  thái, tóm tắt kết quả và liên kết mở nhanh Terminal. Chi tiết log lớn vẫn nằm ở Terminal.
- Nhả chữ (Streaming Typewriter): Hiển thị phản hồi từng ký tự mượt mà với con trỏ
  nhả chữ `▊` và tự động định dạng Markdown.
- Sửa triệt để chồng chữ & vỡ layout: Khắc phục xung đột bộ đệm kép Win32 COMCTL32
  trên `CleanButton` bằng cách chuyển sang kế thừa trực tiếp từ `Control` và triển khai
  `IButtonControl`; sửa đồng bộ màu nền Light/Dark trên `MeshLayout` và `ThemedLabel`
  qua `GetEffectiveBackColor()`, khử hoàn toàn góc đen và bóng chữ ma. 203/203 test qua,
  đã đóng gói phát hành thành công tại `dist/Muse/Muse.exe`.

Bản 0.8.4 sửa vòng chạy: tác vụ hoàn tất sau hơn 30 lượt, ngăn vòng lặp quan sát không tiến triển, hủy trước khi nhận kế hoạch muộn không ghi tệp, chờ tiến trình 45 giây không bị cắt xuống 5 giây và vẫn hủy được.

Bản 0.8.5 bổ sung bộ kiểm thử Turbo: yêu cầu đánh giá dự án chỉ yêu cầu đọc, không ép sửa; lỗi đọc lại DOM không cooldown tài khoản và không gửi lại tự động, còn HTTP 429 thật vẫn cooldown. Tổng bộ test chính hiện là 219, đã qua toàn bộ với Chrome headless.
