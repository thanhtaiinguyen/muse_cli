using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class ProjectReviewTests
{
    private const string Approved="""{"status":"review","verdict":"approved","summary":"Đã đọc mã hiện tại, kết quả đúng tiêu chí.","findings":[]}""";
    private const string Changes="""{"status":"review","verdict":"changes_requested","summary":"Còn một lỗi cụ thể.","findings":[{"path":"app.txt","line":1,"severity":"high","issue":"Giá trị hiện tại chưa đúng.","fix":"Đổi thành fixed.","verification":"Đọc lại phải bằng fixed."}]}""";
    private static (string,List<ToolCall>) Actions(params ToolCall[] calls)=>("",calls.ToList());
    private static ToolCall Read()=>new("read_file",new(){["path"]="app.txt"});
    private static ToolCall Write(string text,string path="app.txt")=>new("write_file",new(){["path"]=path,["content"]=text});

    [Fact]
    public async Task CoderReviewerFixAndRereviewUseRealFilesAndReviewerCannotWrite()
    {
        using var dir=new TempDirectory();File.WriteAllText(dir.File("app.txt"),"old");var step=0;var events=new List<AgentEvent>();
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(messages,_)=>
        {
            var state=JsonNode.Parse(messages[^1]["content"]!.ToString()["APP_STATE:\n".Length..])!;
            var role=state["project_review"]!["role"]!.ToString();
            var current=step++;
            Assert.Equal(current is 2 or 3 or 4 or 7 or 8?"reviewer":"coder",role);
            return Task.FromResult(current switch {
                0=>Actions(Read(),Write("wrong")),1=>("Đã sửa bản đầu.",new List<ToolCall>()),
                2=>Actions(Write("rogue","rogue.txt")),3=>Actions(Read()),4=>(Changes,new List<ToolCall>()),
                5=>Actions(Read(),Write("fixed")),6=>("Đã sửa đúng giá trị.",new List<ToolCall>()),7=>Actions(Read()),
                _=>(Approved,new List<ToolCall>())});
        },(_,_)=>Task.FromResult(true),events.Add){ProjectReviewEnabled=true};
        Assert.True(await loop.RunAsync("Sửa app.txt rồi kiểm tra lại"),string.Join('\n',events.Select(e=>e.Kind+":"+e.Text)));
        Assert.Equal("fixed",File.ReadAllText(dir.File("app.txt")));Assert.False(File.Exists(dir.File("rogue.txt")));
        Assert.Equal("accepted",loop.Review!.Phase);Assert.Equal(2,loop.Review.Round);
        Assert.DoesNotContain(events,e=>e.Kind=="tool_start"&&e.Path=="rogue.txt");
        Assert.Single(events.Where(e=>e.Kind=="completed"));
        Assert.Contains(events,e=>e.Kind=="assistant"&&e.Text.Contains("Phản biện dự án:"));
    }

    [Fact]
    public async Task ApprovalWithoutIndependentReadCannotCompleteAndDoesNotLoopForever()
    {
        using var dir=new TempDirectory();File.WriteAllText(dir.File("app.txt"),"old");var step=0;var events=new List<AgentEvent>();
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>Task.FromResult(step++ switch {
            0=>Actions(Read(),Write("fixed")),1=>("Đã sửa.",new List<ToolCall>()),_=>(Approved,new List<ToolCall>())
        }),(_,_)=>Task.FromResult(true),events.Add){ProjectReviewEnabled=true};
        Assert.False(await loop.RunAsync("Sửa app.txt"));Assert.Equal(5,step);
        Assert.DoesNotContain(events,e=>e.Kind=="completed");Assert.Equal("reviewer",loop.Review!.Phase);
    }

    [Fact]
    public async Task CheckpointsPreservePendingReviewAndQuestionAcrossRestart()
    {
        using var dir=new TempDirectory();File.WriteAllText(dir.File("app.txt"),"old");var store=new ChatSessionStore(dir.File("sessions"));
        var session=new ChatSession{Workspace=dir.Root};var step=0;
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>{
            if(step++==0)return Task.FromResult(Actions(Read(),Write("fixed")));
            if(step==2)return Task.FromResult(("Đã sửa.",new List<ToolCall>()));
            throw new IOException("Simulated connection loss before review");
        },(_,_)=>Task.FromResult(true),_=>{}){ProjectReviewEnabled=true,SaveCheckpoint=async(snapshot,token)=>{
            session.History=snapshot.History;session.Goal=snapshot.Goal;session.Review=snapshot.Review;await store.SaveAsync(session,token);
        }};
        await Assert.ThrowsAsync<IOException>(()=>loop.RunAsync("Sửa app.txt"));
        var loaded=await store.LoadAsync(dir.Root,session.Id);Assert.Equal("reviewer",loaded.Review!.Phase);
        Assert.Contains(loaded.History,m=>m["muse_tool"]?.ToString()=="write_file");
        step=0;
        var resumed=new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>Task.FromResult(step++==0?Actions(Read()):(Approved,new List<ToolCall>())),(_,_)=>Task.FromResult(true),_=>{})
            {ProjectReviewEnabled=true,Review=loaded.Review};
        resumed.History.AddRange(loaded.History);
        Assert.True(await resumed.RunAsync("tiếp tục đi"));Assert.Equal("accepted",resumed.Review!.Phase);
    }

    [Theory]
    [InlineData("cảm ơn ní")][InlineData("hướng dẫn sửa app.txt")]
    public async Task ReviewSettingDoesNotActivateForDiscussion(string prompt)
    {
        using var dir=new TempDirectory();var loop=new AgentLoop(new ToolExecutor(dir.Root),(messages,_)=>{
            var state=JsonNode.Parse(messages[^1]["content"]!.ToString()["APP_STATE:\n".Length..])!;Assert.Null(state["project_review"]);
            return Task.FromResult(("Đã hiểu.",new List<ToolCall>()));
        },(_,_)=>Task.FromResult(true),_=>{}){ProjectReviewEnabled=true};
        Assert.True(await loop.RunAsync(prompt));Assert.Null(loop.Review);
    }

    [Fact]
    public void RepeatedFindingsWithoutCodeProgressCannotBecomeAnEndlessCycle()
    {
        using var dir=new TempDirectory();var review=new ProjectReview();review.StartReview("candidate");review.Record(Read(),"current source",false);
        Assert.Equal("changes_requested",review.ApplyDecision(Changes,dir.Root));
        review.StartReview("same code");review.Record(Read(),"current source",false);
        Assert.Throws<InvalidOperationException>(()=>review.ApplyDecision(Changes.Replace("Giá trị hiện tại chưa đúng.","Cùng lỗi, diễn đạt khác."),dir.Root));
    }
}
