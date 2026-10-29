# Kiểm thử nhánh nâng cấp Muse (.NET 10)

Nhánh: `upgrade/net10-security-ci-release`

**Chưa merge vào main**. Bản chạy từ source có thể thay đổi dữ liệu phiên khi dùng chung thư mục hồ sơ. Để bảo vệ bản Muse 0.8.5, **luôn dùng profile riêng khi test**.

## 1. Lấy branch trên Windows 11

Mở PowerShell tại thư mục repository và chạy:

```powershell
git status
git fetch origin
git switch --track origin/upgrade/net10-security-ci-release
```

Nếu đã tạo branch trên máy trước đó, thay lệnh cuối bằng `git switch upgrade/net10-security-ci-release`.

Không chạy lệnh switch khi có thay đổi chưa commit; cần commit hoặc sao lưu trước.

## 2. Cài .NET 10 SDK và chạy kiểm thử

Cài .NET 10 SDK từ https://dotnet.microsoft.com/download/dotnet/10.0.

```powershell
dotnet --version
dotnet restore Muse2Api.sln
dotnet build Muse2Api.sln -c Release
dotnet test tests/Muse2Api.Tests/Muse2Api.Tests.csproj -c Release
dotnet test tests/Muse2Api.Computer.Tests/Muse2Api.Computer.Tests.csproj -c Release
```

Các bài browser cần Edge/Chrome hoặc Playwright Chromium đã được cài. Chạy thêm:

```powershell
$env:MUSE_BROWSER_TESTS = '1'
dotnet test tests/Muse2Api.Tests/Muse2Api.Tests.csproj -c Release
Remove-Item Env:MUSE_BROWSER_TESTS
```

## 3. Chạy thử bằng hồ sơ riêng (khuyến nghị)

```powershell
$env:MUSE_DESKTOP_PROFILE_ROOT = Join-Path $env:LOCALAPPDATA 'Muse2Api.WinForms-BranchTest'
dotnet run --project src/Muse2Api.WinForms -c Release
```

Mở ứng dụng và đăng nhập Muse trong profile mới. Hồ sơ đang dùng ở
`%LOCALAPPDATA%\Muse2Api.WinForms` **không bị sửa hoặc chuyển đổi**.
Kiểm tra: đăng nhập, đổi tài khoản, chat, đọc file, sửa có duyệt, Turbo có duyệt lệnh,
điều khiển cửa sổ thử nghiệm, khởi động lại và giữ lịch sử.

Khi xong, đóng ứng dụng và chạy:

```powershell
Remove-Item Env:MUSE_DESKTOP_PROFILE_ROOT -ErrorAction SilentlyContinue
```

**Không xóa thư mục profile chính.** Thư mục test vẫn tồn tại để phục vụ kiểm tra lại.

## 4. Tránh dùng chung profile với phiên bản cũ

Trên Windows, source mới dùng DPAPI để mã hóa file `Gateway/accounts.json`,
`Gateway/api_key` và `Gateway/browser-sessions/*.json`. Mã hóa gắn với tài khoản Windows hiện tại.

Nếu chạy bản nâng cấp mà **không** bật `MUSE_DESKTOP_PROFILE_ROOT`, file plaintext cũ có thể được chuyển đổi.
Phiên bản 0.8.5 trước đó không đọc được định dạng mới. Vì vậy hãy sao lưu profile trước khi thử trực tiếp:

```powershell
$source = Join-Path $env:LOCALAPPDATA 'Muse2Api.WinForms'
$backup = Join-Path $env:USERPROFILE 'Desktop\MuseProfile-before-net10.zip'
if (Test-Path $source) {
    Compress-Archive -Path $source -DestinationPath $backup -CompressionLevel Optimal -Force
    Write-Host "Profile backup: $backup"
}
```

Bản sao lưu chứa thông tin đăng nhập nhạy cảm. Lưu riêng tư, không đẩy lên GitHub. Nếu phục hồi
bản 0.8.5, **đóng tất cả tiến trình Muse**, sao lưu dữ liệu mới, rồi phục hồi thư mục gốc từ bản ZIP
trên cùng máy/tài khoản Windows. Không dùng `git reset --hard` để xóa dữ liệu người dùng.

## 5. GitHub Actions và gói ZIP

- Mở tab **Actions → CI (.NET 10 / Windows)** để xem build, core test, Windows computer tests và secret scan.
- Vào **Actions → Package Muse for Windows → Run workflow**, chọn đúng branch để build gói riêng.
- Sau khi job thành công, tải artifact ZIP và kiểm tra SHA256.
- Workflow package không công khai Release nếu không chọn `publish_release=true` và nhập tag phù hợp.

Script `scripts/Publish-Desktop.ps1` có thể thay thế bản Muse đã đóng gói ở `dist/Muse`. Chỉ chạy
khi có chủ đích; để test source hãy dùng `dotnet run` với profile tách biệt.

## 6. Tiêu chí đạt

Build không lỗi; tất cả test CI bắt buộc qua; đăng nhập giữ qua restart; không có cookie plaintext
trong các file DPAPI; không phát sinh thao tác shell trong Turbo khi chưa chấp thuận; bản Windows
được đóng gói và kiểm tra `--check-installation` thành công.

Chưa có kết quả kiểm thử Windows/Muse thực tế thì chưa xem bản này là release ổn định.
