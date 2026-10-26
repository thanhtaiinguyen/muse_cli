using Muse2Api;

var settings = new Settings();
var state = new AppState(settings);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{settings.Host}:{settings.Port}");
var app = builder.Build();

// Request-log middleware for /v1/*, mirroring Python's RequestLogMiddleware.
// Auth and model/task/account are recorded by the endpoint handlers into
// HttpContext.Items / Gateway.CurrentAccountId before the response finishes.
app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var isApi = ctx.Request.Path.StartsWithSegments("/v1");
    await next();
    if (!isApi) return;
    var status = ctx.Response.StatusCode;
    var account = ctx.Items["account_id"]?.ToString() ?? Gateway.CurrentAccountId.Value;
    if (string.IsNullOrEmpty(account) && !state.Driver.RequiresAccount
        && state.Pool.All().Count == 0)
        account = "anonymous";
    state.Requests.Add(new RequestRecord(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ctx.Request.Method,
        ctx.Request.Path.ToString(),
        ctx.Items["model"]?.ToString(),
        ctx.Items["key_id"]?.ToString(),
        ctx.Items["key_name"]?.ToString(),
        account,
        status,
        sw.ElapsedMilliseconds,
        Stream: false,
        Poll: ctx.Request.Path.ToString().Contains("/generations/task_", StringComparison.Ordinal)
            || ctx.Request.Path.ToString().StartsWith("/v1/videos/", StringComparison.Ordinal)
            || ctx.Request.Path.ToString().StartsWith("/v1/images/generations/", StringComparison.Ordinal),
        Error: status >= 400 ? $"HTTP {status}" : null,
        ClientIp: ctx.Connection.RemoteIpAddress?.ToString(),
        TaskId: ctx.Items["task_id"]?.ToString()));
});

// OpenAI-style error bodies for every MuseException, plus a 500 fallback.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (MuseException ex)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = ex.StatusCode;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(ex.ToBody());
        }
    }
    catch (Exception)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new MuseException("internal error").ToBody());
        }
    }
});

Endpoints.Map(app, state);

// Browser driver needs Chromium before the first request; mock/http are no-ops.
await state.Driver.StartupAsync();
var bgCts = new CancellationTokenSource();
_ = Task.Run(() => BackgroundServices.KeepaliveLoopAsync(state, bgCts.Token));
_ = Task.Run(() => BackgroundServices.HousekeepingLoopAsync(state, bgCts.Token));
app.Lifetime.ApplicationStopping.Register(() =>
{
    bgCts.Cancel();
    state.Driver.ShutdownAsync().GetAwaiter().GetResult();
    state.Requests.Dispose();
});

Console.WriteLine($"muse2api (.NET) listening on http://{settings.Host}:{settings.Port}  (driver={settings.Driver})");
app.Run();
