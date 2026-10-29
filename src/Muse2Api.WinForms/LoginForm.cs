using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Muse2Api.WinForms;

/// <summary>Sign in on Muse's own page; connect the desktop automatically once the session exists.</summary>
public sealed class LoginForm : Form
{
    public static readonly string[] RequiredCookies = { "hatch_sess", "hatch_native_auth_device" };
    public static readonly string[] OptionalCookies = { "hatch_gw", "hatch_vml" };
    public const string MuseSite = "https://muse.ai";
    public string? LastAccountId { get; private set; }
    private readonly GatewayClient _client;
    private readonly Func<string> _url, _adminKey;
    private readonly string _accountLabel;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Label _status = DesktopTheme.Label("Đang mở trang đăng nhập…", 10, DesktopTheme.Muted);
    private readonly CleanButton _retry = new() { Text = "Thử lại", Width = 100, Visible = false };
    private readonly CleanButton _switch = new() { Text = "Tài khoản khác", Width = 150, Quiet = true };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private bool _refreshing, _loading, _failed;
    private readonly bool _clearExistingSession;
    private readonly Muse2Api.Account? _savedAccount;
    private bool _sessionVerified;
    private DateTime _nextSessionCheck;
    private readonly string _verificationNonce = Guid.NewGuid().ToString("N");

    public LoginForm(GatewayClient client, Func<string> url, Func<string> adminKey, string defaultLabel = "Tài khoản cá nhân", bool clearExistingSession = false, Muse2Api.Account? savedAccount = null)
    {
        _client = client; _url = url; _adminKey = adminKey;
        _clearExistingSession = clearExistingSession;
        _savedAccount = savedAccount;
        _accountLabel = string.IsNullOrWhiteSpace(defaultLabel) ? "Tài khoản cá nhân" : defaultLabel;
        Text = "Kết nối tài khoản Muse"; Size = new Size(1060, 800); MinimumSize = new Size(800, 620);
        StartPosition = FormStartPosition.CenterParent; Font = DesktopTheme.Body; BackColor = DesktopTheme.Background;
        var layout = DesktopTheme.Column(new(SizeType.Absolute, 75), new(SizeType.Percent, 100), new(SizeType.Absolute, 55));
        var heading = DesktopTheme.Column(new(SizeType.Absolute, 35), new(SizeType.Absolute, 28)); heading.Padding = new Padding(18, 6, 18, 4); heading.BackColor = DesktopTheme.Background;
        heading.Controls.Add(DesktopTheme.Label("Đăng nhập tài khoản Muse", 17, bold: true), 0, 0);
        heading.Controls.Add(DesktopTheme.Label("Đăng nhập ngay trên trang Muse bên dưới. Ứng dụng sẽ tự lưu kết nối khi bạn hoàn tất.", 10, DesktopTheme.Muted), 0, 1);
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(_web, 0, 1);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(18, 6, 18, 6), BackColor = DesktopTheme.Background };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        footer.Controls.Add(_status, 0, 0); footer.Controls.Add(_retry, 1, 0); footer.Controls.Add(_switch, 2, 0); layout.Controls.Add(footer, 0, 2); Controls.Add(layout);
        _switch.Click += (_, _) =>
        {
            if (_web.CoreWebView2 is null || _loading || _refreshing) return;
            _poll.Stop(); _failed = false;
            _sessionVerified = false; _nextSessionCheck = DateTime.MinValue;
            _web.CoreWebView2.CookieManager.DeleteAllCookies();
            _web.CoreWebView2.Navigate(MuseSite);
            _status.Text = "Đăng nhập tài khoản khác trên trang Muse."; _poll.Start();
        };
        _retry.Click += async (_, _) => { _failed = false; _retry.Visible = false; if (_web.CoreWebView2 is null) await InitWebView(); else await RefreshStatus(); };
        _poll.Tick += async (_, _) => await RefreshStatus();
        FormClosed += (_, _) => { _poll.Stop(); _poll.Dispose(); };
        Load += async (_, _) => await InitWebView();
    }

    private async Task InitWebView()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Muse2Api.WinForms", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, data);
            await _web.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;
            _web.CoreWebView2.WebMessageReceived += async (_, message) =>
            {
                try
                {
                    if (!Uri.TryCreate(message.Source, UriKind.Absolute, out var source) || source.Host != "muse.ai") return;
                    var result = JsonNode.Parse(message.WebMessageAsJson);
                    if (result?["kind"]?.ToString() != "muse-session-check" || result?["nonce"]?.ToString() != _verificationNonce) return;
                    _sessionVerified = result?["authenticated"]?.GetValue<bool>() == true;
                    if (_sessionVerified) await RefreshStatus();
                    else if (!IsDisposed) _status.Text = "Phiên đã lưu chưa được Muse xác nhận. Đăng nhập trên trang nếu được yêu cầu; ứng dụng không tự lấy mã mới.";
                }
                catch (Exception) { /* A page message cannot break the login form. */ }
            };
            if (_clearExistingSession) _web.CoreWebView2.CookieManager.DeleteAllCookies();
            else if (_savedAccount is not null)
            {
                // Session cookies may be absent from a fresh WebView process even
                // though the desktop still has a valid saved login.
                var manager = _web.CoreWebView2.CookieManager;
                var existing = await manager.GetCookiesAsync(MuseSite);
                if (RequiredCookies.Any(name => !existing.Any(c => c.Name == name && c.Value.Length > 0)))
                    foreach (var (name, value) in _savedAccount.Cookies.Where(kv => RequiredCookies.Contains(kv.Key) || OptionalCookies.Contains(kv.Key)))
                    {
                        var cookie = manager.CreateCookie(name, value, ".muse.ai", "/"); cookie.IsSecure = true;
                        if (_savedAccount.CookieExpires.GetValueOrDefault(name) is var expiry && expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                            cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime;
                        manager.AddOrUpdateCookie(cookie);
                    }
            }
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await RefreshStatus();
            _web.CoreWebView2.Navigate(MuseSite);
            _status.Text = "Đang chờ bạn đăng nhập. Bạn chỉ cần thao tác trên trang Muse.";
            _poll.Start();
        }
        catch
        {
            if (IsDisposed) return;
            _status.Text = "Chưa mở được cửa sổ đăng nhập. Hãy kiểm tra Edge WebView2 trên máy rồi bấm Thử lại.";
            _retry.Visible = true;
        }
    }

    private async Task RefreshStatus()
    {
        if (_refreshing || _loading || _failed || IsDisposed || _web.CoreWebView2 is null) return;
        _refreshing = true;
        try
        {
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(MuseSite);
            if (IsDisposed) return;
            if (RequiredCookies.Any(name => !cookies.Any(c => c.Name == name && c.Value.Length > 0))) return;
            if (!_sessionVerified)
            {
                if (DateTime.UtcNow < _nextSessionCheck || !Uri.TryCreate(_web.Source?.AbsoluteUri, UriKind.Absolute, out var source) || source.Host != "muse.ai") return;
                _nextSessionCheck = DateTime.UtcNow.AddSeconds(15);
                _status.Text = "Đang kiểm tra và khôi phục phiên Muse đã lưu…";
                var nonce = System.Text.Json.JsonSerializer.Serialize(_verificationNonce);
                await _web.CoreWebView2.ExecuteScriptAsync("(async()=>{const c=new AbortController();const timer=setTimeout(()=>c.abort(),8000);try{const r=await fetch('/api/session',{credentials:'include',cache:'no-store',signal:c.signal});const b=r.ok?await r.json():{};window.chrome.webview.postMessage({kind:'muse-session-check',nonce:" + nonce + ",authenticated:r.ok&&b.status==='assigned'});}catch{window.chrome.webview.postMessage({kind:'muse-session-check',nonce:" + nonce + ",authenticated:false});}finally{clearTimeout(timer);}})()");
                return;
            }
            _loading = true;
            _status.Text = "Đăng nhập thành công. Đang lưu kết nối…";
            var array = new JsonArray();
            foreach (var cookie in cookies.Where(c => RequiredCookies.Contains(c.Name) || OptionalCookies.Contains(c.Name)))
            {
                var entry = new JsonObject { ["name"] = cookie.Name, ["value"] = cookie.Value };
                if (!cookie.IsSession) entry["expirationDate"] = new DateTimeOffset(DateTime.SpecifyKind(cookie.Expires, DateTimeKind.Utc)).ToUnixTimeSeconds();
                array.Add(entry);
            }
            var result = await _client.AddAccountWithCookies(_url(), _adminKey(), _accountLabel, array);
            if (IsDisposed) return;
            LastAccountId = result?["account"]?["id"]?.GetValue<string>();
            if (LastAccountId is null) throw new InvalidOperationException("No saved account returned");
            _status.Text = "Đã kết nối. Bạn có thể bắt đầu làm việc với dự án.";
            DialogResult = DialogResult.OK;
        }
        catch
        {
            if (IsDisposed) return;
            _failed = true;
            _status.Text = "Đã đăng nhập nhưng chưa lưu được kết nối. Bấm Thử lại; bạn không cần đăng nhập lại.";
            _retry.Visible = true;
        }
        finally { _refreshing = false; _loading = false; }
    }
}
