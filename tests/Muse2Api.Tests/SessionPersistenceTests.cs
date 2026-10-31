using Microsoft.Playwright;

namespace Muse2Api.Tests;

public class SessionPersistenceTests
{
    [Fact]
    public void ReopeningSameLogin_ReusesAccountIdentityWithoutResettingCooldown()
    {
        using var dir=new TempDirectory(); var pool=new AccountPool(dir.File("accounts.json"));
        var existing=new Account{Status=AccountStatus.Cooling,CooldownUntil=double.MaxValue,SuccessCount=7,Cookies=new(){["hatch_sess"]="fixture",["hatch_native_auth_device"]="device"}};
        pool.Upsert(existing);
        var reopened=pool.UpsertSession(new Account{Label="Restored",Cookies=new(existing.Cookies)});
        Assert.Equal(existing.Id,reopened.Id); Assert.Single(pool.All()); Assert.Equal(7,reopened.SuccessCount);
        Assert.Equal(AccountStatus.Cooling,reopened.Status); Assert.True(DesktopProfile.HasSavedLogin(reopened));
    }
    [Theory]
    [InlineData(AccountStatus.Active, true)]
    [InlineData(AccountStatus.Cooling, true)]
    [InlineData(AccountStatus.Invalid, false)]
    public void LoginPolicy_DoesNotConfuseCooldownWithLogout(AccountStatus status, bool saved)
    {
        var account = new Account { Status = status, CooldownUntil = double.MaxValue, Cookies = new() { ["hatch_sess"] = "fixture", ["hatch_native_auth_device"] = "fixture-device" } };
        Assert.Equal(saved, DesktopProfile.HasSavedLogin(account));
        account.Enabled = false; Assert.False(DesktopProfile.HasSavedLogin(account));
    }

    [Fact]
    public async Task SavedBrowserState_IsBoundToAccountAndLoginAndWrittenAtomically()
    {
        using var dir = new TempDirectory(); var store = new BrowserSessionStore(dir.Root);
        var account = new Account { Cookies = new() { ["hatch_sess"] = "fixture", ["hatch_native_auth_device"] = "device" } };
        const string state = "{\"cookies\":[],\"origins\":[]}";
        await store.SaveAsync(account, state);
        Assert.Equal(state, new BrowserSessionStore(dir.Root).Load(account));
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain("AuthenticationHash", File.ReadAllText(Directory.GetFiles(Path.Combine(dir.Root, "browser-sessions"))[0]));
        Assert.Null(store.Load(new Account { Cookies = new(account.Cookies) }));
        account.Cookies["hatch_sess"] = "another-login"; Assert.Null(store.Load(account));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Root, "browser-sessions"), "*.tmp"));
    }

    [Fact]
    public async Task BrokenEncryptedBrowserCacheIsPreservedAndCanBeReplacedAfterRecovery()
    {
        if(!OperatingSystem.IsWindows())return;
        using var dir=new TempDirectory();var account=new Account();var store=new BrowserSessionStore(dir.Root);
        const string valid="{\"cookies\":[],\"origins\":[]}";await store.SaveAsync(account,valid);
        var file=Assert.Single(Directory.GetFiles(Path.Combine(dir.Root,"browser-sessions"),"*.json"));
        foreach(var corrupt in new[]{"muse-dpapi-v1:AQID","muse-dpapi-v1:not-base64"})
        {File.WriteAllText(file,corrupt);Assert.Null(store.Load(account));Assert.Equal(corrupt,File.ReadAllText(file));}
        await store.SaveAsync(account,valid);Assert.Equal(valid,store.Load(account));
    }

    [BrowserFact]
    public async Task RealBrowser_RestoresCookieAndLocalStorageWithoutLoggingInAgain()
    {
        using var dir = new TempDirectory(); var store = new BrowserSessionStore(dir.Root); var account = new Account();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = DesktopProfile.FindBrowser() is { Length: > 0 } path ? path : null });
        async Task<IPage> Page(IBrowserContext context)
        {
            await context.RouteAsync("http://muse-session.test/**", route => route.FulfillAsync(new() { Body = "<html><body>Session fixture</body></html>", ContentType = "text/html" }));
            var page = await context.NewPageAsync(); await page.GotoAsync("http://muse-session.test/"); return page;
        }
        var first = await browser.NewContextAsync(); var page = await Page(first);
        await first.AddCookiesAsync(new[] { new Cookie { Name = "fixture_auth", Value = "retained", Domain = "muse-session.test", Path = "/" } });
        await page.EvaluateAsync("localStorage.setItem('fixture_device', 'retained-device')");
        await store.SaveAsync(account, await first.StorageStateAsync()); await first.CloseAsync();
        await using var restored = await browser.NewContextAsync(new() { StorageState = new BrowserSessionStore(dir.Root).Load(account) });
        var reopened = await Page(restored);
        Assert.Contains(await restored.CookiesAsync(), c => c.Name == "fixture_auth" && c.Value == "retained");
        Assert.Equal("retained-device", await reopened.EvaluateAsync<string>("localStorage.getItem('fixture_device')"));
    }
}
