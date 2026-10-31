using Muse2Api.Agent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Muse2Api.Tests;

public class ToolCallValidationTests
{
    [Theory]
    [InlineData("{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.txt\"}")]
    [InlineData("{\"tool_calls\":{\"name\":\"read_file\"}}")]
    [InlineData("{\"actions\":null}")]
    [InlineData("{\"tool_calls\":[{\"name\":\"write_file\",\"arguments\":[],\"path\":\"unsafe.txt\",\"content\":\"do not write\"}]}")]
    [InlineData("{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":null}]}")]
    [InlineData("{\"tool_calls\":[{\"name\":\"nonexistent_tool\",\"arguments\":{}}]}")]
    [InlineData("{\"actions\":[],\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{}}]}")]
    public void InvalidPlansAreErrorsInsteadOfFinalAnswersOrFlatArgumentFallback(string plan)
    {
        foreach(var text in new[]{plan,"```json\n"+plan+"\n```"})
            Assert.Equal("__parse_error__",Assert.Single(ToolCallParser.Parse(text)).Name);
    }

    [Fact]
    public async Task InvalidNativeToolInBatchCannotExecuteAnEarlierValidWrite()
    {
        using var dir=new TempDirectory();var events=new List<AgentEvent>();
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(_,_)=>Task.FromResult(("",new List<ToolCall>
        {new("write_file",new(){["path"]="unsafe.txt",["content"]="must not be written"}),new("unsupported_tool",new())})),(_,_)=>Task.FromResult(true),events.Add);
        Assert.False(await loop.RunAsync("test"));Assert.False(File.Exists(dir.File("unsafe.txt")));
        Assert.DoesNotContain(events,e=>e.Kind=="tool_start");
    }

    [Fact]
    public void MalformedSingleToolBlockRejectsValidEarlierBlocks()
    {
        const string plan="```json\n{\"name\":\"write_file\",\"path\":\"unsafe.txt\",\"content\":\"not executed\"}\n```\n```json\n{\"name\":\"read_file\",\"arguments\":\n```";
        var errors=ToolCallParser.Parse(plan);Assert.NotEmpty(errors);Assert.All(errors,e=>Assert.Equal("__parse_error__",e.Name));
    }

    [Fact]
    public void OrdinaryJsonDataIsNotTreatedAsAToolCall()
        => Assert.Empty(ToolCallParser.Parse("```json\n{\"name\":\"Muse\",\"theme\":\"dark\"}\n```"));

    [Theory]
    [InlineData("null")]
    [InlineData("{\"function\":{\"arguments\":{}}}")]
    [InlineData("{\"function\":{\"name\":\"unsupported_tool\",\"arguments\":\"{}\"}}")]
    public async Task MalformedNativeResponseCannotSilentlyExecuteItsValidPrefix(string invalid)
    {
        using var dir=new TempDirectory();
        var response=new JsonObject{["choices"]=new JsonArray(new JsonObject{["message"]=new JsonObject
        {
            ["content"]="",["tool_calls"]=new JsonArray(
                new JsonObject{["function"]=new JsonObject{["name"]="write_file",["arguments"]="{\"path\":\"unsafe.txt\",\"content\":\"not executed\"}"}},JsonNode.Parse(invalid))
        }})};
        using var client=new AgentClient("http://127.0.0.1:1","muse-chat","fixture",messageHandler:new NativeHandler(response.ToJsonString()));
        var loop=new AgentLoop(new ToolExecutor(dir.Root),(messages,token)=>client.ChatAsync(messages,token),(_,_)=>Task.FromResult(true),_=>{});
        Assert.False(await loop.RunAsync("test"));Assert.False(File.Exists(dir.File("unsafe.txt")));
    }

    sealed class NativeHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
            =>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(payload,Encoding.UTF8,"application/json")});
    }
}
