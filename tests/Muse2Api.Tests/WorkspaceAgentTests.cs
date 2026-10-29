using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class WorkspaceAgentTests
{
    [Fact]
    public async Task ApprovedEdit_ShowsExactDiffThenWritesAndCanBeReverted()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("a.txt"), "old\n");
        var tools = new ToolExecutor(dir.Root);
        var call = new ToolCall("edit_file", new() { ["path"] = "a.txt", ["old_text"] = "old", ["new_text"] = "new" });
        var approval = await tools.PrepareAsync(call);
        Assert.Contains("-old", approval.Preview);
        Assert.Contains("+new", approval.Preview);
        Assert.Equal("old\n", File.ReadAllText(dir.File("a.txt")));
        Assert.StartsWith("edited", await tools.ExecutePreparedAsync(approval));
        Assert.Equal("new\n", File.ReadAllText(dir.File("a.txt")));
        Assert.Single(tools.Changes);
        await tools.RevertAsync("a.txt");
        Assert.Equal("old\n", File.ReadAllText(dir.File("a.txt")));
        Assert.Empty(tools.Changes);
    }

    [Fact]
    public async Task Preview_RejectsFileChangedWhileApprovalWasPending()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("a.txt"), "old");
        var tools = new ToolExecutor(dir.Root);
        var approval = await tools.PrepareAsync(new("write_file", new() { ["path"] = "a.txt", ["content"] = "agent" }));
        File.WriteAllText(dir.File("a.txt"), "external edit");
        Assert.StartsWith("error:", await tools.ExecutePreparedAsync(approval));
        Assert.Equal("external edit", File.ReadAllText(dir.File("a.txt")));
        Assert.Empty(tools.Changes);
    }

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    public async Task EditAndUndo_PreserveOriginalEncodingAndBom(int codePage)
    {
        using var dir = new TempDirectory();
        var encoding = System.Text.Encoding.GetEncoding(codePage);
        File.WriteAllText(dir.File("a.txt"), "old\r\n", encoding);
        var original = File.ReadAllBytes(dir.File("a.txt"));
        var tools = new ToolExecutor(dir.Root);
        var result = await tools.ExecuteAsync(new("edit_file", new() { ["path"] = "a.txt", ["old_text"] = "old", ["new_text"] = "new" }));
        Assert.StartsWith("edited", result);
        Assert.Equal(encoding.GetPreamble(), File.ReadAllBytes(dir.File("a.txt"))[..encoding.GetPreamble().Length]);
        await tools.RevertAsync("a.txt");
        Assert.Equal(original, File.ReadAllBytes(dir.File("a.txt")));
        Assert.Single(Directory.GetFiles(dir.Root));
    }

    [Fact]
    public async Task CancelBeforeWrite_DoesNotChangeFileOrCreateJournalEntry()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("a.txt"), "original");
        var tools = new ToolExecutor(dir.Root);
        var prepared = await tools.PrepareAsync(new("write_file", new() { ["path"] = "a.txt", ["content"] = "updated" }));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.ExecutePreparedAsync(prepared, cancel.Token));
        Assert.Equal("original", File.ReadAllText(dir.File("a.txt")));
        Assert.Empty(tools.Changes);
    }

    [Fact]
    public async Task Undo_ProtectsExternalEditsAndRemovesOnlyNewTrackedFiles()
    {
        using var dir = new TempDirectory();
        var tools = new ToolExecutor(dir.Root);
        await tools.ExecuteAsync(new("write_file", new() { ["path"] = "new.txt", ["content"] = "created" }));
        await tools.RevertAsync("new.txt");
        Assert.False(File.Exists(dir.File("new.txt")));
        File.WriteAllText(dir.File("a.txt"), "original");
        await tools.ExecuteAsync(new("write_file", new() { ["path"] = "a.txt", ["content"] = "changed" }));
        File.WriteAllText(dir.File("a.txt"), "external");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.RevertAsync("a.txt"));
        Assert.Equal("external", File.ReadAllText(dir.File("a.txt")));
    }

    [Fact]
    public async Task SearchAndRead_AreBoundedAndIgnoreGeneratedFolders()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("bin"));
        File.WriteAllText(dir.File("bin/ignored.txt"), "needle");
        File.WriteAllText(dir.File("a.cs"), "first\nneedle\nthird\nfourth");
        var tools = new ToolExecutor(dir.Root);
        var search = await tools.ExecuteAsync(new("search_files", new() { ["query"] = "NEEDLE" }));
        Assert.Contains("a.cs:2:", search);
        Assert.DoesNotContain("ignored", search);
        var page = await tools.ExecuteAsync(new("read_file", new() { ["path"] = "a.cs", ["start_line"] = 2, ["max_lines"] = 1 }));
        Assert.StartsWith("needle", page);
        Assert.Contains("more lines", page);
        Assert.DoesNotContain("fourth", page);
    }

    [Fact]
    public async Task AgentLoop_AsyncDenialPreventsWriteAndRecordsToolResult()
    {
        using var dir = new TempDirectory();
        var tools = new ToolExecutor(dir.Root);
        var rounds = 0;
        var approvals = 0;
        var events = new List<AgentEvent>();
        var loop = new AgentLoop(tools, (_, _) => Task.FromResult(++rounds == 1
            ? ("", new List<ToolCall> { new("write_file", new() { ["path"] = "new.txt", ["content"] = "test" }) })
            : ("Đã giữ nguyên file.", new List<ToolCall>())), async (approval, token) =>
        {
            await Task.Yield();
            approvals++;
            Assert.Contains("+test", approval.Preview);
            return false;
        }, events.Add);
        Assert.False(await loop.RunAsync("create a file"));
        Assert.Equal(1, approvals);
        Assert.False(File.Exists(dir.File("new.txt")));
        Assert.Contains(loop.History, m => m["content"]!.ToString().Contains("user declined"));
        Assert.Contains(events, e => e.Kind == "declined");
    }

    [Fact]
    public async Task AgentLoop_CancelsPendingApprovalAndRetainsInterruptedHistory()
    {
        using var dir = new TempDirectory();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (_, _) => Task.FromResult(("", new List<ToolCall> { new("write_file", new() { ["path"] = "a", ["content"] = "x" }) })),
            async (_, token) => { pending.SetResult(); await Task.Delay(Timeout.Infinite, token); return true; }, _ => { });
        var running = loop.RunAsync("test", cancel.Token);
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(File.Exists(dir.File("a")));
        Assert.Contains(loop.History, m => m["content"]!.ToString().Contains("cancelled by user"));
    }

    [Fact]
    public async Task Command_CancellationKillsProcessAndPropagates()
    {
        using var dir = new TempDirectory();
        var tools = new ToolExecutor(dir.Root);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var command = OperatingSystem.IsWindows() ? "Write-Output 'started'\nStart-Sleep -Seconds 30" : "echo started\nsleep 30";
        var prepared = await tools.PrepareAsync(new("run_command", new() { ["command"] = command }));
        var running = tools.ExecutePreparedAsync(prepared, cancel.Token, output => { if (output.Contains("started")) started.TrySetResult(); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Sessions_RestoreHistoryAndStayIsolatedByWorkspace()
    {
        using var dir = new TempDirectory();
        using var projectA = new TempDirectory();
        using var projectB = new TempDirectory();
        var store = new ChatSessionStore(dir.Root);
        var session = new ChatSession { Workspace = projectA.Root, Title = "read project", History = new() { new() { ["role"] = "user", ["content"] = "hello" } }, Events = new() { new("assistant", "done") } };
        await store.SaveAsync(session);
        var loaded = await store.LoadAsync(projectA.Root, session.Id);
        Assert.Equal("hello", loaded.History[0]["content"]!.ToString());
        Assert.Equal("done", loaded.Events[0].Text);
        Assert.Single(await store.ListAsync(projectA.Root));
        Assert.Empty(await store.ListAsync(projectB.Root));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(projectA.Root, "../outside"));
    }

    [Fact]
    public async Task AgentClient_StreamsThenExecutesRealFileToolsThroughHttpGateway()
    {
        using var project = new TempDirectory();
        File.WriteAllText(project.File("hello.txt"), "old");
        var driver = new ScriptedDriver(new[]
        {
            "```tool\n{\"name\":\"read_file\",\"path\":\"hello.txt\"}\n```",
            "```tool\n{\"name\":\"edit_file\",\"path\":\"hello.txt\",\"old_text\":\"old\",\"new_text\":\"new\"}\n```",
            "Đã sửa hello.txt.",
        });
        await using var host = await GatewayHost.Start(driver);
        using var client = new AgentClient(host.Client.BaseAddress!.ToString(), "muse-chat", "test-api", "session-test");
        var received = new List<string>();
        var tools = new ToolExecutor(project.Root);
        var loop = new AgentLoop(tools, (messages, token) => client.ChatAsync(messages, token, received.Add), (_, _) => Task.FromResult(true), _ => { });
        Assert.True(await loop.RunAsync("read and edit hello.txt"));
        Assert.Equal("new", File.ReadAllText(project.File("hello.txt")));
        Assert.NotEmpty(received);
        Assert.Equal(3, driver.Calls);
        Assert.Single(tools.Changes);
    }

    [Fact]
    public async Task AgentClient_RejectsFailureInMiddleOfStream()
    {
        await using var host = await GatewayHost.Start(new TestDriver { FailAfterOutput = true });
        using var client = new AgentClient(host.Client.BaseAddress!.ToString(), "muse-chat", "test-api");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync(new() { new() { ["role"] = "user", ["content"] = "test" } }, onDelta: _ => { }));
        Assert.Contains("stream failed", error.Message);
    }

    [Theory]
    [InlineData("```tool\n{\"path\":\"a\"}\n```")]
    [InlineData("```tool\n{\"name\":\"read_file\"")]
    public void Parser_ReportsMalformedToolInsteadOfClaimingCompletion(string text)
        => Assert.Equal("__parse_error__", Assert.Single(ToolCallParser.Parse(text)).Name);

    private sealed class ScriptedDriver(string[] replies) : IMuseDriver
    {
        public string Name => "scripted";
        public bool RequiresAccount => false;
        public DriverCapabilities Capabilities => new(Chat: true);
        public int Calls { get; private set; }
        public async IAsyncEnumerable<string> ChatStream(Account account, ChatRequest req)
        {
            var text = replies[Calls++];
            for (var i = 0; i < text.Length; i += 7)
            {
                await Task.Yield();
                req.Cancel.ThrowIfCancellationRequested();
                yield return text.Substring(i, Math.Min(7, text.Length - i));
            }
        }
    }
}
