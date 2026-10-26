# muse2api — .NET port (ASP.NET Core 8)

Bản port sang .NET của repo Python [www222fff/muse2api](https://github.com/www222fff/muse2api)
(FastAPI). Mục tiêu: cùng API OpenAI-compatible, cùng cấu hình bằng biến môi
trường `MUSE2API_*`, để bạn — người chuyên .NET — có thể tiếp tục phát triển
bằng C# thay vì Python.

> **Trạng thái thật:** đây là bản port **giai đoạn 2, đã build và chạy thử với
> driver mock**. Browser driver đã được port sang Playwright (xem dưới),
> nhưng chưa kiểm chứng live với tài khoản thật — xem chi tiết ở cuối file.

## Chạy

```sh
dotnet run --project src/Muse2Api
# hoặc, sau khi build:
# MUSE2API_DRIVER=mock MUSE2API_PORT=18610 dotnet src/Muse2Api/bin/Debug/net8.0/Muse2Api.dll
```

Mặc định nghe ở `http://127.0.0.1:18610` (driver `mock`, không cần tài khoản).
Cấu hình giống bản Python: `MUSE2API_DRIVER`, `MUSE2API_PORT`,
`MUSE2API_API_KEY`, `MUSE2API_ADMIN_KEY`, `MUSE2API_DATA_DIR`, …

```sh
curl http://127.0.0.1:18610/healthz
curl -H "Authorization: Bearer $KEY" http://127.0.0.1:18610/v1/models
curl -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  http://127.0.0.1:18610/v1/chat/completions \
  -d '{"model":"gpt-4o","messages":[{"role":"user","content":"hello"}]}'
```

Trên Windows: mở solution `Muse2Api.sln` bằng Visual Studio / Rider, hoặc
`dotnet run` trong PowerShell — nhưng với bản .NET này bạn cũng có thể dùng
lại UI Tkinter ở repo Python, hoặc tự viết WinForms/WPF gọi API trên.

## Đã port

| Phần Python | .NET |
|---|---|
| `config.py` (env `MUSE2API_*`) | `Settings.cs`, cùng tên biến và giá trị mặc định |
| `core/models.py` (registry + alias) | `Models.cs`, port 1:1, kèm test |
| `core/prompt.py` (flatten multi-turn) | `Models.cs` (`Prompt`), port 1:1, kèm test |
| `errors.py` | `Errors.cs` (cùng status/type/code OpenAI) |
| `drivers/mock.py` (chat, ảnh PNG 64×64, video placeholder, quota) | `Drivers.cs` (`MockDriver`), kèm test |
| `accounts/model.py` + `pool.py` (LRU/round-robin/affinity, cooldown, failover, JSON persistence) | `Store.cs` (`AccountPool`) |
| `auth/keys.py` (key `m2a-…`, chỉ lưu SHA-256) | `Store.cs` (`KeyStore`) |
| `services/gateway.py` (failover qua account) | `Gateway.cs` (dùng Channel vì C# cấm `yield` trong `try/catch`) |
| `services/request_log.py` (SQLite) | `Store.cs` (`RequestLog`, SQLite `data/requests.db`, WAL) |
| `services/tasks.py` (task ảnh/video, JSON persistence) | `Store.cs` (`TaskManager`) |
| `core/media.py` (lưu media, SSRF guard cơ bản) | `Store.cs` (`MediaStore`) + `AppState.cs` (`MediaHelp`) |
| `upstream/muse.py` (session renew, VM wake) | `Upstream.cs` (HttpClient, port đầy đủ) |
| Endpoints: health, models, chat (stream + non-stream), responses (stream + non-stream), images, images/edits, videos, media, admin (accounts, keys, requests, stats, tasks), metrics | `Endpoints.cs` |
| Endpoint `/admin/metrics` mới thêm ở bản Python | `Endpoints.cs` (`/admin/metrics`, cùng định dạng Prometheus) |
| Dashboard | Trang HTML tối giản ở `/dashboard` (trang đầy đủ của Python chưa port) |

## Windows UI (WinForms)

Project `src/Muse2Api.WinForms/` là cửa sổ điều khiển cho gateway .NET
(hoặc gateway Python, vì UI chỉ gọi HTTP): Server (khởi động/dừng, log),
Chat thử, Quản lý (status/accounts/metrics, thêm tài khoản, mở dashboard).
Chạy trên Windows: `dotnet run --project src/Muse2Api.WinForms`.
Trên Linux/macOS, project chỉ build được để kiểm tra lỗi biên dịch
(`EnableWindowsTargeting`), không chạy được giao diện vì thiếu runtime
WindowsDesktop — đó là giới hạn của WinForms, không phải bug.

**Đăng nhập và nạp tài khoản:** ở tab Quản lý, bấm
**Đăng nhập muse.ai...** để mở cửa sổ WebView2 (`LoginForm.cs`) đăng nhập
trực tiếp trên trang muse.ai, rồi nạp cookie phiên vào gateway local qua
`POST /admin/accounts`. UI không bao giờ hỏi hay đọc mật khẩu; chỉ gửi
cookie phiên (`hatch_sess` + `hatch_native_auth_device`, tuỳ chọn
`hatch_gw`/`hatch_vml`) tới gateway local, và gateway chỉ trả về cookie
đã che. Chi tiết và các lưu ý an toàn xem
`src/Muse2Api.WinForms/README.md`. Cách test trên Windows xem `TESTING.md`.

## Browser driver (.NET / Playwright) — mới

Driver `browser` đã được port từ Python `drivers/browser/` sang
[Microsoft.Playwright](https://playwright.dev/dotnet/) trong
`src/Muse2Api/BrowserDriver.cs` + `src/Muse2Api/Dom.cs` (selectors và script
JS port 1:1 từ `dom.py`, kể cả parser quota EN/zh-CN/zh-TW):
một Chromium, một context riêng cho mỗi account, một page/tab tái sử dụng,
hot-page follow-up, deny approval card, chat stream theo delta, chờ media
(ảnh/video) với grace period, tải media qua blob URL trong page, quota qua
Settings → Usage, renew session qua HTTP upstream.

Cài Chromium một lần trên Windows:

```powershell
dotnet tool install --global Microsoft.Playwright.CLI
playwright install chromium
# hoặc set MUSE2API_CHROMIUM_PATH tới Chrome/Chromium đã có sẵn
```

## Chưa port / khác biệt — đọc trước khi dùng thật

- **Browser driver chưa kiểm chứng live.** Code đã build và đường lỗi
  "chưa cài Chromium" đã kiểm tra, nhưng trên VM này không tải được browser
  của Playwright (CDN bị chặn) và không có sẵn Chromium, nên chưa chạy thử
  với muse.ai thật. Chạy với tài khoản thật lần đầu và kỳ vọng các DOM
  mismatch đầu tiên sẽ nằm ở `Dom.cs`, đúng như bản Python.
- **HTTP driver: chat vẫn 501 như bản Python**, chỉ có session renewal đã port.
- **Request log: đã port sang SQLite** (`data/requests.db`, cùng schema
  và index với Python, WAL, prune theo `MUSE2API_REQUEST_LOG_RETENTION_DAYS`
  mặc định 14 ngày khi khởi động). `/admin/requests`, `/admin/stats` và
  `/admin/metrics` giờ sống qua restart — đã kiểm chứng: 2 request trước
  restart vẫn còn đủ 2 sau restart.
- **Matting (tách nền transparent bằng BiRefNet): chưa port.** Ảnh
  `background: "transparent"` hiện trả ảnh mock bình thường.
- **Keepalive loop + housekeeping: đã port** (`BackgroundServices.cs`).
  Khi `MUSE2API_KEEPALIVE_ENABLED=1` và driver hỗ trợ renew, loop định kỳ
  renew từng account đang bật (merge cookie như `/renew`, account lỗi auth
  → invalid). Housekeeping mỗi 60s flush key, mỗi giờ prune request log
  và media cũ. Đã kiểm chứng với mock, interval 5s: log hiện
  `keepalive acc_... -> True` lặp lại.
- **Streaming heartbeat: đã port.** Khi `MUSE2API_STREAM_HEARTBEAT_INTERVAL > 0`,
  stream gửi `: keepalive` trong lúc chờ token đầu tiên (như Python C4);
  lỗi upstream lúc này trả về dạng SSE error thay vì HTTP code vì header
  đã commit. Đã kiểm chứng với interval 1s: comment `: keepalive` xuất
  hiện trước chunk đầu, stream vẫn kết thúc bằng `[DONE]`.
- **SSRF guard: đã port đầy đủ như Python.** `LoadImageRef` resolve DNS
  trước khi fetch, chặn IP private/loopback/link-local/multicast/reserved/
  unspecified, và kiểm tra lại từng redirect (tối đa 5 hop, cap 20 MiB).
  Đã kiểm chứng: localhost, 127.0.0.1, 10.x, 192.168.x, 172.16.x,
  169.254.x đều bị chặn 400.
- **Dashboard đầy đủ, extension zip:** chưa port (extension trả 404).
  Dashboard hiện tại là trang HTML tối giản, không phải dashboard đầy đủ
  của Python.

## Kiểm thử

```sh
dotnet test Muse2Api.sln
```

Test xUnit hiện tại bao phủ logic thuần: model registry, prompt flatten,
mock driver (chat + PNG + quota), account pool + masking cookie, key store,
media store.

> Lưu ý môi trường: trên VM Linux này, `vstest` bị treo ở bước kết nối
> testhost ngay cả với project xUnit tối giản mới tạo, nên `dotnet test`
> chưa chạy xong ở đây. Logic trong `PortTests.cs` đã được kiểm chứng bằng
> một console runner tạm (cùng các assert) và **tất cả đều pass**, kèm
> phát hiện và sửa 1 bug thật trong `KeyStore.Create` (base64 bị cắt ký tự
> `+`/`/` làm `[..24]` throw). Trên máy Windows của bạn, `dotnet test`
> chạy bình thường thì chạy lệnh trên.

Test integration qua HTTP đã kiểm chứng bằng tay với server mock:
health, ready, models, chat thường + stream, responses, images (sync/async),
images/edits, videos (async task), admin accounts/keys/stats/metrics, 401 khi
thiếu key, và persistence account qua restart.

## Cấu trúc

```
src/Muse2Api/
  Program.cs      # host, middleware log request + error body OpenAI-style
  Settings.cs     # env MUSE2API_*
  Errors.cs       # MuseException / UpstreamException
  Models.cs       # ModelRegistry + Prompt
  Drivers.cs      # IMuseDriver, MockDriver, HttpDriver
  BrowserDriver.cs # Playwright browser driver
  Dom.cs          # selectors + JS + quota parsing (port từ dom.py)
  Upstream.cs     # muse.ai session / VM wake
  Store.cs        # AccountPool, KeyStore, RequestLog, TaskManager, MediaStore
  Gateway.cs      # failover
  AppState.cs     # container + JSON helpers + LoadImageRef
  Endpoints.cs    # toàn bộ route
tests/Muse2Api.Tests/
  PortTests.cs    # xUnit
```
