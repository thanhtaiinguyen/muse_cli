# Muse desktop

Mở `dist\Muse\Muse.exe` rồi chọn dự án và kết nối Muse.
UI không yêu cầu key, cấu hình server hay cookie thủ công.

Xem [README](../../README.md) và [cách dùng](../../WORKSPACE.md).

Từ source:

```powershell
dotnet run --project src/Muse2Api.WinForms -c Release
```

Đóng gói Windows độc lập:

```powershell
dotnet publish src/Muse2Api.WinForms -c Release -r win-x64 --self-contained true -o dist/Muse
```

Bản desktop quản lý gateway trong cùng tiến trình; `DesktopProfile` tự sinh và
lưu key nội bộ, `DesktopGatewayHost` bind cổng ngẫu nhiên trên loopback. Không dùng
key/cổng của gateway bên ngoài. Hồ sơ cũ được đọc để nhập tài khoản khi cần,
không tiếp tục yêu cầu người dùng quản lý file cấu hình kỹ thuật.

Từ 0.9.0, hộp thoại Cài đặt có thêm ba mục:

- **Tài khoản Muse**: thêm nhiều tài khoản cùng lúc, bật/tắt hoặc xóa từng tài khoản.
- **Kết nối OpenAI (trên máy này)**: Base URL `http://127.0.0.1:<cổng>/v1` kèm API key để dùng với các client theo chuẩn OpenAI; có thể tạo/thu hồi/xóa key riêng.
- **Kỹ năng**: cài skill từ file `.md`/`.txt` hoặc `.zip` chứa `SKILL.md`, rồi gọi trong chat bằng `@tenskill`, ví dụ `@fe`. Skill được lưu ở `%LOCALAPPDATA%\Muse2Api.WinForms\Skills`.
