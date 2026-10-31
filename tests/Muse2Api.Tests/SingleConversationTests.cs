using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class SingleConversationTests
{
    [Fact]
    public void ConversationBindingSurvivesRestartWithoutPersistingSourceOrTurns()
    {
        using var dir = new TempDirectory();
        new LocalConversationStore(dir.Root).Save("account-a", "desktop-chat", "https://muse.ai/thread/fixture", false);
        var state = new LocalConversationStore(dir.Root).Load("account-a", "desktop-chat");
        Assert.Equal("https://muse.ai/thread/fixture", state!.Url);
        Assert.Throws<UpstreamException>(() => new LocalConversationStore(dir.Root).Load("account-b", "desktop-chat"));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Root, "local-conversations")));
        Assert.DoesNotContain("turns", File.ReadAllText(Directory.GetFiles(Path.Combine(dir.Root, "local-conversations"))[0]));
    }

    [Theory]
    [InlineData("https://evil.test/thread/id")]
    [InlineData("https://muse.ai/thread/new")]
    [InlineData("http://muse.ai/thread/id")]
    public void InvalidBindingNeverAllowsAReplacementThread(string url)
    {
        using var dir = new TempDirectory();
        Assert.Throws<MuseException>(() => new LocalConversationStore(dir.Root).Save("a", "chat", url, false));
    }

    [Fact]
    public void ContinuationSendsNewObservationsAndStateInsteadOfTheWholeTranscript()
    {
        var prompt = Prompt.LocalContinuation(new()
        {
            ("system", "old workspace context"), ("user", "large old source"), ("assistant", "previous plan"),
            ("user", "Tool result for edit_file:\nedited a.cs"), ("user", "APP_STATE:\n{\"goal\":\"fix a.cs\"}"),
        });
        Assert.DoesNotContain("large old source", prompt); Assert.DoesNotContain("previous plan", prompt);
        Assert.Contains("edited a.cs", prompt); Assert.Contains("fix a.cs", prompt);
    }

    [Fact]
    public async Task LocalRateLimitStopsAfterOneSubmissionWithoutAccountFailover()
    {
        using var dir = new TempDirectory();
        var pool = new AccountPool(dir.File("accounts.json")); pool.Upsert(new Account { Id = "a" }); pool.Upsert(new Account { Id = "b" });
        var driver = new RateDriver(); var gateway = new Gateway(pool, driver);
        var request = new ChatRequest("task", "muse-chat", new(), "chat", new(), default, LocalTools: true);
        await Assert.ThrowsAsync<UpstreamException>(async () => { await foreach (var _ in gateway.ChatStream(request)) { } });
        Assert.Equal(1, driver.Calls);
    }

    [BrowserFact]
    public async Task TwentyRoundsAndARestartUseOneUpstreamThreadAndOneBrowserTab()
    {
        using var dir = new TempDirectory(); var fixture = new WebFixture();
        var account = Account(); var settings = BrowserSettings(dir.Root);
        var turns = new List<(string Role, string Text)> { ("user", "read files") };
        var driver = new BrowserDriver(settings);
        await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            for (var i = 0; i < 20; i++)
            {
                var text = await Reply(driver, account, turns);
                Assert.Contains("done", text);
                turns.Add(("assistant", text)); turns.Add(("user", "Tool result for read_file:\nfile " + i));
            }
            Assert.Equal(1, fixture.NewThreads); Assert.Equal(20, fixture.Submissions);
            Assert.Equal(1, (int)(await driver.Health())["tabs"]!);
        }
        finally { await driver.ShutdownAsync(); }
        driver = new BrowserDriver(settings); await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            Assert.Contains("done", await Reply(driver, account, turns));
            Assert.Equal(1, fixture.NewThreads); Assert.Equal(21, fixture.Submissions);
            Assert.Equal("https://muse.ai/thread/fixture-1", new LocalConversationStore(dir.Root).Load(account.Id, "desktop-chat")!.Url);
        }
        finally { await driver.ShutdownAsync(); }
    }

    [BrowserFact]
    public async Task Browser429PreservesBindingAndNeverCreatesOrSubmitsAgainAutomatically()
    {
        using var dir = new TempDirectory(); var fixture = new WebFixture { RateLimited = true }; var account = Account();
        var driver = new BrowserDriver(BrowserSettings(dir.Root)); await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            var failure = await Assert.ThrowsAsync<UpstreamException>(() => Reply(driver, account, new() { ("user", "task") }));
            Assert.Equal(429, failure.StatusCode); Assert.Equal(1, fixture.NewThreads); Assert.Equal(1, fixture.Submissions);
            Assert.NotNull(new LocalConversationStore(dir.Root).Load(account.Id, "desktop-chat"));
        }
        finally { await driver.ShutdownAsync(); }
    }

    [BrowserFact]
    public async Task VisibleRateLimitNoticeStopsWithoutSendingAgain()
    {
        using var dir = new TempDirectory(); var fixture = new WebFixture { RateNotice = true }; var account = Account();
        var driver = new BrowserDriver(BrowserSettings(dir.Root)); await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            var failure = await Assert.ThrowsAsync<UpstreamException>(() => Reply(driver, account, new() { ("user", "task") }));
            Assert.Equal(429, failure.StatusCode); Assert.Equal(1, fixture.NewThreads); Assert.Equal(1, fixture.Submissions);
        }
        finally { await driver.ShutdownAsync(); }
    }

    [BrowserFact]
    public async Task UnavailableSavedThreadNeverFallsBackToANewConversation()
    {
        using var dir = new TempDirectory(); var fixture = new WebFixture(); var account = Account(); var settings = BrowserSettings(dir.Root);
        var driver = new BrowserDriver(settings); await driver.StartupAsync();
        try { await fixture.Install(driver, account); await Reply(driver, account, new() { ("user", "task") }); }
        finally { await driver.ShutdownAsync(); }
        fixture.SavedUnavailable = true; driver = new BrowserDriver(settings); await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            await Assert.ThrowsAsync<UpstreamException>(() => Reply(driver, account, new() { ("user", "continue") }));
            Assert.Equal(1, fixture.NewThreads); Assert.Equal(1, fixture.Submissions);
        }
        finally { await driver.ShutdownAsync(); }
    }

    private static Account Account() => new() { Id = "fixture-account", Cookies = new() { ["hatch_sess"] = "fixture", ["hatch_native_auth_device"] = "device" } };
    [BrowserFact]
    public async Task AttachmentUploadsOncePerPasteAndANewPasteOfTheSamePixelsUploadsAgain()
    {
        using var dir = new TempDirectory(); var fixture = new WebFixture(); var account = Account();
        var driver = new BrowserDriver(BrowserSettings(dir.Root)); await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            var image = new InputImage(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j6n0AAAAASUVORK5CYII="));
            foreach (var batch in new[] { "paste-1", "paste-1", "paste-2" })
                await foreach (var _ in driver.ChatStream(account, new("inspect image", "muse-chat", new() { image }, "desktop-chat", new() { ("user", "image") }, default, true, batch))) { }
            Assert.Equal(2, fixture.Uploads); Assert.Equal(3, fixture.Submissions); Assert.Equal(1, fixture.NewThreads);
        }
        finally { await driver.ShutdownAsync(); }
    }
    private static Settings BrowserSettings(string root) => new()
    {
        DataDir = root, Headless = true, ChromiumPath = DesktopProfile.FindBrowser(), ChatTimeout = 10, FirstTokenTimeout = 5, PageReadyTimeout = 5, ReplyRenderGrace = 1,
    };

    [BrowserFact]
    public async Task HiddenStopAndInactiveReplyDoNotKeepCompletedResponseBusy()
    {
        using var dir = new TempDirectory(); var account = Account();
        var fixture = new WebFixture { Script = """
            const hidden=document.createElement('section');hidden.hidden=true;
            hidden.innerHTML='<button aria-label="Stop">Stop</button><div class="hatch-agent-bubble-bg">inactive reply</div>';
            document.body.append(hidden);
            const oldBubble=bubble; bubble=text=>{oldBubble(text);document.body.append(hidden);};
            const other=document.createElement('button');other.setAttribute('aria-label','Stop playback');document.body.append(other);
            """ };
        var driver = new BrowserDriver(BrowserSettings(dir.Root)); await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            Assert.Contains("fixture done", await Reply(driver, account, new() { ("user", "task") }));
            Assert.Equal(1, fixture.Submissions);
        }
        finally { await driver.ShutdownAsync(); }
    }

    [BrowserFact]
    public async Task StalledPartialReplyStopsWithoutExecutingOrResending()
    {
        using var dir = new TempDirectory(); var account = Account();
        var fixture = new WebFixture { Script = """
            const oldBubble=bubble; bubble=text=>{
              oldBubble('{"tool_calls":[');
              const stop=document.createElement('button');stop.setAttribute('aria-label','Stop generating');document.body.append(stop);
            };
            """ };
        var driver = new BrowserDriver(new Settings { DataDir = dir.Root, Headless = true, ChromiumPath = DesktopProfile.FindBrowser(), ChatTimeout = 10, FirstTokenTimeout = 5, ChatIdleTimeout = .6 });
        await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            var failure = await Assert.ThrowsAsync<UpstreamException>(() => Reply(driver, account, new() { ("user", "task") }));
            Assert.Contains("stopped making progress", failure.Message);
            Assert.Equal(1, fixture.Submissions); Assert.Equal(1, fixture.NewThreads);
            Assert.True(new LocalConversationStore(dir.Root).Load(account.Id, "desktop-chat")!.Pending);
        }
        finally { await driver.ShutdownAsync(); }
    }

    [BrowserFact]
    public async Task GenerationStopsWithoutAReplyFailsWithoutAnotherSubmission()
    {
        using var dir = new TempDirectory(); var account = Account();
        var fixture = new WebFixture { Script = """
            bubble=text=>{
              const stop=document.createElement('button');stop.setAttribute('aria-label','Stop generating');document.body.append(stop);
              setTimeout(()=>stop.remove(),700);
            };
            """ };
        var driver = new BrowserDriver(BrowserSettings(dir.Root)); await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            var failure = await Assert.ThrowsAsync<UpstreamException>(() => Reply(driver, account, new() { ("user", "task") }));
            Assert.Contains("without a completed reply", failure.Message); Assert.Equal(1, fixture.Submissions);
        }
        finally { await driver.ShutdownAsync(); }
    }

    [BrowserFact]
    public async Task SecondReadDoesNotReplayFirstToolCallWhileHistoryHydrates()
    {
        using var dir=new TempDirectory(); File.WriteAllText(dir.File("first.txt"),"FIRST_FILE");File.WriteAllText(dir.File("second.txt"),"SECOND_FILE");
        var fixture=new WebFixture
        {
            Responses=new[]{"{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"first.txt\"}}]}","{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"second.txt\"}}]}","{\"status\":\"done\",\"summary\":\"Đã đọc cả hai tệp.\"}"},
            Script="""
              const render=bubble;let replies=0,first='';bubble=text=>{
                if(++replies===1){first=text;render(text);return;}
                if(replies===2){render(first);const old=messages.lastChild;const latestUser=[...messages.querySelectorAll('.ms-auto')].at(-1);messages.insertBefore(old,latestUser);setTimeout(()=>render(text),1600);return;}
                render(text);
              };
              """,
        };
        var account=Account();var driver=new BrowserDriver(BrowserSettings(dir.Root));await driver.StartupAsync();
        try
        {
            await fixture.Install(driver,account);var events=new List<AgentEvent>();
            var loop=new AgentLoop(new ToolExecutor(dir.Root),async(messages,token)=>
            {
                var reply=await Reply(driver,account,messages.Select(m=>(m["role"]!.ToString(),m["content"]!.ToString())).ToList());
                var calls=ToolCallParser.Parse(reply);return(calls.Count==0?ToolCallParser.PlannerReply(reply):reply,calls);
            },(_,_)=>Task.FromResult(true),events.Add);
            Assert.True(await loop.RunAsync("Đọc hai tệp first.txt và second.txt"));
            Assert.Equal(new[]{"first.txt","second.txt"},events.Where(e=>e.Kind=="tool_start").Select(e=>e.Path).ToArray());
            Assert.Contains(events,e=>e.Kind=="tool_result"&&e.Text.Contains("SECOND_FILE"));
            Assert.Equal(3,fixture.Submissions);Assert.Equal(1,fixture.NewThreads);
        }
        finally {await driver.ShutdownAsync();}
    }

    [BrowserFact]
    public async Task IdenticalReplyInReplacedBranchIsNewButRerenderedOldReplyIsNot()
    {
        using var dir=new TempDirectory();var account=Account();
        var fixture=new WebFixture { Script="""
            let turn=0; const send=document.querySelector('button').onclick;
            document.querySelector('button').onclick=async()=>{
              if(++turn<=2){messages.replaceChildren();const user=document.createElement('div');user.className='group/msg ms-auto items-end';user.textContent='APP_STATE observation '+turn;messages.append(user);}
              else {const submitted=box.value;box.value='';const r=await fetch('/api/chat',{method:'POST'});bubble(await r.text());userBubble(submitted);const stop=document.createElement('button');stop.setAttribute('aria-label','Stop generating');document.body.append(stop);setTimeout(()=>stop.remove(),700);return;}
              await send();
            };
            """ };
        var driver=new BrowserDriver(BrowserSettings(dir.Root));await driver.StartupAsync();
        try
        {
            await fixture.Install(driver,account);
            var first=await Reply(driver,account,new(){("user","task")});
            var second=await Reply(driver,account,new(){("user","next observation")});
            Assert.Equal(first,second);
            // Recreating the old assistant node alone is not proof of a reply
            // to a new user turn, and must never execute its old tool plan.
            var failure=await Assert.ThrowsAsync<UpstreamException>(()=>Reply(driver,account,new(){("user","unchanged UI") }));
            Assert.Equal("upstream_response_unavailable",failure.Code);
            Assert.Equal(3,fixture.Submissions);Assert.Equal(1,fixture.NewThreads);
        }
        finally {await driver.ShutdownAsync();}
    }

    [BrowserFact]
    public async Task CommittedReplyMayRenderMoreThanTwoSecondsAfterStopDisappears()
    {
        using var dir=new TempDirectory();var account=Account();
        var fixture=new WebFixture { Script="""
            const oldBubble=bubble; bubble=text=>{
              const stop=document.createElement('button');stop.setAttribute('aria-label','Stop generating');document.body.append(stop);
              setTimeout(()=>stop.remove(),700);setTimeout(()=>oldBubble(text),4000);
            };
            """ };
        var driver=new BrowserDriver(new Settings{DataDir=dir.Root,Headless=true,ChromiumPath=DesktopProfile.FindBrowser(),ChatTimeout=12,FirstTokenTimeout=10,ReplyRenderGrace=6});
        await driver.StartupAsync();
        try {await fixture.Install(driver,account);Assert.Contains("fixture done",await Reply(driver,account,new(){("user","task")}));Assert.Equal(1,fixture.Submissions);}
        finally {await driver.ShutdownAsync();}
    }

    [BrowserFact]
    public async Task ActiveGenerationWaitsForCommittedReplyAndVisibleWebErrorStopsImmediately()
    {
        using var dir = new TempDirectory(); var account = Account();
        var fixture = new WebFixture { Script = """
            const oldBubble=bubble; bubble=text=>{
              oldBubble('partial');const b=messages.lastChild;
              const stop=document.createElement('button');stop.setAttribute('aria-label','Stop generating');document.body.append(stop);
              setTimeout(()=>{b.remove();oldBubble(text);stop.remove();},1200);
            };
            """ };
        var driver = new BrowserDriver(BrowserSettings(dir.Root)); await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            var result = await Reply(driver, account, new() { ("user", "task") });
            Assert.Contains("fixture done", result); Assert.DoesNotContain("partial", result);
        }
        finally { await driver.ShutdownAsync(); }
        fixture = new WebFixture { Script = "bubble=text=>{const e=document.createElement('div');e.setAttribute('role','alert');e.textContent='Something went wrong';document.body.append(e);};" };
        // New isolated fixture profile: no real upstream requests or user data.
        using var secondDir = new TempDirectory(); driver = new BrowserDriver(BrowserSettings(secondDir.Root)); await driver.StartupAsync();
        try
        {
            await fixture.Install(driver, account);
            var failure = await Assert.ThrowsAsync<UpstreamException>(() => Reply(driver, account, new() { ("user", "task") }));
            Assert.Contains("web stopped responding", failure.Message); Assert.Equal(1, fixture.Submissions);
        }
        finally { await driver.ShutdownAsync(); }
    }
    private static async Task<string> Reply(BrowserDriver driver, Account account, List<(string Role, string Text)> turns)
    {
        var output = "";
        await foreach (var part in driver.ChatStream(account, new("fixture task", "muse-chat", new(), "desktop-chat", turns, default, true))) output += part;
        return output;
    }

    private sealed class RateDriver : IMuseDriver
    {
        public int Calls; public string Name => "test"; public bool RequiresAccount => true; public DriverCapabilities Capabilities => new(Chat: true);
        public async IAsyncEnumerable<string> ChatStream(Account account, ChatRequest request)
        { Calls++; await Task.Yield(); if (Calls > 0) throw UpstreamException.Quota("rate limit"); yield break; }
    }

    private sealed class WebFixture
    {
        public int NewThreads, Submissions, Uploads; public bool RateLimited, RateNotice, SavedUnavailable;
        public string Script = "";
        public string[]? Responses;
        public async Task Install(BrowserDriver driver, Account account)
        {
            var method = typeof(BrowserDriver).GetMethod("GetContext", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var context = await (Task<IBrowserContext>)method.Invoke(driver, new object[] { account })!;
            await context.RouteAsync("https://muse.ai/**", async route =>
            {
                var path = new Uri(route.Request.Url).AbsolutePath;
                if (path == "/api/attachment") { Uploads++; await route.FulfillAsync(new() { Body = "OK" }); return; }
                if (path == "/api/chat")
                {
                    Submissions++;
                    await route.FulfillAsync(new() { Status = RateLimited ? 429 : 200, ContentType = "text/plain", Body = RateNotice ? "RATE_NOTICE" : Responses is {Length:>0} ? Responses[Math.Min(Submissions-1,Responses.Length-1)] : "{\"status\":\"done\",\"summary\":\"fixture done\"}" }); return;
                }
                if (path == "/thread/new") NewThreads++;
                if (SavedUnavailable && path.StartsWith("/thread/") && path != "/thread/new")
                { await route.FulfillAsync(new() { ContentType = "text/html", Body = "Saved conversation unavailable" }); return; }
                await route.FulfillAsync(new() { ContentType = "text/html", Body = """
                    <html><body><textarea></textarea><input type="file"><button aria-label="Send">Send</button><div id="messages"></div>
                    <script>
                    const box=document.querySelector('textarea'), messages=document.querySelector('#messages');
                    document.querySelector('input[type=file]').onchange=()=>fetch('/api/attachment',{method:'POST',body:'fixture image upload'});
                    function userBubble(text){const u=document.createElement('div');u.className='group/msg ms-auto items-end';u.textContent=text;messages.append(u);}
                    function bubble(text){const g=document.createElement('div');g.className='group/msg justify-start';const b=document.createElement('div');b.className='hatch-agent-bubble-bg';const pre=document.createElement('pre'),code=document.createElement('code');code.className='language-json';code.textContent=text;pre.append(code);b.append(pre);g.append(b);messages.append(g);}
                    const saved=JSON.parse(localStorage.getItem(location.pathname)||'[]'); saved.forEach(item=>{userBubble(item.user);bubble(item.text);});
                    document.querySelector('button').onclick=async()=>{
                      if(location.pathname==='/thread/new') history.replaceState({},'', '/thread/fixture-1');
                      const submitted=box.value;userBubble(submitted);box.value=''; const response=await fetch('/api/chat',{method:'POST'});
                      if(!response.ok)return; const text=await response.text();
                      if(text==='RATE_NOTICE'){const alert=document.createElement('div');alert.setAttribute('role','alert');alert.textContent='Too many requests. Rate limit exceeded.';document.body.append(alert);return;}
                      bubble(text);
                      const values=JSON.parse(localStorage.getItem(location.pathname)||'[]');values.push({user:submitted,text});localStorage.setItem(location.pathname,JSON.stringify(values));
                    };
                    </script></body></html>
                    """ + "<script>" + Script + "</script>" });
            });
        }
    }
}
