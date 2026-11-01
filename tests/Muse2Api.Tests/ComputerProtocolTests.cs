using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class ComputerProtocolTests
{
    [Theory]
    [InlineData("Nhập Xin chào vào ô tìm kiếm", LocalWork.Computer | LocalWork.ComputerInput)]
    [InlineData("Cuộn xuống trong cửa sổ đã chọn", LocalWork.Computer | LocalWork.ComputerInteract)]
    [InlineData("Kéo thả mục này", LocalWork.Computer | LocalWork.ComputerInteract)]
    [InlineData("Bấm chuột phải vào mục đó", LocalWork.Computer | LocalWork.ComputerClick)]
    public void NaturalScreenRequestsDoNotForceUnrelatedProjectReads(string prompt,LocalWork required)
    {
        Assert.Equal(required,LocalTaskIntent.Required(prompt));
    }

    [Fact]
    public void UiTestCanUseNativeToolsWhileExplicitProjectAndShellWorkStillRequireReceipts()
    {
        var ui=LocalTaskIntent.Required("Chạy test UI: nhập vào ô tìm kiếm rồi bấm nút Lưu");
        Assert.False(ui.HasFlag(LocalWork.Command));Assert.False(ui.HasFlag(LocalWork.Read));Assert.True(ui.HasFlag(LocalWork.ComputerAssert));
        var both=LocalTaskIntent.Required("Đọc file App.cs, chạy dotnet test rồi kiểm thử giao diện");
        Assert.True(both.HasFlag(LocalWork.Read));Assert.True(both.HasFlag(LocalWork.CommandVerified));Assert.True(both.HasFlag(LocalWork.ComputerAssert));
    }

    [Fact]
    public void DiagnosticWhyQuestionAboutCodeIsReadOnlyNotAComputerTask()
    {
        var required=LocalTaskIntent.Required("kiểm tra code xem tại sao điều khiển màn hình vẫn chưa được thực hiện tự động vậy");
        Assert.Equal(LocalWork.Read,required);
    }

    [Fact]
    public void WhyQuestionThatAlsoAsksForAFixStillRequiresAnEdit()
    {
        var required=LocalTaskIntent.Required("tại sao code này không bấm được nút, sửa giúp tôi");
        Assert.True(required.HasFlag(LocalWork.Edit));
    }

    [Fact]
    public async Task ANewTurnReleasesThePreviousScreenshotBuffer()
    {
        using var dir=new TempDirectory();var tools=new ToolExecutor(dir.Root){Computer=new ImageComputer()};
        await tools.ExecuteAsync(new("computer_screenshot",new()));Assert.NotNull(tools.LastComputerImage);
        tools.BeginAgentTurn();Assert.Null(tools.LastComputerImage);
    }

    [Theory]
    [InlineData("computer_inspect")]
    [InlineData("computer_screenshot")]
    public async Task FreshTransportIdsDoNotHideAnUnchangedUiLoop(string tool)
    {
        using var dir = new TempDirectory(); var attempts = 0;
        var computer = new ChangingIdComputer();
        var loop = new AgentLoop(new ToolExecutor(dir.Root) { Computer = computer }, (_, _) =>
        {
            attempts++; return Task.FromResult(("", new List<ToolCall> { new(tool, new()) }));
        }, (_, _) => Task.FromResult(true), _ => { });
        Assert.False(await loop.RunAsync("Kiểm thử giao diện"));
        Assert.Equal(3, attempts);
    }
    [Theory]
    [InlineData("{\"actions\":[{\"action\":\"ui_drag\",\"snapshot_id\":\"s1\",\"x\":2,\"y\":3,\"to_x\":40,\"to_y\":50}]}", "computer_drag")]
    [InlineData("{\"actions\":[{\"action\":\"ui_focus\",\"element_id\":\"e1\"}]}", "computer_focus")]
    [InlineData("{\"actions\":[{\"action\":\"ui_click\",\"snapshot_id\":\"s1\",\"x\":2,\"y\":3,\"button\":\"right\"}]}", "computer_click")]
    [InlineData("{\"actions\":[{\"action\":\"ui_scroll\",\"snapshot_id\":\"s1\",\"x\":2,\"y\":3,\"direction\":\"left\"}]}", "computer_scroll")]
    [InlineData("{\"actions\":[{\"action\":\"ui_tree\",\"query\":\"Save\",\"offset\":80,\"limit\":20}]}", "computer_inspect")]
    [InlineData("{\"actions\":[{\"action\":\"ui_type\",\"text\":\"Xin chào\"}]}", "computer_type")]
    public void ExtendedUiActionsReachTheLocalExecutor(string text,string name)
        =>Assert.Equal(name,Assert.Single(ToolCallParser.Parse(text)).Name);

    [Theory]
    [InlineData("computer_drag")] [InlineData("computer_focus")] [InlineData("computer_scroll")] [InlineData("computer_key")]
    public async Task AUiMutationAfterAnAssertionCannotReuseTheOldProof(string mutation)
    {
        using var dir=new TempDirectory();var computer=new FixtureComputer();var step=0;var events=new List<AgentEvent>();
        var loop=new AgentLoop(new ToolExecutor(dir.Root){Computer=computer},(_,_)=>Task.FromResult(++step switch
        {
            1=>("",new List<ToolCall>{new("computer_assert",new(){["element_id"]="result",["property"]="name",["expected"]="Saved"})}),
            2=>("",new List<ToolCall>{new(mutation,new())}),
            _=>("Đã kiểm thử giao diện thành công.",new List<ToolCall>()),
        }),(_,_)=>Task.FromResult(true),events.Add);
        Assert.False(await loop.RunAsync("Kiểm thử giao diện"));
        Assert.DoesNotContain(events,e=>e.Kind=="completed");
    }

    private sealed class FixtureComputer:IComputerTools
    {
        public string Context=>"fixture window authorized";
        public Task<ComputerToolResult> ExecuteAsync(ToolCall call,CancellationToken cancel)=>Task.FromResult(new ComputerToolResult(call.Name=="computer_assert"?"assertion_passed=true":"action_dispatched=true"));
    }
    private sealed class ImageComputer:IComputerTools
    {
        public string Context=>"fixture image";
        public Task<ComputerToolResult> ExecuteAsync(ToolCall call,CancellationToken cancel)=>Task.FromResult(new ComputerToolResult("fixture image","data:image/jpeg;base64,AQID"));
    }
    private sealed class ChangingIdComputer:IComputerTools
    {
        public string Context => "fixture";
        public Task<ComputerToolResult> ExecuteAsync(ToolCall call, CancellationToken cancel)
        {
            var state = call.Name == "computer_inspect"
                ? new JsonObject { ["inspection_id"] = Guid.NewGuid().ToString(), ["elements"] = new JsonArray(new JsonObject { ["element_id"] = Guid.NewGuid().ToString(), ["name"] = "Unchanged" }) }
                : new JsonObject { ["snapshot_id"] = Guid.NewGuid().ToString(), ["image_fingerprint"] = "same-frame", ["width"] = 800, ["height"] = 600 };
            return Task.FromResult(new ComputerToolResult(state.ToJsonString()));
        }
    }
}
