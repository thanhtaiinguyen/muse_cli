using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Muse2Api;

/// <summary>Browser state belongs to an account and its current login, never to a build directory.</summary>
public sealed class BrowserSessionStore(string dataDirectory)
{
    private sealed record SavedState(string AuthenticationHash, string StorageState);
    private string PathFor(Account account) => Path.Combine(dataDirectory, "browser-sessions",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Id))) + ".json");

    public static string AuthenticationHash(Account account)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            Upstream.SessionCookies.Select(name => name + "=" + account.Cookies.GetValueOrDefault(name, ""))))));

    public string? Load(Account account)
    {
        try
        {
            var file = PathFor(account);
            if (!File.Exists(file)) return null;
            var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(file));
            if (saved is null || saved.AuthenticationHash != AuthenticationHash(account)) return null;
            using var state = JsonDocument.Parse(saved.StorageState);
            return state.RootElement.ValueKind == JsonValueKind.Object
                && state.RootElement.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array
                && state.RootElement.TryGetProperty("origins", out var origins) && origins.ValueKind == JsonValueKind.Array ? saved.StorageState : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task SaveAsync(Account account, string storageState)
    {
        using var parsed = JsonDocument.Parse(storageState);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid browser state");
        var file = PathFor(account);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new SavedState(AuthenticationHash(account), storageState)));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
