namespace Muse2Api.Agent;

/// <summary>Bounded project browsing; never traverse links or generated/dependency folders.</summary>
public static class WorkspaceFiles
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", "bin", "obj", "artifacts", "node_modules", ".next", "dist", "coverage", "__pycache__", ".venv", "venv" };

    public static IEnumerable<string> Enumerate(string root, bool recursive = false, CancellationToken cancel = default)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0 && visited++ < 2000)
        {
            cancel.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries.OrderBy(e => !Directory.Exists(e)).ThenBy(e => e, StringComparer.OrdinalIgnoreCase))
            {
                cancel.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory && Ignored.Contains(Path.GetFileName(entry))) continue;
                yield return entry;
                if (recursive && isDirectory) pending.Push(entry);
            }
            if (!recursive) yield break;
        }
    }
}
