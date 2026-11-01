using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class WorkflowTests
{
    [Theory]
    [InlineData("{\"status\":\"needs_input\",\"summary\":\"Chọn thư mục nguồn để tiếp tục.\"}")]
    [InlineData("```json\n{\"status\":\"needs_input\",\"reply\":\"Bạn muốn dùng framework nào?\"}\n```")]
    public async Task StructuredQuestionsPauseOnceWithoutFalseCompletionOrRepairSpam(string response)
    {
        using var dir = new TempDirectory(); var requests=0;var events=new List<AgentEvent>();
        var loop = new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>{requests++;return Task.FromResult((response,new List<ToolCall>()));},(_,_)=>Task.FromResult(true),events.Add);
        Assert.False(await loop.RunAsync("/goal Thêm tính năng đăng nhập"));
        Assert.Equal(1,requests);Assert.Equal("waiting_input",loop.Goal!.Status);
        Assert.Equal(ToolCallParser.PlannerReply(response),loop.Goal.PendingQuestion);
        Assert.Contains(events,e=>e.Kind=="waiting_input");
        Assert.DoesNotContain(events,e=>e.Kind is "completed" or "protocol_retry" or "incomplete");
        Assert.DoesNotContain(events,e=>e.Kind=="assistant"&&e.Text.Contains("```"));
    }

    [Fact]
    public async Task AnswerAfterRestartResumesTheOriginalGoalAndKeepsUserTurnSeparate()
    {
        using var dir=new TempDirectory();var store=new ChatSessionStore(dir.File("sessions"));
        var session=new ChatSession {Workspace=dir.Root,Goal=new(){Objective="Thêm tính năng đăng nhập",Status="waiting_input",PendingQuestion="Dùng framework nào?"}};
        await store.SaveAsync(session);var loaded=await store.LoadAsync(dir.Root,session.Id);
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(messages,_)=>
        {
            var state=JsonNode.Parse(messages[^1]["content"]!.ToString()["APP_STATE:\n".Length..])!;
            Assert.Contains("Thêm tính năng đăng nhập",state["goal"]!.ToString());
            Assert.Contains("Dùng WinForms",state["goal"]!.ToString());
            Assert.NotNull(state["goal_mode"]);
            return Task.FromResult(("{\"status\":\"needs_input\",\"summary\":\"Tên ứng dụng là gì?\"}",new List<ToolCall>()));
        },(_,_)=>Task.FromResult(true),_=>{}) {Goal=loaded.Goal};
        Assert.False(await loop.RunAsync("Dùng WinForms"));
        Assert.Equal("Thêm tính năng đăng nhập",loop.Goal!.Objective);
        Assert.Equal("Dùng WinForms",Assert.Single(loop.Goal.FollowUps));
        Assert.Contains(loop.History,m=>m["content"]?.ToString()=="Dùng WinForms");
        Assert.Equal("waiting_input",loop.Goal.Status);
    }

    [Theory]
    [InlineData("cảm ơn ní nha")]
    [InlineData("ok rồi")]
    [InlineData("hướng dẫn chạy test cho tôi")]
    public async Task DiscussionCannotExecuteAnAccidentalActionBatch(string prompt)
    {
        using var dir=new TempDirectory();var events=new List<AgentEvent>();var requests=0;var approvals=0;
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>Task.FromResult(++requests==1
            ? ("",new List<ToolCall>{new("write_file",new(){["path"]="unexpected.txt",["content"]="not requested"})})
            : ("{\"status\":\"done\",\"summary\":\"Mình đã hiểu.\"}",new List<ToolCall>())),(_,_)=>{approvals++;return Task.FromResult(true);},events.Add)
            {Goal=new(){Objective="Việc cũ",Status="paused"}};
        Assert.True(await loop.RunAsync(prompt));Assert.False(File.Exists(dir.File("unexpected.txt")));
        Assert.Equal(0,approvals);Assert.Equal(2,requests);Assert.DoesNotContain(events,e=>e.Kind=="tool_start");
        Assert.Equal("paused",loop.Goal.Status);
    }

    [Fact]
    public async Task ThanksDoesNotResumeAGoalWaitingForAnAnswer()
    {
        using var dir=new TempDirectory();var goal=new GoalRun{Objective="Xây ứng dụng",Status="waiting_input",PendingQuestion="Tên ứng dụng?"};
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(messages,_)=>
        {
            var state=JsonNode.Parse(messages[^1]["content"]!.ToString()["APP_STATE:\n".Length..])!;
            Assert.Null(state["goal_mode"]);return Task.FromResult(("Không có gì nhé.",new List<ToolCall>()));
        },(_,_)=>Task.FromResult(true),_=>{}) {Goal=goal};
        Assert.True(await loop.RunAsync("cảm ơn ní"));Assert.Equal("waiting_input",goal.Status);Assert.Empty(goal.FollowUps);
    }

    [Fact]
    public async Task ReadOnlyTurnRejectsWholeMixedBatchThenAllowsFreshRead()
    {
        using var dir=new TempDirectory();File.WriteAllText(dir.File("app.txt"),"original");var requests=0;var events=new List<AgentEvent>();
        var read=new ToolCall("read_file",new(){["path"]="app.txt"});
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>Task.FromResult(++requests switch
        {
            1=>("",new List<ToolCall>{read,new("write_file",new(){["path"]="app.txt",["content"]="unrequested"})}),
            2=>("",new List<ToolCall>{read}),
            _=>("Nội dung gốc: original.",new List<ToolCall>())
        }),(_,_)=>throw new InvalidOperationException("No approval should be requested"),events.Add);
        Assert.True(await loop.RunAsync("Chỉ đọc app.txt, không sửa file hay chạy lệnh."));
        Assert.Equal("original",File.ReadAllText(dir.File("app.txt")));
        Assert.Single(events.Where(e=>e.Kind=="tool_start"));
    }

    [Fact]
    public async Task ExplainingAProjectAllowsInspectionButNeverAnUnrequestedEdit()
    {
        using var dir=new TempDirectory();File.WriteAllText(dir.File("app.txt"),"original");var requests=0;
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>Task.FromResult(++requests switch {
            1=>("",new List<ToolCall>{new("write_file",new(){["path"]="oops.txt",["content"]="wrong"})}),
            2=>("",new List<ToolCall>{new("read_file",new(){["path"]="app.txt"})}),
            _=>("Ứng dụng dùng tệp app.txt.",new List<ToolCall>())
        }),(_,_)=>throw new InvalidOperationException("No edits authorized"),_=>{});
        Assert.True(await loop.RunAsync("Giải thích dự án của tôi"));Assert.False(File.Exists(dir.File("oops.txt")));
    }

    [Theory]
    [InlineData("{\"status\":\"needs_input\",\"summary\":\"Tên ứng dụng?\",\"actions\":[{\"action\":\"save_text\",\"path\":\"app.txt\",\"text\":\"bad\"}]}")]
    [InlineData("```json\n{\"status\":\"needs_input\",\"summary\":\"Tên ứng dụng?\",\"tool_calls\":[{\"name\":\"write_file\",\"arguments\":{\"path\":\"app.txt\",\"content\":\"bad\"}}]}\n```")]
    public void WaitingForInputCannotExecuteChangesInTheSameEnvelope(string response)
        => Assert.Equal("__parse_error__",Assert.Single(ToolCallParser.Parse(response)).Name);

    [Fact]
    public async Task SkillsRemainPresentInActualContinuationPackets()
    {
        using var dir=new TempDirectory();var skills=dir.File("skills");Directory.CreateDirectory(skills);
        File.WriteAllText(Path.Combine(skills,"fe.md"),"# Frontend\nKEEP_CSS_GRID_RULE");File.WriteAllText(dir.File("a.txt"),"data");var requests=0;
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(messages,_)=>
        {
            var state=JsonNode.Parse(messages[^1]["content"]!.ToString()["APP_STATE:\n".Length..])!;
            Assert.Contains("KEEP_CSS_GRID_RULE",state["active_skills"]!.ToString());
            if(requests>0) Assert.Contains("KEEP_CSS_GRID_RULE",Prompt.LocalContinuation(messages.Select(m=>(m["role"]!.ToString(),m["content"]!.ToString())).ToList())!);
            return Task.FromResult(++requests==1?("",new List<ToolCall>{new("read_file",new(){["path"]="a.txt"})}):("Đã đọc.",new List<ToolCall>()));
        },(_,_)=>Task.FromResult(true),_=>{}) {SkillsDirectory=skills};
        Assert.True(await loop.RunAsync("@fe đọc a.txt"));Assert.Equal(2,requests);
    }

    [Fact]
    public async Task NaturalContinueRestoresAPausedGoalButNeverRestartsACompletedOne()
    {
        using var dir=new TempDirectory();var goal=new GoalRun {Objective="Thêm tính năng tìm kiếm",Status="paused"};var expectGoal=true;
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(messages,_)=>
        {
            var state=JsonNode.Parse(messages[^1]["content"]!.ToString()["APP_STATE:\n".Length..])!;
            Assert.Equal(expectGoal,state["goal_mode"] is not null);
            return Task.FromResult(expectGoal?("{\"status\":\"needs_input\",\"summary\":\"Tìm theo tên hay mô tả?\"}",new List<ToolCall>()):("Đã hoàn tất mục tiêu trước đó.",new List<ToolCall>()));
        },(_,_)=>Task.FromResult(true),_=>{}) {Goal=goal};
        Assert.False(await loop.RunAsync("tiếp tục đi"));Assert.Empty(goal.FollowUps);
        goal.Status="completed";goal.PendingQuestion=null;expectGoal=false;
        Assert.True(await loop.RunAsync("tiếp tục đi"));Assert.Equal("completed",goal.Status);
    }

    [Fact]
    public async Task GoalDoesNotPauseForAnUnnecessaryModelPermissionQuestion()
    {
        using var dir=new TempDirectory();var requests=0;var events=new List<AgentEvent>();
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>
        {
            requests++;return Task.FromResult(("{\"status\":\"needs_input\",\"summary\":\"Bạn có muốn mình chạy các bài test không?\"}",new List<ToolCall>()));
        },(_,_)=>Task.FromResult(true),events.Add);
        Assert.False(await loop.RunAsync("/goal Thêm tính năng đăng nhập"));Assert.Equal(3,requests);
        Assert.DoesNotContain(events,e=>e.Kind=="waiting_input");Assert.Equal("paused",loop.Goal!.Status);
    }

    [Fact]
    public async Task CompletedGoalCannotSilentlyRestartItsEditsOnAnUnqualifiedContinue()
    {
        using var dir=new TempDirectory();var requests=0;
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>Task.FromResult(++requests==1
            ?("",new List<ToolCall>{new("write_file",new(){["path"]="restarted.txt",["content"]="old work"})})
            :("Mục tiêu trước đã hoàn tất; bạn muốn làm thêm phần nào?",new List<ToolCall>())),(_,_)=>throw new InvalidOperationException("Old goal must not restart"),_=>{})
            {Goal=new(){Objective="Việc cũ",Status="completed"}};
        await loop.RunAsync("tiếp tục đi");Assert.False(File.Exists(dir.File("restarted.txt")));Assert.Equal("completed",loop.Goal.Status);
    }
}
