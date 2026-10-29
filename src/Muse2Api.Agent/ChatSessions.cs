using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Muse2Api.Agent;

public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Cuộc trò chuyện mới";
    public string Workspace { get; set; } = "";
    public string Model { get; set; } = "muse-chat";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<JsonObject> History { get; set; } = new();
    public List<AgentEvent> Events { get; set; } = new();
    public override string ToString() => $"{UpdatedAt.ToLocalTime():dd/MM HH:mm} · {Title}";
}

/// <summary>Local chat history per workspace; connection keys are never included.</summary>
public sealed class ChatSessionStore(string root)
{
    private static string Canonical(string workspace)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private string DirectoryFor(string workspace)
        => Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(workspace)))));

    private string FileFor(string workspace, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid session ID");
        return Path.Combine(DirectoryFor(workspace), id + ".json");
    }

    public async Task SaveAsync(ChatSession session, CancellationToken cancel = default)
    {
        var file = FileFor(session.Workspace, session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
        if (json.Length > 8_000_000) throw new InvalidOperationException("Chat history exceeds 8 million characters; start a new conversation");
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, json, cancel);
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<ChatSession> LoadAsync(string workspace, string id, CancellationToken cancel = default)
    {
        var session = JsonSerializer.Deserialize<ChatSession>(await File.ReadAllTextAsync(FileFor(workspace, id), cancel))
            ?? throw new InvalidOperationException("Empty chat session");
        if (session.Id != id || Canonical(session.Workspace) != Canonical(workspace)) throw new InvalidOperationException("Session does not belong to this workspace");
        return session;
    }

    public async Task<List<ChatSession>> ListAsync(string workspace, CancellationToken cancel = default)
    {
        var directory = DirectoryFor(workspace);
        if (!Directory.Exists(directory)) return new();
        var sessions = new List<ChatSession>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
        {
            try { sessions.Add(await LoadAsync(workspace, Path.GetFileNameWithoutExtension(file), cancel)); }
            catch (JsonException) { }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }
        return sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }
}
