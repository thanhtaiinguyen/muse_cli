using System.Security.Cryptography;
using System.Text;

namespace Muse2Api.Agent;

public sealed partial class ToolExecutor
{
    private sealed class ReadVersion(string hash)
    {
        public string Hash { get; } = hash;
        public List<(int Start, int End)> Ranges { get; } = new();
        public bool Complete { get; set; }
    }
    private readonly Dictionary<string, ReadVersion> _readVersions = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public bool LastFileChangeApplied { get; private set; }
    public void BeginAgentTurn() { lock (_changeLock) _readVersions.Clear(); }

    public static string ContentHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void RecordRead(string full, string text, int start, int end, int total)
    {
        var hash = ContentHash(text);
        lock (_changeLock)
        {
            if (!_readVersions.TryGetValue(full, out var version) || version.Hash != hash)
                _readVersions[full] = version = new(hash);
            version.Ranges.Add((start, end));
            var covered = 0;
            foreach (var range in version.Ranges.OrderBy(r => r.Start))
            {
                if (range.Start > covered + 1) break;
                covered = Math.Max(covered, range.End);
            }
            version.Complete = covered >= total;
        }
    }

    public async Task<ToolApproval> PrepareForAgentAsync(ToolCall call, CancellationToken cancel = default)
    {
        var prepared = await PrepareAsync(call, cancel);
        if (call.Name is not ("write_file" or "edit_file") || prepared.OriginalText is null) return prepared;
        var hash = ContentHash(prepared.OriginalText);
        lock (_changeLock)
        {
            if (!_readVersions.TryGetValue(prepared.FilePath!, out var version) || version.Hash != hash)
                throw new InvalidOperationException("Read the latest file with read_file before changing it; the prior snapshot is missing or stale.");
            if (call.Name == "write_file" && !version.Complete)
                throw new InvalidOperationException("Replacing a whole file requires reading all its pages first. Use edit_file for a targeted change instead.");
        }
        if (call.Name == "write_file" && (prepared.OriginalText.Length > 48000
            || prepared.OriginalText.Length >= 1000 && prepared.NewText!.Length < prepared.OriginalText.Length / 2))
            throw new InvalidOperationException("A large or partial whole-file replacement is not allowed for agent writes. Use targeted edit_file replacements so unrelated code cannot be silently discarded.");
        var expected = Str(call.Args, "expected_hash");
        if (expected.Length > 0 && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("expected_hash does not match the current file; read it again before editing.");
        return prepared;
    }
}
