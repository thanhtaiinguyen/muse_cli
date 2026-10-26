using System.Text.Json;
using Microsoft.Playwright;

namespace Muse2Api;

/// <summary>Browser driver, ported from Python <c>drivers/browser/</c> to
/// Microsoft.Playwright.
/// <para>
/// Design (same as Python): one Chromium process; each account gets its own
/// browser context (isolated cookie jar), so switching accounts never requires
/// clearing cookies. One page per tab, reused across requests. Per-account
/// serialisation is guaranteed by the account pool
/// (<c>MUSE2API_ACCOUNT_MAX_CONCURRENCY=1</c> by default).
/// </para>
/// <para>
/// <b>Setup on Windows:</b> after <c>dotnet restore</c>, install the browser once:
/// <c>dotnet tool install --global Microsoft.Playwright.CLI</c> then
/// <c>playwright install chromium</c>. Or set <c>MUSE2API_CHROMIUM_PATH</c> to an
/// existing Chrome/Chromium executable, which Playwright will use instead.
/// </para>
/// <para>
/// <b>Not verified live:</b> the Python driver itself was never verified against
/// a real muse.ai account either, and this port has only been compiled and
/// exercised against its own error paths (missing cookies, missing browser).
/// Run it with a real account and expect the first DOM mismatches to land in
/// <c>Dom.cs</c>, exactly as in the Python project.
/// </para>
/// </summary>
public sealed class BrowserDriver : IMuseDriver
{
    public string Name => "browser";
    public bool RequiresAccount => true;
    public DriverCapabilities Capabilities { get; } = new(
        Chat: true, ChatImages: true, Image: true, ImageEdit: true, Video: true,
        RenewSession: true, Quota: true);

    private const double PollInterval = 0.15;
    private const int StablePollsDone = 4;
    private const int StablePollsForce = 12;
    private const int HotBubbleLimit = 16;
    private const double QuotaCacheSeconds = 1800;
    private const double QuotaClickTimeout = 8;
    private const double QuotaReadTimeout = 12;
    private const double EscapePause = 0.1;

    private readonly Settings _settings;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly Dictionary<string, IBrowserContext> _contexts = new();
    private readonly Dictionary<string, List<BrowserTab>> _tabs = new();
    private readonly Dictionary<string, int> _opening = new();
    private readonly Dictionary<string, Dictionary<string, object?>> _quotaCache = new();
    private readonly Dictionary<string, SemaphoreSlim> _quotaLocks = new();
    private readonly object _lock = new();

    public BrowserDriver(Settings settings) => _settings = settings;

    // ---------- lifecycle ----------
    public async Task StartupAsync()
    {
        if (_browser is not null) return;
        _playwright = await Playwright.CreateAsync();
        var launch = new BrowserTypeLaunchOptions
        {
            Headless = _settings.Headless,
            Args = new[]
            {
                "--no-first-run", "--no-default-browser-check", "--disable-dev-shm-usage",
                "--disable-background-networking", "--autoplay-policy=no-user-gesture-required",
                "--window-size=1440,2200",
            },
        };
        if (!string.IsNullOrEmpty(_settings.ChromiumPath)) launch.ExecutablePath = _settings.ChromiumPath;
        if (!string.IsNullOrEmpty(_settings.BrowserProxy))
            launch.Proxy = new Proxy { Server = _settings.BrowserProxy };
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(launch);
        }
        catch (Exception ex)
        {
            throw new MuseException(
                "Playwright Chromium is not installed. Run: dotnet tool install --global "
                + "Microsoft.Playwright.CLI && playwright install chromium "
                + $" (or set MUSE2API_CHROMIUM_PATH). Original error: {ex.Message}",
                500, "server_error", "browser_not_installed");
        }
    }

    public async Task ShutdownAsync()
    {
        List<BrowserTab> tabs;
        lock (_lock) tabs = _tabs.Values.SelectMany(t => t).ToList();
        foreach (var tab in tabs) await CloseTab(tab);
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        _browser = null;
        _playwright = null;
    }

    public Task<Dictionary<string, object?>> Health()
    {
        List<BrowserTab> tabs;
        lock (_lock) tabs = _tabs.Values.SelectMany(t => t).ToList();
        return Task.FromResult(new Dictionary<string, object?>
        {
            ["driver"] = Name, ["ok"] = _browser is not null,
            ["tabs"] = tabs.Count, ["busy_tabs"] = tabs.Count(t => t.Busy),
        });
    }

    // ---------- tabs ----------
    private sealed class BrowserTab
    {
        public required string AccountId { get; init; }
        public required IPage Page { get; init; }
        public string? Hint { get; set; }
        public List<(string Role, string Text)> Turns { get; set; } = new();
        public int ImageCount { get; set; }
        public string ThreadUrl { get; set; } = "";
        public string QuotaNote { get; set; } = "";
        public bool Busy { get; set; }
        public bool Stale { get; set; }
        public bool Closed { get; set; }
        public bool HasState => Turns.Count > 0 || ThreadUrl.Length > 0;
    }

    private async Task<BrowserTab> Checkout(Account account, Func<BrowserTab, bool>? prefer)
    {
        var limit = Math.Max(1, _settings.AccountMaxConcurrency);
        var deadline = DateTime.UtcNow.AddSeconds(_settings.PoolAcquireTimeout);
        while (true)
        {
            BrowserTab? match = null;
            bool canOpen;
            lock (_lock)
            {
                var tabs = _tabs.GetValueOrDefault(account.Id) ?? new List<BrowserTab>();
                _tabs[account.Id] = tabs;
                tabs.RemoveAll(t => t.Closed);
                var idle = tabs.Where(t => !t.Busy).ToList();
                canOpen = tabs.Count + _opening.GetValueOrDefault(account.Id) < limit;
                match = idle.FirstOrDefault(t => prefer is null || prefer(t));
                if (match is null && idle.Count > 0 && !canOpen)
                    match = idle.FirstOrDefault(t => !t.HasState) ?? idle[0];
                if (match is not null) match.Busy = true;
                else if (canOpen) _opening[account.Id] = _opening.GetValueOrDefault(account.Id) + 1;
            }
            if (match is not null) return match;
            if (canOpen)
            {
                try
                {
                    var tab = await OpenTab(account);
                    lock (_lock)
                    {
                        tab.Busy = true;
                        _tabs[account.Id].Add(tab);
                    }
                    return tab;
                }
                finally
                {
                    lock (_lock) _opening[account.Id] = _opening.GetValueOrDefault(account.Id) - 1;
                }
            }
            if (DateTime.UtcNow > deadline)
                throw MuseException.NoAccount("all tabs of the account are busy, try again later");
            await Task.Delay(100);
        }
    }

    private void Checkin(BrowserTab tab)
    {
        tab.Busy = false;
        if (tab.Stale) _ = CloseTab(tab);
    }

    private async Task<IBrowserContext> GetContext(Account account)
    {
        if (_browser is null) await StartupAsync();
        lock (_lock)
        {
            if (_contexts.TryGetValue(account.Id, out var existing)) return existing;
        }
        if (_browser is null) throw new MuseException("browser is not started", 500);
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 2200 },
        });
        await SetCookies(context, account);
        lock (_lock) _contexts[account.Id] = context;
        return context;
    }

    private async Task<BrowserTab> OpenTab(Account account)
    {
        if (Upstream.MissingSessionCookies(account.Cookies) is { Count: > 0 } missing)
            throw UpstreamException.Auth(
                $"account is missing session cookies: {string.Join(", ", missing)}");
        var context = await GetContext(account);
        var page = await context.NewPageAsync();
        return new BrowserTab { AccountId = account.Id, Page = page };
    }

    private async Task CloseTab(BrowserTab tab)
    {
        if (tab.Closed) return;
        tab.Closed = true;
        tab.Busy = false;
        bool last;
        IBrowserContext? context = null;
        lock (_lock)
        {
            var tabs = _tabs.GetValueOrDefault(tab.AccountId) ?? new List<BrowserTab>();
            tabs.Remove(tab);
            last = tabs.Count == 0 && _opening.GetValueOrDefault(tab.AccountId) == 0;
            if (last && _contexts.TryGetValue(tab.AccountId, out var ctx))
            {
                _contexts.Remove(tab.AccountId);
                context = ctx;
            }
        }
        try { await tab.Page.CloseAsync(); } catch { }
        if (context is not null) try { await context.CloseAsync(); } catch { }
    }

    private static async Task SetCookies(IBrowserContext context, Account account)
    {
        var floor = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        var cookies = new List<Cookie>();
        foreach (var (name, value) in account.Cookies)
        {
            if (string.IsNullOrEmpty(value)) continue;
            var exp = account.CookieExpires.GetValueOrDefault(name);
            cookies.Add(new Cookie
            {
                Name = name, Value = value, Domain = ".muse.ai", Path = "/",
                Secure = true,
                Expires = (float)(exp > floor ? exp : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7 * 86400),
            });
        }
        if (cookies.Count > 0) await context.AddCookiesAsync(cookies);
    }

    private static void Forget(BrowserTab tab)
    {
        tab.Hint = null; tab.Turns = new(); tab.ImageCount = 0; tab.ThreadUrl = "";
    }

    private static void LoadHot(BrowserTab tab, Account account)
    {
        if (tab.Turns.Count > 0 || tab.ThreadUrl.Length > 0) return;
        if (account.Meta.GetValueOrDefault("hot_page") is not System.Text.Json.Nodes.JsonObject hot) return;
        var turns = new List<(string, string)>();
        if (hot["turns"] is System.Text.Json.Nodes.JsonArray arr)
            foreach (var item in arr)
                if (item is System.Text.Json.Nodes.JsonArray pair && pair.Count == 2)
                    turns.Add((pair[0]?.GetValue<string>() ?? "", pair[1]?.GetValue<string>() ?? ""));
        tab.Turns = turns;
        tab.Hint = hot["hint"]?.GetValue<string>();
        tab.ThreadUrl = hot["url"]?.GetValue<string>() ?? "";
        tab.ImageCount = hot["image_count"] is System.Text.Json.Nodes.JsonValue v
            && v.TryGetValue<int>(out var n) ? n : 0;
    }

    private static void SaveHot(BrowserTab tab, Account account, ChatRequest req, string url)
    {
        if (url.Length > 0) tab.ThreadUrl = url;
        tab.Hint = req.ConversationHint;
        tab.Turns = new List<(string, string)>(req.Turns);
        tab.ImageCount = req.Images.Count;
        account.Meta["hot_page"] = new System.Text.Json.Nodes.JsonObject
        {
            ["hint"] = req.ConversationHint ?? "",
            ["turns"] = new System.Text.Json.Nodes.JsonArray(
                req.Turns.Select(t => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray(t.Role, t.Text)).ToArray()),
            ["url"] = tab.ThreadUrl,
            ["image_count"] = tab.ImageCount,
        };
    }

    // ---------- page helpers ----------
    private static async Task<System.Text.Json.Nodes.JsonNode?> Eval(IPage page, string script)
    {
        try
        {
            var el = await page.EvaluateAsync<System.Text.Json.JsonElement>(script);
            return el.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? null : System.Text.Json.Nodes.JsonNode.Parse(el.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(System.Text.Json.Nodes.JsonNode? node, string key)
        => node?[key]?.GetValue<string>();
    private static bool BoolNode(System.Text.Json.Nodes.JsonNode? node, string key)
        => node?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static int IntNode(System.Text.Json.Nodes.JsonNode? node, string key)
        => node?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    private static bool IsThreadUrl(string url)
    {
        var path = new Uri(url).AbsolutePath.TrimEnd('/');
        return path.StartsWith("/thread/", StringComparison.Ordinal) && path != "/thread/new";
    }

    private async Task<System.Text.Json.Nodes.JsonNode?> PageState(IPage page)
        => await Eval(page, Dom.PageState);

    private async Task<System.Text.Json.Nodes.JsonNode?> ChatState(BrowserTab tab)
    {
        var state = await Eval(tab.Page, Dom.ChatState) ?? new System.Text.Json.Nodes.JsonObject();
        var tail = Str(state, "tail") ?? "";
        var low = tail.ToLowerInvariant();
        foreach (var hint in Dom.QuotaHints)
        {
            if (!low.Contains(hint)) continue;
            var i = low.IndexOf(hint, StringComparison.Ordinal);
            var snippet = string.Join(" ", tail[Math.Max(0, i - 120)..Math.Min(tail.Length, i + 120)].Split());
            var reply = Str(state, "lastText") ?? "";
            if (!BoolNode(state, "generating") && reply.ToLowerInvariant().Contains(hint))
                throw UpstreamException.Quota(
                    $"muse.ai reports the account is out of quota: …{snippet}…");
            if (snippet != tab.QuotaNote)
            {
                tab.QuotaNote = snippet;
                Console.WriteLine($"ignoring quota hint '{hint}' outside the reply: {snippet}");
            }
        }
        return state;
    }

    private async Task NewThread(BrowserTab tab)
    {
        Forget(tab);
        await tab.Page.GotoAsync(Upstream.NewThreadUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var deadline = DateTime.UtcNow.AddSeconds(_settings.PageReadyTimeout);
        System.Text.Json.Nodes.JsonNode? state = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            state = await PageState(tab.Page);
            if (BoolNode(state, "ready")) return;
        }
        var head = (Str(state, "head") ?? "").ToLowerInvariant();
        if (Dom.LoginHints.Any(h => head.Contains(h)))
            throw UpstreamException.Auth("muse.ai redirected to login; cookies are no longer valid");
        throw UpstreamException.Timeout("muse.ai page did not become ready");
    }

    private async Task<bool> OnThread(BrowserTab tab)
    {
        var href = tab.Page.Url;
        if (!IsThreadUrl(href)) return false;
        if (tab.ThreadUrl.Length > 0
            && new Uri(href).AbsolutePath.TrimEnd('/') != new Uri(tab.ThreadUrl).AbsolutePath.TrimEnd('/'))
            return false;
        var page = await PageState(tab.Page);
        if (!BoolNode(page, "hasInput")) return false;
        if ((Str(page, "head") ?? "").Contains("Connecting...")) return false;
        var state = await ChatState(tab);
        if (BoolNode(state, "generating")) return false;
        if (Dom.StallHints.Any(h => (Str(state, "tail") ?? "").Contains(h))) return false;
        return IntNode(state, "agentCount") < HotBubbleLimit;
    }

    private async Task<bool> LiveContinuation(BrowserTab tab)
    {
        var lastUser = tab.Turns.Where(t => t.Role == "user").Select(t => t.Text).LastOrDefault() ?? "";
        if (lastUser.Length == 0) return false;
        var page = await PageState(tab.Page);
        if (!BoolNode(page, "hasInput")) return false;
        var state = await ChatState(tab);
        var present = await Eval(tab.Page,
            $"!!(document.body && document.body.innerText.includes({Dom.Q(lastUser[..Math.Min(80, lastUser.Length)])}))");
        if (BoolNode(state, "generating")) return false;
        if (Dom.StallHints.Any(h => (Str(state, "tail") ?? "").Contains(h))) return false;
        if (IntNode(state, "agentCount") >= HotBubbleLimit) return false;
        return present is System.Text.Json.Nodes.JsonValue pv && pv.TryGetValue<bool>(out var b) && b;
    }

    private async Task OpenSaved(BrowserTab tab)
    {
        await tab.Page.GotoAsync(tab.ThreadUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var deadline = DateTime.UtcNow.AddSeconds(_settings.PageReadyTimeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            if (await OnThread(tab)) return;
        }
        throw UpstreamException.Timeout("saved conversation did not become ready");
    }

    private async Task Attach(BrowserTab tab, List<InputImage> images)
    {
        for (var idx = 0; idx < images.Count; idx++)
        {
            var img = images[idx];
            var ext = img.Mime.Split('/').Last().Replace("jpeg", "jpg");
            var res = await Eval(tab.Page, Dom.AttachFile(
                Convert.ToBase64String(img.Data), img.Mime, $"input_{idx}.{ext}"));
            if (!BoolNode(res, "ok"))
                throw new UpstreamException($"failed to attach image: {Str(res, "err")}");
        }
        if (images.Count > 0) await Task.Delay(1000);
    }

    private async Task Send(BrowserTab tab, string prompt)
    {
        var res = await Eval(tab.Page, Dom.FillInput(prompt));
        if (!BoolNode(res, "ok"))
            throw new UpstreamException($"cannot fill chat input: {Str(res, "err")}");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await Eval(tab.Page, Dom.ClickSend) is System.Text.Json.Nodes.JsonValue v
                && v.TryGetValue<string>(out var s) && s == "clicked") return;
            await Task.Delay(100);
        }
        await tab.Page.Keyboard.PressAsync("Enter");
        await Task.Delay(500);
        var empty = await Eval(tab.Page, Dom.InputEmpty);
        if (empty is not System.Text.Json.Nodes.JsonValue ev || !ev.TryGetValue<bool>(out var isEmpty) || !isEmpty)
            throw new UpstreamException("send button not found and Enter did not submit");
    }

    private async Task DenyApprovals(BrowserTab tab)
    {
        var denied = await Eval(tab.Page, Dom.DenyApprovals);
        if (denied is System.Text.Json.Nodes.JsonArray arr)
            foreach (var item in arr)
                Console.WriteLine($"denied muse.ai {item?.GetValue<string>()?.ToLowerInvariant()}");
    }

    private async Task<(BrowserTab Tab, System.Text.Json.Nodes.JsonNode? Base)> BeginChat(
        Account account, ChatRequest req)
    {
        var hint = req.ConversationHint ?? "";
        var tab = await Checkout(account, t => t.HasState && (t.Hint ?? "") == hint);
        var prompt = req.Prompt;
        var images = req.Images;
        try
        {
            List<BrowserTab> others;
            lock (_lock) others = _tabs.GetValueOrDefault(account.Id, new()).Where(t => t != tab && t.HasState).ToList();
            if (others.Count == 0) LoadHot(tab, account);
            var sameOwner = (tab.Hint ?? "") == hint;
            var follow = tab.Turns.Count > 0 && sameOwner
                ? Prompt.FollowupText(tab.Turns, req.Turns) : null;
            if (follow is not null && req.Images.Count >= tab.ImageCount
                && (tab.ThreadUrl.Length > 0 || await LiveContinuation(tab)))
            {
                if (await OnThread(tab) || await LiveContinuation(tab)) { }
                else if (tab.ThreadUrl.Length > 0) await OpenSaved(tab);
                prompt = follow;
                images = req.Images.Skip(tab.ImageCount).ToList();
            }
            else
            {
                if (account.Meta.GetValueOrDefault("hot_page") is System.Text.Json.Nodes.JsonObject saved
                    && (saved["hint"]?.GetValue<string>() ?? "") == hint)
                    account.Meta.Remove("hot_page");
                await NewThread(tab);
            }
            var baseState = await ChatState(tab);
            await Attach(tab, images);
            await Send(tab, prompt);
            return (tab, baseState);
        }
        catch (Exception)
        {
            Forget(tab);
            Checkin(tab);
            throw;
        }
    }

    // ---------- chat ----------
    public IAsyncEnumerable<string> ChatStream(Account account, ChatRequest req)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            BrowserTab? tab = null;
            System.Text.Json.Nodes.JsonNode? baseState = null;
            try
            {
                (tab, baseState) = await BeginChat(account, req);
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); return; }
            var baseCount = IntNode(baseState, "agentCount");
            var baseText = Str(baseState, "lastText") ?? "";
            var started = DateTime.UtcNow;
            var emitted = "";
            string? last = null;
            var stable = 0;
            var gotFirst = false;
            var finished = false;
            try
            {
                while (true)
                {
                    if (req.Cancel.IsCancellationRequested) { channel.Writer.Complete(); return; }
                    var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                    if (elapsed > _settings.ChatTimeout)
                        throw UpstreamException.Timeout("assistant reply timed out");
                    if (!gotFirst && elapsed > _settings.FirstTokenTimeout)
                        throw UpstreamException.Timeout("no first token from assistant");
                    await Task.Delay(TimeSpan.FromSeconds(PollInterval));
                    var st = await ChatState(tab);
                    if (BoolNode(st, "approval")) { await DenyApprovals(tab); continue; }
                    var text = Str(st, "lastText") ?? "";
                    var isNew = IntNode(st, "agentCount") > baseCount || (text.Length > 0 && text != baseText);
                    if (!isNew || text.Length == 0)
                    {
                        if (elapsed > 15 && Dom.StallHints.Any(h => (Str(st, "tail") ?? "").Contains(h)))
                            throw UpstreamException.Timeout("upstream workspace is stuck connecting");
                        continue;
                    }
                    gotFirst = true;
                    if (text != last)
                    {
                        var delta = text.StartsWith(emitted, StringComparison.Ordinal)
                            ? text[emitted.Length..] : text;
                        if (delta.Length > 0)
                        {
                            emitted = text;
                            await channel.Writer.WriteAsync(delta);
                        }
                        last = text; stable = 0;
                    }
                    else
                    {
                        stable++;
                        if ((!BoolNode(st, "generating") && stable >= StablePollsDone)
                            || stable >= StablePollsForce)
                        {
                            finished = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); }
            finally
            {
                if (finished)
                {
                    var url = "";
                    for (var i = 0; i < 10; i++)
                    {
                        url = tab.Page.Url;
                        if (IsThreadUrl(url)) break;
                        await Task.Delay(200);
                    }
                    SaveHot(tab, account, req, IsThreadUrl(url) ? url : "");
                }
                else Forget(tab);
                Checkin(tab);
                if (finished) channel.Writer.Complete();
            }
        });
        return channel.Reader.ReadAllAsync();
    }

    // ---------- media ----------
    private static string MediaPrompt(string prompt, string kind, string? size, int? duration = null)
    {
        var hints = new List<string>();
        if (!string.IsNullOrEmpty(size)) hints.Add($"aspect ratio {size}");
        if (duration is not null) hints.Add($"{duration} seconds long");
        var verb = kind == "image" ? "Generate an image" : "Generate a video";
        var suffix = hints.Count > 0 ? $" ({string.Join(", ", hints)})" : "";
        return $"{verb}{suffix}: {prompt}";
    }

    private async Task<Dictionary<string, object?>> WaitMedia(BrowserTab tab,
        System.Text.Json.Nodes.JsonNode? baseState, string kind, double timeout,
        Action<int>? onProgress, CancellationToken cancel)
    {
        var baseAtts = baseState?["attachments"] is System.Text.Json.Nodes.JsonArray ba ? ba.Count : 0;
        var baseCount = IntNode(baseState, "agentCount");
        var grace = kind == "video" ? 90.0 : 8.0;
        var started = DateTime.UtcNow;
        DateTime? textDoneAt = null;
        var denied = false;
        var askedToAttach = false;
        while (true)
        {
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            var busy = false;
            if (elapsed >= timeout && !(busy && elapsed < timeout * 2.5)) break;
            if (cancel.IsCancellationRequested) throw new OperationCanceledException();
            await Task.Delay(600);
            var st = await ChatState(tab);
            if (BoolNode(st, "approval"))
            {
                await DenyApprovals(tab);
                denied = true; textDoneAt = null;
                continue;
            }
            var atts = st?["attachments"] is System.Text.Json.Nodes.JsonArray all
                ? all.Skip(baseAtts).ToList() : new List<System.Text.Json.Nodes.JsonNode?>();
            var fresh = atts.Where(a =>
                a is System.Text.Json.Nodes.JsonObject o
                && (o["src"]?.GetValue<string>() ?? "").Length > 0
                && (o["kind"]?.GetValue<string>() ?? "") == kind).ToList();
            if (fresh.Count > 0)
            {
                var lastAtt = (System.Text.Json.Nodes.JsonObject)fresh[^1]!;
                return new Dictionary<string, object?>
                {
                    ["src"] = lastAtt["src"]?.GetValue<string>(),
                    ["kind"] = kind,
                    ["w"] = lastAtt["w"] is System.Text.Json.Nodes.JsonValue wv && wv.TryGetValue<int>(out var w) ? w : 0,
                    ["h"] = lastAtt["h"] is System.Text.Json.Nodes.JsonValue hv && hv.TryGetValue<int>(out var h) ? h : 0,
                };
            }
            busy = BoolNode(st, "generating")
                || atts.Any(a => (a?["tid"]?.GetValue<string>() ?? "").Contains(kind));
            elapsed = (DateTime.UtcNow - started).TotalSeconds;
            onProgress?.Invoke(Math.Min(95, (int)(elapsed / timeout * 100)));
            if (atts.Any(a => (a?["tid"]?.GetValue<string>() ?? "").Contains(kind)))
            {
                textDoneAt = null;
                continue;
            }
            var text = Str(st, "lastText") ?? "";
            if (IntNode(st, "agentCount") > baseCount && text.Length > 0 && !BoolNode(st, "generating"))
            {
                if (textDoneAt is null) textDoneAt = DateTime.UtcNow;
                else if ((DateTime.UtcNow - textDoneAt.Value).TotalSeconds >= grace)
                {
                    if (!askedToAttach)
                    {
                        await Send(tab, $"Please attach the final {kind} file here in the chat"
                            + (denied ? " instead of uploading it." : ", as a single attachment."));
                        askedToAttach = true; textDoneAt = null;
                        baseCount = IntNode(st, "agentCount");
                        continue;
                    }
                    var probe = await Eval(tab.Page, Dom.LastBubbleMedia);
                    var links = probe?["links"] is System.Text.Json.Nodes.JsonArray la
                        ? la.Select(x => x?.GetValue<string>() ?? "").ToList() : new List<string>();
                    var url = PickMediaUrl(links, kind);
                    if (url is not null) return new Dictionary<string, object?> { ["src"] = url, ["kind"] = kind };
                    throw UpstreamException.Refused($"upstream replied with text only: {text[..Math.Min(200, text.Length)]}");
                }
            }
            else textDoneAt = null;
            if (elapsed >= timeout && !(busy && elapsed < timeout * 2.5)) break;
        }
        throw UpstreamException.Timeout($"{kind} generation timed out");
    }

    private static readonly string[] VideoExt = { ".mp4", ".webm", ".mov", ".m4v" };
    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".avif" };
    private static readonly string[] MediaHosts =
        { "videodelivery.net", "cloudflarestream.com", "cloudflarestorage.com", "amazonaws.com",
          "storage.googleapis.com", "blob.core.windows.net", "musecdn", "cdn.muse", "muse-cdn" };
    private static readonly string[] MediaPaths =
        { "/media/", "/download", "/dl/", "/video", "/attachment", "/files/" };

    private static string? PickMediaUrl(List<string> links, string kind)
    {
        var exts = kind == "video" ? VideoExt : ImageExt;
        int Score(string u)
        {
            var low = u.ToLowerInvariant();
            var path = low.Split('?')[0].Split('#')[0];
            if (u.StartsWith("blob:", StringComparison.Ordinal) || exts.Any(e => path.EndsWith(e))) return 3;
            if (MediaHosts.Any(h => low.Contains(h))) return 2;
            if (MediaPaths.Any(p => low.Contains(p))) return 1;
            return 0;
        }
        var cands = links.Where(u => u.Length > 0
            && !u.StartsWith("mailto:", StringComparison.Ordinal)
            && !u.StartsWith("javascript:", StringComparison.Ordinal)).ToList();
        cands.Sort((a, b) => Score(b).CompareTo(Score(a)));
        return cands.Count > 0 && Score(cands[0]) > 0 ? cands[0] : null;
    }

    private async Task<MediaResult> Download(BrowserTab tab, Dictionary<string, object?> att, string kind)
    {
        var res = await Eval(tab.Page, Dom.FetchAsBase64(att["src"]?.ToString() ?? ""));
        if (!BoolNode(res, "ok"))
            throw new UpstreamException($"failed to download generated {kind}: {Str(res, "err")}");
        var mime = Str(res, "mime");
        if (string.IsNullOrEmpty(mime)) mime = kind == "video" ? "video/mp4" : "image/png";
        var b64 = Str(res, "b64") ?? "";
        return new MediaResult(Convert.FromBase64String(b64), mime, kind,
            att["w"] is int w && w > 0 ? w : null,
            att["h"] is int h && h > 0 ? h : null);
    }

    private async Task<MediaResult> Generate(BrowserTab tab, Account account, string prompt,
        List<InputImage> images, string kind, double timeout, Action<int>? onProgress,
        CancellationToken cancel)
    {
        // Media always starts a fresh thread, so keep tabs holding a chat for chats.
        var t = tab;
        try
        {
            await NewThread(t);
            var baseState = await ChatState(t);
            await Attach(t, images);
            await Send(t, prompt);
            var att = await WaitMedia(t, baseState, kind, timeout, onProgress, cancel);
            return await Download(t, att, kind);
        }
        finally { Checkin(t); }
    }

    public async Task<List<MediaResult>> GenerateImage(Account account, ImageRequest req)
    {
        var prompt = MediaPrompt(req.Prompt, "image", req.Size);
        var results = new List<MediaResult>();
        for (var i = 0; i < Math.Max(1, req.N); i++)
        {
            var tab = await Checkout(account, t => !t.HasState);
            var r = await Generate(tab, account, prompt, req.References, "image",
                _settings.ImageTimeout, req.OnProgress, CancellationToken.None);
            r = r with { RevisedPrompt = req.Prompt };
            results.Add(r);
        }
        return results;
    }

    public async Task<MediaResult> GenerateVideo(Account account, VideoRequest req)
    {
        var prompt = MediaPrompt(req.Prompt, "video", req.Size, req.Duration);
        var images = req.FirstFrame is null ? new List<InputImage>() : new List<InputImage> { req.FirstFrame };
        var tab = await Checkout(account, t => !t.HasState);
        return await Generate(tab, account, prompt, images, "video",
            _settings.VideoTimeout, req.OnProgress, CancellationToken.None);
    }

    // ---------- session / quota ----------
    public async Task<SessionInfo> RenewSession(Account account)
    {
        var info = await Upstream.RenewSession(account.Cookies, _settings.WakeConfirmSeconds);
        List<BrowserTab> tabs;
        lock (_lock) tabs = _tabs.GetValueOrDefault(account.Id, new()).ToList();
        foreach (var t in tabs) t.Stale = true;
        foreach (var t in tabs.Where(t => !t.Busy)) await CloseTab(t);
        return info;
    }

    public async Task<Dictionary<string, object?>> Quota(Account account)
    {
        if (_quotaCache.TryGetValue(account.Id, out var hit)
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Convert.ToDouble(hit["fetched_at"] ?? 0) < QuotaCacheSeconds)
            return new Dictionary<string, object?>(hit);
        var sem = _quotaLocks.GetValueOrDefault(account.Id);
        if (sem is null) { sem = new SemaphoreSlim(1, 1); _quotaLocks[account.Id] = sem; }
        await sem.WaitAsync();
        try
        {
            if (_quotaCache.TryGetValue(account.Id, out hit)
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Convert.ToDouble(hit["fetched_at"] ?? 0) < QuotaCacheSeconds)
                return new Dictionary<string, object?>(hit);
            var tab = await CheckoutIdle(account);
            Dictionary<string, object?> parsed;
            try { parsed = await ReadQuota(tab); }
            finally { Checkin(tab); }
            var snapshot = new Dictionary<string, object?>(parsed)
            {
                ["source"] = "browser",
                ["fetched_at"] = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            _quotaCache[account.Id] = snapshot;
            return new Dictionary<string, object?>(snapshot);
        }
        finally { sem.Release(); }
    }

    private async Task<BrowserTab> CheckoutIdle(Account account)
    {
        lock (_lock)
        {
            var tabs = _tabs.GetValueOrDefault(account.Id, new()).Where(t => !t.Closed).ToList();
            var idle = tabs.Where(t => !t.Busy && !t.Stale).ToList();
            if (idle.Count > 0)
            {
                var tab = idle.FirstOrDefault(t => !t.HasState) ?? idle[0];
                tab.Busy = true;
                return tab;
            }
            if (tabs.Any(t => t.Busy) || _opening.GetValueOrDefault(account.Id) > 0)
                throw new MuseException($"account '{account.Id}' is serving a request; try the quota again when it is idle",
                    409, "server_error", "account_busy");
        }
        return await Checkout(account, t => !t.HasState);
    }

    private async Task<bool> ClickJs(BrowserTab tab, string expr)
    {
        var deadline = DateTime.UtcNow.AddSeconds(QuotaClickTimeout);
        while (true)
        {
            var pt = await Eval(tab.Page, expr);
            if (pt is System.Text.Json.Nodes.JsonObject o
                && o["x"] is System.Text.Json.Nodes.JsonValue xv && xv.TryGetValue<int>(out var x)
                && o["y"] is System.Text.Json.Nodes.JsonValue yv && yv.TryGetValue<int>(out var y))
            {
                await tab.Page.Mouse.ClickAsync(x, y);
                return true;
            }
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(150);
        }
    }

    private async Task Escape(BrowserTab tab)
    {
        for (var i = 0; i < 2; i++)
        {
            await tab.Page.Keyboard.PressAsync("Escape");
            await Task.Delay(TimeSpan.FromSeconds(EscapePause));
        }
    }

    private async Task<Dictionary<string, object?>> ReadQuota(BrowserTab tab)
    {
        var page = await PageState(tab.Page);
        if (!BoolNode(page, "hasInput")) await NewThread(tab);
        await Escape(tab);
        try
        {
            if (!await ClickJs(tab, Dom.QuotaSettingsEntry))
                return new Dictionary<string, object?>(Dom.ParseUsageText(""))
                    { ["error"] = "settings entry not found" };
            if (!await ClickJs(tab, Dom.QuotaMenuItem))
                return new Dictionary<string, object?>(Dom.ParseUsageText(""))
                    { ["error"] = "settings menu item not found" };
            var deadline = DateTime.UtcNow.AddSeconds(QuotaReadTimeout);
            while (true)
            {
                var dialog = await Eval(tab.Page, Dom.QuotaDialogText);
                var parsed = Dom.ParseUsageText(
                    dialog is System.Text.Json.Nodes.JsonValue dv && dv.TryGetValue<string>(out var s) ? s : "");
                if (parsed["found"] is true || DateTime.UtcNow >= deadline) return parsed;
                await Task.Delay(200);
            }
        }
        finally
        {
            try
            {
                await Escape(tab);
                var dialog = await Eval(tab.Page, Dom.QuotaDialogText);
                if (dialog is System.Text.Json.Nodes.JsonValue dv
                    && dv.TryGetValue<string>(out var s) && s.Length > 0)
                    tab.Stale = true;
            }
            catch { tab.Stale = true; }
        }
    }
}
