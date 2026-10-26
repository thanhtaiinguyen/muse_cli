# Hướng dẫn test trên Windows (cho người pull project về)

> Project này chưa từng chạy với tài khoản muse.ai thật. Các bước dưới
> là checklist để test, và khi gặp lỗi thì báo lại đúng mục nào để sửa.

## 0. Chuẩn bị

```powershell
git clone <repo-url>   # hoặc copy thư mục muse2api-dotnet về máy Windows
cd muse2api-dotnet
dotnet --version       # cần .NET 8 SDK
dotnet build Muse2Api.sln
```

## 1. Gateway với driver mock (không cần tài khoản)

```powershell
dotnet run --project src/Muse2Api
# mở http://127.0.0.1:18610/healthz -> {"ok":true,...}
```

- Chat mock: `POST /v1/chat/completions` với model `muse-chat` phải trả
  `[mock:muse-chat] You said: ...`
- Nếu bước này lỗi, đó là lỗi gateway, chưa liên quan tới login/browser.

## 2. UI WinForms

```powershell
dotnet run --project src/Muse2Api.WinForms
```

- Tab **Server**: nhập admin key, bấm **Khởi động server**, xem log chạy.
- Tab **Chat thử**: gửi câu hỏi với driver mock, xem trả lời.
- Tab **Quản lý**: bấm **Trạng thái / Tài khoản / Metrics**.

## 3. Đăng nhập và nạp tài khoản (cần tài khoản muse.ai thật)

1. Khởi động gateway, nhập admin key ở tab **Server**.
2. Tab **Quản lý** → **Đăng nhập muse.ai...**
3. Đăng nhập trong trang muse.ai hiện ra. **Không gõ mật khẩu vào bất
   kỳ ô nào của tool** — chỉ gõ vào trang muse.ai.
4. Khi trạng thái báo đủ cookie (`hatch_sess`,
   `hatch_native_auth_device`), nhập nhãn rồi bấm
   **Nạp tài khoản này vào gateway**.
5. Kiểm tra: tab **Quản lý** → **Tài khoản** — tài khoản mới phải hiện
   với cookie đã che, `missing_cookies` rỗng.
6. Kiểm tra sức khỏe: ở mục **Kiểm tra tài khoản**, tài khoản vừa nạp
   đã được tự chọn — bấm **Kiểm tra quota**. Với driver `browser`,
   quota phải đọc được từ muse.ai; nếu báo lỗi hết hạn/bận, đó chính
   là tín hiệu cần xử lý (đăng nhập lại hoặc gia hạn session), không
   phải lỗi UI.

Nếu cửa sổ login báo thiếu WebView2 Runtime, cài Edge WebView2 Runtime
từ https://developer.microsoft.com/microsoft-edge/webview2/ rồi mở lại.
Nếu nút Nạp vẫn khóa sau khi đăng nhập xong, bấm **Kiểm tra lại cookie**
(trang muse.ai là SPA nên cookie có thể set mà không tải lại trang).

## 4. Browser driver với tài khoản thật (tùy chọn, nâng cao)

```powershell
dotnet tool install --global Microsoft.Playwright.CLI
playwright install chromium
```

Rồi chạy gateway với `MUSE2API_DRIVER=browser` (hoặc chọn `browser` ở
tab Server của UI), và thử chat với tài khoản vừa nạp. Lần đầu chạy,
các lỗi DOM của muse.ai (nếu có) sẽ cần sửa ở `src/Muse2Api/Dom.cs` —
hãy báo lại nội dung lỗi và, nếu được, đoạn HTML/bubble liên quan
(không gửi cookie).

## 5. Khi báo lỗi về cho dev, gửi kèm

- Bước nào trong checklist trên bị lỗi (1 / 2 / 3 / 4).
- Thông báo lỗi đầy đủ trên UI hoặc log server (đã che key/cookie).
- Driver đang dùng (`mock` hay `browser`), phiên bản Windows và
  .NET SDK (`dotnet --version`).
- **Không gửi mật khẩu, không gửi giá trị cookie đầy đủ, không gửi
  file `data/accounts.json`.**

## 6. Coding agent (muse-agent, kiểu Codex)

```powershell
dotnet run --project src/Muse2Api.Agent -- --workspace C:\code\myapp --api-key <api-key-cua-gateway>
```

Trong REPL, thử: "liệt kê file trong thư mục này", rồi "đọc file X",
rồi "chạy dotnet test". Mỗi bước ghi file / chạy lệnh đều hỏi xác nhận
trừ khi dùng `--yes`. Agent chỉ đọc/ghi trong workspace đã chọn.

Lưu ý: agent chỉ thông minh bằng model qua gateway. Với driver mock,
model chỉ echo nên agent không tự sinh tool call thật — cần tài khoản
thật + browser driver (bước 4) mới test được vòng lặp agent thật.
Chi tiết và giới hạn xem `src/Muse2Api.Agent/README.md`.
