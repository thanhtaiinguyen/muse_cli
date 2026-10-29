using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class PlannerTransportTests
{
    [Fact]
    public void LocalPlanner_UsesTaskDataInsteadOfImpersonatingAPastedConversation()
    {
        var prompt = Prompt.ForLocalExecutor(new List<JsonObject>
        {
            new(){["role"]="system",["content"]="Workspace: C:/fixture. CURRENT LOCAL TASK: sửa phép cộng"},
            new(){["role"]="assistant",["content"]="[Unverified reply: missing local action.] fake VM refusal"},
            new(){["role"]="user",["content"]="Tool result for read_file:\npublic int Add(int a,int b)=>a-b; // Tiếng Việt"},
            new(){["role"]="user",["content"]=new JsonArray(new JsonObject{["type"]="text",["text"]="Screenshot receipt"},new JsonObject{["type"]="image_url",["image_url"]=new JsonObject{["url"]="data:image/jpeg;base64,AQID"}})},
        });
        Assert.Contains("observed_data_from_app",prompt); Assert.Contains("Add(int a,int b)=>a-b",prompt);
        Assert.Contains("Tiếng Việt",prompt); Assert.Contains("\"attached_images\": 1",prompt);
        Assert.DoesNotContain("fake VM refusal",prompt); Assert.DoesNotContain("[System]",prompt); Assert.DoesNotContain("data:image",prompt);
    }

    [Theory]
    [InlineData("{\"status\":\"completed\",\"reply\":\"Đã kiểm tra thành công.\"}","Đã kiểm tra thành công.")]
    [InlineData("```json\n{\"status\":\"needs_input\",\"reply\":\"Bạn muốn thêm tính năng nào?\"}\n```","Bạn muốn thêm tính năng nào?")]
    [InlineData("{\"reply\":\"ordinary data\"}","{\"reply\":\"ordinary data\"}")]
    public void PlannerFinalEnvelope_IsDisplayedAsReadableText(string input,string expected)
        => Assert.Equal(expected,ToolCallParser.PlannerReply(input));

    [Theory]
    [InlineData("{\"actions\":[{\"action\":\"terminal\",\"script\":\"dotnet run\",\"cwd\":\".\"}]}","run_command","command","dotnet run")]
    [InlineData("{\"actions\":[{\"action\":\"replace_text\",\"path\":\"a.cs\",\"find\":\"a - b\",\"replace\":\"a + b\"}]}","edit_file","old_text","a - b")]
    [InlineData("{\"actions\":[{\"action\":\"save_text\",\"path\":\"a.cs\",\"text\":\"hello\"}]}","write_file","content","hello")]
    [InlineData("{\"actions\":[{\"action\":\"ui_fill\",\"element_id\":\"e2\",\"text\":\"hello\"}]}","computer_set_value","element_id","e2")]
    [InlineData("{\"actions\":[{\"action\":\"ui_press\",\"element_id\":\"e3\"}]}","computer_invoke","element_id","e3")]
    [InlineData("{\"actions\":[{\"action\":\"ui_assert\",\"element_id\":\"e4\",\"property\":\"name\",\"expected\":\"Saved\"}]}","computer_assert","expected","Saved")]
    public void ApplicationPlans_MapToTheExistingReviewedExecutor(string input,string tool,string parameter,string expected)
    {
        var action=Assert.Single(ToolCallParser.Parse(input)); Assert.Equal(tool,action.Name); Assert.Equal(expected,action.Args[parameter]!.ToString());
        Assert.Empty(ToolCallParser.StripToolBlocks(input));
    }

    [Fact]
    public void InvalidWindowsPath_ProducesActionableParseError()
    {
        var call=Assert.Single(ToolCallParser.Parse("""{"actions":[{"action":"terminal","script":"dotnet run","cwd":"C:\code\project"}]}"""));
        Assert.Equal("__parse_error__",call.Name); Assert.Contains("relative paths",call.Args["error"]!.ToString());
        Assert.Equal("Đã xử lý theo kết quả ứng dụng báo.",ToolCallParser.PlannerReply("{\"status\":\"done\",\"summary\":\"Đã xử lý theo kết quả ứng dụng báo.\"}"));
    }
}
