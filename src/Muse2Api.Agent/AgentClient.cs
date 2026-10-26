using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Muse2Api.Agent;

/// <summary>Minimal OpenAI-compatible chat client, so this agent can talk
/// to the local muse2api gateway (muse.ai via /v1/chat/completions) or any
/// other OpenAI-compatible base URL. No SDK dependency: plain HttpClient
/// + System.Text.Json, so the binary stays small.</summary>
public sealed class AgentClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string? _apiKey;

    public string Model => _model;

    public AgentClient(string baseUrl, string model, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        if (!_baseUrl.EndsWith("/v1", StringComparison.Ordinal)) _baseUrl += "/v1";
        _model = model;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        // A local gateway must never go through an egress proxy: on machines
        // with HTTP_PROXY set (including this dev VM), a proxied loopback
        // request is rejected instead of reaching the local server.
        var isLoopback = Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri)
            && (uri.Host is "localhost" || System.Net.IPAddress.TryParse(uri.Host, out var ip)
                && System.Net.IPAddress.IsLoopback(ip));
        var handler = new HttpClientHandler { UseProxy = !isLoopback };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    public const string SystemPrompt = """
        You are a coding agent running in a terminal, like Codex. You work in a
        workspace directory and you can only change that workspace through the
        tools below. Never claim you read, edited or ran something you did not
        actually do with a tool, and never invent file contents.

        TOOLS — emit one or more fenced blocks, nothing else inside a block:
        ```tool
        {"name": "list_files", "path": ".", "recursive": false}
        ```
        ```tool
        {"name": "read_file", "path": "src/Program.cs"}
        ```
        ```tool
        {"name": "write_file", "path": "src/New.cs", "content": "...full file content..."}
        ```
        ```tool
        {"name": "edit_file", "path": "src/Program.cs", "old_text": "exact text to replace", "new_text": "replacement"}
        ```
        ```tool
        {"name": "run_command", "command": "dotnet test", "workdir": ".", "timeout_seconds": 300}
        ```
        Rules:
        - edit_file old_text must match exactly once; read the file first if
          you are not sure of its exact content.
        - run_command runs in PowerShell on Windows and bash elsewhere, in the
          workspace root unless workdir is given. Prefer "dotnet test" for
          .NET tests, "dotnet build" to verify a build. Keep commands short
          and non-interactive; never run a command that waits for input.
        - After each tool result, continue with the next tool call, or give
          the final answer. Do not loop forever: if a tool keeps failing the
          same way twice, stop and report the failure.
        - When the task is done, reply in Vietnamese, summarising what you
          changed, which tests you ran and their result. Do not include tool
          blocks in the final answer.
        """;

    public async Task<(string Text, List<ToolCall> Calls)> ChatAsync(
        List<JsonObject> messages, CancellationToken token = default)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(messages.Select(m => m.DeepClone()).ToArray()),
            ["temperature"] = 0.2,
            ["stream"] = false,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_apiKey is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await _http.SendAsync(req, token);
        var text = await resp.Content.ReadAsStringAsync(token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"LLM request failed: HTTP {(int)resp.StatusCode}: {Truncate(text, 500)}");
        var json = JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException("LLM returned invalid JSON");
        var message = json["choices"]?[0]?["message"] as JsonObject;
        var content = message?["content"]?.GetValue<string>() ?? "";
        var calls = ToolCallParser.Parse(content);
        // Native tool_calls, if a future gateway supports them.
        if (message?["tool_calls"] is JsonArray native)
        {
            foreach (var tc in native.OfType<JsonObject>())
            {
                var fn = tc["function"] as JsonObject;
                var name = fn?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                var args = fn?["arguments"] is JsonValue av && av.TryGetValue<string>(out var s)
                    ? JsonNode.Parse(s) as JsonObject ?? new JsonObject()
                    : fn?["arguments"] as JsonObject ?? new JsonObject();
                calls.Add(new ToolCall(name, args));
            }
        }
        return (content, calls);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
