using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class TurboRegressionTests
{
    [Theory]
    [InlineData("kiểm tra độ hoàn thiện của project")]
    [InlineData("đánh giá mức độ hoàn thiện dự án")]
    public void AssessmentDoesNotRequireUnrequestedEdits(string prompt)
    {
        var intent = LocalTaskIntent.Required(prompt);
        Assert.True(intent.HasFlag(LocalWork.Read)); Assert.False(intent.HasFlag(LocalWork.Edit));
    }

    [Fact]
    public void AssessmentFollowedByExplicitCompletionStillRequiresEdit()
        => Assert.True(LocalTaskIntent.Required("kiểm tra độ hoàn thiện của project rồi hoàn thiện phần còn thiếu").HasFlag(LocalWork.Edit));

    [Fact]
    public async Task AssessmentFinishesAfterReadingWithoutForcingAnEdit()
    {
        using var dir = new TempDirectory(); File.WriteAllText(dir.File("README.md"),"fixture");
        var turns = 0;
        var loop = new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>Task.FromResult(++turns == 1
            ? ("",new List<ToolCall>{new("read_file",new(){["path"]="README.md"})})
            : ("Đã đánh giá dựa trên tệp đã đọc; không thay đổi mã.",new List<ToolCall>())),(_,_)=>Task.FromResult(true),_=>{});
        Assert.True(await loop.RunAsync("kiểm tra độ hoàn thiện của project")); Assert.Equal(2,turns);
    }

    [Fact]
    public async Task ReadbackFailureReleasesAccountWithoutCooldownOrAutomaticRetry()
    {
        using var dir = new TempDirectory(); var pool = new AccountPool(dir.File("accounts.json"),acquireTimeout:.2);
        var account = pool.Upsert(new Account { Id="a" }); var driver = new ReadbackDriver(); var gateway=new Gateway(pool,driver);
        var request=new ChatRequest("test","muse-chat",new(),"chat",new(),default,true);
        await Assert.ThrowsAsync<UpstreamException>(async()=>{await foreach(var _ in gateway.ChatStream(request)){} });
        Assert.Equal(1,driver.Calls); Assert.Equal(0,pool.Stats()["inflight"]);
        Assert.Equal(AccountStatus.Active,account.Status); Assert.Equal(0,account.CooldownUntil);
        using var next=await pool.AcquireAsync(Array.Empty<string>(),"chat"); Assert.Equal("a",next.Account.Id);
    }

    [Fact]
    public void GenuineRateLimitStillCoolsDownAccount()
    {
        using var dir=new TempDirectory();var pool=new AccountPool(dir.File("accounts.json"));var account=pool.Upsert(new Account{Id="a"});
        using(var lease=pool.Acquire(Array.Empty<string>(),"chat"))lease.Fail(UpstreamException.Quota("HTTP 429 rate limit"));
        Assert.Equal(AccountStatus.Cooling,account.Status); Assert.True(account.CooldownUntil>DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    sealed class ReadbackDriver : IMuseDriver
    {
        public int Calls; public string Name=>"fixture"; public bool RequiresAccount=>true; public DriverCapabilities Capabilities=>new(Chat:true);
        public async IAsyncEnumerable<string> ChatStream(Account account,ChatRequest request)
        { Calls++;await Task.Yield();if(Calls>0)throw UpstreamException.ResponseUnavailable("DOM response could not be identified");yield break; }
    }
}
