using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.WinForms;

public sealed partial class AgentWorkspaceControl
{
    private readonly CheckBox _backgroundCommand = new() { Text = "Chạy nền", Dock = DockStyle.Fill, ForeColor = DesktopTheme.Text };
    private readonly CleanButton _stopBackground = new() { Text = "Dừng nền", Width = 90 };
    private string? _manualProcessId;
    private string _manualProcessOutput = "";
    private bool _pollingProcess;
    private DateTime _nextProcessPoll;

    private async Task PollManualProcess()
    {
        if (_busy || _pollingProcess || _tools is null || _manualProcessId is null || DateTime.UtcNow < _nextProcessPoll) return;
        _pollingProcess = true; _nextProcessPoll = DateTime.UtcNow.AddSeconds(1);
        var tools = _tools; var id = _manualProcessId;
        try
        {
            var status = await tools.ExecuteAsync(new("process_status", new() { ["process_id"] = id }));
            if (_tools != tools || _manualProcessId != id || IsDisposed) return;
            if (status != _manualProcessOutput)
            {
                static string Output(string receipt) { var i=receipt.IndexOf("output (latest 32000 chars):\n",StringComparison.Ordinal); return i<0?receipt:receipt[(i+"output (latest 32000 chars):\n".Length)..]; }
                var previous = Output(_manualProcessOutput); var current = Output(status);
                AppendConsole(current.StartsWith(previous, StringComparison.Ordinal) ? current[previous.Length..] : "\n[đầu ra gần nhất]\n" + current);
                _manualProcessOutput = status;
            }
            if (!ToolOutcome.IsRunning(status))
            {
                _manualProcessId = null; Publish(new("terminal_result", status, "process_status"));
                _session?.History.Add(new() { ["role"] = "user", ["content"] = "Background terminal result:\n" + status });
                if (_session is not null) await _store.SaveAsync(_session);
            }
        }
        catch (Exception ex) { if (!IsDisposed) _state.Text = "Chưa cập nhật được tiến trình: " + ex.Message; }
        finally { _pollingProcess = false; }
    }

    private async Task StopTerminalProcesses()
    {
        _manualProcessId = null;
        if (_tools is null) return;
        await _tools.StopBackgroundProcessesAsync();
        AppendConsole("\nĐã dừng các tiến trình nền do Muse mở trong dự án này.\n");
        _state.Text = "Đã dừng tiến trình nền.";
    }
}
