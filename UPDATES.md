# Thông báo cập nhật Muse từ 0.8.10

## Người dùng

- Cần cài/chạy bản 0.8.10 hoặc mới hơn một lần. Các bản cũ chưa có bộ kiểm tra
  không thể tự nhận tính năng này từ xa; cần thay bằng bản mới thủ công lần đầu.
- Khi mở app và mỗi 6 giờ khi app đang chạy, Muse kiểm tra phiên bản mới bằng
  HTTP công khai. Có cache trên máy, ETag và thời gian chờ khi GitHub giới hạn
  truy cập. Khởi động nhiều lần hoặc bấm liên tục không tạo hàng loạt request.
- Có bản mới: hiện banner phía trên không gian làm việc. Bấm **Xem bản mới**
  để mở trang phát hành, hoặc **Ẩn thông báo** để ẩn riêng phiên bản đó. Bản
  cao hơn vẫn được báo. **Cài đặt → Kiểm tra cập nhật** có thể hiện lại bản đã ẩn.
- Mất mạng/GitHub lỗi: vẫn dùng app bình thường; xem kết quả kiểm tra trong
  Cài đặt. Nút kiểm tra tay có khoảng nghỉ 1 phút và tôn trọng giới hạn GitHub.
- Không tự tải/chạy bộ cài, thay file đang dùng, đóng app, chạy Git hay đổi
  đăng nhập/lịch sử. Chỉ mở trình duyệt khi người dùng bấm liên kết cập nhật.
- Khi thay bản đóng gói, giữ dữ liệu trong LocalAppData. Nếu đang dùng hồ sơ
  preview, giữ file `muse-preview.profile` cùng executable để tiếp tục dùng
  đúng hồ sơ đó; gói Release công khai mặc định dùng hồ sơ chính.
- App đóng thì không có dịch vụ chạy ngầm để nhận thông báo. Thông báo đến
  trong lần mở/kiểm tra tiếp theo, không phải push tức thời của Windows.

## Bản đóng gói và bản source

Nguồn chính thức cố định: `thanhtaiinguyen/muse_cli`.

- **Bản đóng gói** (gồm `dist/Muse` trong một checkout): kiểm tra
  `GET https://api.github.com/repos/thanhtaiinguyen/muse_cli/releases/latest`.
  Chỉ thông báo stable release cao hơn có đủ `Muse-win-x64.zip` và
  `Muse-win-x64.zip.sha256`. Không dùng draft/prerelease, không báo hạ phiên bản.
- **Bản build từ source** (`src/Muse2Api.WinForms/bin/...` hoặc
  `artifacts/bin/Muse2Api.WinForms/...` của checkout): đọc Version trong project
  WinForms trên nhánh `main`, so với phiên bản executable đang chạy. Có thể
  báo mã nguồn mới dù chưa đóng gói Release; liên kết mở lịch sử `main`.
  Lưu/commit thay đổi cá nhân trước khi cập nhật checkout. Với checkout `main`
  sạch, có thể dùng `git pull --ff-only`, rồi build/run lại. Không tự merge/reset
  nhánh, không ghi đè source đang sửa. Người lấy source ZIP cần lấy source mới.

Mỗi lần cập nhật cần tăng số Version; push commit có cùng số phiên bản không
được coi là một bản cập nhật ứng dụng mới. Khi chạy bản local cao hơn GitHub,
app không gợi ý quay về bản thấp hơn.

## Phát hành cho những người đã cài

Workflow `.github/workflows/package.yml` đã nhận tag `vMAJOR.MINOR.PATCH`.

1. Tăng Version trong `src/Muse2Api.WinForms/Muse2Api.WinForms.csproj` và
   `Endpoints.Version`. Hoàn tất kiểm tra, review và commit các thay đổi.
2. Đẩy commit đã kiểm tra lên `origin/main`, rồi tạo/đẩy tag đúng phiên bản,
   ví dụ `v0.8.10`. Tag phải trỏ đến đúng commit và khớp Version của project.
3. Workflow chạy build/test, kiểm tra gói và tạo ZIP/checksum. Chỉ khi package
   job đạt, release job mới tạo GitHub Release cùng hai asset trên.
4. Người dùng 0.8.10+ sẽ thấy thông báo trong lần kiểm tra tiếp theo. Push source
   đơn thuần chưa tạo gói tải cho người dùng bản đóng gói; cần Release thành công.

Có thể tiếp tục dùng `workflow_dispatch` với `publish_release=true` và tag đã
tồn tại trên đúng commit. Không tạo tag/release mới bằng cách chạy app hoặc
script publish local. Workflow mới phải được push lên GitHub trước khi dùng;
việc chuẩn bị mã nguồn tính năng này không tự phát hành một Release công khai.

Ngày 31/10/2026, kiểm tra API của repo chưa thấy stable Release (`404` ở endpoint
latest); lần phát hành công khai đầu tiên cần thực hiện quy trình trên.

## Dữ liệu và kiểm chứng

Trạng thái kiểm tra/ETag/phiên bản đã ẩn nằm trong thư mục `Updates` dưới hồ sơ
desktop hiện tại. Bản preview dùng hồ sơ preview riêng. Request không gửi cookie,
API/admin key Muse, đường dẫn dự án, lịch sử chat hay mã máy; không cần token GitHub.
URL có thể mở được giới hạn ở đúng repo GitHub HTTPS chính thức.

Tài liệu giao thức: [GitHub Releases API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release),
[conditional requests và giới hạn truy cập](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).
