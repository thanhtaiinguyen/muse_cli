using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class AgentContinuationTests
{
    [Theory]
    [InlineData("thực hiện tối ưu hóa lại ui cho mình đi mình muốn phần cài đặt có dark mode và lightmode")]
    [InlineData("them nut tim kiem cho toi")]
    [InlineData("Hãy cải thiện giao diện chat")]
    [InlineData("thực hiện đi")]
    [InlineData("Tôi muốn phần cài đặt có dark mode và light mode")]
    [InlineData("Add dark mode to settings")]
    public void NaturalImplementationRequests_RequireAnActualEdit(string prompt)
        => Assert.True(LocalTaskIntent.Required(prompt).HasFlag(LocalWork.Edit));

    [Theory]
    [InlineData("Hướng dẫn tôi thêm dark mode")]
    [InlineData("How to edit a file?")]
    public void AdviceRequests_DoNotRequireWrites(string prompt)
        => Assert.Equal(LocalWork.None, LocalTaskIntent.Required(prompt));

    [Fact]
    public async Task ReadThenVmRefusalEndingInQuestion_MustContinueToLocalEdit()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("theme.txt"), "dark");
        var replies = new[]
        {
            "{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"theme.txt\"}}]}",
            "Mình đang ở trên web, không thể sửa bản Windows của bạn. Bạn muốn mình soạn đoạn sửa để bạn áp dụng?",
            "{\"tool_calls\":[{\"name\":\"edit_file\",\"arguments\":{\"path\":\"theme.txt\",\"old_text\":\"dark\",\"new_text\":\"light\"}}]}",
            "Đã đổi giao diện theo yêu cầu.",
        };
        var index = 0;
        var events = new List<AgentEvent>();
        var loop = new AgentLoop(new ToolExecutor(directory.Root), (messages, _) =>
        {
            Assert.Contains("APP_STATE:", messages[^1]["content"]!.ToString());
            Assert.DoesNotContain(messages, m => m["role"]?.ToString() == "assistant" && m["content"]?.ToString().Contains("Bạn phải push mã trước") == true);
            var text = replies[index++];
            return Task.FromResult((text, ToolCallParser.Parse(text)));
        }, (_, _) => Task.FromResult(true), events.Add);
        loop.History.Add(new() { ["role"] = "assistant", ["content"] = "Bản copy trên VM Linux không phải bản Windows. Bạn phải push mã trước." });
        Assert.True(await loop.RunAsync("Thực hiện tối ưu hóa UI của tôi, đổi theme.txt thành light"));
        Assert.Equal(4, index);
        Assert.Equal("light", File.ReadAllText(directory.File("theme.txt")));
        Assert.DoesNotContain(events, e => e.Kind == "waiting_input");
        Assert.DoesNotContain(events, e => e.Kind == "assistant" && e.Text.Contains("trên web"));
        Assert.Single(events.Where(e => e.Kind == "protocol_retry"));
    }

    [Fact]
    public async Task SuccessfulRead_DoesNotValidateAFinalVmRefusal()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("a.txt"), "local");
        var index = 0;
        var events = new List<AgentEvent>();
        var loop = new AgentLoop(new ToolExecutor(directory.Root), (_, _) =>
        {
            var text = index++ == 0 ? "{\"name\":\"read_file\",\"path\":\"a.txt\"}"
                : "Mình đang ở trên web, không truy cập được bản Windows. Nội dung bạn dán là bản VM Linux.";
            return Task.FromResult((text, ToolCallParser.Parse(text)));
        }, (_, _) => Task.FromResult(true), events.Add);
        Assert.False(await loop.RunAsync("Đọc file a.txt trong dự án của tôi"));
        Assert.DoesNotContain(events, e => e.Kind == "completed");
    }

    [Fact]
    public void GenuineClarification_IsStillAllowed()
    {
        Assert.True(LocalTaskIntent.IsClarification("Bạn muốn thêm tính năng nào vào dự án?"));
        Assert.False(LocalTaskIntent.IsClarification("Mình không thể sửa bản Windows trên web. Bạn muốn mình soạn hướng dẫn?"));
    }

    [Fact]
    public void CodeBlockLanguageLabel_DoesNotLeakIntoChat()
    {
        const string reply = "Mình sẽ đọc tệp.\n\njson\n```json\n{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.cs\"}}]}\n```";
        Assert.Equal("Mình sẽ đọc tệp.", ToolCallParser.StripToolBlocks(reply));
    }
}
