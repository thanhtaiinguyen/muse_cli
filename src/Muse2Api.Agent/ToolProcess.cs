using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace Muse2Api.Agent;

public sealed partial class ToolExecutor
{
    public IReadOnlyList<string> LastCommandChangedFiles { get; private set; } = Array.Empty<string>();

    private async Task<string> RunCommandAsync(string command, string shell, string workdir, int timeoutSeconds,
        CancellationToken cancel, Action<string>? output)
    {
        if (string.IsNullOrWhiteSpace(command)) return "error: command must not be empty";
        LastCommandChangedFiles = Array.Empty<string>();
        var before = await SnapshotWorkspace(cancel);
        var result = await RunShellAsync(command, shell, workdir, timeoutSeconds, cancel, output);
        var after = await SnapshotWorkspace(cancel);
        var changed = before.Files.Keys.Where(path => !File.Exists(Resolve(path))
            || after.Files.TryGetValue(path, out var hash) && hash != before.Files[path]).ToList();
        if (!before.Truncated) changed.AddRange(after.Files.Keys.Where(path => !before.Files.ContainsKey(path)));
        LastCommandChangedFiles = changed.Distinct().OrderBy(path => path).Take(100).ToArray();
        return result + (LastCommandChangedFiles.Count == 0 ? "" : "\nworkspace_files_changed_during_command=" + JsonSerializer.Serialize(LastCommandChangedFiles));
    }

    private async Task<(Dictionary<string, string> Files, bool Truncated)> SnapshotWorkspace(CancellationToken cancel)
    {
        var hashes = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long bytes = 0;
        var truncated = false;
        foreach (var path in WorkspaceFiles.Enumerate(Root, true, cancel).Where(File.Exists))
        {
            if (hashes.Count >= 2000 || bytes >= 64 * 1024 * 1024) { truncated = true; break; }
            try
            {
                var resolved = Resolve(path);
                var length = new FileInfo(resolved).Length;
                if (length > MaxTextBytes) { truncated = true; continue; }
                using var stream = File.OpenRead(resolved);
                hashes[Path.GetRelativePath(Root, resolved)] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
                bytes += length;
            }
            catch (IOException) { truncated = true; }
            catch (UnauthorizedAccessException) { truncated = true; }
            catch (InvalidOperationException) { truncated = true; }
        }
        return (hashes, truncated);
    }

    private async Task<string> RunShellAsync(string command, string shell, string workdir, int timeoutSeconds,
        CancellationToken cancel, Action<string>? output)
    {
        var invocation = await CreateShellInvocation(command, shell, cancel);
        try { return await RunProcessAsync(invocation.File, invocation.Args, Resolve(workdir), timeoutSeconds, cancel, output, invocation.Encoding); }
        finally { invocation.Cleanup(); }
    }

    private sealed record ShellInvocation(string File, string[] Args, Encoding? Encoding, string? TemporaryDirectory = null)
    {
        public void Cleanup()
        {
            if (TemporaryDirectory is null) return;
            var script = Path.Combine(TemporaryDirectory, "command.cmd");
            if (System.IO.File.Exists(script)) System.IO.File.Delete(script);
            if (Directory.Exists(TemporaryDirectory)) Directory.Delete(TemporaryDirectory);
        }
    }

    private async Task<ShellInvocation> CreateShellInvocation(string command, string shell, CancellationToken cancel)
    {
        var (file, args) = ResolveShell(shell, command);
        if (OperatingSystem.IsWindows() && Path.GetFileName(file).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            // EncodedCommand transports multiline Unicode/quotes without a
            // second command-line quoting pass. Non-terminating PowerShell
            // errors and failed native tools must not be reported as exit 0.
            var script = "[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)\n[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n$OutputEncoding = [Console]::OutputEncoding\n$ErrorActionPreference = 'Stop'\n$global:LASTEXITCODE = 0\ntry {\n" + command
                + "\nif ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\nif (-not $?) { exit 1 }\n} catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
            args = new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) };
        }
        if (OperatingSystem.IsWindows() && Path.GetFileName(file).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            // cmd.exe does not use the CRT quote parser. A private script
            // preserves embedded quotes/newlines and UTF-8 filenames exactly.
            var directory = Path.Combine(Path.GetTempPath(), "muse-command-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var script = Path.Combine(directory, "command.cmd");
            try
            {
                await File.WriteAllTextAsync(script, "@echo off\r\nchcp 65001 >nul\r\n" + command.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n", new UTF8Encoding(false), cancel);
                return new(file, new[] { "/d", "/c", "call", script }, Encoding.UTF8, directory);
            }
            catch { if (File.Exists(script)) File.Delete(script); Directory.Delete(directory); throw; }
        }
        return new(file, args, OperatingSystem.IsWindows() ? Encoding.UTF8 : null);
    }

    private Task<string> GitAsync(string[] args, CancellationToken cancel)
        => RunProcessAsync("git", args, Root, 30, cancel, null);

    private static ProcessStartInfo ProcessInfo(string file, string[] args, string workdir, Encoding? encoding, bool input = false)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = workdir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input,
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        if (encoding is not null) { start.StandardOutputEncoding = encoding; start.StandardErrorEncoding = encoding; }
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        // Explorer may have an older PATH than the terminal used to install an SDK.
        var extra = new List<string>();
        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet") })
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(Path.Combine(candidate, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))) extra.Add(candidate);
        for (var ancestor = new DirectoryInfo(workdir); ancestor is not null && extra.Count < 12; ancestor = ancestor.Parent)
        {
            var candidate = Path.Combine(ancestor.FullName, "dotnet");
            if (File.Exists(Path.Combine(candidate, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))) extra.Add(candidate);
        }
        if (extra.Count > 0) start.Environment["PATH"] = string.Join(Path.PathSeparator, extra.Distinct()) + Path.PathSeparator + (start.Environment.TryGetValue("PATH", out var oldPath) ? oldPath : "");
        return start;
    }

    private static async Task<string> RunProcessAsync(string file, string[] args, string workdir,
        int timeoutSeconds, CancellationToken cancel, Action<string>? output, Encoding? encoding = null)
    {
        var start = ProcessInfo(file, args, workdir, encoding);
        using var process = new Process { StartInfo = start };
        cancel.ThrowIfCancellationRequested();
        try { process.Start(); }
        catch (Exception ex) { return $"error: could not start {file}: {ex.Message}"; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));
        async Task<string> Drain(StreamReader reader, string prefix)
        {
            var captured = new StringBuilder();
            var tail = new StringBuilder();
            var total = 0;
            var buffer = new char[2048];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
            {
                total += read;
                if (captured.Length < 16000) captured.Append(buffer, 0, Math.Min(read, 16000 - captured.Length));
                tail.Append(buffer, 0, read); if (tail.Length > 8000) tail.Remove(0, tail.Length - 8000);
                output?.Invoke(prefix + new string(buffer, 0, read));
            }
            if (total > captured.Length) { captured.Length = Math.Min(captured.Length, 8000); captured.Append($"\n[output truncated; {total} characters total; last 8000 characters follow]\n").Append(tail); }
            return captured.ToString();
        }
        var stdout = Drain(process.StandardOutput, "");
        var stderr = Drain(process.StandardError, "[stderr] ");
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
            return $"exit_code={process.ExitCode}\nstdout:\n{await stdout}\nstderr:\n{await stderr}";
        }
        catch (OperationCanceledException)
        {
            await ProcessTree.StopAsync(process);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            cancel.ThrowIfCancellationRequested();
            return $"error: command timed out after {Math.Clamp(timeoutSeconds, 1, 3600)}s and was killed";
        }
    }
}
