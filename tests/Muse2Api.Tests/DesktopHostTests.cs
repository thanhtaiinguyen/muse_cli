using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Muse2Api.Tests;

public class DesktopHostTests
{
    [Fact]
    public void PersonalProfile_GeneratesPersistentPrivateCredentialsWithoutUserInput()
    {
        using var directory = new TempDirectory();
        var first = DesktopProfile.Create(directory.Root, browserPath: "");
        Assert.False(string.IsNullOrWhiteSpace(first.ApiKey));
        Assert.Equal(first.ApiKey, first.EffectiveAdminKey);
        Assert.Equal(first.ApiKey, PrivateDataProtection.ReadFile(first.KeyFile));
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(first.ApiKey, File.ReadAllText(first.KeyFile));
        Assert.Equal("127.0.0.1", first.Host);
        Assert.Equal("browser", first.Driver);
        Assert.Equal(first.ApiKey, DesktopProfile.Create(directory.Root, browserPath: "").ApiKey);
    }

    [Fact]
    public async Task FreshDesktop_CanUseApiAndAdminWithItsOwnAutomaticKey()
    {
        using var directory = new TempDirectory();
        var profile = DesktopProfile.Create(directory.Root, browserPath: "");
        await using var host = new DesktopGatewayHost(profile, new MockDriver(0));
        await host.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(host.BaseUrl!) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/admin/status")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/v1/models")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/admin/status")).StatusCode);
        using var saved = await http.PostAsync("/admin/accounts", new StringContent("""{"label":"Tài khoản cá nhân","cookies":{"hatch_sess":"example-session","hatch_native_auth_device":"example-device"}}""", Encoding.UTF8, "application/json"));
        saved.EnsureSuccessStatusCode();
        Assert.NotNull(JsonNode.Parse(await saved.Content.ReadAsStringAsync())!["account"]!["id"]);
    }

    [Fact]
    public async Task PersonalHosts_SelectDifferentPrivatePortsAndNeverReplaceAnExistingServer()
    {
        using var a = new TempDirectory();
        using var b = new TempDirectory();
        await using var first = new DesktopGatewayHost(DesktopProfile.Create(a.Root, ""), new MockDriver(0));
        await using var second = new DesktopGatewayHost(DesktopProfile.Create(b.Root, ""), new MockDriver(0));
        await first.StartAsync();
        await second.StartAsync();
        Assert.NotEqual(first.BaseUrl, second.BaseUrl);
        Assert.Equal("127.0.0.1", new Uri(first.BaseUrl!).Host);
        Assert.NotEqual(first.ApiKey, second.ApiKey);
        await second.DisposeAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(first.BaseUrl + "/healthz")).StatusCode);
    }

    [Fact]
    public async Task Restart_ReusesCredentialsAndSavedAccount()
    {
        using var directory = new TempDirectory();
        var original = DesktopProfile.Create(directory.Root, "");
        await using (var host = new DesktopGatewayHost(original, new MockDriver(0)))
        {
            await host.StartAsync();
            host.State.Pool.Upsert(new Account { Id = "personal", Label = "Saved account" });
        }
        var restored = DesktopProfile.Create(directory.Root, "");
        await using var restarted = new DesktopGatewayHost(restored, new MockDriver(0));
        await restarted.StartAsync();
        Assert.Equal(original.ApiKey, restarted.ApiKey);
        Assert.Equal("Saved account", restarted.State.Pool.Get("personal")!.Label);
    }

    [Fact]
    public async Task FailedDesktopStartup_ReleasesDatabaseAndCanRetry()
    {
        using var directory = new TempDirectory();
        var profile = DesktopProfile.Create(directory.Root, "");
        await using (var broken = new DesktopGatewayHost(profile, new BrokenStartup()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => broken.StartAsync());
            Assert.Null(broken.BaseUrl);
        }
        await using var retry = new DesktopGatewayHost(profile, new MockDriver(0));
        await retry.StartAsync();
        Assert.NotNull(retry.BaseUrl);
    }

    [Fact]
    public async Task MultipleAccounts_StayEnabledTogether_AndCreatedKeyAuthenticates()
    {
        using var directory = new TempDirectory();
        var profile = DesktopProfile.Create(directory.Root, browserPath: "");
        await using var host = new DesktopGatewayHost(profile, new MockDriver(0));
        await host.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(host.BaseUrl!) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.ApiKey);
        string? firstId = null, secondId = null;
        foreach (var (label, session) in new[] { ("TK 1", "sess-a"), ("TK 2", "sess-b") })
        {
            using var response = await http.PostAsync("/admin/accounts", new StringContent($"{{\"label\":\"{label}\",\"cookies\":{{\"hatch_sess\":\"{session}\",\"hatch_native_auth_device\":\"dev-{session}\"}}}}", Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            var id = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["account"]!["id"]!.ToString();
            if (firstId is null) firstId = id; else secondId = id;
        }
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, host.State.Pool.All().Count);
        Assert.All(host.State.Pool.All(), account => Assert.True(account.Enabled));
        using (var patchOff = await http.PatchAsync($"/admin/accounts/{firstId}", new StringContent("{\"enabled\":false}", Encoding.UTF8, "application/json")))
        {
            patchOff.EnsureSuccessStatusCode();
            Assert.False(host.State.Pool.Get(firstId!)!.Enabled);
            Assert.True(host.State.Pool.Get(secondId!)!.Enabled);
        }
        using (var patchOn = await http.PatchAsync($"/admin/accounts/{firstId}", new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json")))
        {
            patchOn.EnsureSuccessStatusCode();
            Assert.True(host.State.Pool.Get(firstId!)!.Enabled);
        }
        using (var created = await http.PostAsync("/admin/keys", new StringContent("{\"name\":\"key phụ\"}", Encoding.UTF8, "application/json")))
            created.EnsureSuccessStatusCode();
        var (_, plainKey) = host.State.Keys.Create("kiểm thử", "");
        using var keyHttp = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(host.BaseUrl!) };
        keyHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
        Assert.Equal(HttpStatusCode.OK, (await keyHttp.GetAsync("/v1/models")).StatusCode);
        using var wrongHttp = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(host.BaseUrl!) };
        wrongHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "m2a-sai-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongHttp.GetAsync("/v1/models")).StatusCode);
    }

    private sealed class BrokenStartup : IMuseDriver
    {
        public string Name => "broken";
        public bool RequiresAccount => false;
        public DriverCapabilities Capabilities => new();
        public Task StartupAsync() => throw new InvalidOperationException("test startup failure");
        public IAsyncEnumerable<string> ChatStream(Account account, ChatRequest req) => throw new NotSupportedException();
    }
}
