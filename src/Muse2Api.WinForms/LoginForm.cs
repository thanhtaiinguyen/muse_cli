using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Muse2Api.WinForms;

/// <summary>
/// Đăng nhập muse.ai và nạp tài khoản vào gateway — thiết kế:
/// <list type="number">
/// <item>Người dùng đăng nhập <b>trực tiếp trên trang muse.ai</b> hiển thị
/// trong WebView2 (Edge) của cửa sổ này. UI không bao giờ hỏi, đọc hay gửi
/// mật khẩu đi đâu cả: mật khẩu chỉ được gõ vào trang muse.ai, SSO/2FA
/// (nếu có) cũng do chính trang đó xử lý.</item>
/// <item>Sau mỗi lần chuyển trang, UI đọc cookie của muse.ai qua
/// <c>CoreWebView2.CookieManager</c> (không scrape DOM, không chạy JS vào
/// trang). Chỉ khi đủ cookie phiên bắt buộc mới cho nạp:
/// <c>hatch_sess</c> + <c>hatch_native_auth_device</c>; tuỳ chọn
/// <c>hatch_gw</c>, <c>hatch_vml</c> nếu có.</item>
/// <item>Nhấn "Nạp tài khoản": UI gửi <b>chỉ danh sách cookie phiên đó</b>
/// tới gateway <b>local</b> đã cấu hình, qua <c>POST /admin/accounts</c>
/// với admin key. Gateway lưu vào <c>data/accounts.json</c> và trả về
/// cookie đã che (dạng <c>secr...3456</c>). UI chỉ hiển thị tên cookie
/// và kết quả đó, <b>không bao giờ hiển thị giá trị cookie đầy đủ</b>.</item>
/// <item>Phiên WebView2 được lưu riêng trong
/// <c>%LOCALAPPDATA%\Muse2Api.WinForms\WebView2</c> nên lần sau mở lại
/// không cần đăng nhập lại; nút "Xóa phiên đăng nhập" sẽ xoá hết cookie
/// trong đó. Nếu máy chưa có Edge WebView2 Runtime, form báo rõ và
/// người dùng dùng cách dán cookie thủ công ở tab Quản lý.</item>
/// </list>
/// </summary>
public sealed class LoginForm : Form
{
    public static readonly string[] RequiredCookies = { "hatch_sess", "hatch_native_auth_device" };
    public static readonly string[] OptionalCookies = { "hatch_gw", "hatch_vml" };
    public const string MuseSite = "https://muse.ai";

    /// <summary>Id of the account just loaded, if LoadAccount succeeded.
    /// MainForm uses it to pre-select the account for an immediate health check.</summary>
    public string? LastAccountId { get; private set; }

    private readonly GatewayClient _client;
    private readonly Func<string> _url;
    private readonly Func<string> _adminKey;
    private WebView2 _web = null!;
    private TextBox _label = null!;
    private Label _status = null!;
    private Button _loadBtn = null!;
    private Button _clearBtn = null!;
    private System.Windows.Forms.Timer _poll = null!;
    private bool _refreshing;

    public LoginForm(GatewayClient client, Func<string> url, Func<string> adminKey, string defaultLabel = "")
    {
        _client = client;
        _url = url;
        _adminKey = adminKey;
        Text = "muse2api — Đăng nhập muse.ai";
        Size = new Size(1000, 700);
        StartPosition = FormStartPosition.CenterParent;

        var top = new Panel { Dock = DockStyle.Top, Height = 80 };
        top.Controls.Add(new Label { Text = "Nhãn tài khoản:", Location = new Point(10, 12), AutoSize = true });
        _label = new TextBox { Location = new Point(120, 10), Width = 220, Text = defaultLabel };
        top.Controls.Add(_label);
        _loadBtn = new Button { Text = "Nạp tài khoản này vào gateway", Location = new Point(350, 8), Width = 230, Enabled = false };
        _loadBtn.Click += async (_, _) => await LoadAccount();
        top.Controls.Add(_loadBtn);
        _clearBtn = new Button { Text = "Xóa phiên đăng nhập", Location = new Point(590, 8), Width = 160 };
        _clearBtn.Click += (_, _) => ClearSession();
        top.Controls.Add(_clearBtn);
        var refreshBtn = new Button { Text = "Kiểm tra lại cookie", Location = new Point(760, 8), Width = 150 };
        refreshBtn.Click += async (_, _) => await RefreshStatus();
        top.Controls.Add(refreshBtn);
        // muse.ai là SPA: đăng nhập xong có thể không tải lại trang hoàn toàn,
        // nên ngoài NavigationCompleted, UI tự kiểm tra cookie mỗi 3 giây.
        _poll = new System.Windows.Forms.Timer { Interval = 3000 };
        _poll.Tick += async (_, _) => await RefreshStatus();
        FormClosed += (_, _) => _poll.Stop();
        _status = new Label
        {
            Location = new Point(10, 42), Width = 960, Height = 30, AutoSize = false,
            Text = "Đang mở trang đăng nhập muse.ai... Hãy đăng nhập trong trang này (mật khẩu chỉ gõ vào trang muse.ai).",
        };
        top.Controls.Add(_status);
        Controls.Add(top);

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);
        Load += async (_, _) => await InitWebView();
    }

    private string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Muse2Api.WinForms", "WebView2");

    private async Task InitWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await RefreshStatus();
            _web.CoreWebView2.Navigate(MuseSite);
            _poll.Start();
        }
        catch (Exception ex)
        {
            // Thiếu Edge WebView2 Runtime: báo rõ, không crash, để người dùng
            // quay lại cách dán cookie thủ công.
            MessageBox.Show(
                "Không khởi động được trình duyệt nhúng (WebView2).\n\n"
                + $"Lỗi: {ex.Message}\n\n"
                + "Hãy cài Edge WebView2 Runtime (miễn phí, có sẵn trên Windows 10/11) từ:\n"
                + "https://developer.microsoft.com/microsoft-edge/webview2/\n"
                + "rồi mở lại cửa sổ này. Hoặc dùng cách dán cookie thủ công ở tab Quản lý.",
                "Thiếu WebView2 Runtime", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    private async Task<List<CoreWebView2Cookie>> SessionCookies()
    {
        if (_web.CoreWebView2 is null) return new List<CoreWebView2Cookie>();
        var all = await _web.CoreWebView2.CookieManager.GetCookiesAsync(MuseSite);
        var wanted = RequiredCookies.Concat(OptionalCookies).ToHashSet(StringComparer.Ordinal);
        return all.Where(c => wanted.Contains(c.Name)).ToList();
    }

    private async Task RefreshStatus()
    {
        if (_refreshing || _web.CoreWebView2 is null || IsDisposed) return;
        _refreshing = true;
        try
        {
            var cookies = await SessionCookies();
            var names = cookies.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var missing = RequiredCookies.Where(r => !names.Contains(r)).ToList();
            if (missing.Count == 0)
            {
                _status.Text = $"Đã đăng nhập muse.ai. Cookie phiên có: {string.Join(", ", names)}. "
                    + "Bấm \"Nạp tài khoản\" để đưa tài khoản này vào gateway.";
                _loadBtn.Enabled = _adminKey().Length > 0;
                if (_adminKey().Length == 0)
                    _status.Text += " (Chưa nhập admin key ở tab Server nên chưa nạp được.)";
            }
            else
            {
                _status.Text = $"Chưa đăng nhập xong. Còn thiếu cookie: {string.Join(", ", missing)}. "
                    + "Hãy đăng nhập trong trang này, rồi đợi trang tải xong.";
                _loadBtn.Enabled = false;
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Không đọc được cookie: {ex.Message}";
            _loadBtn.Enabled = false;
        }
        finally { _refreshing = false; }
    }

    private async Task LoadAccount()
    {
        var label = _label.Text.Trim();
        if (label.Length == 0)
        {
            MessageBox.Show("Hãy nhập nhãn tài khoản (ví dụ: acc-chinh) trước khi nạp.",
                "Thiếu nhãn", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _loadBtn.Enabled = false;
        _status.Text = "Đang nạp tài khoản vào gateway...";
        try
        {
            var cookies = await SessionCookies();
            var missingReq = RequiredCookies
                .Where(r => cookies.All(c => c.Name != r)).ToList();
            if (missingReq.Count > 0)
            {
                _status.Text = $"Chưa đủ cookie phiên, còn thiếu: {string.Join(", ", missingReq)}. "
                    + "Hãy đăng nhập xong trong trang này rồi bấm \"Kiểm tra lại cookie\".";
                _loadBtn.Enabled = false;
                return;
            }
            var arr = new JsonArray();
            foreach (var c in cookies)
            {
                var entry = new JsonObject { ["name"] = c.Name, ["value"] = c.Value };
                // Cookie phiên (session) không có hạn: gateway sẽ tự cho hạn mặc định.
                // CoreWebView2Cookie.Expires là DateTime (UTC) trong bản WebView2 này.
                if (!c.IsSession)
                    entry["expirationDate"] = new DateTimeOffset(
                        DateTime.SpecifyKind(c.Expires, DateTimeKind.Utc)).ToUnixTimeSeconds();
                arr.Add(entry);
            }
            // Chỉ gửi giá trị cookie tới gateway local, không hiển thị lại ở UI.
            var body = await _client.AddAccountWithCookies(_url(), _adminKey(), label, arr);
            LastAccountId = body?["account"]?["id"]?.GetValue<string>();
            var missing = body?["missing_cookies"] is JsonArray m
                ? string.Join(", ", m.Select(x => x?.GetValue<string>() ?? "")) : "";
            _status.Text = string.IsNullOrEmpty(missing)
                ? $"Đã nạp tài khoản \"{label}\" vào gateway. Gateway đã che cookie khi trả về."
                : $"Đã nạp tài khoản \"{label}\", nhưng gateway báo còn thiếu cookie: {missing}. "
                  + "Hãy thử đăng nhập lại trong trang này rồi nạp lại.";
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            _status.Text = $"Nạp thất bại: {ex.Message} — kiểm tra gateway đã chạy và admin key đã đúng chưa, rồi thử lại (phiên đăng nhập vẫn còn trong cửa sổ này).";
            _loadBtn.Enabled = true;
        }
    }

    private void ClearSession()
    {
        if (_web.CoreWebView2 is null) return;
        // CookieManager của WebView2 dùng API đồng bộ cho xóa.
        _web.CoreWebView2.CookieManager.DeleteAllCookies();
        _web.CoreWebView2.Navigate(MuseSite);
        _status.Text = "Đã xóa cookie phiên. Hãy đăng nhập lại trong trang này nếu cần.";
        _loadBtn.Enabled = false;
    }
}
