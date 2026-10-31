# Kiểm chứng

## Tự động

```powershell
dotnet build Muse2Api.sln -c Release
dotnet test Muse2Api.sln -c Release
```

Hiện tại 219 test: model/prompt, mock driver, auth, SSE/Responses, failover/pool,
media, SQLite, task, agent tools, duyệt/từ chối, hủy tiến trình, encoding/undo,
lịch sử và host desktop.

Để chạy cả bài kiểm tra DOM bằng Chromium headless (cần Edge/Chrome hoặc
Chromium của Playwright đã cài):

```powershell
$env:MUSE_BROWSER_TESTS='1'
dotnet test Muse2Api.sln -c Release --artifacts-path artifacts
```

`LocalToolProtocolTests` kiểm tra JSON được render thành code block, native
tool stream chia chunk, phản hồi chỉ bằng chữ không được báo hoàn tất, đọc đủ
2500 dòng qua nhiều trang, batch edit LF/CRLF, Unicode/đường dẫn có khoảng trắng,
mã thoát lỗi và thay đổi tệp qua PowerShell/CMD. `AgentContinuationTests` kiểm
tra yêu cầu tối ưu UI/thêm tính năng bằng lời thường, bỏ ngữ cảnh VM cũ, buộc
tiếp tục từ chối giả dạng câu hỏi, và không báo hoàn tất sau phản hồi nhầm VM
dù trước đó đã đọc tệp thành công. Bản 0.7 thêm `ToolReliabilityTests`,
`SessionPersistenceTests`, `PlannerTransportTests`: chống ghi từ snapshot cũ/
đoạn code thiếu, đọc danh sách thư mục qua nhiều trang, stdin tiếng Việt, dừng
cả tiến trình con, phân biệt metadata với stdout, kế hoạch JSON riêng và dữ
liệu đăng nhập dùng lại qua restart.

Bản 0.7.1 thêm 9 trường hợp trong `FileSearchTests`: tái hiện lỗi Windows
“The parameter is incorrect” khi tìm trong một file `.cs`, đường dẫn tương
đối/tuyệt đối, Unicode, phân biệt hoa thường, thư mục con, đường dẫn thiếu,
file nhị phân, giới hạn workspace và chuỗi tìm → đọc → sửa giữ BOM/CRLF.
Đã xác nhận 7 trường hợp fail trước sửa và cả 9 qua sau sửa. Lần chạy bộ
đầy đủ có 183 test qua, 2 bài Chromium fail vì browser đóng ngay khi khởi
động. Chạy lại với `MUSE_BROWSER_TESTS=0`: 183 qua, 2 skip. Chưa xác nhận lại
đầu cuối với Muse thật trong bản 0.7.1; không mở cửa sổ kiểm thử.

Bản 0.7.2 thêm 5 bài `ProtocolRecoveryTests`, tất cả fail trước sửa và qua
sau sửa. Phản hồi 0.7.1 thực tế chứa dấu nháy C# chưa được escape; thông báo
cũ lại hướng dẫn sửa đường dẫn. Nay yêu cầu JSON trong code fence để Markdown
không xóa escape, trả lỗi có vị trí JSON chính xác, loại kế hoạch hỏng khỏi
ngữ cảnh gửi tới model và xin lại một action nhỏ. Không chạy phần hợp lệ của
batch có lỗi; dừng có giải thích riêng sau 3 phản hồi lỗi liên tiếp. Test
kiểm chứng hai lần sai định dạng vẫn có thể sửa được, qua duyệt đúng một lần,
giữ BOM/CRLF. Test DOM cũng kiểm tra nguyên vẹn code có nháy, tiếng Việt,
gạch chéo và xuống dòng. Toàn bộ **190/190 qua**, không skip, với
`MUSE_BROWSER_TESTS=1` và `MUSE2API_CHROMIUM_PATH` trỏ Chrome đã có trên máy.

Kiểm thử nền qua Muse thật ở bản 0.7.2: đọc Program.cs, sửa riêng hàm Label
để trả đúng chuỗi `Chế độ "Sáng" · C:\Muse\theme.json`, chạy `dotnet run`
ra `QUOTE_TEST_OK`, exit 0, nhận phản hồi kết thúc. Chạy kiểm tra độc lập
xác nhận `CODE_RESULT completed=True verified=True`. Không mở cửa sổ test.
Log không chứa credential: `artifacts/full-live-quoted-code-latest.log`.

Gói mới: `dist/Muse/Muse.exe` (0.8.2); các bản dùng
cùng hồ sơ đăng nhập/lịch sử của người dùng.

Bản 0.7.3 chỉ đổi cách trình bày chat. Kiểm tra WinForms nền trong
`artifacts/compact-chat-smoke`: 150 thao tác không thêm thẻ chat; khi hoàn
thành chỉ có một tin người dùng và một kết quả. Lưu/mở lại lịch sử giữ cùng
kết quả; câu hỏi cần trả lời, lỗi và hủy vẫn hiển thị. Console giữ các receipt.
Không gọi Show hoặc mở cửa sổ kiểm thử. Đã render và kiểm tra ảnh trạng thái
đang sửa và hoàn tất tại `artifacts/compact-chat-working.png` và
`artifacts/compact-chat-complete.png`. Build không có cảnh báo/lỗi. Không
chạy lại bộ gateway/browser cho thay đổi thuần trình bày này.

Bản 0.8.0 áp dụng Mesh Gradient cho WinForms, giữ tiếng Việt, sáng/tối và chat
gọn. Dùng native controls, gradient cache và icon vẽ vector, không thêm thư
viện web hay animation nền. Bảng tệp/thay đổi/terminal dùng tab có thể thao
tác bằng bàn phím; toolbar xuống hàng ở cửa sổ nhỏ.

Đã chạy lại **190/190 test qua**, có Chrome headless, không skip. Kiểm tra
WinForms nền `artifacts/mesh-ui-smoke` xác nhận:
- Mở/sửa/lưu file thật trong fixture, giữ nội dung/BOM/CRLF.
- Nút Tệp/Thay đổi/Terminal mở đúng trang; nút tab và Ctrl+Tab chuyển trang.
- Chọn CMD cập nhật shell, chạy lệnh trả `MESH_TERMINAL_OK`, exit 0.
- Busy khóa Gửi/bật Dừng, hoàn tất bật lại Gửi; lịch sử không mất tin.
- Render sáng/tối, chat và drawer ở 1280×820 và 900×640; kiểm tra nút toolbar
  không vượt khung. Đã xem ảnh và sửa lỗi cắt nút, màu chat sau đổi theme,
  footer bị cắt và tên tab khi drawer hẹp.
- Text/muted/accent trên surface và chữ trắng trên hai đầu gradient của nút
  primary đạt tối thiểu 4.5:1 ở cả hai palette.

Log: `artifacts/mesh-ui-smoke-latest.log`; ảnh: `artifacts/mesh-*.png`.
Kiểm tra 150 thao tác trong `compact-chat-smoke` cũng qua. Không mở cửa sổ
kiểm thử; đây là kiểm chứng control/render trong bộ nhớ. Chưa kiểm tra mọi
màn hình/DPI thực hoặc thao tác màn hình đầu cuối qua Muse trong bản này.

Bản 0.8.1 xử lý hai lỗi người dùng phát hiện sau 0.8.0:

- **Vẽ chồng/DPI:** đối chứng DLL 0.8.0 bằng Graphics có translation/clip:
  1040 pixel bị vẽ ngoài nút; bản sửa là 0. TextRenderer nay giữ clipping và
  translation; nền nút được vẽ lại đầy đủ. MainForm/PreferencesForm scale từ
  96 DPI. Fixture `artifacts/ui-repair-smoke` dùng cấu hình DPI như ứng dụng,
  xác nhận DPI hệ thống **120 (125%)**, nút không bị cắt, Cài đặt sáng/tối,
  mở rộng chi tiết và lựa chọn shell/theme/read-only. Không gọi Show; nội
  dung được render sau khi Form đã áp dụng scale. Ảnh `ui-repair-*.png` đã xem.
  Không coi ảnh render panel 0.8.0 là bằng chứng cho native paint/DPI.
- **Một chat Muse:** `SingleConversationTests` có 10 trường hợp. Trang Muse
  mô phỏng chạy qua 20 bước, restart rồi gửi tiếp: tổng **1 thread, 21 lượt
  gửi, 1 tab**. Không áp giới hạn 16 bubble để mở thread mới ở local mode.
  Mapping chỉ lưu URL/quyền sở hữu/hash ảnh, không lưu lại code/history vào
  account metadata. Chỉ gửi quan sát mới sau kế hoạch trước. Cả HTTP 429 và
  thông báo rate limit hiển thị đều dừng sau một lần gửi. Thread cũ không
  mở được không dẫn tới tạo thread thay thế. Không failover local requests.

Đã chạy bộ **200/200 test qua**, không skip; kiểm tra mesh-ui-smoke vẫn giữ
BOM/CRLF, editor/CMD/tab/lịch sử. Các bài browser trên đây dùng fixture
headless, không tạo chat Muse thật hay dùng thêm quota của người dùng.
Đã sửa fixture heartbeat để chờ headers trước khi phát lỗi, thay vì phụ
thuộc delay 100ms dễ race khi các bài browser khởi động song song.
Chưa kiểm chứng đầu cuối với dịch vụ Muse thật ở bản 0.8.1 hoặc mọi DPI.

Bản 0.8.2: **203/203 qua** khi chạy quy trình publish. `ChatImageTests` kiểm
tra ảnh vào context của agent, image-only, ID ảnh ổn định giữa các bước và
đổi ID khi người dùng dán lại, không ghi base64 vào JSON lịch sử. Test browser
mô phỏng xác nhận 3 lượt trong cùng chat với ID paste-1/paste-1/paste-2 chỉ
upload 2 lần, tạo 1 thread. Không dùng quota Muse thật.

`artifacts/paste-image-smoke` kiểm tra PNG stream/Bitmap của IDataObject,
Ctrl+V/Shift+Insert/WM_PASTE, fallback text, preview/bỏ ảnh, giới hạn số ảnh,
giữ draft khi chưa kết nối, gửi ảnh-only qua HTTP loopback với đúng kích
thước ảnh và conversation ID. Không thay clipboard hệ thống, không mở cửa
sổ test. Kiểm tra Form sáng/tối ở DPI 125% cũng qua với nút Dán ảnh/Gửi.

`scripts/Publish-Desktop.ps1` chạy test rồi mới publish/check-installation;
kiểm tra version, dữ liệu riêng tư, process đang dùng release và đường dẫn
không có link trước khi dọn. Sau lần publish 0.8.2: xóa 6 bản cũ, tổng
1.761.121.632 byte; chỉ còn `dist/Muse`, dung lượng giảm ròng khoảng 1,37 GiB.
Không chạm LocalAppData hay source. Quy trình này áp dụng cho các bản sau.

`DesktopHostTests` kiểm tra hồ sơ trắng tự sinh credential, gọi admin/API mà
không có thiết lập thủ công, cổng riêng giữa nhiều instance, giữ key/tài khoản
qua restart và giải phóng tài nguyên khi khởi động lỗi.

`WorkspaceAgentTests` chạy HTTP streaming qua model script, đọc/sửa file thật
trong thư mục tạm và kiểm tra ranh giới duyệt, phát hiện file đổi và cancellation.

## Bản đóng gói

```powershell
dotnet publish src/Muse2Api.WinForms -c Release -r win-x64 --self-contained true -o dist/Muse
.\dist\Muse\Muse.exe --check-installation
```

Kiểm tra lệnh trả exit 0, có đủ runtime, SQLite và driver Playwright. Kiểm tra
output không chứa `accounts.json`, `api_key`, `keys.json`, `requests.db` hay file
cấu hình cá nhân. Khởi động app không được yêu cầu cài SDK hoặc nhập key.

## Thao tác người dùng

1. Mở Muse.exe, quan sát welcome/gợi ý, không có tab Server hoặc ô API/admin key.
2. Chọn dự án; thử gợi ý và gửi yêu cầu. Khi thiếu tài khoản, ứng dụng dẫn tới
   đăng nhập, không dẫn người dùng tới terminal/tài liệu kỹ thuật.
3. Đăng nhập Muse thật; kết nối tự lưu, quay lại chat. Đóng/mở app và kiểm tra
   kết nối vẫn dùng được. Đổi tài khoản từ Cài đặt.
4. Xem tệp, kiểm tra diff trước/sau, cho phép/từ chối, hoàn tác, dừng thao tác,
   mở lại lịch sử và kiểm tra prompt không mất khi hủy đăng nhập.
5. Mở **Terminal** để xem chi tiết hoặc chạy lệnh thủ công; đổi PowerShell/CMD
   ngay trong bảng. Các thông báo chính phải dễ hiểu.
6. Kiểm tra kích thước cửa sổ nhỏ và DPI 100%/125%/150%; các nút chính không bị che.

Phần kiểm tra giao diện tự động bị người dùng dừng bằng Esc trong phiên này.
Sau đó chỉ tiếp tục sửa source/build; không tiếp tục điều khiển cửa sổ.
Quan sát lại welcome/DPI và đăng nhập mới sau sửa còn cần kiểm chứng.
Khung chat 0.6.1 đã được render offscreen ở cửa sổ rộng 1000px và hẹp 520px,
kiểm tra các khung không chồng lấn. Đây là fixture UI, không điều khiển lại
cửa sổ Muse của người dùng.

## Kiểm chứng 0.7 ngày 28/10/2026

Luồng Muse thật chạy nền đã sửa `Calculator.Add` từ `a - b` thành `a + b`
trong một project C# có BOM và CRLF, gọi `dotnet run`, nhận `ADD_TEST_OK` và
exit 0. Kết quả độc lập: `CODE_RESULT completed=True verified=True`.
Đã dừng/khởi động lại gateway, tải lại account/browser state và gửi chat mới:
`RESTART_LOGIN preserved=True`, `AUTH_AFTER_RESTART ok=True`,
`requestedNewLogin=False`. Không xin mã đăng nhập mới.

Bài chạy thật đã bắt được lỗi model chỉ nhập dữ liệu mà báo xong, và lỗi
model từ chối JSON giả dạng công cụ native. Đã thay bằng schema kế hoạch
`actions` do ứng dụng thực thi, yêu cầu đủ bước nhập/bấm/assert/ảnh phù hợp,
và yêu cầu kiểm tra lệnh thành công sau lần sửa file cuối.

Project `tests/Muse2Api.Computer.Tests` có 2 bài chính sách chạy nền và 3 bài
native mở ứng dụng fixture riêng. Ba bài native đã qua trước khi người dùng
yêu cầu chỉ kiểm tra nền: UIA đọc/set/invoke/assert/checkbox/screenshot, gõ
Unicode và click theo ảnh, từ chối ảnh cũ/cancellation. Lần chạy sau với
`MUSE_COMPUTER_TESTS=0`: 2 passed, 3 skipped; không mở cửa sổ.

Chuỗi UI đầu cuối qua Muse chưa được đánh dấu pass: cửa sổ fixture bị đóng,
sau đó người dùng yêu cầu không mở thêm cửa sổ. Giữ giới hạn kiểm thử này,
không tự chạy lại các bài native.

Chạy chính sách nền:

```powershell
$env:MUSE_COMPUTER_TESTS='0'
dotnet test tests/Muse2Api.Computer.Tests -c Release --artifacts-path artifacts
```

Chỉ khi người dùng yêu cầu kiểm tra có cửa sổ, đặt biến trên thành `1`.
Đăng nhập mới qua WebView, quota/media, app bên thứ ba và DPI khác chưa được
kiểm chứng đầy đủ. Không coi test mocked/scripted là bằng chứng cho các phần này.

Ngày 27/10/2026 đã chạy luồng thật qua gateway desktop và tài khoản Muse đã lưu,
chỉ cho phép sửa tệp fixture và lệnh echo cố định: Muse phát 3 local tool calls
(`read_file`, `edit_file`, `run_command`), tệp đổi BEFORE → AFTER, CMD trả
`exit_code=0` và `MUSE_LOCAL_COMMAND_OK`, sau đó kết thúc hội thoại. Kết quả:
`completed=True localToolCalls=3 expectedFile=True`. Không dùng VM Muse để
thực hiện các thao tác này. Quota/media và đăng nhập mới chưa thuộc kiểm tra này.

Kiểm tra thật bổ sung cho lỗi tiếp tục sửa: lượt đầu “Đọc project của tôi”,
lượt sau yêu cầu tối ưu cài đặt trong `appearance.json` bằng tiếng Việt thông
thường, không nêu tool/protocol. Dữ liệu lịch sử có cả câu nhầm VM như lỗi báo
cáo. Agent sửa đúng `allowLightMode: false` → `true`, giữ `theme: dark`, qua
duyệt giới hạn trong fixture. Kết quả:
`readCompleted=True completed=True editTurnCalls=1 expectedFile=True`.

Đợt sửa vòng chạy 0.8.4 bổ sung kiểm thử: tác vụ hoàn tất sau hơn 30 lượt,
giới hạn chỉ áp dụng khi được truyền rõ ràng, ngăn vòng lặp quan sát không
tiến triển, hủy trước khi nhận kế hoạch muộn không được ghi tệp, chờ tiến
trình 45 giây không bị cắt xuống 5 giây và vẫn hủy được.
Fixture Chrome headless kiểm tra nút Stop ẩn, phản hồi ở nhánh ẩn, nút dừng
phát media không được coi là đang tạo chat, nội dung đang tạo không được
thực thi sớm, phản hồi đứng giữa chừng, lỗi web hiển thị và web dừng mà
không có phản hồi. Không gửi yêu cầu đến Muse thật trong các kiểm thử này.
Kiểm tra WinForms ngoài màn hình xác nhận 150 thao tác chỉ tạo tin người
dùng và kết quả cuối, lịch sử giữ cùng cách hiển thị, lỗi ở giai đoạn cập
nhật giao diện vẫn nhả trạng thái bận; kiểm tra Turbo/chỉ xem, dán ảnh và
bố cục sáng/tối tại DPI hệ thống 120. Không mở cửa sổ kiểm thử.

Đợt 0.8.5 bổ sung tình huống Turbo với yêu cầu “kiểm tra độ hoàn thiện của
project”: chỉ yêu cầu đọc để đánh giá, không ép sửa mã. Native offscreen UI
được kiểm tra qua HTTP loopback để xác nhận mục tiêu và ID cuộc chat không
bị thay đổi khi bật Turbo; Turbo tự duyệt và chế độ chỉ xem vẫn chặn thao tác.
Fixture Chrome mô phỏng nhánh chat dựng lại với số khối AI và nội dung JSON
giống lượt trước, phản hồi tới chậm sau khi nút Stop biến mất, và dựng lại
khối AI cũ mà không có tin người dùng mới (không được xem là kế hoạch mới).
Lỗi đọc lại DOM không cooldown tài khoản hoặc gửi lại tự động; HTTP 429 thật
vẫn cooldown. Cấu trúc nhóm tin nhắn đã được đối chiếu bằng cách chỉ đọc
cuộc chat lỗi đã có, chặn POST; không gửi chat thật trong đợt kiểm tra này.

Sửa cuộn hội thoại sau 0.8.5: ChatTranscript nay bám đáy theo trạng thái stick-to-bottom. Trước đây chỉ cuộn khi thêm thẻ mới nên khi thẻ Thinking thu gọn, thẻ công cụ đổi trạng thái hoặc chữ streaming làm tăng chiều cao thì khung chat đứng yên và nội dung mới tuột khỏi tầm nhìn. Nay mỗi lần chiều cao nội dung thay đổi, nếu người dùng đang ở gần đáy thì khung tự cuộn xuống cuối, kèm một lần cuộn hoãn sau khi layout xong; nếu người dùng đã kéo lên đọc lại thì không bị giật xuống nữa. Kiểm tra render-check ngoài màn hình bổ sung tình huống 30 thẻ công cụ và tin nhắn dài 60 dòng, kết quả SCROLL_CHECK_OK và RENDER_CHECK_OK; build toàn bộ giải pháp 0 cảnh báo, 0 lỗi.

Đợt 0.8.6 tái hiện lượt đọc thứ hai bị lấy lại tool call của lượt đầu khi web
tải lại lịch sử: trước sửa nhận `first.txt, first.txt, second.txt`; sau sửa chỉ
đọc `first.txt, second.txt`, cùng một thread. Mỗi lần gửi nội bộ có dấu mốc
riêng trong tin người dùng; chỉ phản hồi nằm sau đúng dấu mốc mới được dùng,
không suy luận từ việc số khối chat hay văn bản lịch sử thay đổi.
`ToolCallValidationTests` kiểm tra JSON bị cắt, envelope sai kiểu, arguments
sai kiểu, công cụ không tồn tại và batch native có phần tử lỗi: không được
âm thầm coi là câu trả lời cuối hoặc thực hiện phần lệnh hợp lệ trước lỗi.
`tests/Muse2Api.WinForms.Checks` chạy STA/message loop ngoài màn hình, tái hiện
và kiểm tra vị trí khi lượt chat thứ hai thêm thinking/tool cards, cùng việc
giữ vị trí khi người dùng đang đọc lịch sử. Script publish chạy kiểm tra này
trước khi thay hoặc xóa bản cũ; tuyệt đối không gọi Show/ShowDialog.

Đợt tích hợp .NET 10 0.8.7: giữ bộ kiểm tra lượt thứ hai/cuộn/tool call của
0.8.6, bổ sung kiểm tra Turbo với đường dẫn Windows tương đương, alias shell,
cấu hình build/credential nhạy cảm, cache browser session mã hóa bị hỏng và
khôi phục. Preview marker được kiểm tra bằng WinForms ngoài màn hình để xác
nhận exe dùng hồ sơ riêng; gateway và workspace đều tôn trọng profile override.
266/266 core/browser đạt; 2 computer tests đạt, 3 bài cần cửa sổ thật để lại
cho kiểm tra tay. Xem MANUAL_TESTING_NET10.md. Script Prepare-PreviewProfile.ps1
được thử bằng file giả: không ghi nguồn và không ghi đè dữ liệu preview có sẵn.
Người dùng đã báo kiểm tra tay OK ngày 31/10/2026 trước khi yêu cầu push bản tích hợp lên GitHub.

Khi báo lỗi, gửi bước xảy ra và thông báo trong giao diện. Không gửi mật khẩu,
giá trị cookie, key hay file hồ sơ cá nhân.
