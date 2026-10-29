using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Muse2Api;

/// <summary>Hosts the desktop's own gateway on a random loopback port in the same process.</summary>
public sealed class DesktopGatewayHost(Settings settings, IMuseDriver? driver = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    public string? BaseUrl { get; private set; }
    public string ApiKey => settings.ApiKey;
    public string AdminKey => settings.EffectiveAdminKey;
    public AppState State => _app?.Services.GetRequiredService<AppState>() ?? throw new InvalidOperationException("Desktop host has not started");

    public async Task StartAsync(CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            if (_app is not null) return;
            if (settings.Host != "127.0.0.1") throw new InvalidOperationException("Desktop host must bind to loopback");
            settings.ResolveApiKey();
            var app = GatewayApplication.Create(new[] { "--Logging:LogLevel:Default=Warning" }, settings, driver);
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            try
            {
                await app.StartAsync(cancel);
                BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
                _app = app;
            }
            catch
            {
                var state = app.Services.GetRequiredService<AppState>();
                state.Requests.Dispose();
                await app.DisposeAsync();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is null) return;
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
            BaseUrl = null;
        }
        finally { _gate.Release(); }
    }
}
