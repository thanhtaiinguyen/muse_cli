namespace Muse2Api;

/// <summary>Background loops, ported from Python <c>app.py</c> lifespan +
/// <c>accounts/keepalive.py</c>.
/// <para>
/// Keepalive: when <c>MUSE2API_KEEPALIVE_ENABLED=1</c> and the driver can
/// renew sessions, periodically renew every enabled, non-invalid account
/// and merge refreshed cookies back into the pool (same merge as the
/// /admin/accounts/{id}/renew endpoint). A driver that cannot renew is
/// skipped by the capability check, like Python.
/// </para>
/// <para>
/// Housekeeping: every 60s flush pending key "last used" times; every
/// hour prune old request rows (retention days) and old media files
/// (media retention days). Both loops stop on cancellation and never
/// throw out of the loop — a failed pass is logged and retried next time.
/// </para>
/// </summary>
public static class BackgroundServices
{
    public static async Task KeepaliveLoopAsync(AppState s, CancellationToken token)
    {
        if (!s.Settings.KeepaliveEnabled || !s.Driver.Capabilities.RenewSession) return;
        var interval = TimeSpan.FromSeconds(Math.Max(1, s.Settings.KeepaliveInterval));
        Console.WriteLine($"keepalive loop started (interval={s.Settings.KeepaliveInterval}s)");
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(interval, token); }
            catch (OperationCanceledException) { break; }
            foreach (var acc in s.Pool.All())
            {
                if (token.IsCancellationRequested) break;
                if (!acc.Enabled || acc.Status == AccountStatus.Invalid) continue;
                try
                {
                    var info = await s.Driver.RenewSession(acc);
                    foreach (var (k, v) in info.Cookies) acc.Cookies[k] = v;
                    foreach (var (k, v) in info.CookieExpires) acc.CookieExpires[k] = v;
                    foreach (var (k, v) in info.Meta)
                        acc.Meta[k] = v is null ? null : System.Text.Json.Nodes.JsonValue.Create(v);
                    acc.Meta["renewed_at"] = System.Text.Json.Nodes.JsonValue.Create(
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    s.Pool.NoteWake(acc, info.Meta);
                    s.Pool.Upsert(acc);
                    Console.WriteLine($"keepalive {acc.Id} -> {info.Ok}");
                }
                catch (UpstreamException ex) when (ex.Code == "upstream_auth_failed")
                {
                    acc.Status = AccountStatus.Invalid;
                    acc.LastError = ex.Message;
                    s.Pool.Upsert(acc);
                    Console.WriteLine($"keepalive {acc.Id} -> invalid: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"keepalive failed for {acc.Id}: {ex.Message}");
                }
            }
        }
    }

    public static async Task HousekeepingLoopAsync(AppState s, CancellationToken token)
    {
        var lastPrune = DateTime.UtcNow;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), token); }
            catch (OperationCanceledException) { break; }
            try
            {
                s.Keys.Flush();
                if (DateTime.UtcNow - lastPrune >= TimeSpan.FromHours(1))
                {
                    lastPrune = DateTime.UtcNow;
                    var pruned = s.Requests.Prune(s.Settings.RequestLogRetentionDays);
                    if (pruned > 0) Console.WriteLine($"request log cleanup: removed {pruned} row(s)");
                    var removed = s.Media.Prune(s.Settings.MediaRetentionDays * 86400);
                    if (removed > 0) Console.WriteLine($"media cleanup: removed {removed} expired file(s)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"housekeeping failed: {ex.Message}");
            }
        }
    }
}
