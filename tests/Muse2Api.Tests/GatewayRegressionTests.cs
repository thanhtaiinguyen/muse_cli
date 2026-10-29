namespace Muse2Api.Tests;

public class GatewayRegressionTests
{
    private static ChatRequest Request(CancellationToken cancel = default)
        => new("test", "muse-chat", new(), null, new(), cancel);

    [Fact]
    public async Task Failover_RecordsActualServingAccountAndInvalidatesExpiredOne()
    {
        using var dir = new TempDirectory();
        var pool = new AccountPool(dir.File("accounts.json"), "lru");
        pool.Upsert(new Account { Id = "a", LastUsedAt = 0 });
        pool.Upsert(new Account { Id = "b", LastUsedAt = 1 });
        var driver = new TestDriver { RequiresAccount = true, FailFirstAccount = true };
        var gateway = new Gateway(pool, driver);
        string? serving = null;
        var output = "";
        await foreach (var delta in gateway.ChatStream(Request(), id => serving = id)) output += delta;
        Assert.Equal("hello world", output);
        Assert.Equal("b", serving);
        Assert.Equal(AccountStatus.Invalid, pool.Get("a")!.Status);
        Assert.Equal(1, pool.Get("b")!.SuccessCount);
        Assert.Equal(0, pool.Stats()["inflight"]);
    }

    [Fact]
    public async Task Failover_DoesNotRetryAfterEmittingOutput()
    {
        using var dir = new TempDirectory();
        var pool = new AccountPool(dir.File("accounts.json"));
        pool.Upsert(new Account { Id = "a" });
        pool.Upsert(new Account { Id = "b" });
        var driver = new TestDriver { RequiresAccount = true, FailAfterOutput = true };
        var gateway = new Gateway(pool, driver);
        var output = "";
        await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var delta in gateway.ChatStream(Request())) output += delta;
        });
        Assert.Equal("hello", output);
        Assert.Equal(1, driver.Calls);
        Assert.Equal(0, pool.Stats()["inflight"]);
    }

    [Fact]
    public async Task Cancellation_ReleasesAccountWithoutPenalizingIt()
    {
        using var dir = new TempDirectory();
        var pool = new AccountPool(dir.File("accounts.json"));
        pool.Upsert(new Account { Id = "a" });
        var gateway = new Gateway(pool, new TestDriver { RequiresAccount = true, FirstDelayMs = 10000 });
        using var cancel = new CancellationTokenSource();
        await using var stream = gateway.ChatStream(Request(cancel.Token)).GetAsyncEnumerator();
        var pending = stream.MoveNextAsync().AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, pool.Stats()["inflight"]);
        Assert.Equal(0, pool.Get("a")!.SuccessCount);
        Assert.Equal(0, pool.Get("a")!.FailCount);
        Assert.Equal(AccountStatus.Active, pool.Get("a")!.Status);
    }

    [Fact]
    public async Task EarlyConsumerDisposal_ReleasesLeaseWithoutBackgroundProducer()
    {
        using var dir = new TempDirectory();
        var pool = new AccountPool(dir.File("accounts.json"));
        pool.Upsert(new Account { Id = "a" });
        var gateway = new Gateway(pool, new TestDriver { RequiresAccount = true });
        await using (var stream = gateway.ChatStream(Request()).GetAsyncEnumerator()) Assert.True(await stream.MoveNextAsync());
        Assert.Equal(0, pool.Stats()["inflight"]);
    }
}
