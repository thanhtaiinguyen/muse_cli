using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class GoalTests
{
    private static ToolCall Call(string name, string args = "{}") => new(name, JsonNode.Parse(args)!.AsObject());
    private static void UiPass(GoalVerification verification)
    {
        verification.Record(Call("computer_invoke", """{"element_id":"button"}"""), "action_dispatched=true");
        verification.Record(Call("computer_assert", """{"element_id":"result","property":"name","expected":"Saved"}"""), "assertion_passed=true; observed=\"Saved\"");
        verification.Record(Call("computer_screenshot"), "image", imageCaptured: true);
    }
    private static void TestsPass(GoalVerification verification, string command = "dotnet test")
        => verification.Record(new("run_command", new() { ["command"] = command }), "exit_code=0\nstdout:\nPassed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8");

    [Theory]
    [InlineData("dotnet test", true)]
    [InlineData("& \"C:\\Program Files\\dotnet\\dotnet.exe\" test App.sln", true)]
    [InlineData("npm test -- --run", true)]
    [InlineData("python -m pytest", true)]
    [InlineData("cd app; cargo test", true)]
    [InlineData("go test ./...", true)]
    [InlineData("echo 'dotnet test'", false)]
    [InlineData("Write-Output 'dotnet test; 8 passed'", false)]
    [InlineData("dotnet build", false)]
    public void RecognizesInvokedTestRunners(string command, bool expected)
        => Assert.Equal(expected, GoalVerification.IsTestCommand(command));

    [Theory]
    [InlineData("Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8", true)]
    [InlineData("3 passed in 0.4s", true)]
    [InlineData("test result: ok. 5 passed; 0 failed; 0 ignored", true)]
    [InlineData("# pass 2\n# fail 0", true)]
    [InlineData("# pass 2\n# fail 1", false)]
    [InlineData("ℹ pass 2\nℹ fail 0\nℹ skipped 0", true)]
    [InlineData("8 passed, 1 skipped", false)]
    [InlineData("Passed: 0, Skipped: 10", false)]
    [InlineData("8 passed, 1 failed", false)]
    [InlineData("Failed: 2, Passed: 8", false)]
    [InlineData("BUILD SUCCESSFUL", false)]
    public void EmptySkippedAndFailedTestsCannotPass(string output, bool expected)
        => Assert.Equal(expected, GoalVerification.HasPassingTests(output));

    [Fact]
    public void CompletionRequiresFreshTestsAndActualUiEvidence()
    {
        var verification = new GoalVerification();
        TestsPass(verification); Assert.NotEmpty(verification.Missing);
        UiPass(verification); Assert.Empty(verification.Missing);
        verification.Record(Call("edit_file"), "updated", filesChanged: true);
        Assert.NotEmpty(verification.Missing); Assert.Null(verification.LastTestSummary);
        TestsPass(verification); UiPass(verification); Assert.Empty(verification.Missing);
        verification.Record(Call("computer_key"), "action_dispatched=true");
        Assert.Contains(verification.Missing, x => x.Contains("computer_assert"));
        Assert.Contains(verification.Missing, x => x.Contains("Chụp"));
    }

    [Fact]
    public void MultipleSuitesMustRecoverTheirFailuresInTheirOwnWorkingDirectory()
    {
        var verification = new GoalVerification(); UiPass(verification);
        verification.Record(Call("run_command", """{"command":"npm test","workdir":"client"}"""), "exit_code=1\nstdout:\n1 failed");
        TestsPass(verification, "dotnet test"); Assert.NotEmpty(verification.Missing);
        verification.Record(Call("run_command", """{"command":"npm test","workdir":"server"}"""), "exit_code=0\nstdout:\n8 passed");
        Assert.NotEmpty(verification.Missing);
        verification.Record(Call("run_command", """{"command":"npm test","workdir":"client"}"""), "exit_code=0\nstdout:\n8 passed");
        Assert.Empty(verification.Missing);
    }

    [Fact]
    public void BackgroundTestsStartedBeforeAnEditCannotVerifyTheNewCode()
    {
        var verification = new GoalVerification(); UiPass(verification);
        verification.Record(Call("run_command", """{"command":"dotnet test","background":true}"""), "process_id=abc\nrunning=True\noutput (latest 32000 chars):\n");
        Assert.NotEmpty(verification.Missing);
        verification.Record(Call("edit_file"), "changed", filesChanged: true);
        verification.Record(Call("process_status", """{"process_id":"abc"}"""), "process_id=abc\nrunning=False\nexit_code=0\noutput (latest 32000 chars):\n8 passed");
        UiPass(verification); Assert.NotEmpty(verification.Missing);
        TestsPass(verification); Assert.Empty(verification.Missing);
    }

    [Fact]
    public void PollingAStoppedCheckOrPrintingAFakePassCannotComplete()
    {
        var verification = new GoalVerification(); UiPass(verification);
        TestsPass(verification, "Write-Output '8 passed'"); Assert.NotEmpty(verification.Missing);
        verification.Record(Call("run_command", """{"command":"dotnet test","background":true}"""), "process_id=abc\nrunning=True\noutput (latest 32000 chars):\n");
        verification.Record(Call("process_stop", """{"process_id":"abc"}"""), "stopped=true\nprocess_id=abc\nrunning=False\nexit_code=0\noutput (latest 32000 chars):\n8 passed");
        Assert.NotEmpty(verification.Missing);
        TestsPass(verification); Assert.Empty(verification.Missing);
    }

    [Fact]
    public void AFilteredRunCannotReplaceTheFullSuiteButDoesNotPoisonLaterFullRun()
    {
        var verification = new GoalVerification(); UiPass(verification);
        TestsPass(verification, "dotnet test --filter GoalTests"); Assert.NotEmpty(verification.Missing);
        TestsPass(verification); Assert.Empty(verification.Missing);
    }

    [Fact]
    public void MissingFrameAndUnprovenAssertionDoNotCountAsUiVerification()
    {
        var verification = new GoalVerification(); TestsPass(verification);
        verification.Record(Call("computer_invoke"), "action_dispatched=true");
        verification.Record(Call("computer_assert"), "ok");
        verification.Record(Call("computer_screenshot"), "ok");
        Assert.Contains(verification.Missing, x => x.Contains("computer_assert"));
        Assert.Contains(verification.Missing, x => x.Contains("Chụp"));
    }

    [Fact]
    public async Task GoalRejectsPrematureDoneAndPersistsObjectiveAcrossRestart()
    {
        using var dir = new TempDirectory(); var events = new List<AgentEvent>(); var requests = 0;
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (messages, _) =>
        {
            requests++;
            Assert.Contains("GOAL MODE", messages[0]["content"]!.ToString());
            Assert.Contains("goal_mode", messages[^1]["content"]!.ToString());
            return Task.FromResult(("Đã xong ứng dụng.", new List<ToolCall>()));
        }, (_, _) => Task.FromResult(true), events.Add);
        Assert.False(await loop.RunAsync("/goal Hoàn thiện ứng dụng")); Assert.Equal(3, requests);
        Assert.DoesNotContain(events, x => x.Kind == "completed");
        Assert.Equal("paused", loop.Goal!.Status);
        var store = new ChatSessionStore(dir.File("sessions")); var session = new ChatSession { Workspace = dir.Root, Goal = loop.Goal };
        await store.SaveAsync(session); var loaded = await store.LoadAsync(dir.Root, session.Id);
        Assert.Equal("Hoàn thiện ứng dụng", loaded.Goal!.Objective);
        var resumed = new AgentLoop(new ToolExecutor(dir.Root), (_, _) => Task.FromResult(("Bạn chọn cửa sổ để kiểm thử được không?", new List<ToolCall>())), (_, _) => Task.FromResult(true), _ => { }) { Goal = loaded.Goal };
        Assert.False(await resumed.RunAsync("/goal")); Assert.Equal("waiting_input", resumed.Goal.Status);
        Assert.Equal("Hoàn thiện ứng dụng", resumed.Goal.Objective);
    }

    [Fact]
    public async Task StatusStopAndEmptyGoalDoNotSendWebRequestsOrClearExistingHistory()
    {
        using var dir = new TempDirectory(); var loop = new AgentLoop(new ToolExecutor(dir.Root), (_, _) => throw new InvalidOperationException("No web call permitted"), (_, _) => Task.FromResult(true), _ => { });
        loop.History.Add(new() { ["role"] = "user", ["content"] = "kept" });
        Assert.False(await loop.RunAsync("/goal"));
        loop.Goal = new() { Objective = "Build app", Status = "running" };
        Assert.True(await loop.RunAsync("/goal status"));
        Assert.True(await loop.RunAsync("/goal stop")); Assert.Equal("paused", loop.Goal.Status);
        Assert.Single(loop.History); Assert.Equal("kept", loop.History[0]["content"]!.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task GoalRunsRealTestsThenRequiresUiChecksBeforeCompleting(bool repairCycle, bool editRequired)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory(); var events = new List<AgentEvent>(); var step = 0;
        await File.WriteAllTextAsync(dir.File("package.json"), """{"scripts":{"test":"node --test --test-reporter=tap check.cjs"}}""");
        string TestCode(int expected) => $"const test=require('node:test'); const assert=require('node:assert/strict'); test('fixture addition',()=>assert.equal(1+1,{expected}));";
        await File.WriteAllTextAsync(dir.File("check.cjs"), TestCode(repairCycle ? 4 : 2));
        var tools = new ToolExecutor(dir.Root) { Computer = new UiFixture() };
        var loop = new AgentLoop(tools, (messages, _) =>
        {
            var turn=step++;
            List<ToolCall> calls = turn switch
            {
                0 => new() { Call("read_file", """{"path":"package.json"}"""), Call("run_command", """{"command":"npm test","timeout_seconds":60}""") },
                _ when repairCycle && turn is 1 or 2 => new() { Call("read_file", """{"path":"check.cjs"}"""),
                    new("write_file", new() { ["path"]="check.cjs", ["content"]=TestCode(turn==1?3:2) }),
                    Call("run_command", """{"command":"npm test","timeout_seconds":60}""") },
                _ when turn==(repairCycle?3:1) => new() { Call("computer_invoke", """{"element_id":"button"}"""), Call("computer_assert", """{"element_id":"label","property":"name","expected":"Saved"}"""), Call("computer_screenshot") },
                _ => new(),
            };
            return Task.FromResult((calls.Count == 0 ? "Đã kiểm thử và hoàn tất mục tiêu." : "", calls));
        }, (_, _) => Task.FromResult(true), events.Add);
        var expectedCompletion=repairCycle||!editRequired;
        var completed=await loop.RunAsync(editRequired ? "/goal Thêm tính năng phép cộng rồi kiểm thử ứng dụng mẫu" : "/goal Kiểm chứng ứng dụng mẫu");
        Assert.True(expectedCompletion==completed, string.Join("\n",events.Select(e=>e.Kind+": "+e.Text)));
        Assert.Equal(expectedCompletion?"completed":"paused", loop.Goal!.Status);
        Assert.Contains(events, e => e.Tool == "run_command" && e.Kind == "tool_result" && e.Text.Contains("# pass 1"));
        Assert.Equal(expectedCompletion,events.Any(e => e.Kind == "completed"));
        Assert.DoesNotContain(loop.History, m => m.ToJsonString().Contains("data:image"));
        Assert.Equal(repairCycle?2:0,events.Count(e=>e.Kind=="tool_result"&&e.Tool=="run_command"&&ToolOutcome.IsFailure(e.Text)));
    }

    private sealed class UiFixture : IComputerTools
    {
        public string Context => "authorized fixture; no real UI is opened";
        public Task<ComputerToolResult> ExecuteAsync(ToolCall call, CancellationToken cancel) => Task.FromResult(call.Name switch
        {
            "computer_assert" => new ComputerToolResult("assertion_passed=true; observed=\"Saved\""),
            "computer_screenshot" => new ComputerToolResult("{\"snapshot_id\":\"s1\"}", "data:image/jpeg;base64,AQID"),
            _ => new ComputerToolResult("action_dispatched=true"),
        });
    }
}
