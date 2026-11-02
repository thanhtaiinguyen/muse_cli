namespace Muse2Api.WinForms;

public sealed partial class AgentWorkspaceControl
{
    private bool _projectReviewEnabled;
    private string BehaviorFile => Path.Combine(DesktopDataPaths.Root, "agent_behavior.json");
    private void LoadBehavior()
    {
        try { if (File.Exists(BehaviorFile)) _projectReviewEnabled = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(BehaviorFile))?["project_review"]?.GetValue<bool>() ?? false; }
        catch { _projectReviewEnabled = false; }
    }
    public void ShowPreferences(IWin32Window owner, string dataDirectory, string diagnostics,
        Func<Task<Muse2Api.UpdateCheckResult>>? checkUpdates = null, Action<Muse2Api.AvailableUpdate>? openUpdate = null,
        Muse2Api.UpdateCheckResult? latestUpdate = null, Muse2Api.UpdateFeed updateFeed = Muse2Api.UpdateFeed.Release,
        Func<Task>? addAccount = null)
    {
        ConnectPanelInfo? connect = null;
        GatewayClient? panelClient = null;
        try
        {
            var connection = _connection();
            panelClient = new GatewayClient();
            connect = new ConnectPanelInfo(connection.BaseUrl, connection.ApiKey, panelClient);
        }
        catch { connect = null; }
        using var dialog = new PreferencesForm(_connected, _busy, _permissions.SelectedIndex == 1, _permissions.SelectedIndex == 2,
            _shell.SelectedIndex, dataDirectory, diagnostics, checkUpdates, openUpdate, latestUpdate, updateFeed,
            connect, Path.Combine(DesktopDataPaths.Root, "Skills"));
        dialog.ProjectReviewEnabled = _projectReviewEnabled;
        dialog.LoginRequested += async (_, _) => { dialog.Close(); await _login(); };
        dialog.AddAccountRequested += async (_, _) => { dialog.Close(); if (addAccount is not null) await addAccount(); else await _login(); };
        var result = dialog.ShowDialog(owner);
        panelClient?.Dispose();
        if (result != DialogResult.OK || _busy) return;
        _permissions.SelectedIndex = dialog.Turbo ? 2 : dialog.ReadOnly ? 1 : 0;
        _projectReviewEnabled = dialog.ProjectReviewEnabled;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BehaviorFile)!);
            var temporary = BehaviorFile + ".tmp";
            File.WriteAllText(temporary, new System.Text.Json.Nodes.JsonObject { ["project_review"] = _projectReviewEnabled }.ToJsonString());
            File.Move(temporary, BehaviorFile, overwrite: true);
        }
        catch (Exception ex) { _state.Text = "Không lưu được lựa chọn phản biện: " + ex.Message; }
        if (dialog.ReadOnly) DisableComputer();
        _shell.SelectedIndex = dialog.ShellIndex;
        _terminalShell.SelectedIndex = _shell.SelectedIndex;
        DesktopTheme.SetMode(dialog.SelectedTheme);
        try { File.WriteAllText(Path.Combine(dataDirectory, "ui_theme.txt"), DesktopTheme.Mode.ToString()); } catch { }
        DesktopTheme.Apply(this);
        if (_tools is not null) RefreshTree();
        _state.Text = dialog.Turbo ? "Turbo — tự sửa mã và chạy lệnh. Dùng /goal kèm mục tiêu để xây dựng và kiểm thử liên tục." : dialog.ReadOnly ? "Chế độ chỉ xem — Muse không được sửa tệp hoặc chạy lệnh." : "Mọi thay đổi sẽ được hỏi trước khi thực hiện.";
    }
}
