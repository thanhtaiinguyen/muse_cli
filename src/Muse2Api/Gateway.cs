namespace Muse2Api;

/// <summary>Gateway: runs driver calls on pooled accounts with automatic
/// failover, ported from Python <c>services/gateway.py</c>.</summary>
public sealed class Gateway
{
    /// <summary>Account serving the current request, for the request log.
    /// Mirrors Python's <c>current_record</c> / <c>note_account</c>.</summary>
    public static readonly AsyncLocal<string?> CurrentAccountId = new();

    private readonly AccountPool _pool;
    private readonly IMuseDriver _driver;
    private readonly int _maxFailover;
    private static readonly Account Anonymous = new() { Id = "anonymous", Label = "driver without accounts" };

    public Gateway(AccountPool pool, IMuseDriver driver, int maxFailover = 2)
    {
        _pool = pool; _driver = driver; _maxFailover = maxFailover;
    }

    public (IAsyncEnumerable<string> Stream, string AccountId) ChatStreamWithAccount(ChatRequest req)
    {
        // C# forbids yield inside a try/catch, so failover runs in a background
        // task feeding a channel; callers enumerate the channel instead.
        // The first lease is acquired here, in the request's context, so the
        // request log sees the serving account (AsyncLocal does not flow back
        // from the background task).
        Lease firstLease;
        try { firstLease = LeaseFor(new List<string>(), req.ConversationHint); }
        catch (Exception ex)
        {
            var failed = System.Threading.Channels.Channel.CreateUnbounded<string>();
            failed.Writer.TryComplete(ex);
            return (failed.Reader.ReadAllAsync(), "");
        }
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            var tried = new List<string>();
            var lease = firstLease;
            for (var attempt = 0; attempt <= _maxFailover; attempt++)
            {
                var emitted = false;
                try
                {
                    await foreach (var delta in _driver.ChatStream(lease.Account, req))
                    {
                        emitted = true;
                        await channel.Writer.WriteAsync(delta);
                    }
                    channel.Writer.Complete();
                    lease.Dispose();
                    return;
                }
                catch (UpstreamException ex)
                {
                    lease.Fail(ex);
                    tried.Add(lease.Account.Id);
                    lease.Dispose();
                    if (emitted || !ShouldRetry(ex, attempt, tried))
                    {
                        channel.Writer.TryComplete(ex);
                        return;
                    }
                    try { lease = LeaseFor(tried, req.ConversationHint); }
                    catch (Exception lex) { channel.Writer.TryComplete(lex); return; }
                }
                catch (Exception ex)
                {
                    lease.Dispose();
                    channel.Writer.TryComplete(ex);
                    return;
                }
            }
            channel.Writer.TryComplete(MuseException.NoAccount());
        });
        return (channel.Reader.ReadAllAsync(), firstLease.Account.Id);
    }

    public async Task<(List<MediaResult> Results, string AccountId)> GenerateImage(ImageRequest req)
        => await Run(acc => _driver.GenerateImage(acc, req));

    public async Task<(MediaResult Result, string AccountId)> GenerateVideo(VideoRequest req)
        => await Run(acc => _driver.GenerateVideo(acc, req));

    private async Task<(T Result, string AccountId)> Run<T>(Func<Account, Task<T>> fn, string? hint = null)
    {
        var tried = new List<string>();
        for (var attempt = 0; attempt <= _maxFailover; attempt++)
        {
            using var lease = LeaseFor(tried, hint);
            try { return (await fn(lease.Account), lease.Account.Id); }
            catch (UpstreamException ex)
            {
                lease.Fail(ex);
                tried.Add(lease.Account.Id);
                if (!ShouldRetry(ex, attempt, tried)) throw;
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    private Lease LeaseFor(List<string> tried, string? hint)
    {
        Lease lease;
        if (!_driver.RequiresAccount && _pool.All().Count == 0)
            lease = new Lease(_pool, Anonymous);
        else
            lease = _pool.Acquire(tried, hint);
        CurrentAccountId.Value = lease.Account.Id;
        return lease;
    }

    private bool ShouldRetry(UpstreamException ex, int attempt, List<string> tried)
        => ex.Retryable && attempt < _maxFailover && _pool.HasAlternative(tried);
}
