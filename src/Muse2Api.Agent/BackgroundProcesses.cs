using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Muse2Api.Agent;

public sealed partial class ToolExecutor
{
    private sealed class RunningProcess(Process process, ShellInvocation invocation)
    {
        public Process Process { get; } = process;
        public StringBuilder Output { get; } = new();
        public object Sync { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool TimedOut { get; set; }
        public void Append(string value) { lock (Sync) { Output.Append(value); if (Output.Length > 32000) Output.Remove(0, Output.Length - 32000); } }
        public string Text { get { lock (Sync) return Output.ToString(); } }
        public void Cleanup() => invocation.Cleanup();
    }
    private readonly Dictionary<string, RunningProcess> _processes = new();
    private readonly object _processLock = new();

    private async Task<string> StartProcessAsync(JsonObject args, CancellationToken cancel)
    {
        var command = Str(args, "command");
        if (string.IsNullOrWhiteSpace(command)) return "error: command must not be empty";
        lock (_processLock)
        {
            foreach (var expiredId in _processes.Where(p => p.Value.Completion.IsCompleted).Take(Math.Max(0, _processes.Count - 50)).Select(p => p.Key).ToArray())
            { _processes[expiredId].Process.Dispose(); _processes.Remove(expiredId); }
            if (_processes.Values.Count(p => !p.Completion.IsCompleted) >= 8) return "error: background process limit reached (8). Stop an existing process first.";
        }
        LastCommandChangedFiles = Array.Empty<string>();
        var directory = Resolve(Str(args, "workdir", "."));
        if (!Directory.Exists(directory)) return "error: working directory does not exist";
        var invocation = await CreateShellInvocation(command, Str(args, "shell"), cancel);
        var process = new Process { StartInfo = ProcessInfo(invocation.File, invocation.Args, directory, invocation.Encoding, input: true) };
        try { cancel.ThrowIfCancellationRequested(); process.Start(); }
        catch { invocation.Cleanup(); process.Dispose(); throw; }
        var running = new RunningProcess(process, invocation);
        var id = Guid.NewGuid().ToString("N");
        async Task Drain(StreamReader reader, string prefix)
        {
            var buffer = new char[2048]; int read;
            while ((read = await reader.ReadAsync(buffer)) > 0) running.Append(prefix + new string(buffer, 0, read));
        }
        running.Completion = Task.Run(async () =>
        {
            var stdout = Drain(process.StandardOutput, ""); var stderr = Drain(process.StandardError, "[stderr] ");
            try
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(Math.Clamp(Int(args, "timeout_seconds", 300), 1, 3600))); }
                catch (TimeoutException) { running.TimedOut = true; await ProcessTree.StopAsync(process); running.Append("\n[background process timed out and was killed]\n"); }
                await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
            }
            catch (Exception ex) { running.Append("\nerror: " + ex.Message); }
            finally { running.Cleanup(); }
        });
        lock (_processLock) _processes[id] = running;
        try { await Task.Delay(250, cancel); }
        catch (OperationCanceledException) { await ProcessTree.StopAsync(process); throw; }
        return ProcessStatus(id, running);
    }

    private static string ProcessStatus(string id, RunningProcess running)
        => $"process_id={id}\nrunning={!running.Completion.IsCompleted}\n" + (running.Completion.IsCompleted ? $"exit_code={running.Process.ExitCode}\n" : "") + (running.TimedOut ? "timed_out=true\n" : "") + "output (latest 32000 chars):\n" + running.Text;

    private async Task<string> ProcessToolAsync(ToolCall call, CancellationToken cancel)
    {
        var id = Str(call.Args, "process_id");
        RunningProcess running;
        lock (_processLock) running = _processes.GetValueOrDefault(id) ?? throw new InvalidOperationException("Unknown process_id in this workspace session");
        cancel.ThrowIfCancellationRequested();
        if (call.Name == "process_stop") { await ProcessTree.StopAsync(running.Process); await running.Completion.WaitAsync(cancel); }
        else if (call.Name == "process_input")
        {
            if (running.Completion.IsCompleted) return "error: process has already exited";
            var input = Str(call.Args, "text");
            if (input.Length > 16000) return "error: process input is too long";
            await running.Process.StandardInput.WriteAsync(input.AsMemory(), cancel);
            if (Bool(call.Args, "newline", true)) await running.Process.StandardInput.WriteLineAsync();
            await running.Process.StandardInput.FlushAsync(cancel);
        }
        else await Task.WhenAny(running.Completion, Task.Delay(Math.Clamp(Int(call.Args, "wait_ms", 30000), 0, 60000), cancel));
        cancel.ThrowIfCancellationRequested();
        return (call.Name == "process_stop" ? "stopped=true\n" : "") + ProcessStatus(id, running);
    }

    public async Task StopBackgroundProcessesAsync()
    {
        RunningProcess[] processes; lock (_processLock) { processes = _processes.Values.ToArray(); _processes.Clear(); }
        await Task.WhenAll(processes.Select(process => ProcessTree.StopAsync(process.Process)));
        foreach (var process in processes) { await process.Completion; process.Process.Dispose(); }
    }
}
