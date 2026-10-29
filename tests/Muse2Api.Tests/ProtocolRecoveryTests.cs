using System.Text;
using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class ProtocolRecoveryTests
{
    // The saved 0.7.1 reply contained source quotes rendered outside a code fence.
    private const string BrokenReply = """{"actions":[{"action":"replace_text","path":"theme.cs","find":"old","replace":"Path.Combine(root, "Muse2Api.WinForms", "theme.json");"}]}""";

    [Fact]
    public void MalformedSourceQuotesReportJsonPositionAndCodeFenceRepair()
    {
        var error = Assert.Single(ToolCallParser.Parse(BrokenReply));
        Assert.Equal("__parse_error__", error.Name);
        var detail = error.Args["error"]!.ToString();
        Assert.Contains("BytePositionInLine", detail);
        Assert.Contains("```json", detail);
        Assert.Contains("escape", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidBatchNeverReturnsExecutableActionsFromItsValidPrefix()
    {
        const string mixed = """
            ```json
            {"actions":[{"action":"save_text","path":"unsafe.txt","text":"must not be written"}]}
            ```
            ```json
            {"actions":[{"action":"terminal","script":"echo "invalid""}]}
            ```
            """;
        Assert.All(ToolCallParser.Parse(mixed), call => Assert.Equal("__parse_error__", call.Name));
    }

    [Fact]
    public void BrowserPlannerRequiresFencedEscapedJsonAndKeepsRepairFeedback()
    {
        var prompt = Prompt.ForLocalExecutor(new List<JsonObject>
        {
            new() { ["role"] = "assistant", ["content"] = "[Invalid plan: rejected before execution.]\nDO_NOT_REPEAT_THIS_BROKEN_PLAN" },
            new() { ["role"] = "user", ["content"] = "APP_PROTOCOL_REPAIR:\n{\"error\":\"quote at position 677\",\"max_actions\":1}" },
        });
        Assert.Contains("```json", prompt);
        Assert.Contains("\\\"", prompt);
        Assert.Contains("protocol_repair", prompt);
        Assert.Contains("quote at position 677", prompt);
        Assert.DoesNotContain("DO_NOT_REPEAT_THIS_BROKEN_PLAN", prompt);
    }

    [Fact]
    public async Task TwoMalformedRepliesCanRecoverToAReviewedWriteWithoutChangingCodeBytes()
    {
        using var dir = new TempDirectory();
        const string before = "// keep\r\nvar value = \"old\";\r\n";
        const string after = "// keep\r\nvar value = \"Muse\\theme.json\";\r\n";
        File.WriteAllText(dir.File("theme.cs"), before, new UTF8Encoding(true));
        var good = new JsonObject { ["actions"] = new JsonArray(new JsonObject
        {
            ["action"] = "replace_text", ["path"] = "theme.cs", ["find"] = "var value = \"old\";",
            ["replace"] = "var value = \"Muse\\theme.json\";"
        }) }.ToJsonString();
        var replies = new[]
        {
            """{"actions":[{"action":"inspect_text","path":"theme.cs","details":true}]}""",
            BrokenReply, BrokenReply, "```json\n" + good + "\n```", "Đã sửa file theo yêu cầu."
        };
        var index = 0; var approvals = 0; var events = new List<AgentEvent>();
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (messages, _) =>
        {
            if (index is 2 or 3)
            {
                var feedback = Prompt.ForLocalExecutor(messages);
                Assert.Contains("protocol_repair", feedback);
                Assert.DoesNotContain("Path.Combine(root", feedback);
                Assert.Equal(before, File.ReadAllText(dir.File("theme.cs")));
            }
            var reply = replies[index++];
            return Task.FromResult((reply, ToolCallParser.Parse(reply)));
        }, (approval, _) => { approvals++; Assert.Equal("edit_file", approval.Call.Name); return Task.FromResult(true); }, events.Add);
        Assert.True(await loop.RunAsync("Đọc và sửa theme.cs"));
        Assert.Equal(after, File.ReadAllText(dir.File("theme.cs")));
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, File.ReadAllBytes(dir.File("theme.cs")).Take(3));
        Assert.Equal(1, approvals);
        Assert.DoesNotContain(events, e => e.Kind == "tool_start" && e.Tool == "__parse_error__");
        Assert.Equal(2, events.Count(e => e.Kind == "protocol_retry"));
    }

    [Fact]
    public async Task RepeatedMalformedPlansStopWithASpecificMessageAndNoWrites()
    {
        using var dir = new TempDirectory();
        var calls = 0; var approvals = 0; var events = new List<AgentEvent>();
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (_, _) =>
        {
            calls++;
            return Task.FromResult((BrokenReply, ToolCallParser.Parse(BrokenReply)));
        }, (_, _) => { approvals++; return Task.FromResult(true); }, events.Add);
        Assert.False(await loop.RunAsync("Sửa theme.cs"));
        Assert.InRange(calls, 3, 4);
        Assert.Equal(0, approvals);
        Assert.Empty(Directory.GetFiles(dir.Root));
        Assert.Contains(events, e => e.Kind == "incomplete" && e.Text.Contains("định dạng"));
    }
}
