# Điều khiển cửa sổ trong Muse 0.8.11

## Dùng bằng lời nhắn

1. Chọn dự án và kết nối Muse như khi đọc/sửa code.
2. Mở ứng dụng cần thử và đưa lên phía trước. Khi Muse gọi thao tác giao diện
   đầu tiên, tool TỰ CHỌN cửa sổ đang ở phía trước (foreground); nếu chính Muse
   đang ở phía trước thì chọn cửa sổ trên cùng theo thứ tự Z-order. Không cần mở
   hộp chọn thủ công. Chỉ khi không tự chọn được (không có cửa sổ phù hợp) hộp
   chọn mới hiện ra. Muốn dùng cửa sổ khác với cửa sổ đã tự chọn, bấm **Màn hình**
   và chọn thủ công; lựa chọn thủ công áp dụng cho các thao tác sau trong lượt.
3. Ví dụ: `Đọc giao diện cửa sổ đã chọn, nhập MUSE_UI_OK vào ô tìm kiếm, bấm nút
   Tìm và kiểm tra kết quả.` Hoặc `Chụp cửa sổ đã chọn và mô tả các nút đang có.`
4. Khi muốn dừng, bấm **Dừng** hoặc nhấn **Esc**. Quyền cũng hết khi kết thúc lượt,
   chuyển dự án hoặc đóng Muse. Lượt mới cần chọn/cho phép lại.

Ảnh và nội dung giao diện được gửi tới Muse để hiểu/đề xuất thao tác. Chỉ chọn
cửa sổ bạn muốn chia sẻ. Không thao tác ô mật khẩu; ảnh che các vùng được UIA
đánh dấu là ô mật khẩu. Không chụp toàn desktop hoặc tự mở cửa sổ thử.

## Phần được bổ sung/sửa

- Tự chọn cửa sổ (WindowAutoPicker + TryAutoAuthorize): ưu tiên gợi ý tên nếu
  có, rồi cửa sổ foreground (hoặc cửa sổ gốc sở hữu nó), rồi cửa sổ trên cùng
  theo Z-order (EnumWindows). Không bao giờ tự chọn cửa sổ của chính Muse, cửa
  sổ không có tiêu đề hoặc tiến trình shell (SearchHost,
  StartMenuExperienceHost, ShellExperienceHost, TextInputHost, LockApp).
  DesktopComputer.ExecuteAsync thử tự chọn trước; không được mới gọi hộp chọn
  qua RequestAuthorization. Nút Màn hình vẫn chọn thủ công/đổi cửa sổ như cũ.
- Hộp chọn có lọc tên/tiêu đề, làm mới, trạng thái rỗng, Hủy và Tắt điều khiển.
  Không giữ một mục không còn khớp bộ lọc hoặc ứng dụng đã khởi động lại.
- UIA chạy trên một thread MTA sống lâu, ngoài luồng giao diện. Mỗi lời gọi có
  timeout 12 giây; nếu provider cũ chưa thoát, lời gọi sau chờ hàng đợi tối đa
  2 giây rồi báo rõ, không chờ vô hạn hoặc tạo thêm hàng loạt worker.
- Mã phần tử có namespace mới sau mỗi lần đọc, tránh e0/e1 bị trỏ sang phần tử
  khác. Trước thao tác kiểm tra lại phạm vi, khả dụng và tên/loại phần tử. Assert
  có thể đọc nhãn mới sau thao tác; nhãn cũ không được dùng để bấm phần tử đã đổi.
- Kiểm tra lặp không coi ID quan sát mới là tiến triển. UIA bỏ các ID tạm khi
  so trạng thái; ảnh có fingerprint nội dung. Đọc/chụp liên tiếp cùng kết quả
  vẫn bị dừng như quan sát không tiến triển, thay vì gọi web liên tục.
- Hỗ trợ UIA descendants thuộc renderer Chromium/Electron bằng kiểm tra cây
  cha thật, không loại nhầm mọi phần tử có PID khác. Ngoài cửa sổ gốc, chỉ nhận
  menu/hộp thoại có chuỗi owner thuộc cửa sổ đó và cùng PID; không mở rộng sang
  mọi cửa sổ của ứng dụng hoặc ứng dụng khác.
- Giao diện lớn có query, phân trang offset/limit và đọc cây con theo element_id.
  Mỗi kết quả tối đa khoảng 40.000 ký tự; có cờ cắt/next_offset, giới hạn độ sâu
  và 1.500 node quét/lần. Cần lọc/đọc vùng con nếu cây quá lớn hoặc provider chậm.
- Ảnh trả kích thước thật, kích thước ảnh thu nhỏ và tỉ lệ X/Y. Tọa độ chuột được
  chuyển theo từng trục, hỗ trợ màn hình có tọa độ âm và kích thước ảnh làm tròn.
  Thread thao tác dùng tọa độ DPI vật lý. Ảnh cũ bị bỏ sau mọi thao tác UI, kể cả
  thao tác thất bại một phần; cửa sổ di chuyển/đổi kích thước cần chụp lại.
- Chuột phải/bấm đúp/kéo thả/cuộn dọc-ngang được nối xuyên parser → executor →
  native. Nếu không có ScrollPattern, có thể dùng điểm trên ảnh mới để cuộn.
  Kiểm tra cửa sổ tại điểm thao tác, thả nút chuột/phím tổng hợp khi lỗi/hủy.
- Nhập trực tiếp bằng ValuePattern hoặc Unicode; khi canvas/editor không có
  ValuePattern, bấm ô nhập từ ảnh rồi gõ vào phần tử đang focus trong phạm vi.
  Dừng nếu focus đổi; không tự gõ vào ô mật khẩu. Esc, phím chuyển ứng dụng/hệ
  thống và phím đóng ứng dụng không có trong tập phím hỗ trợ.
- “Nhập vào ô tìm kiếm”/“chạy test UI” không bị ép thành đọc file hoặc chạy shell.
  Yêu cầu đọc file/chạy dotnet thật vẫn phải có biên nhận tương ứng. Mọi thay đổi
  giao diện làm mất bằng chứng assert/ảnh cũ; gửi input không đồng nghĩa test đạt.

## Giới hạn và kiểm thử

- Windows vẫn có thể từ chối đưa cửa sổ lên trước hoặc chặn input vào ứng dụng
  có quyền cao hơn. Không hỗ trợ secure desktop/UAC, ứng dụng khác phạm vi hoặc
  nhập mật khẩu. Khi bị chặn, đưa cửa sổ thường lên trước/chọn lại; không dùng shell
  để vượt quyền. Hộp thoại độc lập/khác tiến trình cần được người dùng chọn riêng.
- Ảnh cần cửa sổ nằm trọn trên các màn hình và không bị ứng dụng khác che. Vùng
  menu ngoài khung ảnh hoặc UI canvas không có thuộc tính để assert có thể cần
  chụp/chọn lại hoặc xác minh thủ công; không giả lập OCR/khẳng định test đạt.
- Esc/Dừng chặn các thao tác tiếp theo và thu hồi quyền. Không thể hoàn tác input
  đã được Windows/provider nhận. Nếu COM provider bị treo mãi, có thể phải đóng/mở
  lại Muse; app báo lỗi hữu hạn và vẫn dùng được chat/file/terminal.
- Đã kiểm tra UIA thật với helper có HWND nhưng **không hiển thị**: đọc, lọc,
  ID mới/cũ, cấm tương tác phần tử ẩn và không đổi foreground. Còn có kiểm tra nền
  tọa độ, phím, đối số, MTA, timeout/hủy; hộp chọn được kiểm tra layout ngoài màn hình.
- Bốn bài cần hiển thị fixture và gửi chuột/phím thật vẫn opt-in, không chạy vì
  yêu cầu chỉ kiểm thử nền. Bài mới bao gồm chuột phải/đúp/kéo thả/cuộn. Việc kiểm
  tra với app thật và qua tài khoản Muse vẫn cần người dùng thử, chưa được coi là
  đã xác minh chỉ từ mock hoặc từ kiểm thử cửa sổ ẩn.

Tham khảo: [UIA threading](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading),
[UI Automation tree](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-treeoverview),
[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput),
[MOUSEINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput).
