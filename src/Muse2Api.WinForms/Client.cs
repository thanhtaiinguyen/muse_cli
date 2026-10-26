using System.Text;
using System.Text.Json.Nodes;

namespace Muse2Api.WinForms;

/// <summary>Stdlib-only HTTP client for the muse2api gateway (.NET or Python).
/// No reference to the gateway project: this UI can control any local gateway.</summary>
public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public string BaseUrl(string host, int port) => $"http://{host}:{port}";

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
                if (m?["id"]?.GetValue<string>() is { } id) list.Add(id);
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
            ["label"] = label, ["cookies"] = cookies,
        });

    /// <summary>Same endpoint, but cookies as a structured list
    /// [{name, value, expirationDate}] so expiry is preserved.
    /// Used by the WebView2 login flow; never log the values.</summary>
    public Task<JsonNode?> AddAccountWithCookies(string url, string adminKey, string label, JsonArray cookies)
        => Request(url + "/admin/accounts", adminKey, new JsonObject
        {
            ["label"] = label, ["cookies"] = cookies,
        });

    /// <summary>Accounts the gateway knows, masked by the gateway itself.
    /// Returns (id, label, status) triples in gateway order.</summary>
    public async Task<List<(string Id, string Label, string Status)>> ListAccounts(string url, string adminKey)
    {
        var body = await Request(url + "/admin/accounts", adminKey);
        var list = new List<(string, string, string)>();
        if (body?["data"] is JsonArray arr)
            foreach (var a in arr)
                if (a is JsonObject o)
                    list.Add((
                        o["id"]?.GetValue<string>() ?? "",
                        o["label"]?.GetValue<string>() ?? "",
                        o["status"]?.GetValue<string>() ?? ""));
        return list;
    }

    public Task<JsonNode?> GetQuota(string url, string adminKey, string accountId)
        => Request(url + $"/admin/accounts/{accountId}/quota", adminKey);

    public Task<JsonNode?> RenewAccount(string url, string adminKey, string accountId)
        => Request(url + $"/admin/accounts/{accountId}/renew", adminKey, new JsonObject());

    public void Dispose() => _http.Dispose();
}
