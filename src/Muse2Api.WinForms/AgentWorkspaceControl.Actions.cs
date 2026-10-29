using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.WinForms;

public sealed partial class AgentWorkspaceControl
{
    private string? _pendingAssistantText;
    private async Task RestoreWorkspace()
    {
        try
        {
            if (!File.Exists(_preferences)) return;
            var settings = JsonNode.Parse(await File.ReadAllTextAsync(_preferences));
            var path = settings?["workspace"]?.GetValue<string>();
            if (path is not null && Directory.Exists(path)) await SetWorkspace(path);
        }
        catch (Exception ex) { _state.Text = "Không khôi phục được dự án: " + ex.Message; }
    }

    private async Task PickWorkspace()
    {
        if (_busy || !DiscardEditor()) return;
        using var picker = new FolderBrowserDialog { Description = "Chọn thư mục dự án để agent làm việc", UseDescriptionForTitle = true };
        if (_tools is not null) picker.SelectedPath = _tools.Root;
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { await SetWorkspace(picker.SelectedPath); }
        catch (Exception ex) { _state.Text = ex.Message; }
    }

    private async Task SetWorkspace(string path)
    {
        if (_tools is not null) await _tools.StopBackgroundProcessesAsync();
        _manualProcessId = null;
        DisableComputer();
        var tools = new ToolExecutor(path, _shell.Text) { Computer = _computer };
        _tools = tools;
        _workspace.Text = new DirectoryInfo(tools.Root).Name;
        _choose.Text = new DirectoryInfo(tools.Root).Name;
        _projectHint.Text = "Muse có thể đọc, chỉnh sửa và kiểm tra dự án này.";
        _tips.SetToolTip(_workspace, tools.Root);
        _editorPath = null; _editorBaseline = ""; _dirty = false;
        _settingEditor = true; _editor.Clear(); _settingEditor = false;
        UpdateEditorTitle();
        NewSession();
        RefreshTree(); RefreshChanges();
        await RefreshSessions();
        Directory.CreateDirectory(Path.GetDirectoryName(_preferences)!);
        await File.WriteAllTextAsync(_preferences, new JsonObject { ["workspace"] = tools.Root }.ToJsonString());
        _state.Text = "Đã chọn dự án. Hãy nhập điều bạn muốn làm hoặc chọn một gợi ý.";
        UpdateWelcome();
    }

    private void NewSession()
    {
        if (_tools is null) return;
        _session = new ChatSession { Workspace = _tools.Root, Model = _models.Text };
        _chat.Clear(); _console.Clear();
        _activeThinking = null; _activeToolCall = null; _activeAssistantCard = null;
        ClearImageDraft();
        _sessions.SelectedIndex = -1;
        UpdateWelcome();
    }

    private async Task RefreshSessions(CancellationToken cancel = default)
    {
        if (_tools is null) return;
        var sessions = await _store.ListAsync(_tools.Root, cancel);
        _binding = true;
        try
        {
            _sessions.Items.Clear();
            foreach (var session in sessions) _sessions.Items.Add(session);
            _sessions.SelectedItem = sessions.FirstOrDefault(s => s.Id == _session?.Id);
        }
        finally { _binding = false; }
    }

    private async Task LoadSession(string id)
    {
        if (_tools is null || _busy) return;
        try
        {
            _session = await _store.LoadAsync(_tools.Root, id);
            ClearImageDraft();
            _models.Text = _session.Model;
            _chat.Clear(); _console.Clear();
            _activeThinking = null; _activeToolCall = null; _activeAssistantCard = null;
            string? pendingAnswer = null;
            foreach (var item in _session.Events)
            {
                switch (item.Kind)
                {
                    case "user":
                        pendingAnswer = null;
                        _chat.AddUserMessage(item.Text);
                        break;
                    case "tool_start":
                        pendingAnswer = null;
                        _chat.AddToolCall(item.Tool ?? "tool", item.Path, item.Text).SetResult("", isError: false);
                        break;
                    case "tool_result":
                    case "terminal_result":
                        break;
                    case "assistant":
                        pendingAnswer = ToolCallParser.StripToolBlocks(item.Text);
                        break;
                    case "completed":
                    case "waiting_input":
                        if (!string.IsNullOrWhiteSpace(pendingAnswer)) _chat.AddAssistantMessage(pendingAnswer, stream: false);
                        pendingAnswer = null;
                        break;
                    case "error":
                    case "incomplete":
                    case "declined":
                    case "cancelled":
                        _chat.AddNotice(item.Kind == "error" ? DesktopCopy.FriendlyError(item.Text) : item.Text);
                        pendingAnswer = null;
                        break;
                }
                if (item.Kind is "tool_start" or "tool_result" or "terminal_result" or "command_output" or "error" or "cancelled" or "incomplete" or "declined" or "protocol_error") AppendConsole(item.Text + "\n");
            }
            UpdateWelcome();
            _state.Text = "Đã mở lại cuộc trò chuyện. Bạn có thể tiếp tục yêu cầu.";
        }
        catch (Exception ex) { _state.Text = "Không đọc được lịch sử: " + ex.Message; }
    }

    private void RefreshTree()
    {
        if (_tools is null) return;
        _tree.Nodes.Clear();
        var root = new TreeNode(Path.GetFileName(_tools.Root) is { Length: > 0 } name ? name : _tools.Root) { Tag = _tools.Root };
        _tree.Nodes.Add(root);
        Populate(root); root.Expand();
    }

    private void Populate(TreeNode node)
    {
        if (_tools is null || node.Tag is not string path) return;
        node.Nodes.Clear();
        try
        {
            foreach (var entry in WorkspaceFiles.Enumerate(_tools.ResolvePath(path)).Take(500))
            {
                var child = new TreeNode(Path.GetFileName(entry)) { Tag = entry, ToolTipText = entry };
                if (Directory.Exists(entry)) { child.Nodes.Add(new TreeNode("…")); child.ForeColor = DesktopTheme.Folder; }
                node.Nodes.Add(child);
            }
        }
        catch (Exception ex) { _state.Text = "Không đọc được thư mục: " + ex.Message; }
    }

    private bool DiscardEditor()
        => !_dirty || MessageBox.Show(this, "File đang có nội dung chưa lưu. Bỏ nội dung này để tiếp tục?", "File chưa lưu",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private async Task OpenFile(string path)
    {
        if (_tools is null || _busy || !DiscardEditor()) return;
        try
        {
            var resolved = _tools.ResolvePath(path);
            var content = await ToolExecutor.ReadTextAsync(resolved);
            _editorPath = Path.GetRelativePath(_tools.Root, resolved);
            _editorBaseline = content;
            _settingEditor = true; _editor.Text = content; _settingEditor = false;
            _editorDisplayBaseline = _editor.Text;
            // RichTextBox normalizes newlines; retain the normalized baseline
            // separately from the exact on-disk snapshot used for concurrency checks.
            _dirty = false;
            UpdateEditorTitle();
        }
        catch (Exception ex) { _state.Text = "Không mở được file: " + ex.Message; }
    }

    private void UpdateEditorTitle() => _editorTitle.Text = (_editorPath ?? "Chọn một tệp trong dự án") + (_dirty ? " · Chưa lưu" : "");

    private async Task SaveEditor()
    {
        if (_busy || _tools is null || _editorPath is null) return;
        try
        {
            var content = _editor.Text.Replace("\r\n", "\n");
            if (_editorBaseline?.Contains("\r\n") == true) content = content.Replace("\n", "\r\n");
            var prepared = await _tools.PrepareAsync(new("write_file", new() { ["path"] = _editorPath, ["content"] = content }));
            if (prepared.OriginalText != _editorBaseline) throw new InvalidOperationException("File trên đĩa đã thay đổi. Mở lại file trước khi lưu.");
            var result = await _tools.ExecutePreparedAsync(prepared);
            if (result.StartsWith("error:")) throw new InvalidOperationException(result);
            _editorBaseline = content;
            _editorDisplayBaseline = _editor.Text;
            _dirty = false;
            UpdateEditorTitle(); RefreshChanges();
            await NoteWorkspaceChange($"The user saved {_editorPath} in the editor. Re-read it before relying on earlier contents.");
            _state.Text = "Đã lưu " + _editorPath;
        }
        catch (Exception ex) { _state.Text = ex.Message; }
    }

    private void RefreshChanges()
    {
        var selected = (_changes.SelectedItem as WorkspaceChange)?.Path;
        _changes.Items.Clear();
        if (_tools is null) return;
        foreach (var change in _tools.Changes) _changes.Items.Add(change);
        _changesButton.Text = _changes.Items.Count == 0 ? "Thay đổi" : $"Thay đổi · {_changes.Items.Count}";
        if (_changes.Items.Count > 0) _changes.SelectedIndex = Math.Max(0, _tools.Changes.ToList().FindIndex(c => c.Path == selected));
        else _diff.Text = "Chưa có thay đổi nào trong lần làm việc này.";
    }

    private async Task UndoSelected()
    {
        if (_busy || _tools is null || _changes.SelectedItem is not WorkspaceChange change || !DiscardEditor()) return;
        if (MessageBox.Show(this, $"Khôi phục {change.Path} về trước lần sửa đầu tiên trong phiên mở dự án này?", "Hoàn tác file",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            await _tools.RevertAsync(change.Path);
            RefreshChanges(); RefreshTree();
            if (_editorPath == change.Path)
            {
                _dirty = false;
                if (File.Exists(_tools.ResolvePath(change.Path))) await OpenFile(_tools.ResolvePath(change.Path));
                else { _editor.Clear(); _editorPath = null; _editorBaseline = ""; _dirty = false; UpdateEditorTitle(); }
            }
            _state.Text = "Đã hoàn tác " + change.Path;
            await NoteWorkspaceChange($"The user reverted {change.Path}. Re-read it before relying on earlier contents.");
        }
        catch (Exception ex) { _state.Text = ex.Message; }
    }

    private async Task StartOperation(Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        if (_tools is null || _session is null) { await PickWorkspace(); if (_tools is null || _session is null) return; }
        if (_dirty) { _state.Text = "Lưu hoặc mở lại file đang chỉnh sửa trước khi chạy agent/lệnh."; return; }
        _cancel = new CancellationTokenSource();
        _computer.BeginTurn();
        if (_computer.Target is not null && !Muse2Api.Computer.DesktopComputer.RegisterEmergencyStop(Handle))
        {
            DisableComputer(); _cancel.Dispose(); _cancel = null;
            _state.Text = "Chưa đăng ký được phím Esc để dừng. Tắt ứng dụng đang giữ phím này rồi chọn lại cửa sổ kiểm thử.";
            return;
        }
        _tools.Shell = _shell.Text;
        _terminalShell.SelectedIndex = _shell.SelectedIndex;
        SetBusy(true);
        _activeTask = RunOperation(operation, _cancel.Token);
        await _activeTask;
    }

    private async Task RunOperation(Func<CancellationToken, Task> operation, CancellationToken cancel)
    {
        try { await operation(cancel); }
        catch (OperationCanceledException) { _state.Text = "Đã dừng. Các bước hoàn tất trước đó vẫn được giữ lại."; }
        catch (Exception ex) { Publish(new("error", ex.Message)); _state.Text = DesktopCopy.FriendlyError(ex.Message); }
        finally
        {
            try
            {
                DisableComputer();
                if (cancel.IsCancellationRequested && _tools is not null) { _manualProcessId = null; await _tools.StopBackgroundProcessesAsync(); }
                DrainEvents();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                if (_session is not null)
                {
                    _session.Events = _session.Events.TakeLast(500).ToList();
                    try { await _store.SaveAsync(_session, cleanup.Token); await RefreshSessions(cleanup.Token); }
                    catch (Exception ex) { _state.Text = "Không lưu được lịch sử: " + ex.Message; }
                }
                RefreshChanges(); RefreshTree();
                if (_editorPath is not null && _tools is not null && File.Exists(_tools.ResolvePath(_editorPath))) await OpenFileAfterRun(cleanup.Token);
            }
            catch (Exception ex)
            {
                AppendConsole("Không hoàn tất việc cập nhật giao diện/lịch sử: " + ex.Message + "\n");
                _state.Text = "Lượt xử lý đã dừng. Không cập nhật được một phần giao diện; xem Terminal.";
            }
            finally
            {
                _activeThinking?.Complete("Đã dừng");
                _activeThinking = null; _activeToolCall = null; _activeAssistantCard = null;
                _pendingAssistantText = null;
                SetBusy(false);
                _cancel?.Dispose(); _cancel = null;
            }
        }
    }

    private async Task OpenFileAfterRun(CancellationToken cancel = default)
    {
        try
        {
            _editorBaseline = await ToolExecutor.ReadTextAsync(_tools!.ResolvePath(_editorPath!), cancel);
            _settingEditor = true; _editor.Text = _editorBaseline; _settingEditor = false;
            _editorDisplayBaseline = _editor.Text;
            _dirty = false; UpdateEditorTitle();
        }
        catch (Exception ex) { _state.Text = "Không tải lại được file: " + ex.Message; }
    }

    private async Task RunTurn(CancellationToken cancel)
    {
        var prompt = _prompt.Text.Trim();
        if (prompt.Length == 0 && _imageDraft.Count == 0) return;
        if (!await _ensureReady()) return;
        cancel.ThrowIfCancellationRequested();
        var connection = _connection();
        var pastedImages = _imageDraft.Select(image => image.DataUrl).ToList();
        if (prompt.Length == 0) prompt = "Hãy xem ảnh đính kèm.";
        // Turbo changes the local approval policy, never the user's task or
        // the evidence required to complete it. Vietnamese is in SystemPrompt.
        var model = string.IsNullOrWhiteSpace(_models.Text) ? "muse-chat" : _models.Text.Trim();
        _session!.Model = model;
        if (_session.History.Count == 0) _session.Title = prompt[..Math.Min(60, prompt.Length)].Replace('\n', ' ');
        var displayPrompt = prompt + (pastedImages.Count > 0 ? "\n[Đã đính kèm " + pastedImages.Count + " ảnh]" : "");
        _session.Events.Add(new("user", displayPrompt));
        _chat.AddUserMessage(displayPrompt); _prompt.Clear(); ClearImageDraft(); UpdateWelcome();
        _pendingAssistantText = null;
        _activeThinking = null;
        _activeToolCall = null;
        _activeAssistantCard = null;
        using var client = new AgentClient(connection.BaseUrl, model, connection.ApiKey, _session.Id);
        var loop = new AgentLoop(_tools!, (messages, token) => client.ChatAsync(messages, token, _ => { }),
            ConfirmTool, Publish);
        loop.History.AddRange(_session.History.Select(m => (JsonObject)m.DeepClone()));
        try { await Task.Run(() => loop.RunAsync(prompt, cancel, pastedImages), cancel); }
        finally { _session.History = loop.History.Select(m => (JsonObject)m.DeepClone()).ToList(); }
    }

    private async Task RunManualCommand(CancellationToken cancel)
    {
        var command = _command.Text.Trim();
        if (command.Length == 0) return;
        var prepared = await _tools!.PrepareAsync(new("run_command", new() { ["command"] = command, ["shell"] = _shell.Text, ["timeout_seconds"] = _backgroundCommand.Checked ? 3600 : 300, ["background"] = _backgroundCommand.Checked }), cancel);
        if (!await ConfirmTool(prepared, cancel)) { _state.Text = "Đã từ chối lệnh."; return; }
        _session!.Events.Add(new("user", "Chạy lệnh trong " + _terminalShell.Text + ":\n" + command));
        _chat.AddUserMessage("Chạy lệnh trong " + _terminalShell.Text + ":\n" + command); UpdateWelcome();
        Publish(new("tool_start", prepared.Description, "run_command"));
        try
        {
            var result = await Task.Run(() => _tools.ExecutePreparedAsync(prepared, cancel, chunk => Publish(new("command_output", chunk, "run_command"))), cancel);
            Publish(new("terminal_result", result, "run_command"));
            if (ToolOutcome.IsRunning(result))
            {
                _manualProcessId = System.Text.RegularExpressions.Regex.Match(result, @"(?m)^process_id=([a-f0-9]+)").Groups[1].Value;
                _manualProcessOutput = result;
            }
            _session!.History.Add(new() { ["role"] = "user", ["content"] = $"The user ran a terminal command:\n{command}\nResult:\n{result}" });
            _state.Text = ToolOutcome.IsRunning(result) ? "Tiến trình đang chạy nền." : ToolOutcome.IsFailure(result) ? "Lệnh chưa thành công. Xem kết quả trong Terminal." : "Lệnh đã chạy thành công.";
        }
        catch (OperationCanceledException)
        {
            _session!.History.Add(new() { ["role"] = "user", ["content"] = $"The user cancelled this terminal command: {command}. It may have partially changed the workspace; inspect before continuing." });
            throw;
        }
    }

    private async Task NoteWorkspaceChange(string text)
    {
        if (_session is null || _session.History.Count == 0) return;
        _session.History.Add(new() { ["role"] = "user", ["content"] = text });
        await _store.SaveAsync(_session);
    }

    private Task<bool> ConfirmTool(ToolApproval approval, CancellationToken cancel)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (cancel.IsCancellationRequested) { completion.TrySetCanceled(cancel); return; }
            if (_permissions.SelectedIndex == 1) { _state.Text = "Chế độ chỉ xem đang bật. Có thể đổi chế độ trong Cài đặt."; completion.TrySetResult(false); return; }
            if (_permissions.SelectedIndex == 2 && !TurboApprovalPolicy.RequiresExplicitApproval(approval.Call))
            { completion.TrySetResult(true); return; }
            DrainEvents();
            _state.Text = "Muse đang chờ bạn xem và cho phép thao tác tiếp theo.";
            using var dialog = new ToolApprovalForm(approval);
            using var registration = cancel.Register(() => Post(() => { if (!dialog.IsDisposed) dialog.Close(); }));
            var result = dialog.ShowDialog(FindForm());
            if (result == DialogResult.Abort) { _cancel?.Cancel(); completion.TrySetCanceled(cancel); }
            else completion.TrySetResult(result == DialogResult.OK && !cancel.IsCancellationRequested);
        });
        return completion.Task.WaitAsync(cancel);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _send.Visible = !busy; _pasteImage.Enabled = !busy;
        _activityGlyph.Visible = busy;
        _activityFrame = 0;
        if (busy) { _activityGlyph.Text = "◐"; _state.Text = "Đang suy nghĩ…"; }
        _stop.Enabled = busy;
        foreach (var control in new Control[] { _choose, _welcomeProject, _models, _shell, _terminalShell, _permissions, _sessions, _new, _save, _undo, _run, _computerButton, _backgroundCommand }) control.Enabled = !busy;
        _loginButton.Enabled = !busy && !_preparing;
        _welcomeConnect.Enabled = !_connected && !busy && !_preparing;
        _stop.Visible = busy;
        _editor.ReadOnly = busy;
    }

    public async Task StopAsync()
    {
        _timer.Stop(); _manualProcessId = null;
        _cancel?.Cancel();
        if (_activeTask is not null) await _activeTask;
        if (_tools is not null) await _tools.StopBackgroundProcessesAsync();
    }

    private void DrainEvents()
    {
        while (_events.TryDequeue(out var item))
        {
            switch (item.Kind)
            {
                case "assistant_start":
                    _pendingAssistantText = null;
                    _state.Text = "Đang suy nghĩ…";
                    if (_activeThinking == null) _activeThinking = _chat.AddThinking("Đang suy nghĩ…");
                    break;
                case "assistant_delta":
                    continue;
                case "assistant":
                    _pendingAssistantText = ToolCallParser.StripToolBlocks(item.Text);
                    break;
                case "tool_start":
                    _pendingAssistantText = null;
                    var activityDesc = DesktopCopy.Activity(item.Tool, item.Path, item.Text);
                    _state.Text = activityDesc;
                    AppendConsole("\n› " + item.Text + "\n");
                    _activeThinking?.AddThought(activityDesc);
                    _activeToolCall = _chat.AddToolCall(item.Tool ?? "tool", item.Path, item.Text);
                    _activeToolCall.ViewDetailsClicked += () => ToggleDrawer(2, forceOpen: true);
                    break;
                case "command_output":
                    AppendConsole(item.Text);
                    continue;
                case "terminal_result":
                case "tool_result":
                    AppendConsole(item.Text + "\n");
                    if (_activeToolCall != null)
                    {
                        _activeToolCall.SetResult(item.Text, ToolOutcome.IsFailure(item.Text));
                        _activeToolCall = null;
                    }
                    if (_activeThinking != null)
                        _activeThinking.AddThought("Hoàn tất.");
                    break;
                case "protocol_error":
                    AppendConsole(item.Text + "\n");
                    _activeThinking?.AddThought("Đang điều chỉnh phản hồi...");
                    break;
                case "protocol_retry":
                    AppendConsole(item.Text + "\n");
                    _activeThinking?.AddThought(item.Text);
                    break;
                case "error":
                    _pendingAssistantText = null;
                    AppendConsole(item.Text + "\n");
                    _activeThinking?.Complete("Đã dừng do gặp lỗi");
                    _activeThinking = null;
                    _activeToolCall = null;
                    _activeAssistantCard?.FinalizeStream();
                    _activeAssistantCard = null;
                    _chat.AddNotice(DesktopCopy.FriendlyError(item.Text));
                    _state.Text = DesktopCopy.FriendlyError(item.Text);
                    break;
                case "incomplete":
                case "declined":
                case "cancelled":
                    _pendingAssistantText = null;
                    AppendConsole(item.Text + "\n");
                    _activeThinking?.Complete(item.Text);
                    _activeThinking = null;
                    _activeToolCall = null;
                    _activeAssistantCard?.FinalizeStream();
                    _activeAssistantCard = null;
                    _chat.AddNotice(item.Text);
                    _state.Text = item.Text;
                    break;
                case "completed":
                    _activeThinking?.Complete();
                    _activeThinking = null;
                    _activeToolCall = null;
                    ShowPendingAnswer();
                    _state.Text = _tools?.Changes.Count > 0 ? "Đã hoàn tất. Bấm Thay đổi để xem kết quả hoặc hoàn tác." : "Đã hoàn tất. Bạn có thể hỏi tiếp.";
                    break;
                case "waiting_input":
                    _activeThinking?.Complete("Chờ phản hồi từ bạn");
                    _activeThinking = null;
                    _activeToolCall = null;
                    ShowPendingAnswer();
                    _state.Text = "Muse cần thêm thông tin. Bạn có thể trả lời ngay trong ô chat.";
                    break;
                default:
                    _state.Text = item.Text;
                    break;
            }
            if (item.Kind != "assistant_start" && item.Kind != "assistant_delta")
                _session?.Events.Add(item);
        }
    }

    private void ShowPendingAnswer()
    {
        if (!string.IsNullOrWhiteSpace(_pendingAssistantText)) _activeAssistantCard = _chat.AddAssistantMessage(_pendingAssistantText, stream: true);
        _pendingAssistantText = null;
    }

    private void AppendChat(string role, string text)
    {
        if (text.Length == 0) return;
        role = role switch { "user" or "bạn" => "Bạn", "assistant" or "muse" => "Muse", _ => role };
        _chat.AddMessage(role, text);
    }

    private static string TerminalResultSummary(string text)
        => ToolOutcome.IsRunning(text) ? "Tiến trình đang chạy nền. Có thể gửi yêu cầu tiếp; xem kết quả trong Terminal."
            : ToolOutcome.IsFailure(text) ? "Lệnh chưa thành công. Xem lỗi trong Terminal." : "Lệnh đã hoàn tất. Xem kết quả trong Terminal.";

    private void AppendConsole(string text)
    {
        if (_console.TextLength > 200000) { _console.Select(0, _console.TextLength - 100000); _console.SelectedText = ""; }
        _console.AppendText(text); _console.SelectionStart = _console.TextLength; _console.ScrollToCaret();
    }
}
