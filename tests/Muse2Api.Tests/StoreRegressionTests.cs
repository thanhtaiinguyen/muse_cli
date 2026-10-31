using System.Text.Json;
using System.Text.Json.Nodes;

namespace Muse2Api.Tests;

public class StoreRegressionTests
{
    private static async Task<BgTask> Finished(TaskManager manager, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.Get(id) is not { Finished: true }) await Task.Delay(10, timeout.Token);
        return manager.Get(id)!;
    }

    [Fact]
    public async Task Tasks_PreserveIdTimestampAndResultAcrossRestart()
    {
        using var dir = new TempDirectory();
        var manager = new TaskManager(dir.File("tasks.json"));
        var submitted = manager.Submit("image", new() { ["model"] = "muse-image" },
            (_, _) => Task.FromResult(new Dictionary<string, JsonNode?> { ["url"] = "test.png" }));
        await Finished(manager, submitted.Id);
        await manager.StopAsync();
        var reloaded = new TaskManager(dir.File("tasks.json"));
        reloaded.Load();
        var task = reloaded.Get(submitted.Id);
        Assert.NotNull(task);
        Assert.Equal(submitted.CreatedAt, task.CreatedAt);
        Assert.Equal("succeeded", task.Status);
        Assert.Equal("test.png", task.Result!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Tasks_MarkInterruptedTaskFailedAndPersistIt()
    {
        using var dir = new TempDirectory();
        var task = new BgTask { Status = "running", Kind = "video", CreatedAt = 123 };
        File.WriteAllText(dir.File("tasks.json"), JsonSerializer.Serialize(new[] { task }));
        var manager = new TaskManager(dir.File("tasks.json"));
        manager.Load();
        Assert.Equal("failed", manager.Get(task.Id)!.Status);
        Assert.Equal("interrupted", manager.Get(task.Id)!.Error!["code"]!.GetValue<string>());
        Assert.Contains("interrupted", File.ReadAllText(dir.File("tasks.json")));
    }

    [Fact]
    public async Task Tasks_CancelReachesRunnerAndNeverBecomesSucceeded()
    {
        using var dir = new TempDirectory();
        var manager = new TaskManager(dir.File("tasks.json"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = manager.Submit("video", new(), async (_, cancel) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancel);
            return new Dictionary<string, JsonNode?>();
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(manager.Cancel(submitted.Id));
        Assert.Equal("cancelled", (await Finished(manager, submitted.Id)).Status);
        await manager.StopAsync();
        Assert.False(manager.Cancel(submitted.Id));
    }

    [Fact]
    public async Task Tasks_ConcurrentCompletionProducesValidStore()
    {
        using var dir = new TempDirectory();
        var manager = new TaskManager(dir.File("tasks.json"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 25).Select(_ => manager.Submit("image", new(), async (progress, cancel) =>
        {
            await gate.Task.WaitAsync(cancel);
            progress(50);
            return new Dictionary<string, JsonNode?> { ["ok"] = true };
        })).ToList();
        gate.SetResult();
        foreach (var task in tasks) await Finished(manager, task.Id);
        await manager.StopAsync();
        var reload = new TaskManager(dir.File("tasks.json"));
        reload.Load();
        Assert.Equal(25, reload.List().Count);
        Assert.All(reload.List(), t => Assert.Equal("succeeded", t.Status));
    }

    [Fact]
    public void AccountPool_ProtectsCredentialsAndMigratesLegacyPlaintext()
    {
        using var dir = new TempDirectory();
        var path = dir.File("accounts.json");
        const string originalCookie = "sensitive-cookie-test-fixture";
        var legacy = new List<Account> { new() { Id = "a", Cookies = new() { ["hatch_sess"] = originalCookie } } };
        File.WriteAllText(path, JsonSerializer.Serialize(legacy));
        var pool = new AccountPool(path);
        pool.Load();
        Assert.Equal(originalCookie, pool.Get("a")!.Cookies["hatch_sess"]);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain(originalCookie, File.ReadAllText(path));
            Assert.True(PrivateDataProtection.IsEncrypted(File.ReadAllText(path)));
        }
        var again = new AccountPool(path);
        again.Load();
        Assert.Equal(originalCookie, again.Get("a")!.Cookies["hatch_sess"]);
    }

    [Fact]
    public async Task AccountPool_WaitsForLeaseAndHonorsCancellation()
    {
        using var dir = new TempDirectory();
        var pool = new AccountPool(dir.File("accounts.json"), acquireTimeout: 2);
        pool.Upsert(new Account { Id = "a" });
        using var lease = pool.Acquire(Array.Empty<string>(), null);
        var pending = pool.AcquireAsync(Array.Empty<string>(), null);
        Assert.False(pending.IsCompleted);
        lease.Dispose();
        using var next = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("a", next.Account.Id);
        using var cancel = new CancellationTokenSource();
        var blocked = pool.AcquireAsync(Array.Empty<string>(), null, cancel: cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }

    [Fact]
    public async Task AccountPool_HonorsKindBlockAndTimeout()
    {
        using var dir = new TempDirectory();
        var pool = new AccountPool(dir.File("accounts.json"), acquireTimeout: 0.02);
        var account = new Account { Id = "a" };
        pool.NoteWake(account, new() { ["vm_wake_ok"] = "false" });
        pool.Upsert(account);
        Assert.Throws<MuseException>(() => pool.Acquire(Array.Empty<string>(), null, "video"));
        var error = await Assert.ThrowsAsync<MuseException>(() => pool.AcquireAsync(Array.Empty<string>(), null, "video"));
        Assert.Equal(503, error.StatusCode);
        using var chat = pool.Acquire(Array.Empty<string>(), null, "chat");
        Assert.Equal("a", chat.Account.Id);
    }

    [Fact]
    public void KeyStore_ConcurrentCreatesAndTouchesSurviveReload()
    {
        using var dir = new TempDirectory();
        var keys = new KeyStore(dir.File("keys.json"));
        Parallel.For(0, 20, i => { var (key, _) = keys.Create($"key-{i}", ""); keys.Touch(key); });
        keys.Flush();
        var reload = new KeyStore(dir.File("keys.json"));
        reload.Load();
        Assert.Equal(20, reload.All().Count);
        Assert.All(reload.All(), key => Assert.True(key.LastUsedAt > 0));
    }

    [Theory]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/gif", ".gif")]
    [InlineData("video/webm", ".webm")]
    [InlineData("video/quicktime", ".mov")]
    public void MediaStore_ReturnsMatchingMime(string mime, string extension)
    {
        using var dir = new TempDirectory();
        var store = new MediaStore(dir.Root);
        var name = store.Save(new byte[] { 1 }, mime);
        Assert.EndsWith(extension, name);
        Assert.Equal(mime, MediaStore.MimeOf(name));
    }

    [Fact]
    public void RequestLog_SupportsQuotedDatabasePathAndLiteralPathFilter()
    {
        using var dir = new TempDirectory();
        using var log = new RequestLog(dir.File("requests;test.db"));
        log.Add(new RequestRecord(1000, "POST", "/v1/test_%", "muse-chat", "key", "name", "acc", 400, 12, true, false, "invalid", "local", null));
        var (rows, total) = log.Query(null, null, "failed", "/v1/test_%", null, true, 100, 0);
        Assert.Equal(1, total);
        Assert.True(Assert.Single(rows).Stream);
        Assert.Equal(0, log.Query(null, null, null, "/v1/nope", null, false, 100, 0).Total);
    }

    [Fact]
    public void RequestLog_DistinguishesExactStatusFromStatusClassAndDisablesRetentionAtZero()
    {
        using var log = new RequestLog();
        foreach (var status in new[] { 400, 404, 500 })
            log.Add(new RequestRecord(1000, "GET", "/v1/test", null, null, null, null, status, 1, false, false, null, null, null));
        Assert.Equal(1, log.Query(null, null, "404", null, null, false, 100, 0).Total);
        Assert.Equal(2, log.Query(null, null, "4xx", null, null, false, 100, 0).Total);
        Assert.Equal(0, log.Prune(0));
        Assert.Equal(3, log.Query(null, null, null, null, null, false, 100, 0).Total);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("keys")]
    [InlineData("tasks")]
    public void Stores_RejectCorruptJsonWithoutOverwritingExistingFile(string kind)
    {
        using var dir = new TempDirectory();
        var file = dir.File(kind + ".json");
        File.WriteAllText(file, "{broken");
        Action load = kind switch
        {
            "accounts" => new AccountPool(file).Load,
            "keys" => new KeyStore(file).Load,
            _ => new TaskManager(file).Load,
        };
        Assert.Throws<InvalidOperationException>(load);
        Assert.Equal("{broken", File.ReadAllText(file));
    }
}
