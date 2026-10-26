using System.Text.Json.Nodes;

namespace Muse2Api;

public sealed class AppState
{
    public Settings Settings { get; }
    public IMuseDriver Driver { get; }
    public AccountPool Pool { get; }
    public Gateway Gateway { get; }
    public TaskManager Tasks { get; }
    public MediaStore Media { get; }
    public KeyStore Keys { get; }
    public RequestLog Requests { get; }

    public AppState(Settings settings)
    {
        Settings = settings;
        settings.EnsureDirs();
        settings.ResolveApiKey();
        Driver = DriverFactory.Create(settings);
        Pool = new AccountPool(settings.AccountsFile, settings.PoolStrategy,
            settings.AccountMaxConcurrency, settings.AccountCooldown, settings.PoolAcquireTimeout);
        Pool.Load();
        Gateway = new Gateway(Pool, Driver, settings.MaxFailover);
        Tasks = new TaskManager(settings.TasksFile);
        Tasks.Load();
        Media = new MediaStore(settings.MediaDir);
        Keys = new KeyStore(settings.KeysFile);
        Keys.Load();
        Requests = new RequestLog(settings.RequestsFile);
        Requests.Prune(settings.RequestLogRetentionDays);
    }
}

/// <summary>Small helpers for the flexible OpenAI JSON shapes (content can be a
/// string or a list of parts, unknown fields are ignored).</summary>
public static class JsonHelp
{
    public static string? Str(JsonNode? n) => n is JsonValue v ? v.TryGetValue<string>(out var s) ? s : null : null;
    public static string StrOr(JsonNode? n, string fallback) => Str(n) ?? fallback;
    public static bool Bool(JsonNode? n, bool fallback = false)
        => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    public static int Int(JsonNode? n, int fallback = 0)
        => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
    public static int ApproxTokens(string text) => Math.Max(1, text.Length / 4);

    public static async Task<JsonObject> ReadBody(HttpRequest request)
    {
        if (request.ContentLength is null or 0) return new JsonObject();
        try { return await request.ReadFromJsonAsync<JsonObject>() ?? new JsonObject(); }
        catch { throw MuseException.Invalid("request body must be valid JSON"); }
    }

    public static string BaseUrl(HttpRequest request, Settings settings)
    {
        if (!string.IsNullOrEmpty(settings.PublicBase)) return settings.PublicBase.TrimEnd('/');
        return $"{request.Scheme}://{request.Host}".TrimEnd('/');
    }

    public static async Task<List<InputImage>> LoadImageRefs(IEnumerable<string> refs)
    {
        var loaded = new List<InputImage>();
        foreach (var r in refs) loaded.Add(await MediaHelp.LoadImageRef(r));
        return loaded;
    }
}

public static class MediaHelp
{
    /// <summary>True for loopback, private, link-local, multicast, reserved
    /// and unspecified IPs — the same set Python's ipaddress checks reject
    /// (is_private / is_loopback / is_link_local / is_multicast /
    /// is_reserved / is_unspecified), with IPv4-mapped IPv6 unwrapped first.</summary>
    public static bool IsNonPublicIp(System.Net.IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (b[0] == 0) return true; // unspecified 0.0.0.0/8
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true; // link-local
            if (b[0] >= 224 && b[0] <= 239) return true; // multicast
            if (b[0] >= 240) return true; // reserved
            return false;
        }
        // IPv6: ::1 loopback handled above; :: unspecified, fe80::/10
        // link-local, fc00::/7 unique-local, ff00::/8 multicast, 100::/64 discard.
        if (b.All(x => x == 0)) return true;
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;
        if ((b[0] & 0xfe) == 0xfc) return true;
        if (b[0] == 0xff) return true;
        if (b[0] == 0x01 && b.Skip(1).Take(14).All(x => x == 0)) return true;
        return false;
    }

    public static async Task AssertPublicHostAsync(string host)
    {
        System.Net.IPAddress[] addrs;
        try { addrs = await System.Net.Dns.GetHostAddressesAsync(host); }
        catch { throw MuseException.Invalid("image_url host does not resolve"); }
        if (addrs.Length == 0 || addrs.Any(IsNonPublicIp))
            throw MuseException.Invalid("image_url must resolve to a public IP address");
    }

    public static async Task<InputImage> LoadImageRef(string reference)
    {
        var r = reference.Trim();
        if (r.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = r.IndexOf(',');
            var header = comma >= 0 ? r[..comma] : r;
            var mime = header[5..].Split(';')[0];
            if (string.IsNullOrEmpty(mime)) mime = "image/png";
            var payload = comma >= 0 ? r[(comma + 1)..] : "";
            try { return new InputImage(Convert.FromBase64String(payload), mime); }
            catch { throw MuseException.Invalid("invalid base64 image data"); }
        }
        if (r.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Port of Python _fetch_public_image: resolve the host first,
            // then fetch with manual redirects, re-checking every redirect
            // target the same way, with a 20 MiB streaming cap.
            var uri = new Uri(r);
            await AssertPublicHostAsync(uri.Host);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            for (var hop = 0; hop <= 5; hop++)
            {
                using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if ((int)resp.StatusCode is >= 301 and <= 308
                    && resp.StatusCode is not System.Net.HttpStatusCode.NotModified)
                {
                    var location = resp.Headers.Location;
                    if (location is null)
                        throw MuseException.Invalid("image_url redirect missing location");
                    uri = new Uri(uri, location);
                    await AssertPublicHostAsync(uri.Host);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    throw MuseException.Invalid($"image_url returned HTTP {(int)resp.StatusCode}");
                var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var ms = new MemoryStream();
                var buf = new byte[65536];
                int read;
                while ((read = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, read);
                    if (ms.Length > 20 * 1024 * 1024)
                        throw MuseException.Invalid("image_url response exceeds 20 MiB");
                }
                return new InputImage(ms.ToArray(), mime);
            }
            throw MuseException.Invalid("image_url exceeded redirect limit");
        }
        try { return new InputImage(Convert.FromBase64String(r), "image/png"); }
        catch { throw MuseException.Invalid("image must be a data URL, http(s) URL or base64 string"); }
    }
}
