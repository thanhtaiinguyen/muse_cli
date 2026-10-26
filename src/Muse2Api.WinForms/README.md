# muse2api — Windows UI (WinForms, .NET 8)

Một cửa sổ duy nhất để điều khiển gateway, thay cho nhiều cửa sổ PowerShell.
Dùng được với cả gateway .NET trong repo này lẫn gateway Python gốc, vì UI
chỉ gọi HTTP tới host/port đã cấu hình.

## Chạy trên Windows

```powershell
dotnet run --project src/Muse2Api.WinForms
# hoặc mở Muse2Api.sln bằng Visual Studio / Rider rồi chạy project WinForms
```

Trong cửa sổ:

- **Server:** chọn driver (`mock` không cần tài khoản, `browser` cần tài khoản
  thật + Chromium của Playwright), host/port, key, thư mục gateway, rồi bấm
  **Khởi động server**. Nhật ký server hiện ngay bên dưới. UI ưu tiên chạy
  `Muse2Api.dll` đã build sẵn, nếu chưa có thì chạy `dotnet run --project`
  (sẽ build lần đầu, hơi lâu).
- **Chat thử:** nhập câu hỏi, bấm **Gửi**.
- **Quản lý:** bấm **Trạng thái / Tài khoản / Metrics**, **Mở Dashboard**,
  và thêm tài khoản nếu cần (chỉ trên máy của bạn, không gửi cookie đi đâu khác).

Cấu hình UI lưu ở `ui_config.json` cạnh file `.exe` (gồm key local).
**Đừng commit file này.**

## Đăng nhập và nạp tài khoản (LoginForm)

Đây là cách khuyên dùng để đưa tài khoản vào gateway, thay cho việc dán
cookie thủ công hay cài extension trình duyệt như bản Python:

1. Khởi động gateway ở tab **Server**, nhập **admin key**.
2. Ở tab **Quản lý**, bấm **Đăng nhập muse.ai...** — một cửa sổ mới mở
   trang `https://muse.ai` bằng WebView2 (Edge) nhúng trong app.
3. Đăng nhập bình thường trong trang đó (mật khẩu, SSO, 2FA đều do chính
   trang muse.ai xử lý). **UI không bao giờ hỏi hay đọc mật khẩu.**
4. Khi đủ cookie phiên bắt buộc (`hatch_sess` +
   `hatch_native_auth_device`, tuỳ chọn `hatch_gw`/`hatch_vml`), nút
   **Nạp tài khoản này vào gateway** được bật.
5. Bấm nút đó: UI gửi chỉ danh sách cookie phiên (kèm hạn, nếu có) tới
   **gateway local** qua `POST /admin/accounts` với admin key. Gateway
   lưu vào `data/accounts.json` và trả về cookie đã che; UI chỉ hiển thị
   tên cookie và kết quả, **không hiển thị giá trị cookie đầy đủ**.
6. Phiên WebView2 lưu ở `%LOCALAPPDATA%\Muse2Api.WinForms\WebView2` nên
   lần sau không cần đăng nhập lại; nút **Xóa phiên đăng nhập** trong
   cửa sổ login sẽ xoá hết cookie đó.

Lưu ý an toàn:

- Cookie chỉ được gửi tới gateway local đã cấu hình, không gửi đi nơi khác.
- File `data/accounts.json` của gateway chứa cookie phiên — **đừng chia sẻ
  hay commit file đó**, giống như bản Python dặn về cookie.
- Nếu máy chưa có Edge WebView2 Runtime, cửa sổ login sẽ báo rõ và hướng
  dẫn cài runtime (miễn phí, có sẵn trên Windows 10/11); khi đó vẫn dùng
  được cách dán cookie thủ công ở tab Quản lý.

## Kiểm tra tài khoản vừa nạp

Ở tab **Quản lý**, mục **Kiểm tra tài khoản**:

- **Tải danh sách**: lấy tài khoản từ gateway (cookie đã che sẵn).
- **Kiểm tra quota**: gọi `GET /admin/accounts/{id}/quota`. Đây là cách
  kiểm tra sức khỏe tài khoản mà không tốn lượt chat. Với driver
  `browser`, quota đọc từ Settings → Usage của muse.ai nên lỗi ở đây
  (hết hạn, tài khoản bận) chính là tín hiệu tài khoản có vấn đề. Với
  driver `mock`, quota chỉ là giá trị giả lập — không dùng để kết luận
  tài khoản thật.
- **Gia hạn session**: gọi `POST /admin/accounts/{id}/renew`, thay cookie
  bằng cookie mới từ muse.ai. UI hỏi xác nhận trước vì thao tác này
  thay đổi cookie đã lưu.

Sau khi nạp bằng LoginForm, UI tự chọn tài khoản vừa nạp để bạn bấm
**Kiểm tra quota** ngay mà không cần tìm lại trong danh sách.

## Lưu ý

- WinForms chỉ chạy trên Windows. Trên Linux/macOS, project vẫn build được
  (để kiểm tra lỗi biên dịch) nhưng không chạy được giao diện — hãy dùng
  UI Tkinter ở repo Python (`tools/windows_ui/`) hoặc gọi API bằng curl.
- Đóng cửa sổ sẽ dừng luôn server mà UI đã khởi động.
