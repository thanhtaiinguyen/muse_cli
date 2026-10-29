using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Text;

namespace Muse2Api.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "muse2api-tests", Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Root);
    public string File(string name) => Path.Combine(Root, name);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal sealed class GatewayHost : IAsyncDisposable
{
    private readonly TempDirectory _dir;
    public WebApplication App { get; }
    public AppState State => App.Services.GetRequiredService<AppState>();
    public HttpClient Client { get; }

    private GatewayHost(WebApplication app, TempDirectory dir)
    {
        App = app;
        _dir = dir;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(address) };
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
    }

    public static async Task<GatewayHost> Start(IMuseDriver? driver = null, double heartbeat = 0)
    {
        var dir = new TempDirectory();
        var app = GatewayApplication.Create(new[] { "--Logging:LogLevel:Default=None" }, new Settings
        {
            DataDir = dir.Root,
            Host = "127.0.0.1",
            Port = 18610,
            Driver = "mock",
            ApiKey = "test-api",
            AdminKey = "test-admin",
            KeepaliveEnabled = false,
            StreamHeartbeatInterval = heartbeat,
            PoolAcquireTimeout = 0.5,
        }, driver ?? new MockDriver(0));
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        return new GatewayHost(app, dir);
    }

    public Task<HttpResponseMessage> Post(string path, string json)
        => Client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        _dir.Dispose();
    }
}

internal sealed class TestDriver : IMuseDriver
{
    public string Name => "test";
    public bool RequiresAccount { get; init; }
    public DriverCapabilities Capabilities => new(Chat: true);
    public bool FailFirstAccount { get; init; }
    public bool FailAfterOutput { get; init; }
    public bool FailBeforeOutput { get; init; }
    public int FirstDelayMs { get; init; }
    public Task? BeforeOutput { get; init; }
    public int Calls { get; private set; }
    public bool Healthy { get; init; } = true;
    public List<string> Accounts { get; } = new();

    public async IAsyncEnumerable<string> ChatStream(Account account, ChatRequest req)
    {
        Calls++;
        Accounts.Add(account.Id);
        await Task.Delay(FirstDelayMs, req.Cancel);
        if (BeforeOutput is not null) await BeforeOutput.WaitAsync(req.Cancel);
        if (FailBeforeOutput) throw UpstreamException.Timeout("no first token");
        if (FailFirstAccount && account.Id == "a") throw UpstreamException.Auth("expired");
        yield return "hello";
        if (FailAfterOutput) throw UpstreamException.Timeout("interrupted");
        yield return " world";
    }

    public Task<Dictionary<string, object?>> Health()
        => Task.FromResult(new Dictionary<string, object?> { ["ok"] = Healthy });
}
