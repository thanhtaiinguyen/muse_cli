using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Muse2Api.Tests;

public class DesktopUpdateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static HttpResponseMessage Response(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body) };
    private static string Release(string version, string? url = null, bool draft = false, bool prerelease = false, bool assets = true)
        => JsonSerializer.Serialize(new
        {
            tag_name = "v" + version, draft, prerelease,
            html_url = url ?? DesktopUpdates.RepositoryUrl + "/releases/tag/v" + version,
            assets = assets ? new[] { "Muse-win-x64.zip", "Muse-win-x64.zip.sha256" }.Select(name => new
            { name, browser_download_url = DesktopUpdates.RepositoryUrl + "/releases/download/v" + version + "/" + name }).ToArray() : [],
        });

    [Theory]
    [InlineData("0.8.9", "0.8.10", UpdateStatus.Available)]
    [InlineData("0.8.10+localbuild", "0.8.10", UpdateStatus.Current)]
    [InlineData("0.8.11", "0.8.10", UpdateStatus.Current)]
    [InlineData("1.0.0", "0.99.99", UpdateStatus.Current)]
    public async Task ComparesNumericVersionsInsteadOfStringsAndNeverRecommendsDowngrades(string current, string latest, UpdateStatus status)
    {
        using var dir = new TempDirectory(); using var handler = new Fake(() => Response(Release(latest))); using var http = new HttpClient(handler);
        var result = await new DesktopUpdates(http, dir.Root, () => Start).CheckAsync(current, UpdateFeed.Release);
        Assert.Equal(status, result.Status); Assert.Equal(status == UpdateStatus.Available, result.Notify);
        Assert.Equal(DesktopUpdates.ReleaseEndpoint, handler.Requests.Single().Url);
        Assert.Null(handler.Requests.Single().Auth); Assert.Null(handler.Requests.Single().Cookie);
    }

    [Theory]
    [InlineData("0.8.11-beta")]
    [InlineData("v0.8.11-rc.1")]
    [InlineData("0.08.11")]
    [InlineData("999999999999.0.0")]
    [InlineData("https://evil.test/update.exe")]
    public void RejectsInvalidStableVersions(string value) => Assert.False(DesktopUpdates.TryVersion(value, out _));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task OnlyNotifiesForStableReleasesWithAnActualWindowsPackage(bool draft, bool prerelease, bool assets)
    {
        using var dir = new TempDirectory(); using var handler = new Fake(() => Response(Release("0.8.11", draft: draft, prerelease: prerelease, assets: assets))); using var http = new HttpClient(handler);
        var result = await new DesktopUpdates(http, dir.Root, () => Start).CheckAsync("0.8.10", UpdateFeed.Release);
        Assert.Equal(UpdateStatus.NoRelease, result.Status); Assert.False(result.Notify); Assert.Null(result.Update);
    }

    [Theory]
    [InlineData("https://evil.test/releases/tag/v0.8.11")]
    [InlineData("http://github.com/thanhtaiinguyen/muse_cli/releases/tag/v0.8.11")]
    [InlineData("https://github.com.evil.test/thanhtaiinguyen/muse_cli/releases/tag/v0.8.11")]
    [InlineData("https://github.com/another/repo/releases/tag/v0.8.11")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public async Task RejectsExternalOrExecutableUpdateLinks(string url)
    {
        using var dir = new TempDirectory(); using var handler = new Fake(() => Response(Release("0.8.11", url))); using var http = new HttpClient(handler);
        var result = await new DesktopUpdates(http, dir.Root, () => Start).CheckAsync("0.8.10", UpdateFeed.Release);
        Assert.Equal(UpdateStatus.Unavailable, result.Status); Assert.Null(result.Update); Assert.False(result.Notify);
    }

    [Fact]
    public async Task NoReleaseAndOfflineFailuresAreNotMistakenForUpdates()
    {
        using var dir = new TempDirectory(); using var handler = new Fake(() => Response("{}", HttpStatusCode.NotFound)); using var http = new HttpClient(handler);
        var result = await new DesktopUpdates(http, dir.Root, () => Start).CheckAsync("0.8.10", UpdateFeed.Release);
        Assert.Equal(UpdateStatus.NoRelease, result.Status);
        using var secondDir = new TempDirectory(); using var offline = new Fake(() => throw new HttpRequestException("offline")); using var offlineHttp = new HttpClient(offline);
        Assert.Equal(UpdateStatus.Unavailable, (await new DesktopUpdates(offlineHttp, secondDir.Root, () => Start).CheckAsync("0.8.10", UpdateFeed.Release)).Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{broken")]
    public async Task BadResponsesDoNotCrashOrAnnounceAnUpdate(string body)
    {
        using var dir = new TempDirectory(); using var handler = new Fake(() => Response(body)); using var http = new HttpClient(handler);
        var result = await new DesktopUpdates(http, dir.Root, () => Start).CheckAsync("0.8.10", UpdateFeed.Release);
        Assert.Equal(UpdateStatus.Unavailable, result.Status); Assert.False(result.Notify);
    }

    [Fact]
    public async Task CacheThrottlesStartupAcrossRestartsAnd304RetainsValidatedMetadata()
    {
        using var dir = new TempDirectory(); var now = Start;
        using var handler = new Fake(() =>
        {
            var response = Response(Release("0.8.11")); response.Headers.ETag = new EntityTagHeaderValue("\"release-a\""); return response;
        }); using var http = new HttpClient(handler);
        Assert.True((await new DesktopUpdates(http, dir.Root, () => now).CheckAsync("0.8.10", UpdateFeed.Release)).Notify);
        var reopened = new DesktopUpdates(http, dir.Root, () => now);
        Assert.True((await reopened.CheckAsync("0.8.10", UpdateFeed.Release)).Notify);
        Assert.Single(handler.Requests);
        now += DesktopUpdates.CheckInterval;
        handler.Next = () => Response("", HttpStatusCode.NotModified);
        var result = await reopened.CheckAsync("0.8.10", UpdateFeed.Release);
        Assert.Equal("0.8.11", result.Update!.Version);
        Assert.Equal("\"release-a\"", handler.Requests[^1].ETag);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(UpdateStatus.Current, (await reopened.CheckAsync("0.8.11", UpdateFeed.Release)).Status);
    }

    [Fact]
    public async Task DismissalPersistsForOneVersionButNewerReleasesNotifyAgain()
    {
        using var dir = new TempDirectory(); var now = Start;
        using var handler = new Fake(() => Response(Release("0.8.11"))); using var http = new HttpClient(handler);
        var checker = new DesktopUpdates(http, dir.Root, () => now);
        var first = await checker.CheckAsync("0.8.10", UpdateFeed.Release); await checker.DismissAsync(first.Update!);
        checker = new DesktopUpdates(http, dir.Root, () => now);
        Assert.False((await checker.CheckAsync("0.8.10", UpdateFeed.Release)).Notify);
        Assert.True((await checker.CheckAsync("0.8.10", UpdateFeed.Release, manual: true)).Notify);
        Assert.Single(handler.Requests);
        now += DesktopUpdates.CheckInterval; handler.Next = () => Response(Release("0.8.12"));
        Assert.True((await checker.CheckAsync("0.8.10", UpdateFeed.Release)).Notify);
    }

    [Fact]
    public async Task HonorsRetryAfterAndRateLimitResetEvenForManualChecks()
    {
        using var dir = new TempDirectory(); var now = Start;
        using var handler = new Fake(() =>
        {
            var response = Response("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            response.Headers.Add("X-RateLimit-Reset", Start.AddMinutes(10).ToUnixTimeSeconds().ToString()); return response;
        }); using var http = new HttpClient(handler);
        var checker = new DesktopUpdates(http, dir.Root, () => now);
        Assert.Equal(UpdateStatus.RateLimited, (await checker.CheckAsync("0.8.10", UpdateFeed.Release)).Status);
        now += TimeSpan.FromMinutes(3);
        Assert.Equal(UpdateStatus.RateLimited, (await new DesktopUpdates(http, dir.Root, () => now).CheckAsync("0.8.10", UpdateFeed.Release, manual: true)).Status);
        Assert.Single(handler.Requests);
        now += TimeSpan.FromMinutes(8); handler.Next = () => Response(Release("0.8.11"));
        Assert.True((await checker.CheckAsync("0.8.10", UpdateFeed.Release, manual: true)).Notify);
    }

    [Fact]
    public async Task SourceUsersCheckMainVersionAndDoNotRequireBinaryReleases()
    {
        using var dir = new TempDirectory(); using var handler = new Fake(() => Response("<Project><PropertyGroup><Version>0.8.11</Version></PropertyGroup></Project>")); using var http = new HttpClient(handler);
        var result = await new DesktopUpdates(http, dir.Root, () => Start).CheckAsync("0.8.10", UpdateFeed.Source);
        Assert.True(result.Notify); Assert.Equal(UpdateFeed.Source, result.Update!.Feed);
        Assert.Equal(DesktopUpdates.SourceEndpoint, handler.Requests.Single().Url);
        Assert.EndsWith("/commits/main/", result.Update.Url);
        Assert.Contains("pull", result.Message);
    }

    [Fact]
    public async Task SourceXmlCannotReadLocalFilesThroughExternalEntities()
    {
        using var dir = new TempDirectory(); using var handler = new Fake(() => Response("<!DOCTYPE Project [<!ENTITY xxe SYSTEM 'file:///C:/private.txt'>]><Project><PropertyGroup><Version>&xxe;</Version></PropertyGroup></Project>")); using var http = new HttpClient(handler);
        Assert.Equal(UpdateStatus.Unavailable, (await new DesktopUpdates(http, dir.Root, () => Start).CheckAsync("0.8.10", UpdateFeed.Source)).Status);
    }

    [Fact]
    public void DetectsSourceBuildsWhileDistInsideTheRepositoryIsStillABinaryRelease()
    {
        using var dir = new TempDirectory(); File.WriteAllText(dir.File("Muse2Api.sln"), "fixture");
        Directory.CreateDirectory(dir.File("src/Muse2Api.WinForms")); File.WriteAllText(dir.File("src/Muse2Api.WinForms/Muse2Api.WinForms.csproj"), "fixture");
        Assert.Equal(UpdateFeed.Source, DesktopUpdates.DetectFeed(dir.File("src/Muse2Api.WinForms/bin/Release/net10.0-windows")));
        Assert.Equal(UpdateFeed.Source, DesktopUpdates.DetectFeed(dir.File("artifacts/bin/Muse2Api.WinForms/release")));
        Assert.Equal(UpdateFeed.Release, DesktopUpdates.DetectFeed(dir.File("dist/Muse")));
        Assert.Equal(UpdateFeed.Release, DesktopUpdates.DetectFeed(dir.Root));
    }

    [Fact]
    public async Task CorruptCacheAndLargeResponsesFailWithoutTouchingOtherData()
    {
        using var dir = new TempDirectory(); File.WriteAllText(dir.File("update-release.json"), "{broken"); File.WriteAllText(dir.File("accounts.json"), "DO_NOT_TOUCH");
        using var handler = new Fake(() => Response(new string('x', 300000))); using var http = new HttpClient(handler);
        Assert.Equal(UpdateStatus.Unavailable, (await new DesktopUpdates(http, dir.Root, () => Start).CheckAsync("0.8.10", UpdateFeed.Release)).Status);
        Assert.Equal("DO_NOT_TOUCH", File.ReadAllText(dir.File("accounts.json")));
        Assert.Empty(Directory.GetFiles(dir.Root, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentBackgroundChecksShareOneRequestThroughTheCache()
    {
        using var dir = new TempDirectory(); using var handler = new Delayed(); using var http = new HttpClient(handler);
        var checker = new DesktopUpdates(http, dir.Root, () => Start);
        var first = checker.CheckAsync("0.8.10", UpdateFeed.Release);
        await handler.Started.Task;
        var second = checker.CheckAsync("0.8.10", UpdateFeed.Release);
        handler.Response.SetResult(Response(Release("0.8.11")));
        Assert.All(await Task.WhenAll(first, second), r => Assert.True(r.Notify));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ClosingCancelsTheNetworkRequestWithoutWritingFailureState()
    {
        using var dir = new TempDirectory(); using var handler = new Delayed(); using var http = new HttpClient(handler); using var cancel = new CancellationTokenSource();
        var task = new DesktopUpdates(http, dir.Root, () => Start).CheckAsync("0.8.10", UpdateFeed.Release, cancel: cancel.Token);
        await handler.Started.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Directory.GetFiles(dir.Root));
    }

    private sealed class Delayed : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Interlocked.Increment(ref Calls); Started.TrySetResult();
            return await Response.Task.WaitAsync(cancel);
        }
    }

    private sealed class Fake(Func<HttpResponseMessage> next) : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Next = next;
        public List<(string Url, string? ETag, string? Auth, string? Cookie)> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!.ToString(), request.Headers.IfNoneMatch.FirstOrDefault()?.ToString(), request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("Cookie", out var values) ? string.Join(';', values) : null));
            return Task.FromResult(Next());
        }
    }
}
