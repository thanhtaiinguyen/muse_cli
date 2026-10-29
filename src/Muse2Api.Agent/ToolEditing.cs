using System.Text;
using System.Text.Json.Nodes;

namespace Muse2Api.Agent;

public sealed record ToolApproval(ToolCall Call, string Description, string Preview,
    string? FilePath = null, string? OriginalText = null, string? NewText = null, Encoding? FileEncoding = null);
public sealed record WorkspaceChange(string Path, string? OriginalText, string CurrentText, Encoding? FileEncoding = null)
{
    public string Kind => OriginalText is null ? "added" : "modified";
    public string Diff => TextDiff.Create(Path, OriginalText, CurrentText);
}

public static class TextDiff
{
    // Single-hunk review preview; line endings are normalized for display.
    public static string Create(string path, string? before, string after)
    {
        var oldLines = before is null ? Array.Empty<string>() : before.Replace("\r\n", "\n").Split('\n');
        var newLines = after.Replace("\r\n", "\n").Split('\n');
        var prefix = 0;
        while (prefix < Math.Min(oldLines.Length, newLines.Length) && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(oldLines.Length, newLines.Length) - prefix && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
        if (prefix == oldLines.Length && prefix == newLines.Length) return "(không có thay đổi)";
        var start = Math.Max(0, prefix - 3);
        var oldEnd = Math.Min(oldLines.Length, oldLines.Length - suffix + 3);
        var newEnd = Math.Min(newLines.Length, newLines.Length - suffix + 3);
        var text = new StringBuilder($"--- {(before is null ? "/dev/null" : "a/" + path)}\n+++ b/{path}\n@@ -{(oldEnd == 0 ? 0 : start + 1)},{oldEnd - start} +{start + 1},{newEnd - start} @@\n");
        foreach (var line in oldLines[start..prefix]) text.AppendLine(" " + line);
        foreach (var line in oldLines[prefix..(oldLines.Length - suffix)]) text.AppendLine("-" + line);
        foreach (var line in newLines[prefix..(newLines.Length - suffix)]) text.AppendLine("+" + line);
        foreach (var line in newLines[(newLines.Length - suffix)..newEnd]) text.AppendLine(" " + line);
        return text.ToString();
    }
}

public sealed partial class ToolExecutor
{
    public const long MaxTextBytes = 2 * 1024 * 1024;
    private readonly Dictionary<string, WorkspaceChange> _changes = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _changeLock = new();
    public IReadOnlyList<WorkspaceChange> Changes { get { lock (_changeLock) return _changes.Values.OrderBy(c => c.Path).ToList(); } }
    private static int Int(JsonObject args, string name, int fallback)
        => args[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;

    public string ResolvePath(string path) => Resolve(path);
    public static async Task<string> ReadTextAsync(string path, CancellationToken cancel = default)
        => (await ReadTextFileAsync(path, cancel)).Text;

    private static async Task<(string Text, Encoding Encoding)> ReadTextFileAsync(string path, CancellationToken cancel)
    {
        if (new FileInfo(path).Length > MaxTextBytes) throw new InvalidOperationException("text file exceeds 2 MiB");
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancel);
        if (text.Length > MaxTextBytes) throw new InvalidOperationException("text file exceeds 2 MiB");
        if (text.Contains('\0')) throw new InvalidOperationException("binary files are not supported");
        return (text, reader.CurrentEncoding);
    }

    public async Task<ToolApproval> PrepareAsync(ToolCall original, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var call = new ToolCall(original.Name, (JsonObject)original.Args.DeepClone());
        if (call.Name is not ("write_file" or "edit_file"))
        {
            var detail = call.Name == "run_command"
                ? $"Workspace: {Resolve(Str(call.Args, "workdir", "."))}\nShell: {ResolveShell(Str(call.Args, "shell", Shell), "").File}\nTimeout: {Math.Clamp(Int(call.Args, "timeout_seconds", 120), 1, 3600)}s\n\n{Str(call.Args, "command")}\n\nLệnh shell chạy với quyền của bạn và có thể truy cập ngoài thư mục dự án."
                : Describe(call);
            return new ToolApproval(call, Describe(call), detail);
        }
        var path = Str(call.Args, "path");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("path is required");
        var full = Resolve(path);
        string? before = null;
        Encoding encoding = new UTF8Encoding(false);
        if (File.Exists(full)) (before, encoding) = await ReadTextFileAsync(full, cancel);
        string after;
        if (call.Name == "write_file")
        {
            if (call.Args["content"] is not JsonValue v || !v.TryGetValue<string>(out var content)) throw new InvalidOperationException("content must be a string");
            after = content;
        }
        else
        {
            if (before is null) throw new FileNotFoundException($"file not found: {path}");
            after = before;
            var edits = call.Args["edits"] is JsonArray batch ? batch : new JsonArray(call.Args.DeepClone());
            if (edits.Count is < 1 or > 100) throw new InvalidOperationException("edits must contain 1 to 100 replacements");
            foreach (var item in edits)
            {
                if (item is not JsonObject edit) throw new InvalidOperationException("each edit must be an object");
                var old = Str(edit, "old_text", Str(edit, "search"));
                if (old.Length == 0) throw new InvalidOperationException("old_text must not be empty");
                var value = edit["new_text"] ?? edit["replace"];
                if (value is not JsonValue v || !v.TryGetValue<string>(out var replacement)) throw new InvalidOperationException("new_text must be a string");
                // Model replies and browser code blocks commonly use LF, while
                // Windows source files use CRLF. Match normalized text exactly
                // once and map its range back to the original file.
                var normalized = after.Replace("\r\n", "\n");
                old = old.Replace("\r\n", "\n");
                var index = normalized.IndexOf(old, StringComparison.Ordinal);
                if (index < 0) throw new InvalidOperationException("old_text not found in file; read the latest content first");
                if (normalized.IndexOf(old, index + old.Length, StringComparison.Ordinal) >= 0) throw new InvalidOperationException("old_text matches more than once; make it more specific");
                var start = OriginalOffset(after, index);
                var end = OriginalOffset(after, index + old.Length);
                var newline = after.Contains("\r\n") ? "\r\n" : "\n";
                replacement = replacement.Replace("\r\n", "\n").Replace("\n", newline);
                after = after[..start] + replacement + after[end..];
            }
        }
        if (Encoding.UTF8.GetByteCount(after) > MaxTextBytes) throw new InvalidOperationException("text file exceeds 2 MiB");
        return new ToolApproval(call, Describe(call), TextDiff.Create(path, before, after), full, before, after, encoding);
    }

    private async Task<string> ApplyPreparedAsync(ToolApproval change, CancellationToken cancel)
    {
        LastFileChangeApplied = false;
        var full = Resolve(change.FilePath ?? throw new InvalidOperationException("missing prepared file change"));
        var current = File.Exists(full) ? await ReadTextAsync(full, cancel) : null;
        if (current != change.OriginalText) return "error: file changed after preview; read the file and request a new edit";
        if (current == change.NewText) return "unchanged: requested content already matches " + Path.GetRelativePath(Root, full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".muse-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, change.NewText!, change.FileEncoding ?? new UTF8Encoding(false), cancel);
            cancel.ThrowIfCancellationRequested();
            Resolve(full);
            var latest = File.Exists(full) ? await ReadTextAsync(full, cancel) : null;
            if (latest != change.OriginalText) return "error: file changed while applying edit; request a new preview";
            File.Move(temporary, full, overwrite: true);
            // Once committed, record the journal even if cancellation arrives now.
            var verified = await ReadTextAsync(full, CancellationToken.None);
            if (verified != change.NewText) return "error: file changed immediately after saving; re-read it before continuing";
            LastFileChangeApplied = true;
            var lines = verified.Split('\n').Length;
            RecordRead(full, verified, 1, lines, lines);
        }
        finally { if (File.Exists(temporary)) File.Delete(Resolve(temporary)); }
        var relative = Path.GetRelativePath(Root, full);
        lock (_changeLock)
        {
            var original = _changes.TryGetValue(relative, out var existing) ? existing.OriginalText : change.OriginalText;
            if (original == change.NewText) _changes.Remove(relative);
            else _changes[relative] = new WorkspaceChange(relative, original, change.NewText!, existing?.FileEncoding ?? change.FileEncoding);
        }
        return change.Call.Name == "write_file" ? $"wrote {relative} ({change.NewText!.Length} chars)" : $"edited {relative} ({(change.Call.Args["edits"] as JsonArray)?.Count ?? 1} replacement(s))";
    }

    private static int OriginalOffset(string text, int normalizedOffset)
    {
        var position = 0;
        for (var normalized = 0; normalized < normalizedOffset; normalized++, position++)
            if (text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n') position++;
        return position;
    }

    public async Task RevertAsync(string path, CancellationToken cancel = default)
    {
        WorkspaceChange change;
        lock (_changeLock) change = _changes.GetValueOrDefault(path) ?? throw new InvalidOperationException("no tracked change for this file");
        var full = Resolve(path);
        if (!File.Exists(full) || await ReadTextAsync(full, cancel) != change.CurrentText)
            throw new InvalidOperationException("File đã đổi ngoài agent. Tải lại và kiểm tra trước khi hoàn tác.");
        if (change.OriginalText is null) { cancel.ThrowIfCancellationRequested(); File.Delete(full); }
        else
        {
            var temporary = full + ".muse-undo-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, change.OriginalText, change.FileEncoding ?? new UTF8Encoding(false), cancel);
                if (await ReadTextAsync(full, cancel) != change.CurrentText) throw new InvalidOperationException("File changed while preparing undo; no overwrite was performed.");
                cancel.ThrowIfCancellationRequested(); Resolve(full); File.Move(temporary, full, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(Resolve(temporary)); }
        }
        lock (_changeLock) _changes.Remove(path);
    }

    public async Task<string> WorkspaceContextAsync(CancellationToken cancel = default)
    {
        var listing = string.Join('\n', WorkspaceFiles.Enumerate(Root).Take(80).Select(p => Path.GetRelativePath(Root, p) + (Directory.Exists(p) ? "/" : "")));
        var context = $"Workspace: {Root}\nOS: {Environment.OSVersion.Platform}; shell: {Shell}\nProject entries (generated folders omitted):\n{listing}\n";
        context += "\nComputer tools: " + (Computer?.Context ?? "unavailable") + "\n";
        var instructions = Resolve("AGENTS.md");
        if (File.Exists(instructions)) context += "\nProject AGENTS.md:\n" + Truncate(await ReadTextAsync(instructions, cancel), 12000);
        return context;
    }

    private async Task<string> SearchFilesAsync(JsonObject args, CancellationToken cancel)
    {
        var query = Str(args, "query");
        if (query.Length == 0) return "error: query must not be empty";
        var path = Str(args, "path", ".");
        var scope = Resolve(path);
        var singleFile = File.Exists(scope);
        if (!singleFile && !Directory.Exists(scope)) return $"error: search path not found: {path}. Use list_files to locate the file or directory.";
        // A model may narrow a search to one source file. Never pass that file
        // to Directory.GetFileSystemEntries (ERROR_INVALID_PARAMETER on Windows).
        var files = singleFile ? new[] { scope } : WorkspaceFiles.Enumerate(scope, true, cancel).Where(File.Exists);
        var comparison = Bool(args, "case_sensitive") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<string>();
        var scanned = 0;
        foreach (var file in files)
        {
            cancel.ThrowIfCancellationRequested();
            if (scanned >= 2000 || matches.Count >= 100) break;
            scanned++;
            string text;
            try { text = await ReadTextAsync(Resolve(file), cancel); }
            // Directory scans can skip non-text/inaccessible entries. An explicitly
            // selected file must report the reason it could not be searched.
            catch (IOException) when (!singleFile) { continue; }
            catch (UnauthorizedAccessException) when (!singleFile) { continue; }
            catch (InvalidOperationException) when (!singleFile) { continue; }
            catch (DecoderFallbackException) when (!singleFile) { continue; }
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && matches.Count < 100; i++)
                if (lines[i].Contains(query, comparison))
                    matches.Add($"{Path.GetRelativePath(Root, file).Replace('\\', '/')}:{i + 1}: {Truncate(lines[i].TrimEnd('\r'), 300)}");
        }
        return matches.Count == 0 ? $"No matches (scanned {scanned} files)." : string.Join('\n', matches) + $"\n[{matches.Count} matches; limit 100; scanned {scanned} files]";
    }
}
