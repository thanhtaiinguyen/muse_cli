namespace Muse2Api;

/// <summary>Shared host setup for the executable and real HTTP integration tests.</summary>
public static class GatewayApplication
{
    public static WebApplication Create(string[] args, Settings? settings = null, IMuseDriver? driver = null)
    {
        settings ??= new Settings();
        settings.Validate();
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://{settings.Host}:{settings.Port}");
        builder.Services.AddSingleton(_ => new AppState(settings, driver));
        builder.Services.AddHostedService<GatewayLifetime>();
        var app = builder.Build();
        var state = app.Services.GetRequiredService<AppState>();

        app.Use(async (ctx, next) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await next();
            if (!ctx.Request.Path.StartsWithSegments("/v1")) return;
            var status = ctx.Items["error_status"] is int errorStatus ? errorStatus : ctx.Response.StatusCode;
            state.Requests.Add(new RequestRecord(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ctx.Request.Method, ctx.Request.Path.ToString(),
                ctx.Items["model"]?.ToString(), ctx.Items["key_id"]?.ToString(), ctx.Items["key_name"]?.ToString(),
                ctx.Items["account_id"]?.ToString(), status, sw.ElapsedMilliseconds,
                Stream: ctx.Items["stream"] is true,
                Poll: ctx.Request.Method == "GET" &&
                    (ctx.Request.Path.StartsWithSegments("/v1/videos") || ctx.Request.Path.StartsWithSegments("/v1/images/generations")),
                Error: ctx.Items["error_code"]?.ToString() ?? (status >= 400 ? $"HTTP {status}" : null),
                ClientIp: ctx.Connection.RemoteIpAddress?.ToString(), TaskId: ctx.Items["task_id"]?.ToString()));
        });
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                ctx.Items["error_status"] = 499;
                ctx.Items["error_code"] = "client_disconnected";
            }
            catch (Exception ex)
            {
                var error = ex switch
                {
                    MuseException muse => muse,
                    BadHttpRequestException bad => new MuseException(bad.Message, bad.StatusCode, "invalid_request_error", "invalid_request"),
                    _ => new MuseException("internal error"),
                };
                ctx.Items["error_status"] = error.StatusCode;
                ctx.Items["error_code"] = error.Code;
                if (error.StatusCode >= 500) app.Logger.LogError(ex, "Request failed: {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.Clear();
                    ctx.Response.StatusCode = error.StatusCode;
                    await ctx.Response.WriteAsJsonAsync(error.ToBody(), ctx.RequestAborted);
                }
                else if (ctx.Response.ContentType?.StartsWith("text/event-stream") is true)
                {
                    var payload = ctx.Items["response_failure"] is Func<MuseException, System.Text.Json.Nodes.JsonObject> failure
                        ? "event: response.failed\ndata: " + failure(error).ToJsonString() + "\n\n"
                        : "data: " + System.Text.Json.JsonSerializer.Serialize(error.ToBody()) + "\n\ndata: [DONE]\n\n";
                    await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                else ctx.Abort();
            }
        });
        Endpoints.Map(app, state);
        return app;
    }

    private sealed class GatewayLifetime(AppState state) : BackgroundService
    {
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            try { await state.Driver.StartupAsync(); }
            catch { await state.Driver.ShutdownAsync(); throw; }
            await base.StartAsync(cancellationToken);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
            => Task.WhenAll(BackgroundServices.KeepaliveLoopAsync(state, stoppingToken),
                BackgroundServices.HousekeepingLoopAsync(state, stoppingToken));

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            await state.Tasks.StopAsync();
            await state.Driver.ShutdownAsync();
            state.Keys.Flush();
            state.Requests.Dispose();
        }
    }
}
