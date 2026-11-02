using System.Text;
using System.Text.Json.Nodes;

namespace Muse2Api.WinForms;

/// <summary>Stdlib-only HTTP client for the muse2api gateway (.NET or Python).
/// Deliberately uses no gateway types: this client can control any local gateway.</summary>
public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(10) };

    public string BaseUrl(string host, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("Port phải nằm trong khoảng 1–65535.");
        if (host is "0.0.0.0" or "*" or "+" or "::" or "[::]") host = "127.0.0.1";
        return new UriBuilder("http", host, port).Uri.GetLeftPart(UriPartial.Authority);
    }

    private async Task<JsonNode?> Request(string url, string key, JsonObject? payload = null,
        HttpMethod? method = null)
    {
        using var req = new HttpRequestMessage(method ?? (payload is null ? HttpMethod.Get : HttpMethod.Post), url);
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        if (payload is not null)
            req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}: {text[..Math.Min(500, text.Length)]}");
        try { return JsonNode.Parse(text); }
        catch { return JsonValue.Create(text); }
    }

    public Task<JsonNode?> Health(string url) => Request(url + "/healthz", "");
    public Task<JsonNode?> Ready(string url, string key) => Request(url + "/readyz", key);

    public async Task<List<string>> Models(string url, string key)
    {
        var body = await Request(url + "/v1/models", key);
        var list = new List<string>();
        if (body?["data"] is JsonArray arr)
            foreach (var m in arr)
                if (m?["kind"]?.GetValue<string>() == "chat" && m?["id"]?.GetValue<string>() is { } id) list.Add(id);
        return list;
    }

    public async Task<string> Chat(string url, string key, string model, string prompt)
    {
        var body = await Request(url + "/v1/chat/completions", key, new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["stream"] = false,
        });
        return body?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? body?.ToJsonString() ?? "";
    }

    public Task<JsonNode?> Admin(string url, string adminKey, string path)
        => Request(url + path, adminKey);

    public Task<JsonNode?> AddAccount(string url, string adminKey, string label, string cookies)
        => Request(url + "/admin/accounts", adminKey, new JsonObject
        {
            ["label"] = label,
            ["cookies"] = cookies,
        });

    /// <summary>Same endpoint, but cookies as a structured list
    /// [{name, value, expirationDate}] so expiry is preserved.
    /// Used by the WebView2 login flow; never log the values.</summary>
    public Task<JsonNode?> AddAccountWithCookies(string url, string adminKey, string label, JsonArray cookies, string? id = null)
    {
        var payload = new JsonObject
        {
            ["label"] = label,
            ["cookies"] = cookies,
            ["reuse_session"] = true,
        };
        if (!string.IsNullOrWhiteSpace(id)) payload["id"] = id;
        return Request(url + "/admin/accounts", adminKey, payload);
    }

    /// <summary>Accounts the gateway knows, masked by the gateway itself.
    /// Returns (id, label, status, enabled) in gateway order. Every enabled
    /// account serves traffic at the same time (the pool schedules them).</summary>
    public async Task<List<(string Id, string Label, string Status, bool Enabled)>> ListAccounts(string url, string adminKey)
    {
        var body = await Request(url + "/admin/accounts", adminKey);
        var list = new List<(string, string, string, bool)>();
        if (body?["data"] is JsonArray arr)
            foreach (var a in arr)
                if (a is JsonObject o)
                    list.Add((
                        o["id"]?.GetValue<string>() ?? "",
                        o["label"]?.GetValue<string>() ?? "",
                        o["status"]?.GetValue<string>() ?? "",
                        o["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) ? on : true));
        return list;
    }

    public Task<JsonNode?> SetAccountEnabled(string url, string adminKey, string accountId, bool enabled)
        => Request(url + $"/admin/accounts/{accountId}", adminKey, new JsonObject { ["enabled"] = enabled }, HttpMethod.Patch);

    public Task<JsonNode?> DeleteAccount(string url, string adminKey, string accountId)
        => Request(url + $"/admin/accounts/{accountId}", adminKey, null, HttpMethod.Delete);

    /// <summary>API keys created on the gateway (not the built-in desktop key).
    /// Plaintext is only ever returned once, by CreateKey.</summary>
    public async Task<List<(string Id, string Name, string Prefix, bool Revoked)>> ListKeys(string url, string adminKey)
    {
        var body = await Request(url + "/admin/keys", adminKey);
        var list = new List<(string, string, string, bool)>();
        if (body?["data"] is JsonArray arr)
            foreach (var k in arr)
                if (k is JsonObject o)
                    list.Add((
                        o["id"]?.GetValue<string>() ?? "",
                        o["name"]?.GetValue<string>() ?? "",
                        o["prefix"]?.GetValue<string>() ?? "",
                        o["revoked"] is JsonValue revoked && revoked.TryGetValue<bool>(out var off) && off));
        return list;
    }

    public async Task<string> CreateKey(string url, string adminKey, string name)
    {
        var body = await Request(url + "/admin/keys", adminKey, new JsonObject { ["name"] = name });
        return body?["api_key"]?.GetValue<string>()
            ?? throw new Exception("Gateway không trả về API key mới.");
    }

    public Task<JsonNode?> RevokeKey(string url, string adminKey, string keyId, bool revoked = true)
        => Request(url + $"/admin/keys/{keyId}", adminKey, new JsonObject { ["revoked"] = revoked }, HttpMethod.Patch);

    public Task<JsonNode?> DeleteKey(string url, string adminKey, string keyId)
        => Request(url + $"/admin/keys/{keyId}", adminKey, null, HttpMethod.Delete);

    public Task<JsonNode?> GetQuota(string url, string adminKey, string accountId)
        => Request(url + $"/admin/accounts/{accountId}/quota", adminKey);

    public Task<JsonNode?> RenewAccount(string url, string adminKey, string accountId)
        => Request(url + $"/admin/accounts/{accountId}/renew", adminKey, new JsonObject());

    public void Dispose() => _http.Dispose();
}
