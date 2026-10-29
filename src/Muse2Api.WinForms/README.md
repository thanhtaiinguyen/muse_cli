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
