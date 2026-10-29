using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Muse2Api.Tests;

public class ApiRegressionTests
{
    private const string ChatBody = """{"model":"muse-chat","messages":[{"role":"user","content":"hello"}]}""";

    [Fact]
    public async Task Auth_SeparatesApiAndAdminAndRevokesCreatedKey()
    {
        await using var host = await GatewayHost.Start();
        host.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/v1/models")).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-api");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/v1/models")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/admin/status")).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        using var created = await host.Post("/admin/keys", """{"name":"integration"}""");
        created.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var plaintext = body["api_key"]!.GetValue<string>();
        var id = body["key"]!["id"]!.GetValue<string>();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);
        Assert.Equal(HttpStatusCode.OK, (await host.Post("/v1/chat/completions", ChatBody)).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        using var revoked = await host.Client.PatchAsync($"/admin/keys/{id}", new StringContent("""{"revoked":true}""", Encoding.UTF8, "application/json"));
        revoked.EnsureSuccessStatusCode();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Post("/v1/chat/completions", ChatBody)).StatusCode);
    }

    [Fact]
    public async Task Chat_AcceptsChunkedJsonWithoutContentLength()
    {
        await using var host = await GatewayHost.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new ChunkedContent(ChatBody) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await host.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("You said: hello", json["choices"]![0]!["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AgentClient_CallsGatewayAndReportsAuthenticationFailure()
    {
        await using var host = await GatewayHost.Start();
        var messages = new List<JsonObject> { new() { ["role"] = "user", ["content"] = "hello" } };
        using var client = new Muse2Api.Agent.AgentClient(host.Client.BaseAddress!.ToString(), "muse-chat", "test-api");
        var (text, calls) = await client.ChatAsync(messages);
        Assert.Contains("You said: hello", text);
        Assert.Empty(calls);
        using var invalid = new Muse2Api.Agent.AgentClient(host.Client.BaseAddress!.ToString(), "muse-chat", "wrong");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.ChatAsync(messages));
        Assert.Contains("HTTP 401", error.Message);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"messages":[{"role":"","content":"hello"}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":3}]}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":{}}]}""")]
    public async Task Chat_InvalidInputReturns400WithOpenAiError(string body)
    {
        await using var host = await GatewayHost.Start();
        using var response = await host.Post("/v1/chat/completions", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request_error", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"prompt":""}""")]
    [InlineData("""{"prompt":"test","n":"two"}""")]
    [InlineData("""{"prompt":"test","n":0}""")]
    [InlineData("""{"prompt":"test","response_format":"bad"}""")]
    public async Task Images_RejectInvalidParameters(string body)
    {
        await using var host = await GatewayHost.Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post("/v1/images/generations", body)).StatusCode);
    }

    [Fact]
    public async Task Streaming_ChatAndResponsesFinishAndLogStreamFlag()
    {
        await using var host = await GatewayHost.Start();
        using var chat = await host.Post("/v1/chat/completions", """{"messages":[{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":true}}""");
        var text = await chat.Content.ReadAsStringAsync();
        Assert.Contains("data: [DONE]", text);
        Assert.Contains("\"finish_reason\":\"stop\"", text);
        Assert.Contains("\"usage\"", text);
        using var response = await host.Post("/v1/responses", """{"input":"hi","stream":true}""");
        var events = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: response.output_text.done", events);
        Assert.Contains("event: response.content_part.done", events);
        Assert.Contains("event: response.completed", events);
        var payloads = events.Split('\n').Where(line => line.StartsWith("data: ")).Select(line => JsonNode.Parse(line[6..])!).ToList();
        Assert.Equal(Enumerable.Range(0, payloads.Count), payloads.Select(p => p["sequence_number"]!.GetValue<int>()));
        var rows = host.State.Requests.Query(null, null, null, null, null, false, 100, 0).Rows;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.True(r.Stream); Assert.Equal("anonymous", r.AccountId); });
        Assert.False(File.Exists(host.State.Settings.AccountsFile));
    }

    [Fact]
    public async Task Streaming_ReportsMidStreamFailureAndLogsError()
    {
        await using var host = await GatewayHost.Start(new TestDriver { FailAfterOutput = true });
        using var response = await host.Post("/v1/chat/completions", """{"messages":[{"content":"hi"}],"stream":true}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadAsStringAsync();
        Assert.Contains("hello", events);
        Assert.Contains("upstream_timeout", events);
        Assert.Contains("data: [DONE]", events);
        Assert.DoesNotContain("\"finish_reason\":\"stop\"", events);
        var row = Assert.Single(host.State.Requests.Query(null, null, null, null, null, false, 100, 0).Rows);
        Assert.Equal(504, row.StatusCode);
        Assert.Equal("upstream_timeout", row.Error);
    }

    [Fact]
    public async Task Streaming_HeartbeatKeepsPendingFirstTokenAlive()
    {
        await using var host = await GatewayHost.Start(new TestDriver { FirstDelayMs = 100 }, heartbeat: 0.01);
        using var response = await host.Post("/v1/chat/completions", """{"messages":[{"content":"hi"}],"stream":true}""");
        var events = await response.Content.ReadAsStringAsync();
        Assert.Contains(": keepalive", events);
        Assert.Contains("data: [DONE]", events);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "\"messages\":[{\"content\":\"hi\"}]", "data: [DONE]")]
    [InlineData("/v1/responses", "\"input\":\"hi\"", "event: response.failed")]
    public async Task Streaming_HeartbeatReportsFailureBeforeFirstToken(string path, string input, string terminal)
    {
        // Release the failure only after a heartbeat has committed the headers.
        // A fixed 100ms delay raced browser startup under parallel test load.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await GatewayHost.Start(new TestDriver { BeforeOutput = release.Task, FailBeforeOutput = true }, heartbeat: 0.01);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent("{" + input + ",\"stream\":true}", Encoding.UTF8, "application/json") };
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadAsStringAsync();
        Assert.Contains(": keepalive", events);
        Assert.Contains("upstream_timeout", events);
        Assert.Contains(terminal, events);
    }

    [Fact]
    public async Task Streaming_ResponsesReportsFailureAfterOutput()
    {
        await using var host = await GatewayHost.Start(new TestDriver { FailAfterOutput = true });
        using var response = await host.Post("/v1/responses", """{"input":"test","stream":true}""");
        var events = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: response.failed", events);
        Assert.DoesNotContain("event: response.completed", events);
    }

    [Fact]
    public async Task Renew_RestoresSuccessfullyRenewedInvalidAccount()
    {
        await using var host = await GatewayHost.Start();
        host.State.Pool.Upsert(new Account { Id = "test", Status = AccountStatus.Invalid, LastError = "expired" });
        using var response = await host.Post("/admin/accounts/test/renew", "{}");
        response.EnsureSuccessStatusCode();
        Assert.Equal(AccountStatus.Active, host.State.Pool.Get("test")!.Status);
        Assert.Null(host.State.Pool.Get("test")!.LastError);
    }

    [Fact]
    public async Task Streaming_FirstFailureRetainsHttpErrorBeforeHeadersStart()
    {
        await using var host = await GatewayHost.Start(new TestDriver { RequiresAccount = true });
        using var response = await host.Post("/v1/chat/completions", """{"messages":[{"content":"hi"}],"stream":true}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Ready_Returns503WhenDriverIsUnhealthy()
    {
        await using var host = await GatewayHost.Start(new TestDriver { Healthy = false });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Client.GetAsync("/readyz")).StatusCode);
    }

    [Fact]
    public async Task ImagesAndVideos_CreatePollAndServeMedia()
    {
        await using var host = await GatewayHost.Start();
        using var response = await host.Post("/v1/images/generations", """{"prompt":"test","async":true}""");
        var task = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var id = task["id"]!.GetValue<string>();
        var image = await Poll(host, $"/v1/images/generations/{id}");
        var url = image["result"]!["data"]![0]!["url"]!.GetValue<string>();
        using var media = await host.Client.GetAsync(url);
        Assert.Equal("image/png", media.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, (await media.Content.ReadAsByteArrayAsync())[..4]);
        using var video = await host.Post("/v1/videos", """{"prompt":"test","duration":3}""");
        var videoId = JsonNode.Parse(await video.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        Assert.Equal("succeeded", (await Poll(host, $"/v1/videos/{videoId}"))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AccountCookieExpiry_Preserves64BitTimestamp()
    {
        await using var host = await GatewayHost.Start();
        using var response = await host.Post("/admin/accounts", """{"cookies":{"hatch_sess":"secret"},"cookie_expires":{"hatch_sess":4102444800}}""");
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(4102444800L, body["account"]!["cookie_expires"]!["hatch_sess"]!.GetValue<long>());
        Assert.DoesNotContain("secret", body["account"]!["cookies"]!.ToJsonString());
    }

    [Fact]
    public async Task ImageEdits_RejectsUploadedMask()
    {
        await using var host = await GatewayHost.Start();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("test"), "prompt");
        form.Add(new ByteArrayContent(new byte[] { 1 }), "image", "test.png");
        form.Add(new ByteArrayContent(new byte[] { 1 }), "mask", "mask.png");
        using var response = await host.Client.PostAsync("/v1/images/edits", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<JsonNode> Poll(GatewayHost host, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            using var response = await host.Client.GetAsync(path, timeout.Token);
            response.EnsureSuccessStatusCode();
            var task = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))!;
            if (task["status"]!.GetValue<string>() == "succeeded") return task;
            Assert.DoesNotContain(task["status"]!.GetValue<string>(), new[] { "failed", "cancelled" });
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ChunkedContent(string json) : HttpContent
    {
        private readonly byte[] _data = Encoding.UTF8.GetBytes(json);
        public ChunkedContent() : this(ChatBody) { }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await stream.WriteAsync(_data);
        // A non-seekable content forces HTTP/1.1 chunked transfer.
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(_data));
    }
}
