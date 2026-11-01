using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class ConversationIntentTests
{
    [Theory]
    [InlineData("mà dùng vpn test luồng chạy dễ ăn captcha lắm")]
    [InlineData("Khi chạy test thì ứng dụng hay bị chậm")]
    [InlineData("Tôi thấy app đang chạy hơi chậm")]
    [InlineData("Nếu chạy test thì có cần mạng không?")]
    [InlineData("Chạy test có cần mạng không?")]
    [InlineData("Đừng chạy test")]
    [InlineData("Không cần chạy lệnh")]
    [InlineData("Don't run tests")]
    [InlineData("The tests run slowly on my computer")]
    public void MentioningCommandsDoesNotRequireShellExecution(string prompt)
        => Assert.Equal(LocalWork.None, LocalTaskIntent.Required(prompt));

    [Theory]
    [InlineData("Chạy dotnet test")]
    [InlineData("Hãy chạy test giúp tôi")]
    [InlineData("Bạn chạy test giúp tui đi")]
    [InlineData("Tôi muốn chạy test")]
    [InlineData("Bạn có thể chạy dotnet test được không?")]
    [InlineData("Đọc file rồi chạy kiểm thử")]
    [InlineData("Please run tests")]
    public void ActualCommandRequestsStillRequireVerifiedExecution(string prompt)
    {
        var required = LocalTaskIntent.Required(prompt);
        Assert.True(required.HasFlag(LocalWork.Command), required.ToString());
        Assert.True(required.HasFlag(LocalWork.CommandVerified), required.ToString());
    }

    [Fact]
    public async Task DiscussionAfterCodingCompletesInOneRequestWithoutForcingTools()
    {
        using var dir = new TempDirectory();
        var calls = 0;
        var events = new List<AgentEvent>();
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (messages, _) =>
        {
            calls++;
            var state = JsonNode.Parse(messages[^1]["content"]!.ToString()["APP_STATE:\n".Length..])!;
            Assert.Equal("None", state["remaining"]!.ToString());
            Assert.Contains("discussion", state["next"]!.ToString());
            return Task.FromResult(("Mình hiểu, bạn đang mô tả vấn đề chứ chưa yêu cầu chạy lệnh.", new List<ToolCall>()));
        }, (_, _) => throw new Exception("Discussion unexpectedly requested approval"), events.Add);
        loop.History.Add(new() { ["role"] = "user", ["content"] = "Chạy test dự án" });
        loop.History.Add(new() { ["role"] = "assistant", ["content"] = "Đã chạy test, exit_code=0." });
        Assert.True(await loop.RunAsync("Khi chạy test thì ứng dụng hay bị chậm"));
        Assert.Equal(1, calls);
        Assert.Single(events.Where(e => e.Kind == "completed"));
        Assert.DoesNotContain(events, e => e.Kind is "tool_start" or "protocol_retry" or "incomplete");
    }

    [Fact]
    public void ContinuationKeepsExecutionFeedbackAndCurrentGoalWithoutOldSource()
    {
        var prompt = Prompt.LocalContinuation(new()
        {
            ("user", "old source that must not be resent"),
            ("assistant", "[Unverified reply: missing operations.] rejected reply"),
            ("user", "LOCAL EXECUTOR: Required check has not run. Current task: Chạy test"),
            ("user", "APP_STATE:\n{\"goal\":\"Chạy test\",\"remaining\":\"CommandVerified\"}"),
        });
        var inputs = JsonNode.Parse(prompt[(prompt.IndexOf("INPUT DATA:", StringComparison.Ordinal) + "INPUT DATA:".Length)..])!.AsArray();
        Assert.Contains(inputs, i => i!["kind"]!.ToString() == "execution_repair" && i["text"]!.ToString().Contains("has not run"));
        Assert.Contains(inputs, i => i!["kind"]!.ToString() == "current_app_state" && i["text"]!.ToString().Contains("CommandVerified"));
        Assert.DoesNotContain("old source that must not be resent", prompt);
        Assert.DoesNotContain("rejected reply", prompt);
    }

    [Fact]
    public async Task GenuineCommandWithoutReceiptsStillCannotClaimCompletion()
    {
        using var dir = new TempDirectory();
        var calls = 0;
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (messages, _) =>
        {
            if (calls++ > 0)
            {
                var transport = Prompt.LocalContinuation(messages.Select(m => (m["role"]!.ToString(), m["content"]!.ToString())).ToList());
                Assert.Contains("execution_repair", transport);
                Assert.Contains("actions in the application schema", transport);
            }
            return Task.FromResult(("Đã chạy kiểm thử thành công.", new List<ToolCall>()));
        }, (_, _) => Task.FromResult(true), _ => { });
        Assert.False(await loop.RunAsync("Chạy dotnet test"));
        Assert.Equal(3, calls);
    }
}
