using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Muse2Api;

public enum UpdateFeed { Release, Source }
public enum UpdateStatus { Available, Current, NoRelease, Unavailable, RateLimited }
public sealed record AvailableUpdate(string Version, string Url, UpdateFeed Feed);
public sealed record UpdateCheckResult(UpdateStatus Status, string Message, AvailableUpdate? Update = null, bool Notify = false);

/// <summary>Public version metadata only. Never downloads/runs an update, invokes Git, or uses Muse credentials.</summary>
public sealed class DesktopUpdates(HttpClient http, string cacheDirectory, Func<DateTimeOffset>? clock = null)
{
    public const string Repository = "thanhtaiinguyen/muse_cli";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    public const string ReleaseEndpoint = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string SourceEndpoint = "https://raw.githubusercontent.com/" + Repository + "/main/src/Muse2Api.WinForms/Muse2Api.WinForms.csproj";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<UpdateFeed, Cache> _memory = new();
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public sealed class Cache
    {
        public string Repository { get; set; } = DesktopUpdates.Repository;
        public DateTimeOffset CheckedAt { get; set; }
        public DateTimeOffset RetryNotBefore { get; set; }
        public string? ETag { get; set; }
        public string? DismissedVersion { get; set; }
        public AvailableUpdate? Candidate { get; set; }
        public UpdateStatus Status { get; set; } = UpdateStatus.NoRelease;
    }

    public static bool TryVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        var normalized = value?.Split('+')[0];
        if (normalized?.StartsWith('v') == true) normalized = normalized[1..];
        return normalized is not null && Regex.IsMatch(normalized, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant)
            && Version.TryParse(normalized, out version!);
    }

    public static bool IsTrustedLink(AvailableUpdate update)
    {
        if (!TryVersion(update.Version, out var version) || !Uri.TryCreate(update.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var path = uri.AbsolutePath;
        if (update.Feed == UpdateFeed.Source) return path == "/" + Repository + "/commits/main/";
        return path == "/" + Repository + "/releases/tag/v" + version || path == "/" + Repository + "/releases/tag/" + version;
    }

    public static UpdateFeed DetectFeed(string baseDirectory)
    {
        var app = Path.GetFullPath(baseDirectory);
        for (var root = new DirectoryInfo(app); root is not null; root = root.Parent)
        {
            if (!File.Exists(Path.Combine(root.FullName, "Muse2Api.sln"))
                || !File.Exists(Path.Combine(root.FullName, "src", "Muse2Api.WinForms", "Muse2Api.WinForms.csproj"))) continue;
            var relative = Path.GetRelativePath(root.FullName, app).Replace('\\', '/');
            return relative.StartsWith("src/Muse2Api.WinForms/bin/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("artifacts/bin/Muse2Api.WinForms/", StringComparison.OrdinalIgnoreCase)
                ? UpdateFeed.Source : UpdateFeed.Release;
        }
        return UpdateFeed.Release;
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, UpdateFeed feed, bool manual = false, CancellationToken cancel = default)
    {
        if (!TryVersion(currentVersion, out var current)) return new(UpdateStatus.Unavailable, "Không xác định được phiên bản đang chạy.");
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var state = Load(feed);
            var now = Now;
            if (state.RetryNotBefore > now) return new(UpdateStatus.RateLimited, "GitHub đang giới hạn kiểm tra cập nhật. Vui lòng thử lại sau.");
            if (state.CheckedAt <= now && state.CheckedAt > now - (manual ? TimeSpan.FromMinutes(1) : CheckInterval)) return Result(state, current, manual);
            using var request = new HttpRequestMessage(HttpMethod.Get, feed == UpdateFeed.Release ? ReleaseEndpoint : SourceEndpoint);
            request.Headers.UserAgent.ParseAdd("Muse-UpdateChecker/" + current);
            request.Headers.Accept.ParseAdd(feed == UpdateFeed.Release ? "application/vnd.github+json" : "application/xml");
            if (feed == UpdateFeed.Release) request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            if (EntityTagHeaderValue.TryParse(state.ETag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (request.Headers.IfNoneMatch.Count == 0)
                        throw new InvalidDataException("Unexpected 304 without a valid cache");
                    state.Status = state.Candidate is null ? UpdateStatus.NoRelease : UpdateStatus.Available;
                }
                else if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter?.Date ?? (response.Headers.RetryAfter?.Delta is { } delta ? now + delta : now.AddMinutes(15));
                    if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out var epoch)
                        && epoch >= 0 && epoch <= 253402300799) retry = DateTimeOffset.FromUnixTimeSeconds(epoch) > retry ? DateTimeOffset.FromUnixTimeSeconds(epoch) : retry;
                    state.RetryNotBefore = retry < now.AddMinutes(1) ? now.AddMinutes(1) : retry > now.AddDays(1) ? now.AddDays(1) : retry;
                    state.Status = UpdateStatus.RateLimited;
                }
                else if (response.StatusCode == HttpStatusCode.NotFound && feed == UpdateFeed.Release)
                {
                    state.Candidate = null; state.Status = UpdateStatus.NoRelease; state.ETag = null;
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    var body = await ReadBounded(response.Content, timeout.Token).ConfigureAwait(false);
                    state.Candidate = feed == UpdateFeed.Release ? ParseRelease(body) : ParseSource(body);
                    state.Status = state.Candidate is null ? UpdateStatus.NoRelease : UpdateStatus.Available;
                    state.ETag = response.Headers.ETag?.ToString();
                }
                state.CheckedAt = now;
                Save(feed, state);
                return Result(state, current, manual);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                return Failure(feed, state, now);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or XmlException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                return Failure(feed, state, now);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DismissAsync(AvailableUpdate update, CancellationToken cancel = default)
    {
        if (!IsTrustedLink(update)) return;
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try { var state = Load(update.Feed); state.DismissedVersion = update.Version; Save(update.Feed, state); }
        finally { _gate.Release(); }
    }

    private UpdateCheckResult Failure(UpdateFeed feed, Cache state, DateTimeOffset now)
    {
        state.Status = UpdateStatus.Unavailable; state.CheckedAt = now;
        Save(feed, state);
        return Result(state, new Version(0, 0, 0), false);
    }

    private static UpdateCheckResult Result(Cache state, Version current, bool manual)
    {
        if (state.Status == UpdateStatus.RateLimited) return new(UpdateStatus.RateLimited, "GitHub đang giới hạn kiểm tra cập nhật. Vui lòng thử lại sau.");
        if (state.Status == UpdateStatus.Unavailable) return new(UpdateStatus.Unavailable, "Chưa kiểm tra được cập nhật. Bạn vẫn có thể dùng Muse và thử lại khi có mạng.");
        if (state.Candidate is { } update && IsTrustedLink(update) && TryVersion(update.Version, out var version) && version > current)
            return new(UpdateStatus.Available, update.Feed == UpdateFeed.Source ? $"Có mã nguồn Muse {version} mới trên GitHub. Lưu thay đổi rồi pull và build lại."
                : $"Đã có Muse {version}. Bạn có thể xem và tải bản mới từ GitHub.", update, manual || state.DismissedVersion != update.Version);
        return state.Candidate is null ? new(UpdateStatus.NoRelease, "Chưa có bản phát hành phù hợp trên GitHub.")
            : new(UpdateStatus.Current, $"Không có phiên bản mới hơn Muse {current} trên GitHub.");
    }

    private static AvailableUpdate? ParseRelease(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString();
        if (!TryVersion(tag, out var version) || tag!.Contains('+')) return null;
        var candidate = new AvailableUpdate(version.ToString(), root.GetProperty("html_url").GetString() ?? "", UpdateFeed.Release);
        if (!IsTrustedLink(candidate)) throw new InvalidDataException("Untrusted release URL");
        var assets = root.GetProperty("assets").EnumerateArray().ToList();
        foreach (var name in new[] { "Muse-win-x64.zip", "Muse-win-x64.zip.sha256" })
        {
            var url = RepositoryUrl + "/releases/download/" + tag + "/" + name;
            if (!assets.Any(a => a.TryGetProperty("name", out var n) && n.GetString() == name
                && a.TryGetProperty("browser_download_url", out var u) && u.GetString() == url)) return null;
        }
        return candidate;
    }

    private static AvailableUpdate ParseSource(string body)
    {
        using var text = new StringReader(body);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var project = XDocument.Load(reader);
        var raw = project.Root?.Elements("PropertyGroup").Elements("Version").FirstOrDefault()?.Value.Trim();
        if (!TryVersion(raw, out var version)) throw new InvalidDataException("Invalid source version");
        return new(version.ToString(), RepositoryUrl + "/commits/main/", UpdateFeed.Source);
    }

    private static async Task<string> ReadBounded(HttpContent content, CancellationToken cancel)
    {
        const int limit = 256 * 1024;
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Update response too large");
        using var stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Update response too large");
            output.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private string CacheFile(UpdateFeed feed) => Path.Combine(cacheDirectory, "update-" + feed.ToString().ToLowerInvariant() + ".json");
    private Cache Load(UpdateFeed feed)
    {
        if (_memory.TryGetValue(feed, out var memory)) return memory;
        try
        {
            var file = CacheFile(feed);
            if (File.Exists(file) && new FileInfo(file).Length < 16000)
            {
                var state = JsonSerializer.Deserialize<Cache>(File.ReadAllText(file));
                if (state?.Repository == Repository && Enum.IsDefined(state.Status) && (state.Candidate is null || state.Candidate.Feed == feed && IsTrustedLink(state.Candidate)))
                {
                    if (state.RetryNotBefore > Now.AddDays(1)) state.RetryNotBefore = Now.AddDays(1);
                    return _memory[feed] = state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return _memory[feed] = new();
    }

    private void Save(UpdateFeed feed, Cache state)
    {
        _memory[feed] = state;
        var file = CacheFile(feed); var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
