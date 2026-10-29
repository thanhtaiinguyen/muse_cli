# Kết quả rà soát và sửa lỗi

Rà soát ngày 27/10/2026: đọc các project gateway, WinForms, coding agent và test;
kiểm tra build trên Windows/.NET 8, bổ sung test hồi quy và test HTTP localhost.
Không dùng tài khoản/cookie muse.ai thật trong quá trình kiểm tra.

## Các lỗi đã sửa

### Gateway và API

- Body HTTP chunked không có Content-Length bị đọc thành body rỗng.
- Request JSON sai cấu trúc, role rỗng hoặc content sai kiểu gây lỗi 500 thay vì 400.
- Stream báo 200 trước khi nhận token đầu tiên do flush lúc đóng writer, làm mất HTTP status lỗi.
- Lỗi giữa stream bị nuốt, thiếu tín hiệu kết thúc/lỗi; Responses thiếu event name và event done.
- Heartbeat dùng lại ValueTask không an toàn; bổ sung heartbeat cho Responses.
- Producer chạy nền có thể tiếp tục sau khi consumer ngừng đọc; gateway/browser chat chuyển sang async iterator.
- Failover ghi nhận tài khoản đầu tiên thay vì tài khoản thực sự trả lời.
- Pool bỏ qua acquire timeout và giới hạn riêng từng loại tác vụ; lỗi số nguyên/số thực ở mốc chặn video.
- Request log luôn ghi stream=false; lỗi upstream giữa stream bị tính thành request thành công.
- Readiness trả 200 ngay cả khi driver không sẵn sàng.
- Kiểm tra số ảnh, định dạng trả ảnh, prompt trống, mask multipart và giới hạn ảnh upload thiếu nhất quán.
- Thời hạn cookie sau năm 2038 bị mất do ép về Int32.
- Gia hạn thành công không khôi phục tài khoản ở trạng thái invalid.
- Prometheus dùng định dạng số theo locale; bộ lọc status không phân biệt 404 với 4xx.

### Tác vụ và dữ liệu

- ID và thời điểm tạo task không được deserialize, khiến mất đường dẫn poll sau restart.
- Cancel task chỉ cancel token nội bộ mà không truyền tới driver.
- Ghi task/key đồng thời dùng chung file tạm, gây tranh chấp hoặc lưu dữ liệu không nhất quán.
- Task còn chạy sau restart được đánh dấu lỗi và ghi trạng thái lại vào kho.
- File JSON lỗi bị bỏ qua rồi có thể bị ghi đè bằng kho rỗng; nay khởi động báo lỗi và giữ file.
- SQLite connection string không xử lý đúng đường dẫn có dấu chấm phẩy; pooling giữ khóa file sau khi đóng trên Windows.
- Trả sai MIME cho WebP, GIF, WebM và MOV.
- Mock không có tài khoản vẫn cập nhật/lưu account ảo vào pool.
- Các loop nền được quản lý theo vòng đời host; hủy và đợi task khi shutdown, flush key trước khi đóng log.

### Browser và tải ảnh

- Rà soát giới hạn timeout media và tránh kết thúc chat khi nút generating vẫn hiện.
- Đồng bộ lúc khởi tạo browser/context và truy cập cache quota đồng thời; dùng TTL cấu hình.
- Làm mới tab/cache khi cookie đổi, tài khoản bị xóa hoặc renew.
- Tab bị hủy/lỗi được đánh dấu stale để đóng thay vì tái sử dụng lúc upstream còn sinh nội dung.
- Tải ảnh kiểm tra IP ngay trong kết nối, tắt proxy cho đường fetch này và kiểm tra từng redirect để giảm rủi ro DNS rebinding/SSRF.
- Bổ sung chặn IP shared/reserved, kiểm tra MIME/data URL, dữ liệu rỗng và giới hạn 20 MiB.
- Dispose request/response HTTP upstream và giữ cancellation trong vòng poll VM.
- Hướng dẫn cài Chromium dùng script của phiên bản Playwright đã build.

### WinForms

- Thư mục gateway mặc định trỏ nhầm vào `src`; nay tìm thư mục gốc thực tế.
- Key trống khiến chat/quản trị không dùng được sau khi server tự tạo key; UI điền và lưu key.
- Admin key trống không fallback về API key như gateway.
- Tab chat liệt kê cả model ảnh/video; nay lọc model chat và dùng đúng tên model người dùng nhập.
- Validate port trước khi start, hỗ trợ URL IPv6/wildcard và bỏ proxy cho client điều khiển gateway.
- Cấu hình chuyển sang LocalAppData, có đọc cấu hình cũ; xử lý lỗi ghi file.
- Giảm lỗi callback khi đóng form, chặn nạp tài khoản trùng do polling trong lúc submit, dispose timer/client/process.
- Loại reference WPF không dùng do WebView2 tự thêm, sửa cảnh báo WindowsBase.

### Coding agent

- Kiểm tra đường dẫn chỉ theo chuỗi không chặn symlink/junction; bổ sung kiểm tra từng thành phần và bỏ qua link khi liệt kê đệ quy.
- Workspace có dấu phân cách cuối hoặc khác hoa/thường trên Windows bị xử lý sai.
- Lịch sử làm mất tool result, khiến lượt sau thiếu thông tin file/lệnh đã đọc/chạy.
- Lỗi HTTP bị biến thành câu trả lời rỗng và exit 0; nay báo lỗi và trả exit 1 ở chế độ một nhiệm vụ.
- Tham số thiếu giá trị, tùy chọn sai và max-steps không hợp lệ không được kiểm tra; nay trả exit 2.
- Hết giới hạn bước được báo là nhiệm vụ chưa hoàn thành; đợi process bằng async, giới hạn timeout hợp lệ và dispose HttpClient.

## Kiểm chứng

```powershell
dotnet build Muse2Api.sln -c Release
dotnet test Muse2Api.sln -c Release --no-build
```

Kết quả: build 0 lỗi, 0 cảnh báo; 92/92 test chạy qua, 0 test bỏ qua.
Các test HTTP chạy gateway riêng với cổng ngẫu nhiên và dữ liệu tạm, không dùng
cổng hoặc dữ liệu của gateway đang mở. Các test agent bao gồm lệnh shell thật
và kiểm tra thoát workspace qua junction trên Windows.

## Chưa được xác minh

- DOM muse.ai, đăng nhập WebView2, renew session upstream, quota và sinh ảnh/video thật cần tài khoản để kiểm tra.
- Giao diện WinForms đã build; chưa kiểm tra tương tác trực quan với WebView2 trong đợt này.
- Driver HTTP chưa có chat, chưa có matting/native function calling, dashboard đầy đủ và extension; xem README.

Bản Debug đang được tiến trình khác dùng trong phiên rà soát nên xác nhận build
cuối bằng Release, không dừng tiến trình đang chạy.
