using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Muse2Api.WinForms;

/// <summary>Windows control panel for the muse2api gateway (WinForms, .NET 8).
/// One window instead of many PowerShell windows: start/stop the server,
/// try a chat, and check status/accounts/metrics.
/// <para>
/// This UI does not reference the gateway project, so it can control either
/// the .NET gateway in this repo or the original Python gateway, as long as
/// it is listening on the configured host/port.
/// </para>
/// </summary>
public sealed class MainForm : Form
{
    private readonly GatewayClient _client = new();
    private Process? _server;
    private string _configPath = Path.Combine(AppContext.BaseDirectory, "ui_config.json");

    private TextBox _host = null!, _port = null!, _apiKey = null!, _adminKey = null!,
        _dataDir = null!, _gatewayDir = null!;
    private ComboBox _driver = null!;
    private RichTextBox _log = null!;
    private Button _startBtn = null!, _stopBtn = null!;
    private ToolStripStatusLabel _status = null!;

    private ComboBox _model = null!;
    private RichTextBox _prompt = null!, _answer = null!;
    private RichTextBox _manageOut = null!;
    private TextBox _accLabel = null!, _accCookie = null!;
    private ComboBox _accountCombo = null!;
    private List<(string Id, string Label, string Status)> _accounts = new();

    public MainForm()
    {
        Text = "muse2api — Bảng điều khiển (.NET)";
        Size = new Size(900, 660);
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildServerTab());
        tabs.TabPages.Add(BuildChatTab());
        tabs.TabPages.Add(BuildManageTab());
        Controls.Add(tabs);

        var strip = new StatusStrip();
        _status = new ToolStripStatusLabel("Chưa chạy server.");
        strip.Items.Add(_status);
        Controls.Add(strip);

        LoadConfig();
        FormClosing += (_, _) => StopServer();
    }

    // ---------- layout helpers ----------
    private static int Row(Control parent, int y, string label, Control control, int labelWidth = 170)
    {
        var lbl = new Label { Text = label, Location = new Point(10, y + 4), Width = labelWidth, AutoSize = false };
        control.Location = new Point(10 + labelWidth, y);
        control.Width = 620;
        parent.Controls.Add(lbl);
        parent.Controls.Add(control);
        return y + 32;
    }

    private static TextBox Tb(string text = "", bool password = false) => new()
    {
        Text = text, UseSystemPasswordChar = password, Width = 620,
    };

    private static Button Btn(string text, EventHandler handler)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        b.Click += handler;
        return b;
    }

    private TabPage BuildServerTab()
    {
        var page = new TabPage("Server");
        var y = 10;
        _host = Tb("127.0.0.1"); y = Row(page, y, "Host:", _host);
        _port = Tb("18610"); y = Row(page, y, "Port:", _port);
        _driver = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 620 };
        _driver.Items.AddRange(new object[] { "mock", "browser", "http" });
        _driver.SelectedIndex = 0;
        y = Row(page, y, "Driver:", _driver);
        _apiKey = Tb(password: true); y = Row(page, y, "API key:", _apiKey);
        _adminKey = Tb(password: true); y = Row(page, y, "Admin key:", _adminKey);
        _dataDir = Tb(); y = Row(page, y, "Data dir (trống = mặc định):", _dataDir);
        _gatewayDir = Tb(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")));
        y = Row(page, y, "Thư mục muse2api-dotnet:", _gatewayDir);

        var buttons = new FlowLayoutPanel
        {
            Location = new Point(10, y), Width = 840, Height = 40, AutoSize = true,
        };
        _startBtn = Btn("▶ Khởi động server", (_, _) => StartServer());
        _stopBtn = Btn("■ Dừng server", (_, _) => StopServer());
        _stopBtn.Enabled = false;
        buttons.Controls.AddRange(new Control[]
        {
            _startBtn, _stopBtn,
            Btn("Lưu cấu hình", (_, _) => { SaveConfig(); SetStatus("Đã lưu cấu hình."); }),
            Btn("Chọn gateway dir", (_, _) => PickFolder(_gatewayDir)),
            Btn("Chọn data dir", (_, _) => PickFolder(_dataDir)),
            Btn("Kiểm tra health", async (_, _) => await CheckHealth()),
        });
        page.Controls.Add(buttons);
        y += 50;

        page.Controls.Add(new Label { Text = "Nhật ký server:", Location = new Point(10, y), AutoSize = true });
        _log = new RichTextBox
        {
            Location = new Point(10, y + 20), Width = 840, Height = 340,
            ReadOnly = true, BackColor = SystemColors.Window,
        };
        page.Controls.Add(_log);
        AppendLog("Driver mock không cần tài khoản. Driver browser cần tài khoản thật và Chromium của Playwright (xem README).\n");
        return page;
    }

    private TabPage BuildChatTab()
    {
        var page = new TabPage("Chat thử");
        var y = 10;
        _model = new ComboBox { Width = 620 };
        _model.Items.AddRange(new object[] { "muse-chat", "gpt-4o", "dall-e-3" });
        _model.SelectedIndex = 0;
        y = Row(page, y, "Model:", _model);
        var refresh = Btn("Tải danh sách model", async (_, _) => await RefreshModels());
        refresh.Location = new Point(800, 10);
        page.Controls.Add(refresh);
        page.Controls.Add(new Label { Text = "Câu hỏi:", Location = new Point(10, y), AutoSize = true });
        _prompt = new RichTextBox { Location = new Point(10, y + 20), Width = 840, Height = 120 };
        page.Controls.Add(_prompt);
        y += 150;
        var send = Btn("Gửi", async (_, _) => await SendChat());
        send.Location = new Point(10, y);
        page.Controls.Add(send);
        y += 40;
        page.Controls.Add(new Label { Text = "Trả lời:", Location = new Point(10, y), AutoSize = true });
        _answer = new RichTextBox
        {
            Location = new Point(10, y + 20), Width = 840, Height = 300, ReadOnly = true,
        };
        page.Controls.Add(_answer);
        return page;
    }

    private TabPage BuildManageTab()
    {
        var page = new TabPage("Quản lý");
        var buttons = new FlowLayoutPanel
        {
            Location = new Point(10, 10), Width = 840, Height = 40, AutoSize = true,
        };
        buttons.Controls.AddRange(new Control[]
        {
            Btn("Trạng thái", async (_, _) => await ShowAdmin("/admin/status")),
            Btn("Tài khoản", async (_, _) => await ShowAdmin("/admin/accounts")),
            Btn("Metrics", async (_, _) => await ShowAdmin("/admin/metrics")),
            Btn("Mở Dashboard", (_, _) => OpenDashboard()),
            Btn("Đăng nhập muse.ai...", (_, _) => OpenLogin()),
        });
        page.Controls.Add(buttons);
        page.Controls.Add(new Label
        {
            Text = "Cách khuyên dùng: bấm \"Đăng nhập muse.ai...\" để đăng nhập trong app rồi nạp tài khoản (không cần dán cookie). "
                + "Cách thủ công bên dưới chỉ dùng khi máy chưa có WebView2 Runtime — chỉ trên máy của bạn, không gửi cookie cho ai khác, không commit file chứa cookie:",
            Location = new Point(10, 55), Width = 840, Height = 30, AutoSize = false,
        });
        _accLabel = Tb(); _accLabel.Location = new Point(60, 90); _accLabel.Width = 150;
        _accCookie = Tb(password: true); _accCookie.Location = new Point(280, 90); _accCookie.Width = 400;
        page.Controls.Add(new Label { Text = "Nhãn:", Location = new Point(10, 93), AutoSize = true });
        page.Controls.Add(_accLabel);
        page.Controls.Add(new Label { Text = "Cookie:", Location = new Point(220, 93), AutoSize = true });
        page.Controls.Add(_accCookie);
        var add = Btn("Thêm", async (_, _) => await AddAccount());
        add.Location = new Point(690, 88);
        page.Controls.Add(add);
        // Kiểm tra tài khoản đã nạp: quota (không tốn lượt chat) và gia hạn session.
        page.Controls.Add(new Label { Text = "Kiểm tra tài khoản:", Location = new Point(10, 123), AutoSize = true });
        _accountCombo = new ComboBox { Location = new Point(120, 120), Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
        page.Controls.Add(_accountCombo);
        var reloadAcc = Btn("Tải danh sách", async (_, _) => await RefreshAccounts());
        reloadAcc.Location = new Point(470, 118);
        page.Controls.Add(reloadAcc);
        var quotaBtn = Btn("Kiểm tra quota", async (_, _) => await CheckQuota());
        quotaBtn.Location = new Point(570, 118);
        page.Controls.Add(quotaBtn);
        var renewBtn = Btn("Gia hạn session", async (_, _) => await RenewSelectedAccount());
        renewBtn.Location = new Point(685, 118);
        page.Controls.Add(renewBtn);
        _manageOut = new RichTextBox
        {
            Location = new Point(10, 155), Width = 840, Height = 405, ReadOnly = true,
        };
        page.Controls.Add(_manageOut);
        return page;
    }

    // ---------- config ----------
    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            var cfg = JsonNode.Parse(File.ReadAllText(_configPath)) as JsonObject;
            if (cfg is null) return;
            _host.Text = cfg["host"]?.GetValue<string>() ?? _host.Text;
            _port.Text = cfg["port"]?.ToString() ?? _port.Text;
            _driver.SelectedItem = cfg["driver"]?.GetValue<string>() ?? "mock";
            _apiKey.Text = cfg["api_key"]?.GetValue<string>() ?? "";
            _adminKey.Text = cfg["admin_key"]?.GetValue<string>() ?? "";
            _dataDir.Text = cfg["data_dir"]?.GetValue<string>() ?? "";
            _gatewayDir.Text = cfg["gateway_dir"]?.GetValue<string>() ?? _gatewayDir.Text;
        }
        catch { /* corrupted config starts with defaults */ }
    }

    private void SaveConfig()
    {
        var cfg = new JsonObject
        {
            ["host"] = _host.Text.Trim(),
            ["port"] = _port.Text.Trim(),
            ["driver"] = _driver.SelectedItem?.ToString() ?? "mock",
            ["api_key"] = _apiKey.Text,
            ["admin_key"] = _adminKey.Text,
            ["data_dir"] = _dataDir.Text.Trim(),
            ["gateway_dir"] = _gatewayDir.Text.Trim(),
        };
        File.WriteAllText(_configPath, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private void PickFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog();
        if (dlg.ShowDialog() == DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    private string Url() => _client.BaseUrl(_host.Text.Trim(), int.Parse(_port.Text.Trim()));

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        _status.Text = text;
    }

    private void AppendLog(string text)
    {
        if (_log.IsDisposed) return;
        if (_log.InvokeRequired) { _log.BeginInvoke(() => AppendLog(text)); return; }
        _log.AppendText(text);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    // ---------- server ----------
    private void StartServer()
    {
        if (_server is { HasExited: false }) { SetStatus("Server đang chạy rồi."); return; }
        SaveConfig();
        var gatewayDir = _gatewayDir.Text.Trim();
        var dll = Path.Combine(gatewayDir, "src", "Muse2Api", "bin", "Debug", "net8.0", "Muse2Api.dll");
        var csproj = Path.Combine(gatewayDir, "src", "Muse2Api", "Muse2Api.csproj");
        string args;
        if (File.Exists(dll)) args = $"\"{dll}\"";
        else if (File.Exists(csproj)) args = $"run --project \"{csproj}\"";
        else
        {
            MessageBox.Show("Không tìm thấy Muse2Api.dll hay Muse2Api.csproj trong thư mục đã chọn.\n"
                + "Hãy build gateway trước (dotnet build) hoặc chọn đúng thư mục muse2api-dotnet.",
                "Không tìm thấy gateway", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var psi = new ProcessStartInfo("dotnet", args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = gatewayDir,
        };
        psi.Environment["MUSE2API_DRIVER"] = _driver.SelectedItem?.ToString() ?? "mock";
        psi.Environment["MUSE2API_PORT"] = _port.Text.Trim();
        psi.Environment["MUSE2API_HOST"] = _host.Text.Trim();
        if (_apiKey.Text.Length > 0) psi.Environment["MUSE2API_API_KEY"] = _apiKey.Text;
        if (_adminKey.Text.Length > 0) psi.Environment["MUSE2API_ADMIN_KEY"] = _adminKey.Text;
        if (_dataDir.Text.Trim().Length > 0) psi.Environment["MUSE2API_DATA_DIR"] = _dataDir.Text.Trim();
        try
        {
            _server = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _server.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data + "\n"); };
            _server.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data + "\n"); };
            _server.Exited += (_, _) => BeginInvoke(() =>
            {
                _startBtn.Enabled = true; _stopBtn.Enabled = false;
                SetStatus("Server đã dừng.");
            });
            _server.Start();
            _server.BeginOutputReadLine();
            _server.BeginErrorReadLine();
            _startBtn.Enabled = false;
            _stopBtn.Enabled = true;
            SetStatus($"Server đang chạy ở {Url()} (driver={_driver.SelectedItem}).");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không khởi động được server: {ex.Message}", "Lỗi",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopServer()
    {
        var proc = _server;
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch { }
        _server = null;
        if (!_startBtn.IsDisposed) { _startBtn.Enabled = true; _stopBtn.Enabled = false; }
        SetStatus("Server đã dừng.");
    }

    // ---------- actions ----------
    private async Task CheckHealth()
    {
        try
        {
            var body = await _client.Health(Url());
            SetStatus($"health: {body?.ToJsonString()}");
        }
        catch (Exception ex) { SetStatus($"Lỗi: {ex.Message}"); }
    }

    private async Task RefreshModels()
    {
        try
        {
            var models = await _client.Models(Url(), _apiKey.Text);
            if (models.Count > 0)
            {
                _model.Items.Clear();
                _model.Items.AddRange(models.ToArray());
                _model.SelectedIndex = 0;
            }
        }
        catch (Exception ex) { SetStatus($"Lỗi: {ex.Message}"); }
    }

    private async Task SendChat()
    {
        var prompt = _prompt.Text.Trim();
        if (prompt.Length == 0)
        {
            MessageBox.Show("Hãy nhập câu hỏi trước khi gửi.", "Thiếu câu hỏi",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _answer.Text = "Đang gửi...";
        try
        {
            _answer.Text = await _client.Chat(Url(), _apiKey.Text,
                _model.SelectedItem?.ToString() ?? "muse-chat", prompt);
        }
        catch (Exception ex) { _answer.Text = $"Lỗi: {ex.Message}"; }
    }

    private async Task ShowAdmin(string path)
    {
        try
        {
            var body = await _client.Admin(Url(), _adminKey.Text, path);
            _manageOut.Text = body?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
                ?? body?.ToString() ?? "";
            if (body is JsonValue v && v.TryGetValue<string>(out var s)) _manageOut.Text = s;
        }
        catch (Exception ex) { _manageOut.Text = $"Lỗi: {ex.Message}"; }
    }

    private async Task AddAccount()
    {
        if (_accCookie.Text.Trim().Length == 0)
        {
            MessageBox.Show("Hãy dán chuỗi cookie trước khi thêm.", "Thiếu cookie",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var body = await _client.AddAccount(Url(), _adminKey.Text, _accLabel.Text.Trim(), _accCookie.Text.Trim());
            _manageOut.Text = body?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "";
            SetStatus("Đã gửi yêu cầu thêm tài khoản.");
        }
        catch (Exception ex) { _manageOut.Text = $"Lỗi: {ex.Message}"; }
    }

    private void OpenLogin()
    {
        if (_adminKey.Text.Length == 0)
        {
            MessageBox.Show("Hãy nhập admin key ở tab Server trước khi nạp tài khoản.\n"
                + "Cửa sổ đăng nhập vẫn mở được, nhưng nút Nạp sẽ bị khóa cho tới khi có admin key.",
                "Thiếu admin key", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        using var login = new LoginForm(_client, Url, () => _adminKey.Text, _accLabel.Text.Trim());
        if (login.ShowDialog(this) == DialogResult.OK)
        {
            SetStatus("Đã nạp tài khoản qua đăng nhập muse.ai. Đang chọn tài khoản đó để kiểm tra ngay.");
            _ = RefreshAccounts(selectId: login.LastAccountId);
        }
    }

    private string? SelectedAccountId()
    {
        var idx = _accountCombo.SelectedIndex;
        if (idx < 0 || idx >= _accounts.Count) return null;
        return _accounts[idx].Id;
    }

    private async Task RefreshAccounts(string? selectId = null)
    {
        try
        {
            _accounts = await _client.ListAccounts(Url(), _adminKey.Text);
            _accountCombo.Items.Clear();
            foreach (var (id, label, status) in _accounts)
                _accountCombo.Items.Add($"{label} ({status}) — {id}");
            if (_accounts.Count > 0)
            {
                var idx = selectId is null ? 0
                    : Math.Max(0, _accounts.FindIndex(a => a.Id == selectId));
                _accountCombo.SelectedIndex = idx;
            }
            SetStatus($"Đã tải {_accounts.Count} tài khoản.");
        }
        catch (Exception ex) { _manageOut.Text = $"Lỗi tải danh sách tài khoản: {ex.Message}"; }
    }

    private async Task CheckQuota()
    {
        var id = SelectedAccountId();
        if (id is null)
        {
            MessageBox.Show("Hãy bấm \"Tải danh sách\" rồi chọn một tài khoản trước.",
                "Chưa chọn tài khoản", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var body = await _client.GetQuota(Url(), _adminKey.Text, id);
            _manageOut.Text = body?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "";
            SetStatus("Đã kiểm tra quota. Lưu ý: với driver mock, quota trả về là giá trị giả lập, không phải quota thật của muse.ai.");
        }
        catch (Exception ex)
        {
            // Quota lỗi cũng là một tín hiệu sức khỏe: cookie hết hạn, tài khoản
            // bận, hoặc driver không hỗ trợ quota (http) — hiển thị nguyên văn.
            _manageOut.Text = $"Kiểm tra quota thất bại (tài khoản có thể đã hết hạn, đang bận, hoặc driver không hỗ trợ quota):\n{ex.Message}";
        }
    }

    private async Task RenewSelectedAccount()
    {
        var id = SelectedAccountId();
        if (id is null)
        {
            MessageBox.Show("Hãy bấm \"Tải danh sách\" rồi chọn một tài khoản trước.",
                "Chưa chọn tài khoản", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show("Gia hạn sẽ thay cookie của tài khoản bằng cookie mới từ muse.ai (nếu đổi mới thành công). Tiếp tục?",
                "Gia hạn session", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            var body = await _client.RenewAccount(Url(), _adminKey.Text, id);
            _manageOut.Text = body?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "";
            SetStatus("Đã gia hạn session. Cookie trả về đã được gateway che.");
            await RefreshAccounts(selectId: id);
        }
        catch (Exception ex) { _manageOut.Text = $"Gia hạn thất bại: {ex.Message}"; }
    }

    private void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url() + "/dashboard") { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus($"Lỗi: {ex.Message}"); }
    }
}
