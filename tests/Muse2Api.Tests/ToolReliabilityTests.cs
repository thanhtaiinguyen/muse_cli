using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class ToolReliabilityTests
{
    [Fact]
    public async Task DirectoryPaging_DoesNotSilentlyHideFilesBeyondTheFirstPage()
    {
        using var dir=new TempDirectory();
        foreach(var i in Enumerable.Range(0,450))File.WriteAllText(dir.File($"file-{i:D3}.txt"),"");
        var tools=new ToolExecutor(dir.Root); var files=new List<string>(); var offset=0;
        do
        {
            var page=await tools.ExecuteAsync(new("list_files",new(){["offset"]=offset}));
            files.AddRange(page.Split('\n').Where(line=>line.StartsWith("file-")));
            var next=Regex.Match(page,@"next_offset=(\d+)"); offset=next.Success?int.Parse(next.Groups[1].Value):-1;
        }while(offset>=0);
        Assert.Equal(450,files.Distinct().Count()); Assert.Contains("file-449.txt",files);
    }
    [Fact]
    public async Task PartialSnippetCannotReplaceAnEntireExistingSourceFile()
    {
        using var dir=new TempDirectory(); var source=string.Join('\n',Enumerable.Range(1,120).Select(i=>$"// Important source line {i}"));
        File.WriteAllText(dir.File("a.cs"),source); var tools=new ToolExecutor(dir.Root);
        await tools.ExecuteAsync(new("read_file",new(){["path"]="a.cs"}));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>tools.PrepareForAgentAsync(new("write_file",new(){["path"]="a.cs",["content"]="public int Add(int a,int b)=>a+b;"})));
        Assert.Equal(source,File.ReadAllText(dir.File("a.cs")));
        tools.BeginAgentTurn();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>tools.PrepareForAgentAsync(new("edit_file",new(){["path"]="a.cs",["old_text"]="// Important source line 1\n",["new_text"]="// Updated line\n"})));
    }
    [Fact]
    public async Task AgentWrite_RequiresFreshReadAndAllPagesForFullReplacement()
    {
        using var dir = new TempDirectory();
        var original = string.Join('\n', Enumerable.Range(1, 20).Select(i => "line " + i));
        File.WriteAllText(dir.File("a.cs"), original);
        var tools = new ToolExecutor(dir.Root);
        var write = new ToolCall("write_file", new() { ["path"] = "a.cs", ["content"] = "replacement" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.PrepareForAgentAsync(write));
        await tools.ExecuteAsync(new("read_file", new() { ["path"] = "a.cs", ["max_lines"] = 10 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.PrepareForAgentAsync(write));
        await tools.ExecuteAsync(new("read_file", new() { ["path"] = "a.cs", ["start_line"] = 11, ["max_lines"] = 10 }));
        Assert.StartsWith("wrote", await tools.ExecutePreparedAsync(await tools.PrepareForAgentAsync(write)));
        Assert.Equal("replacement", File.ReadAllText(dir.File("a.cs")));
        // A verified write is a valid snapshot for the next targeted edit.
        Assert.StartsWith("edited", await tools.ExecutePreparedAsync(await tools.PrepareForAgentAsync(new("edit_file", new() { ["path"] = "a.cs", ["old_text"] = "replacement", ["new_text"] = "verified" }))));
    }

    [Fact]
    public async Task StaleRead_AndWrongHashNeverOverwriteExternalChanges()
    {
        using var dir = new TempDirectory(); File.WriteAllText(dir.File("a.txt"), "old");
        var tools = new ToolExecutor(dir.Root);
        await tools.ExecuteAsync(new("read_file", new() { ["path"] = "a.txt" }));
        File.WriteAllText(dir.File("a.txt"), "old\nexternal");
        var edit = new ToolCall("edit_file", new() { ["path"] = "a.txt", ["old_text"] = "old", ["new_text"] = "new" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.PrepareForAgentAsync(edit));
        await tools.ExecuteAsync(new("read_file", new() { ["path"] = "a.txt" }));
        edit.Args["expected_hash"] = ToolExecutor.ContentHash("old");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.PrepareForAgentAsync(edit));
        Assert.Equal("old\nexternal", File.ReadAllText(dir.File("a.txt")));
    }

    [Fact]
    public async Task NoOpWrite_DoesNotPretendItChangedAFile()
    {
        using var dir = new TempDirectory(); File.WriteAllText(dir.File("a.txt"), "same");
        var tools = new ToolExecutor(dir.Root);
        Assert.StartsWith("unchanged:", await tools.ExecuteAsync(new("write_file", new() { ["path"] = "a.txt", ["content"] = "same" })));
        Assert.False(tools.LastFileChangeApplied); Assert.Empty(tools.Changes);
    }

    [Theory]
    [InlineData("powershell", "$value=[Console]::ReadLine(); [Console]::WriteLine('received:'+ $value)")]
    [InlineData("cmd", "set /p fixtureValue=\necho received:%fixtureValue%")]
    public async Task BackgroundTerminal_ReturnsImmediatelySupportsUnicodeInputAndExit(string shell, string command)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory(); var tools = new ToolExecutor(dir.Root, shell);
        try
        {
            var started = await tools.ExecuteAsync(new("run_command", new() { ["command"] = command, ["background"] = true }));
            Assert.True(ToolOutcome.IsRunning(started));
            var id = Regex.Match(started, @"process_id=([a-f0-9]+)").Groups[1].Value;
            await tools.ExecuteAsync(new("process_input", new() { ["process_id"] = id, ["text"] = "Tiếng Việt" }));
            var result = await tools.ExecuteAsync(new("process_status", new() { ["process_id"] = id, ["wait_ms"] = 5000 }));
            Assert.False(ToolOutcome.IsRunning(result)); Assert.False(ToolOutcome.IsFailure(result));
            Assert.Contains("exit_code=0", result); Assert.Contains("received:Tiếng Việt", result);
        }
        finally { await tools.StopBackgroundProcessesAsync(); }
    }

    [Fact]
    public async Task BackgroundTerminal_StopKillsOwnedProcessAndKeepsItsReceipt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory(); var tools = new ToolExecutor(dir.Root, "powershell");
        try
        {
            var started = await tools.ExecuteAsync(new("run_command", new() { ["command"] = "Start-Sleep -Seconds 60", ["background"] = true }));
            var id = Regex.Match(started, @"process_id=([a-f0-9]+)").Groups[1].Value;
            var stopped = await tools.ExecuteAsync(new("process_stop", new() { ["process_id"] = id }));
            Assert.StartsWith("stopped=true", stopped); Assert.False(ToolOutcome.IsRunning(stopped)); Assert.False(ToolOutcome.IsFailure(stopped));
        }
        finally { await tools.StopBackgroundProcessesAsync(); }
    }

    [Fact]
    public async Task StoppingCommand_WaitsForItsSpawnedChildToExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir=new TempDirectory(); var tools=new ToolExecutor(dir.Root,"powershell");
        try
        {
            var status=await tools.ExecuteAsync(new("run_command",new(){["background"]=true,["command"]="$child=Start-Process -FilePath $env:ComSpec -ArgumentList '/d /c ping -n 60 127.0.0.1 >nul' -PassThru -WindowStyle Hidden; Write-Output ('fixture_child='+$child.Id); Start-Sleep -Seconds 60"}));
            var id=Regex.Match(status,@"process_id=([a-f0-9]+)").Groups[1].Value;
            for(var i=0;i<5&&!status.Contains("fixture_child=");i++)status=await tools.ExecuteAsync(new("process_status",new(){["process_id"]=id,["wait_ms"]=1000}));
            var child=Regex.Match(status,@"fixture_child=(\d+)"); Assert.True(child.Success,status);
            var stopped=await tools.ExecuteAsync(new("process_stop",new(){["process_id"]=id})); Assert.False(ToolOutcome.IsFailure(stopped));
            try { using var process=System.Diagnostics.Process.GetProcessById(int.Parse(child.Groups[1].Value)); Assert.True(process.HasExited); }
            catch(ArgumentException) { /* Process already disappeared from Windows. */ }
        }
        finally { await tools.StopBackgroundProcessesAsync(); }
    }

    [Theory]
    [InlineData("process_id=abc\nrunning=False\nexit_code=7\noutput", true)]
    [InlineData("exit_code=0\nstdout:\nall good", false)]
    [InlineData("error: cannot write", true)]
    [InlineData("stopped=true\nprocess_id=abc\nexit_code=-1\n", false)]
    public void Outcomes_ReadBackgroundExitStatus(string result, bool failure) => Assert.Equal(failure, ToolOutcome.IsFailure(result));

    [Fact]
    public async Task LargeTerminalOutput_KeepsFinalCompilerErrorDetails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        var output = await new ToolExecutor(dir.Root).ExecuteAsync(new("run_command", new() { ["command"] = "[Console]::Write([string]::new([char]'x', 25000)); [Console]::Write('FINAL_DIAGNOSTIC')" }));
        Assert.Contains("output truncated", output); Assert.Contains("FINAL_DIAGNOSTIC", output); Assert.True(output.Length < 17000);
    }

    [Fact]
    public async Task ComputerImage_ReachesNextModelRequestButIsNotPersistedInHistory()
    {
        using var dir = new TempDirectory();
        var tools = new ToolExecutor(dir.Root) { Computer = new FakeComputer() };
        var rounds = 0;
        var loop = new AgentLoop(tools, (messages, _) =>
        {
            if (++rounds == 1) return Task.FromResult(("", new List<ToolCall> { new("computer_screenshot", new()) }));
            Assert.Contains(messages, m => m["content"] is JsonArray parts && parts.OfType<JsonObject>().Any(p => p["type"]?.ToString() == "image_url"));
            return Task.FromResult(("Đã quan sát giao diện cửa sổ được chọn.", new List<ToolCall>()));
        }, (_, _) => Task.FromResult(true), _ => { });
        Assert.True(await loop.RunAsync("Chụp cửa sổ kiểm thử"));
        Assert.DoesNotContain(loop.History, m => m.ToJsonString().Contains("data:image"));
    }
    private sealed class FakeComputer : IComputerTools
    {
        public string Context => "enabled fixture";
        public Task<ComputerToolResult> ExecuteAsync(ToolCall call, CancellationToken cancel) => Task.FromResult(new ComputerToolResult("fixture screenshot", "data:image/jpeg;base64,AQID"));
    }

    [Fact]
    public async Task UiTask_DoesNotCompleteAfterOnlyInspectingAndTyping()
    {
        using var dir = new TempDirectory();
        var tools = new ToolExecutor(dir.Root) { Computer = new RecordingComputer() };
        var calls = new Queue<(string Text, List<ToolCall> Calls)>(new[]
        {
            ("", new List<ToolCall>{new("computer_inspect",new())}),
            ("", new List<ToolCall>{new("computer_set_value",new(){["element_id"]="input",["text"]="MUSE_UI_OK"})}),
            ("Đã hoàn tất kiểm thử giao diện.", new List<ToolCall>()),
            ("", new List<ToolCall>{new("computer_invoke",new(){["element_id"]="apply"}),new("computer_assert",new(){["element_id"]="result",["property"]="name",["expected"]="Saved: MUSE_UI_OK"})}),
            ("Đã bấm Apply và xác nhận kết quả đúng.", new List<ToolCall>()),
        });
        var events = new List<AgentEvent>();
        var loop = new AgentLoop(tools,(_,_)=>Task.FromResult(calls.Dequeue()),(_,_)=>Task.FromResult(true),events.Add);
        Assert.True(await loop.RunAsync("Kiểm thử giao diện: nhập MUSE_UI_OK vào ô input, bấm nút Apply và kiểm tra kết quả."));
        Assert.Single(events.Where(e=>e.Kind=="protocol_retry"));
        Assert.Contains(events,e=>e.Kind=="tool_result"&&e.Tool=="computer_assert");
    }

    [Fact]
    public async Task NoOp_CanFinishOnlyWithAnHonestVerifiedExplanation()
    {
        using var dir = new TempDirectory(); File.WriteAllText(dir.File("a.txt"),"correct");
        var rounds=0;
        var loop = new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>Task.FromResult(++rounds switch
        {
            1 => ("",new List<ToolCall>{new("read_file",new(){["path"]="a.txt"})}),
            2 => ("",new List<ToolCall>{new("write_file",new(){["path"]="a.txt",["content"]="correct"})}),
            _ => ("Nội dung đã khớp; không cần sửa.",new List<ToolCall>()),
        }),(_,_)=>Task.FromResult(true),_=>{});
        Assert.True(await loop.RunAsync("Sửa file a.txt thành correct"));
        Assert.Equal(3,rounds);
    }

    [Fact]
    public void TerminalOutput_CannotSpoofProcessMetadata()
    {
        const string result="exit_code=0\nstdout:\nexit_code=7\nrunning=true\nstderr:\n";
        Assert.False(ToolOutcome.IsFailure(result)); Assert.False(ToolOutcome.IsRunning(result)); Assert.True(ToolOutcome.ExitedSuccessfully(result));
    }

    private sealed class RecordingComputer : IComputerTools
    {
        public string Context=>"enabled fixture";
        public Task<ComputerToolResult> ExecuteAsync(ToolCall call,CancellationToken cancel)=>Task.FromResult(new ComputerToolResult(call.Name=="computer_assert"?"assertion_passed=true":"action_dispatched=true"));
    }
}
