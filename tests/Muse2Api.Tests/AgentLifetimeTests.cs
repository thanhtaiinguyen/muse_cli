using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class AgentLifetimeTests
{
    [Fact]
    public async Task ProcessPollHonorsLongWaitAndRemainsCancellable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory(); var tools = new ToolExecutor(dir.Root,"powershell");
        try
        {
            var started = await tools.ExecuteAsync(new("run_command",new(){["command"]="Start-Sleep -Seconds 7; Write-Output 'POLL_DONE'",["background"]=true}));
            var id = System.Text.RegularExpressions.Regex.Match(started,@"process_id=([a-f0-9]+)").Groups[1].Value;
            using(var cancel = new CancellationTokenSource(200))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>tools.ExecuteAsync(new("process_status",new(){["process_id"]=id,["wait_ms"]=45000}),cancel.Token));
            var result = await tools.ExecuteAsync(new("process_status",new(){["process_id"]=id,["wait_ms"]=45000}));
            Assert.False(ToolOutcome.IsRunning(result)); Assert.True(ToolOutcome.ExitedSuccessfully(result)); Assert.Contains("POLL_DONE",result);
        }
        finally { await tools.StopBackgroundProcessesAsync(); }
    }

    [Fact]
    public async Task DefaultContinuesPastThirtyRoundsWithoutInventingAStepBudget()
    {
        using var dir = new TempDirectory();
        for (var i = 0; i < 36; i++) File.WriteAllText(dir.File($"file{i}.txt"), i.ToString());
        var calls = 0; var events = new List<AgentEvent>();
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (_, _) =>
        {
            var step = calls++;
            return Task.FromResult(step < 36
                ? ("", new List<ToolCall> { new("read_file", new() { ["path"] = $"file{step}.txt" }) })
                : ("Đã hoàn tất.", new List<ToolCall>()));
        }, (_, _) => Task.FromResult(true), events.Add);
        Assert.True(await loop.RunAsync("test"));
        Assert.Equal(37, calls); Assert.Null(Options.Parse(Array.Empty<string>()).MaxSteps);
        Assert.DoesNotContain(events, e => e.Text.Contains("bước") || e.Kind == "incomplete");
    }

    [Fact]
    public async Task RepeatedUnchangedObservationStopsInsteadOfSpammingForever()
    {
        using var dir = new TempDirectory(); var calls = 0; var events = new List<AgentEvent>();
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (_, _) =>
        {
            calls++;
            return Task.FromResult(("", new List<ToolCall> { new("list_files", new()) }));
        }, (_, _) => Task.FromResult(true), events.Add);
        Assert.False(await loop.RunAsync("test")); Assert.Equal(3, calls);
        Assert.Contains(events, e => e.Kind == "incomplete" && e.Text.Contains("không có tiến triển"));
    }

    [Fact]
    public async Task LateModelReplyAfterCancellationCannotWriteFiles()
    {
        using var dir = new TempDirectory(); using var cancel = new CancellationTokenSource();
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (_, _) =>
        {
            cancel.Cancel();
            return Task.FromResult(("", new List<ToolCall> { new("write_file", new() { ["path"] = "late.txt", ["content"] = "unsafe late result" }) }));
        }, (_, _) => Task.FromResult(true), _ => { });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.RunAsync("test", cancel.Token));
        Assert.False(File.Exists(dir.File("late.txt")));
    }
}
