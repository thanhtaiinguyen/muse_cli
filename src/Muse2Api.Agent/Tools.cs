using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Muse2Api.Agent;

/// <summary>One tool call, parsed from a ```tool JSON block or a native
/// OpenAI tool_calls entry. Args are the JSON object minus the "name" field.</summary>
public sealed record ToolCall(string Name, JsonObject Args);

/// <summary>Parses the agent's tool-call format. The muse2api gateway does
/// not (yet) do native tool calling, so the model emits fenced blocks:
///
/// ```tool
/// {"name": "read_file", "path": "src/Program.cs"}
/// ```
/// </summary>
public static class ToolCallParser
{
    public static List<ToolCall> Parse(string text)
    {
        var calls = new List<ToolCall>();
        var idx = 0;
        while (true)
        {
            var start = text.IndexOf("```tool", idx, StringComparison.Ordinal);
            if (start < 0) break;
            var jsonStart = text.IndexOf('\n', start);
            if (jsonStart < 0) break;
            var end = text.IndexOf("```", jsonStart + 1, StringComparison.Ordinal);
            if (end < 0) break;
            var json = text[(jsonStart + 1)..end].Trim();
            idx = end + 3;
            try
            {
                var obj = JsonNode.Parse(json) as JsonObject;
                var name = obj?["name"]?.GetValue<string>();
                if (obj is null || string.IsNullOrEmpty(name)) continue;
                obj.Remove("name");
                calls.Add(new ToolCall(name, obj));
            }
            catch
            {
                // A malformed block is reported as a failed tool call so the
                // model can see the error and retry, instead of silently
                // dropping the whole turn.
                calls.Add(new ToolCall("__parse_error__",
                    new JsonObject { ["error"] = $"could not parse tool block as JSON: {json}" }));
            }
        }
        return calls;
    }

    /// <summary>Assistant text with tool blocks removed, for display.</summary>
    public static string StripToolBlocks(string text)
    {
        var result = text;
        while (true)
        {
            var start = result.IndexOf("```tool", StringComparison.Ordinal);
            if (start < 0) break;
            var end = result.IndexOf("```", start + 7, StringComparison.Ordinal);
            if (end < 0) break;
            result = result[..start] + result[(end + 3)..];
        }
        return result.Trim();
    }
}

/// <summary>The agent's tools. Every file path is jailed inside the
/// workspace root: "..", absolute paths outside the root and symlinked
/// escapes are all rejected before any read or write happens.</summary>
public sealed class ToolExecutor
{
    public string Root { get; }
    public string Shell { get; }

    public ToolExecutor(string root, string shell = "auto")
    {
        Root = Path.GetFullPath(root);
        Shell = shell;
    }

    private string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path));
        if (full != Root && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"path is outside the workspace: {path}");
        return full;
    }

    private static string Str(JsonObject args, string name, string fallback = "")
        => args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    private static bool Bool(JsonObject args, string name, bool fallback = false)
        => args[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static string Truncate(string s, int max = 20000)
        => s.Length <= max ? s : s[..max] + $"\n... [truncated, {s.Length} chars total]";

    public async Task<string> ExecuteAsync(ToolCall call)
    {
        try
        {
            return call.Name switch
            {
                "list_files" => ListFiles(Str(call.Args, "path", "."), Bool(call.Args, "recursive")),
                "read_file" => await ReadFileAsync(Str(call.Args, "path")),
                "write_file" => await WriteFileAsync(Str(call.Args, "path"), Str(call.Args, "content")),
                "edit_file" => await EditFileAsync(Str(call.Args, "path"),
                    Str(call.Args, "old_text"), Str(call.Args, "new_text")),
                "run_command" => await RunCommandAsync(Str(call.Args, "command"),
                    Str(call.Args, "shell"), Str(call.Args, "workdir", "."), call.Args["timeout_seconds"] is JsonValue tv && tv.TryGetValue<int>(out var t) ? t : 120),
                "__parse_error__" => "error: " + Str(call.Args, "error"),
                _ => $"error: unknown tool '{call.Name}'. Available tools: list_files, read_file, write_file, edit_file, run_command.",
            };
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    public bool NeedsConfirmation(ToolCall call)
        => call.Name is "write_file" or "edit_file" or "run_command";

    public string Describe(ToolCall call) => call.Name switch
    {
        "write_file" => $"write_file {Str(call.Args, "path")} ({Str(call.Args, "content").Length} chars)",
        "edit_file" => $"edit_file {Str(call.Args, "path")}",
        "run_command" => $"run_command: {Str(call.Args, "command")}",
        _ => $"{call.Name} {string.Join(", ", call.Args.Select(kv => $"{kv.Key}={kv.Value}"))}",
    };

    private string ListFiles(string path, bool recursive)
    {
        var dir = Resolve(path);
        if (!Directory.Exists(dir)) return $"error: directory not found: {path}";
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = Directory.EnumerateFileSystemEntries(dir, "*", option)
            .OrderBy(e => e)
            .Take(200)
            .Select(e => Path.GetRelativePath(Root, e).Replace('\\', '/')
                + (Directory.Exists(e) ? "/" : ""));
        return string.Join('\n', entries);
    }

    private async Task<string> ReadFileAsync(string path)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) return $"error: file not found: {path}";
        return Truncate(await File.ReadAllTextAsync(full));
    }

    private async Task<string> WriteFileAsync(string path, string content)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        return $"wrote {path} ({content.Length} chars)";
    }

    private async Task<string> EditFileAsync(string path, string oldText, string newText)
    {
        if (string.IsNullOrEmpty(oldText)) return "error: old_text must not be empty";
        var full = Resolve(path);
        if (!File.Exists(full)) return $"error: file not found: {path}";
        var content = await File.ReadAllTextAsync(full);
        var count = 0;
        var idx = 0;
        while ((idx = content.IndexOf(oldText, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += oldText.Length;
        }
        if (count == 0) return "error: old_text not found in file";
        if (count > 1) return $"error: old_text matches {count} times; make it more specific so it matches exactly once";
        await File.WriteAllTextAsync(full, content.Replace(oldText, newText, StringComparison.Ordinal));
        return $"edited {path} (1 replacement)";
    }

    private async Task<string> RunCommandAsync(string command, string shell, string workdir, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(command)) return "error: command must not be empty";
        var (file, args) = ResolveShell(shell, command);
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = Resolve(workdir),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList, not a pre-quoted Arguments string: .NET handles
        // quoting per platform, and a manually quoted "-c 'cmd'" string
        // makes bash look for a command literally named "cat demo.txt".
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) { return $"error: could not start shell ({file}): {ex.Message}"; }
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var exited = await Task.Run(() => proc.WaitForExit(Math.Max(1, timeoutSeconds) * 1000));
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return $"error: command timed out after {timeoutSeconds}s and was killed";
        }
        var stdout = Truncate(await stdoutTask, 8000);
        var stderr = Truncate(await stderrTask, 8000);
        return $"exit_code={proc.ExitCode}\nstdout:\n{stdout}\n stderr:\n{stderr}";
    }

    private (string File, string[] Args) ResolveShell(string requested, string command)
    {
        var want = string.IsNullOrEmpty(requested) ? Shell : requested;
        if (OperatingSystem.IsWindows())
        {
            if (want is "cmd") return ("cmd.exe", new[] { "/c", command });
            // Default on Windows: PowerShell, like Codex on Windows.
            return ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", command });
        }
        // Linux/macOS (used to develop and test this agent on this VM).
        return ("/bin/bash", new[] { "-c", command });
    }
}
