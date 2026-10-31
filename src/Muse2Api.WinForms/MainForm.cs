using System.Text;
using System.Text.Json.Nodes;
using Muse2Api;

namespace Muse2Api.WinForms;

/// <summary>The desktop owns its private backend; setup never asks for an API/admin key.</summary>
public sealed class MainForm : Form
{
    private readonly GatewayClient _client = new();
    private readonly AgentWorkspaceControl _workspace;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _dataDirectory = Path.Combine(DesktopDataPaths.Root, "Gateway");
    private readonly StringBuilder _diagnostics = new();
    private DesktopGatewayHost? _gateway;
    private Task<bool>? _setupTask;
    private LoginForm? _login;
    private bool _closing, _closed;

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        try
        {
            var themeFile = Path.Combine(_dataDirectory, "ui_theme.txt");
            if (File.Exists(themeFile) && File.ReadAllText(themeFile).Trim() == "Light") DesktopTheme.SetMode(DesktopTheme.ThemeMode.Light);
        }
        catch { }
        Text = $"Muse {Application.ProductVersion.Split('+')[0]} — " + (DesktopDataPaths.IsPreview ? "Kiểm thử .NET 10 · hồ sơ riêng" : "Không gian làm việc");
        Font = DesktopTheme.Body;
        BackColor = DesktopTheme.Background;
        Size = new Size(1280, 820);
        MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;
        _workspace = new AgentWorkspaceControl(GetConnection, ConnectAccount, () => EnsureConnected(), OpenSettings);
        Controls.Add(_workspace);
        DesktopTheme.Apply(this);
        DesktopTheme.ThemeChanged += (_, _) => { BackColor = DesktopTheme.Background; DesktopTheme.Apply(this); };
        ResumeLayout(performLayout: true);
        Shown += async (_, _) =>
        {
            _setupTask = ConnectCore(interactive: false, forceLogin: false);
            await _setupTask;
        };
        FormClosing += CloseApplication;
    }

    private AgentConnection GetConnection()
        => _gateway?.BaseUrl is { } url ? new(url, _gateway.ApiKey)
            : throw new InvalidOperationException("Ứng dụng đang chuẩn bị kết nối. Vui lòng đợi một chút.");

    private Task<bool> EnsureConnected()
    {
        if (_setupTask is { IsCompleted: false }) return WaitForSetup();
        _setupTask = ConnectCore(interactive: true, forceLogin: false);
        return _setupTask;
    }

    private async Task<bool> WaitForSetup()
    {
        if (await _setupTask!) return true;
        if (_lifetime.IsCancellationRequested) return false;
        _setupTask = ConnectCore(interactive: true, forceLogin: false);
        return await _setupTask;
    }

    private async Task ConnectAccount()
    {
        if (_setupTask is { IsCompleted: false }) { await _setupTask; return; }
        _setupTask = ConnectCore(interactive: true, forceLogin: true);
        await _setupTask;
    }

    private void Status(string title, string description, bool connected = false, bool working = false)
    {
        if (!IsDisposed) _workspace.SetConnectionState(title, description, connected, working);
    }

    private async Task<bool> ConnectCore(bool interactive, bool forceLogin)
    {
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            Status("Đang chuẩn bị Muse", "Ứng dụng tự thiết lập kết nối trên máy của bạn…", working: true);
            if (_gateway is null)
            {
                ImportLegacyAccounts();
                var settings = DesktopProfile.Create(_dataDirectory);
                _gateway = new DesktopGatewayHost(settings);
            }
            try { await Task.Run(() => _gateway.StartAsync(_lifetime.Token), _lifetime.Token); }
            catch (MuseException ex) when (ex.Code == "browser_not_installed")
            {
                _diagnostics.AppendLine(ex.ToString());
                if (!interactive)
                {
                    Status("Bắt đầu với Muse", "Bấm Kết nối Muse. Ứng dụng sẽ chuẩn bị những gì cần thiết cho lần đầu.");
                    return false;
                }
                Status("Đang chuẩn bị kết nối", "Lần đầu cần tải thành phần trình duyệt. Bạn không cần mở terminal hay chạy lệnh.", working: true);
                await _gateway.DisposeAsync();
                _gateway = null;
                var installed = await BrowserSetup.InstallAsync(_lifetime.Token);
                if (installed != 0) throw new InvalidOperationException("Không tải được thành phần kết nối. Kiểm tra mạng và bấm Thử lại.");
                _lifetime.Token.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(Path.Combine(_dataDirectory, "bundled_browser"), "1", _lifetime.Token);
                _gateway = new DesktopGatewayHost(DesktopProfile.Create(_dataDirectory, browserPath: ""));
                await Task.Run(() => _gateway.StartAsync(_lifetime.Token), _lifetime.Token);
            }
            // Cooling/quota/network errors do not mean the user logged out.
            var available = _gateway.State.Pool.All().Any(DesktopProfile.HasSavedLogin);
            if (!available || forceLogin)
            {
                if (!interactive)
                {
                    Status("Chưa kết nối tài khoản", "Đăng nhập Muse một lần để bắt đầu. Ứng dụng tự giữ kết nối cho những lần sau.");
                    return false;
                }
                Status("Đăng nhập Muse", "Đăng nhập trong cửa sổ vừa mở. Kết nối sẽ được lưu tự động.", working: true);
                var savedAccount = _gateway.State.Pool.All().Where(a => a.Enabled && Upstream.MissingSessionCookies(a.Cookies).Count == 0).OrderByDescending(a => a.LastUsedAt).FirstOrDefault();
                _login = new LoginForm(_client, () => _gateway.BaseUrl!, () => _gateway.AdminKey, "Tài khoản cá nhân", savedAccount: savedAccount);
                using (_login)
                {
                    if (_login.ShowDialog(this) != DialogResult.OK)
                    {
                        Status("Chưa hoàn tất đăng nhập", "Bạn có thể bấm Kết nối Muse để tiếp tục bất cứ lúc nào.");
                        return false;
                    }
                    var selected = _login.LastAccountId;
                    foreach (var account in _gateway.State.Pool.All())
                    {
                        account.Enabled = account.Id == selected;
                        _gateway.State.Pool.Upsert(account);
                    }
                }
                _login = null;
            }
            Status("Đã kết nối Muse", "Sẵn sàng — hãy chọn dự án và mô tả điều bạn muốn làm.", connected: true);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _diagnostics.AppendLine(ex.ToString());
            Status("Chưa thể kết nối Muse", "Kiểm tra mạng rồi bấm Kết nối Muse để thử lại. Chi tiết có trong Cài đặt.");
            return false;
        }
    }

    private void ImportLegacyAccounts()
    {
        if (DesktopDataPaths.IsOverride) return; // An isolated test profile must not import live accounts.
        var destination = Path.Combine(_dataDirectory, "accounts.json");
        if (File.Exists(destination)) return;
        var candidates = new List<string>();
        foreach (var config in new[]
        {
            Path.Combine(DesktopDataPaths.Root, "ui_config.json"),
            Path.Combine(AppContext.BaseDirectory, "ui_config.json"),
        })
        {
            try
            {
                if (!File.Exists(config)) continue;
                var json = JsonNode.Parse(File.ReadAllText(config));
                var gateway = json?["gateway_dir"]?.GetValue<string>();
                var data = json?["data_dir"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(gateway)) candidates.Add(Path.Combine(Path.GetFullPath(string.IsNullOrWhiteSpace(data) ? "data" : data, gateway), "accounts.json"));
            }
            catch (Exception ex) { _diagnostics.AppendLine("Legacy settings: " + ex.GetType().Name); }
        }
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Muse2Api.sln"))) candidates.Add(Path.Combine(directory.FullName, "data", "accounts.json"));
        foreach (var source in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(source)) continue;
            try
            {
                // Invalid legacy data must not prevent a clean personal setup.
                if (JsonNode.Parse(File.ReadAllText(source)) is not JsonArray) continue;
                Directory.CreateDirectory(_dataDirectory);
                File.Copy(source, destination, overwrite: false);
                break;
            }
            catch (Exception ex) { _diagnostics.AppendLine("Legacy accounts: " + ex.GetType().Name); }
        }
    }

    private void OpenSettings()
        => _workspace.ShowPreferences(this, _dataDirectory, _diagnostics.ToString());

    private async void CloseApplication(object? sender, FormClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        if (!_workspace.CanCloseWorkspace()) return;
        _closing = true;
        _lifetime.Cancel();
        _login?.Close();
        try
        {
            await _workspace.StopAsync();
            if (_setupTask is not null) await _setupTask;
            if (_gateway is not null) await _gateway.DisposeAsync();
        }
        catch (Exception ex) { _diagnostics.AppendLine(ex.ToString()); }
        _client.Dispose();
        _lifetime.Dispose();
        _closed = true;
        Close();
    }
}
