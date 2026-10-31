namespace Muse2Api.WinForms;

public sealed partial class AgentWorkspaceControl
{
    public void ShowPreferences(IWin32Window owner, string dataDirectory, string diagnostics)
    {
        using var dialog = new PreferencesForm(_connected, _busy, _permissions.SelectedIndex == 1, _permissions.SelectedIndex == 2,
            _shell.SelectedIndex, dataDirectory, diagnostics);
        dialog.LoginRequested += async (_, _) => { dialog.Close(); await _login(); };
        if (dialog.ShowDialog(owner) != DialogResult.OK || _busy) return;
        _permissions.SelectedIndex = dialog.Turbo ? 2 : dialog.ReadOnly ? 1 : 0;
        if (dialog.ReadOnly) DisableComputer();
        _shell.SelectedIndex = dialog.ShellIndex;
        _terminalShell.SelectedIndex = _shell.SelectedIndex;
        DesktopTheme.SetMode(dialog.SelectedTheme);
        try { File.WriteAllText(Path.Combine(dataDirectory, "ui_theme.txt"), DesktopTheme.Mode.ToString()); } catch { }
        DesktopTheme.Apply(this);
        if (_tools is not null) RefreshTree();
        _state.Text = dialog.Turbo ? "Chế độ Turbo — tự sửa file thường; chạy lệnh hoặc sửa file nhạy cảm vẫn cần duyệt." : dialog.ReadOnly ? "Chế độ chỉ xem — Muse không được sửa tệp hoặc chạy lệnh." : "Mọi thay đổi sẽ được hỏi trước khi thực hiện.";
    }
}
