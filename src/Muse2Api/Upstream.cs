using System.Text.Json.Nodes;

namespace Muse2Api;

/// <summary>Facts about the muse.ai web app, ported from Python <c>upstream/muse.py</c>.
/// Everything here was observed from the public web client and may change.</summary>
public static class Upstream
{
    public const string Site = "https://muse.ai";
    public const string NewThreadUrl = Site + "/thread/new";
    public const string SessionApi = Site + "/api/session";
    public const string VmWakeApi = Site + "/api/hatch/vm/wake";
    public static readonly string[] SessionCookies = { "hatch_sess", "hatch_native_auth_device" };
    public static readonly string[] OptionalSessionCookies = { "hatch_gw", "hatch_vml" };
    public const string UserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/140.0.0.0 Safari/537.36";

    public static string CookieHeader(Dictionary<string, string> cookies)
        => string.Join("; ", cookies.Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={kv.Value}"));

    public static List<string> MissingSessionCookies(Dictionary<string, string> cookies)
        => SessionCookies.Where(c => string.IsNullOrEmpty(cookies.GetValueOrDefault(c))).ToList();

    public static async Task<SessionInfo> RenewSession(Dictionary<string, string> cookies,
        double confirmSeconds = 20, double pollSeconds = 2, CancellationToken ct = default)
    {
        var jar = new Dictionary<string, string>(cookies);
        var expires = new Dictionary<string, long>();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Add("Origin", Site);
        client.DefaultRequestHeaders.Add("Referer", NewThreadUrl);
        client.DefaultRequestHeaders.Add("Accept", "application/json");

        async Task<JsonObject?> GetSession()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, SessionApi);
            req.Headers.Add("Cookie", CookieHeader(jar));
            using var resp = await client.SendAsync(req, ct);
            MergeCookies(resp, jar, expires);
            if ((int)resp.StatusCode is 401 or 403)
                throw UpstreamException.Auth($"session rejected by upstream (HTTP {(int)resp.StatusCode})");
            if (!resp.IsSuccessStatusCode)
                throw new UpstreamException($"session endpoint returned HTTP {(int)resp.StatusCode}");
            var text = await resp.Content.ReadAsStringAsync(ct);
            try { return JsonNode.Parse(text) as JsonObject; } catch { return null; }
        }

        var body = await GetSession() ?? new JsonObject();
        string? Str(string k) => body[k]?.GetValue<string>() is { } s ? s[..Math.Min(128, s.Length)] : null;
        var meta = new Dictionary<string, string?>
        {
            ["session_status"] = Str("status"),
            ["vm_id"] = Str("vm_id"),
            ["vm_state"] = Str("vm_state"),
        };
        if (meta["vm_id"] is not null && meta["vm_state"] != "DISABLED")
        {
            try
            {
                using var wake = new HttpRequestMessage(HttpMethod.Post, VmWakeApi);
                wake.Headers.Add("Cookie", CookieHeader(jar));
                wake.Content = JsonContent.Create(new
                {
                    vm_id = meta["vm_id"],
                    retry_count = 0,
                    connect_attempt_id = Guid.NewGuid().ToString(),
                });
                using var w = await client.SendAsync(wake, ct);
                MergeCookies(w, jar, expires);
                meta["vm_wake_http"] = ((int)w.StatusCode).ToString();
            }
            catch (HttpRequestException ex) { meta["vm_wake_http"] = ex.GetType().Name; }
            var after = await ConfirmRunning(client, jar, expires, confirmSeconds, pollSeconds, ct);
            meta["vm_state_after"] = after;
            meta["vm_wake_ok"] = after is null ? null : (after == "RUNNING" ? "true" : "false");
        }
        return new SessionInfo(meta["session_status"] == "assigned", jar, expires, meta);
    }

    private static async Task<string?> ConfirmRunning(HttpClient client,
        Dictionary<string, string> jar, Dictionary<string, long> expires,
        double confirmSeconds, double pollSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(confirmSeconds);
        string? state = null;
        while (true)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, SessionApi);
                req.Headers.Add("Cookie", CookieHeader(jar));
                using var resp = await client.SendAsync(req, ct);
                MergeCookies(resp, jar, expires);
                if (resp.IsSuccessStatusCode)
                {
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    var vm = (JsonNode.Parse(text) as JsonObject)?["vm_state"]?.GetValue<string>();
                    if (vm is not null)
                    {
                        state = vm[..Math.Min(128, vm.Length)];
                        if (state == "RUNNING") return state;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* a failed poll only costs that poll */ }
            if (DateTime.UtcNow.AddSeconds(pollSeconds) > deadline) return state;
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct);
        }
    }

    private static void MergeCookies(HttpResponseMessage resp,
        Dictionary<string, string> jar, Dictionary<string, long> expires)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var header in values)
        {
            var first = header.Split(';', 2)[0];
            var eq = first.IndexOf('=');
            if (eq <= 0) continue;
            var name = first[..eq].Trim();
            var value = first[(eq + 1)..].Trim();
            if (value.Length > 0) jar[name] = value;
            foreach (var part in header.Split(';').Skip(1))
            {
                var p = part.Trim();
                if (p.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase)
                    && DateTimeOffset.TryParse(p[8..], out var dt))
                    expires[name] = dt.ToUnixTimeSeconds();
            }
        }
    }
}
