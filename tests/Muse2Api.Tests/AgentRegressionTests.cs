using Muse2Api.Agent;
using System.Text.Json.Nodes;

namespace Muse2Api.Tests;

public class AgentRegressionTests
{
    [Fact]
    public async Task Tools_RejectTraversalAndHandleRootWithTrailingSeparator()
    {
        using var dir = new TempDirectory();
        var tools = new ToolExecutor(dir.Root + Path.DirectorySeparatorChar);
        Assert.StartsWith("wrote", await tools.ExecuteAsync(new("write_file", new() { ["path"] = "a.txt", ["content"] = "hello" })));
        Assert.Equal("hello", await tools.ExecuteAsync(new("read_file", new() { ["path"] = "a.txt" })));
        Assert.StartsWith("error:", await tools.ExecuteAsync(new("read_file", new() { ["path"] = "../outside.txt" })));
    }

    [Fact]
    public async Task Tools_RejectJunctionOrSymlinkEscape()
    {
        using var dir = new TempDirectory();
        using var outside = new TempDirectory();
        File.WriteAllText(outside.File("secret.txt"), "secret");
        var link = dir.File("linked");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, outside.Root }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, outside.Root);
        try
        {
            var tools = new ToolExecutor(dir.Root);
            Assert.StartsWith("error:", await tools.ExecuteAsync(new("read_file", new() { ["path"] = "linked/secret.txt" })));
            Assert.StartsWith("error:", await tools.ExecuteAsync(new("write_file", new() { ["path"] = "linked/new.txt", ["content"] = "bad" })));
            Assert.False(File.Exists(outside.File("new.txt")));
            Assert.DoesNotContain("secret.txt", await tools.ExecuteAsync(new("list_files", new() { ["recursive"] = true })));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task AgentLoop_KeepsToolResultsForFollowupTurns()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("a.txt"), "important contents");
        var calls = 0;
        var loop = new AgentLoop(new ToolExecutor(dir.Root), _ =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? ("reading", new List<ToolCall> { new("read_file", new() { ["path"] = "a.txt" }) })
                : ("done", new List<ToolCall>()));
        }, _ => true, _ => { });
        await loop.RunAsync("read a.txt");
        Assert.Contains(loop.History, message => message["content"]!.GetValue<string>().Contains("important contents"));
        Assert.Equal("done", loop.History.Last()["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tools_ExecuteCommandAndReportExitCode()
    {
        using var dir = new TempDirectory();
        var tools = new ToolExecutor(dir.Root);
        var result = await tools.ExecuteAsync(new("run_command", new() { ["command"] = "echo success", ["timeout_seconds"] = 45 }));
        Assert.Contains("exit_code=0", result);
        Assert.Contains("success", result);
    }

    [Theory]
    [InlineData("--api-key")]
    [InlineData("--max-steps", "0")]
    [InlineData("--max-steps", "bad")]
    [InlineData("--unknown")]
    [InlineData("--base-url", "file:///tmp")]
    public void Options_RejectMissingOrInvalidArguments(params string[] args)
        => Assert.Throws<ArgumentException>(() => Options.Parse(args));

    [Fact]
    public async Task AgentLoop_ReportsStepLimitAsIncomplete()
    {
        using var dir = new TempDirectory();
        var loop = new AgentLoop(new ToolExecutor(dir.Root), _ => Task.FromResult(("reading", new List<ToolCall> { new("list_files", new()) })),
            _ => true, _ => { }, maxSteps: 1);
        Assert.False(await loop.RunAsync("test"));
        Assert.NotEmpty(loop.History);
    }
}
