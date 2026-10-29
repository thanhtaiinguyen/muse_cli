using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class LocalToolProtocolTests
{
    [Theory]
    [InlineData("```json\n{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.cs\"}}]}\n```")]
    [InlineData("{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.cs\\\"}\"}]}")]
    [InlineData("```\n{\"name\":\"readFile\",\"file_path\":\"a.cs\"}\n```")]
    [InlineData("~~~json\n[{\"name\":\"READ_FILE\",\"parameters\":{\"path\":\"a.cs\"}}]\n~~~")]
    public void Parser_AcceptsActualRenderedJsonAndNormalizesBeforeExecution(string response)
    {
        var call = Assert.Single(ToolCallParser.Parse(response));
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.cs", call.Args["path"]!.ToString());
        Assert.Empty(ToolCallParser.StripToolBlocks(response));
    }

    [Fact]
    public void Parser_DoesNotExecuteOrdinaryJsonOrEraseCodeExamples()
    {
        const string json = "```json\n{\"name\":\"my-project\",\"version\":1}\n```";
        Assert.Empty(ToolCallParser.Parse(json));
        Assert.Equal(json, ToolCallParser.StripToolBlocks(json));
        Assert.Empty(ToolCallParser.Parse("{\"name\":42}"));
    }

    [Fact]
    public async Task PlainVmReply_IsNotReportedAsCompletedLocalWork()
    {
        using var directory = new TempDirectory();
        var events = new List<AgentEvent>();
        var calls = 0;
        var loop = new AgentLoop(new ToolExecutor(directory.Root), (_, _) =>
        {
            calls++;
            return Task.FromResult(("Đã đọc dự án trên VM Linux, không phải ổ C của bạn.", new List<ToolCall>()));
        }, (_, _) => Task.FromResult(true), events.Add);
        Assert.False(await loop.RunAsync("Hãy đọc chi tiết các file trong dự án"));
        Assert.Equal(3, calls);
        Assert.DoesNotContain(events, e => e.Kind == "completed");
        Assert.Contains(events, e => e.Kind == "incomplete");
    }

    [Fact]
    public async Task PlainReply_IsRepairedIntoAnActualReadAndEdit()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("a.cs"), "before");
        var replies = new[]
        {
            "Bạn hãy gửi nội dung file vì tôi chỉ truy cập được VM Linux.",
            "```json\n{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.cs\",\"details\":true}}]}\n```",
            "Đã sửa file.", // No write evidence yet: must not finish here.
            "{\"tool_calls\":[{\"name\":\"edit_file\",\"arguments\":{\"path\":\"a.cs\",\"old_text\":\"before\",\"new_text\":\"after\"}}]}",
            "Đã cập nhật file và đã kiểm tra lại nội dung.",
        };
        var index = 0;
        var events = new List<AgentEvent>();
        var loop = new AgentLoop(new ToolExecutor(directory.Root), (messages, _) =>
        {
            Assert.Contains("CURRENT LOCAL TASK", messages[0]["content"]!.ToString());
            var response = replies[index++];
            return Task.FromResult((response, ToolCallParser.Parse(response)));
        }, (_, _) => Task.FromResult(true), events.Add);
        Assert.True(await loop.RunAsync("Hãy đọc và chỉnh sửa file a.cs từ before thành after"));
        Assert.Equal(5, index);
        Assert.Equal("after", File.ReadAllText(directory.File("a.cs")));
        Assert.Equal(2, events.Count(e => e.Kind == "protocol_retry"));
    }

    [Fact]
    public async Task DetailedRead_PagesEveryLineWithoutSilentTruncation()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("long.cs"), string.Join("\r\n", Enumerable.Range(1, 2500).Select(i => $"Dòng {i} · nội dung chi tiết của dự án")));
        var tools = new ToolExecutor(directory.Root);
        var read = new List<int>();
        var start = 1;
        do
        {
            var page = await tools.ExecuteAsync(new("read_file", new() { ["path"] = "long.cs", ["details"] = true, ["start_line"] = start }));
            read.AddRange(Regex.Matches(page, @"(?m)^\s*(\d+):").Select(m => int.Parse(m.Groups[1].Value)));
            var next = Regex.Match(page, @"next_start_line=(\d+)");
            start = next.Success ? int.Parse(next.Groups[1].Value) : 0;
        } while (start > 0);
        Assert.Equal(Enumerable.Range(1, 2500), read);
    }

    [Fact]
    public async Task BatchEdit_MatchesLfAgainstWindowsCrlfAndCommitsAllOrNothing()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("a.txt"), "first\r\nsecond\r\nthird\r\n");
        var tools = new ToolExecutor(directory.Root);
        var call = new ToolCall("edit_file", new()
        {
            ["path"] = "a.txt", ["edits"] = new JsonArray(
                new JsonObject { ["old_text"] = "first\nsecond", ["new_text"] = "one\ntwo" },
                new JsonObject { ["old_text"] = "third", ["new_text"] = "three" }),
        });
        Assert.StartsWith("edited", await tools.ExecuteAsync(call));
        Assert.Equal("one\r\ntwo\r\nthree\r\n", File.ReadAllText(directory.File("a.txt")));
        var before = File.ReadAllBytes(directory.File("a.txt"));
        ((JsonArray)call.Args["edits"]!)[0]!["old_text"] = "one\ntwo";
        ((JsonArray)call.Args["edits"]!)[1]!["old_text"] = "missing";
        Assert.StartsWith("error:", await tools.ExecuteAsync(call));
        Assert.Equal(before, File.ReadAllBytes(directory.File("a.txt")));
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("cmd")]
    public async Task Shell_CreatesAndReadsUnicodeFileInDirectoryWithSpaces(string shell)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var root = directory.File("thư mục có dấu"); Directory.CreateDirectory(root);
        var tools = new ToolExecutor(root, shell);
        var command = shell == "cmd" ? "echo LOCAL_CMD_OK>\"ghi chú.txt\"\r\ntype \"ghi chú.txt\""
            : "Set-Content -LiteralPath 'ghi chú.txt' -Value 'Xin chào \"Muse\"' -Encoding UTF8\nGet-Content -LiteralPath 'ghi chú.txt'";
        var result = await tools.ExecuteAsync(new("run_command", new() { ["command"] = command, ["shell"] = shell }));
        Assert.Contains("exit_code=0", result);
        Assert.True(File.Exists(Path.Combine(root, "ghi chú.txt")));
        Assert.Contains(shell == "cmd" ? "LOCAL_CMD_OK" : "Xin chào \"Muse\"", result);
        Assert.Contains("ghi chú.txt", tools.LastCommandChangedFiles);
        Assert.Contains("workspace_files_changed_during_command=", result);
    }

    [Fact]
    public async Task Shell_ChangesSatisfyEditTaskButNoOpDoesNot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("a.txt"), "before");
        var tools = new ToolExecutor(directory.Root, "cmd");
        var replies = new[]
        {
            "{\"tool_calls\":[{\"name\":\"run_cmd\",\"arguments\":{\"command\":\"echo checking\"}}]}",
            "Đã sửa file.",
            "{\"tool_calls\":[{\"name\":\"run_cmd\",\"arguments\":{\"command\":\"echo after>a.txt\"}}]}",
            "Đã sửa file bằng CMD.",
        };
        var index = 0;
        var events = new List<AgentEvent>();
        var loop = new AgentLoop(tools, (_, _) =>
        {
            var text = replies[index++];
            return Task.FromResult((text, ToolCallParser.Parse(text)));
        }, (_, _) => Task.FromResult(true), events.Add);
        Assert.True(await loop.RunAsync("Chỉnh sửa file a.txt bằng lệnh CMD"));
        Assert.Equal("after", File.ReadAllText(directory.File("a.txt")).Trim());
        Assert.Single(events.Where(e => e.Kind == "protocol_retry"));
        await tools.ExecuteAsync(new("run_command", new() { ["command"] = "echo unchanged" }));
        Assert.Empty(tools.LastCommandChangedFiles);
    }

    [Theory]
    [InlineData("powershell", "cmd.exe /c exit 7", "exit_code=7")]
    [InlineData("powershell", "Get-Content -LiteralPath 'file-that-does-not-exist.txt'", "exit_code=1")]
    [InlineData("cmd", "exit /b 9", "exit_code=9")]
    public async Task Shell_ReportsFailuresInsteadOfFalseSuccess(string shell, string command, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var result = await new ToolExecutor(directory.Root, shell).ExecuteAsync(new("run_command", new() { ["command"] = command }));
        Assert.Contains(expected, result);
    }

    [Fact]
    public async Task NativeStream_ReassemblesToolArgumentsAcrossChunks()
    {
        var pieces = new[]
        {
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"read_file","arguments":"{\"pa"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a.cs\"}"}}]}}]}""",
        };
        var payload = string.Join("\n\n", pieces.Select(p => "data: " + p)) + "\n\ndata: [DONE]\n\n";
        using var client = new AgentClient("http://127.0.0.1:1", "muse-chat", "test", messageHandler: new StreamHandler(payload));
        var result = await client.ChatAsync(new() { new() { ["role"] = "user", ["content"] = "read a.cs" } }, onDelta: _ => { });
        Assert.Equal("a.cs", Assert.Single(result.Calls).Args["path"]!.ToString());
    }

    [BrowserFact]
    public async Task Browser_RenderedCodeBlockPreservesExecutableToolJson()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = DesktopProfile.FindBrowser() is { Length: > 0 } path ? path : null });
        var page = await browser.NewPageAsync();
        const string source = "var label = \"Chế độ \\\"Sáng\\\"\";\nvar path = @\"C:\\Muse\\theme.json\";";
        var protocol = new JsonObject { ["actions"] = new JsonArray(new JsonObject
        { ["action"] = "replace_text", ["path"] = "hello world.cs", ["find"] = "old", ["replace"] = source }) }.ToJsonString();
        await page.SetContentAsync("<div class='hatch-agent-bubble-bg'><p>Mình sẽ đọc tệp.</p><button>Copy</button><pre><code class='language-json'><span>" + WebUtility.HtmlEncode(protocol) + "</span></code></pre></div>");
        var state = await page.EvaluateAsync<JsonElement>(Dom.ChatState);
        var response = state.GetProperty("lastText").GetString()!;
        Assert.Contains("```json", response);
        Assert.DoesNotContain("Copy", response);
        var action = Assert.Single(ToolCallParser.Parse(response));
        Assert.Equal("hello world.cs", action.Args["path"]!.ToString());
        Assert.Equal(source, action.Args["new_text"]!.ToString());
    }

    private sealed class StreamHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "text/event-stream") });
        }
    }
}

internal sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute() { if (Environment.GetEnvironmentVariable("MUSE_BROWSER_TESTS") != "1") Skip = "Set MUSE_BROWSER_TESTS=1 to run the real browser DOM integration test"; }
}
