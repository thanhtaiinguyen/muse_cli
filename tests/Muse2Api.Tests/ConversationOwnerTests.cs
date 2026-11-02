namespace Muse2Api.Tests;

public class ConversationOwnerTests
{
    private static ChatRequest Request => new("continue","muse-chat",new(),"saved-chat",new(),default,true);
    [Theory]
    [InlineData("affinity")] [InlineData("lru")] [InlineData("round_robin")]
    public async Task RestartChoosesTheSavedOwnerInsteadOfTheLeastRecentlyUsedAccount(string strategy)
    {
        using var dir=new TempDirectory();var store=new LocalConversationStore(dir.Root);
        var pool=new AccountPool(dir.File("accounts.json"),strategy);pool.Upsert(new(){Id="original",LastUsedAt=100});pool.Upsert(new(){Id="other",LastUsedAt=0});
        store.Save("original","saved-chat","https://muse.ai/thread/owner",false);
        pool=new AccountPool(dir.File("accounts.json"),strategy);pool.Load();
        var driver=new OwnerDriver(new LocalConversationStore(dir.Root));var gateway=new Gateway(pool,driver);
        await foreach(var _ in gateway.ChatStream(Request)){}
        Assert.Equal(new[]{"original"},driver.Used);
        Assert.Equal(0,pool.Get("other")!.SuccessCount);
    }
    [Theory]
    [InlineData("missing")] [InlineData("disabled")] [InlineData("busy")]
    public async Task UnavailableOwnerNeverFallsThroughToAnotherAccount(string state)
    {
        using var dir=new TempDirectory();var store=new LocalConversationStore(dir.Root);
        var pool=new AccountPool(dir.File("accounts.json"),acquireTimeout:.15);pool.Upsert(new(){Id="other"});
        if(state!="missing")pool.Upsert(new(){Id="original",Enabled=state!="disabled"});
        store.Save("original","saved-chat","https://muse.ai/thread/owner",false);
        using var lease=state=="busy"?pool.Acquire(new[]{"other"},"hold","chat"):null;
        var driver=new OwnerDriver(store);var gateway=new Gateway(pool,driver);
        await Assert.ThrowsAnyAsync<MuseException>(async()=>{await foreach(var _ in gateway.ChatStream(Request)){} });
        Assert.Empty(driver.Used);Assert.Equal(0,pool.Get("other")!.FailCount);
        Assert.Equal("https://muse.ai/thread/owner",store.Load("original","saved-chat")!.Url);
    }
    private sealed class OwnerDriver(LocalConversationStore store):IMuseDriver,ILocalConversationOwner
    {
        public string Name=>"owner-fixture";public bool RequiresAccount=>true;public DriverCapabilities Capabilities=>new(Chat:true);
        public List<string> Used {get;}=new();
        public string? ResolveConversationOwner(string hint,IReadOnlyList<Account> accounts)=>store.ResolveAccount(hint,accounts);
        public async IAsyncEnumerable<string> ChatStream(Account account,ChatRequest request)
        {Used.Add(account.Id);store.Load(account.Id,request.ConversationHint!);await Task.Yield();yield return "owner confirmed";}
    }
}
