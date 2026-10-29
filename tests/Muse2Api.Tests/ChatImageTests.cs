using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class ChatImageTests
{
    [Fact]
    public async Task PastedImageReachesTheAgentAndKeepsItsIdentityAcrossToolRounds()
    {
        using var dir = new TempDirectory(); File.WriteAllText(dir.File("a.txt"), "fixture");
        const string image = "data:image/png;base64,iVBORw0KGgo=";
        string? identity = null; var calls = 0;
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (messages, _) =>
        {
            var parts = messages.Where(m => m["content"] is JsonArray).SelectMany(m => ((JsonArray)m["content"]!).OfType<JsonObject>()).Where(p => p["type"]?.ToString() == "image_url").ToArray();
            Assert.Single(parts); Assert.Equal(image, parts[0]["image_url"]?["url"]!.ToString());
            var current = Prompt.ImageBatchId(messages); Assert.False(string.IsNullOrEmpty(current));
            if (identity is null) identity = current; else Assert.Equal(identity, current);
            var text = calls++ == 0 ? "{\"name\":\"read_file\",\"path\":\"a.txt\"}" : "Đã đọc file và xem ảnh.";
            return Task.FromResult((text, ToolCallParser.Parse(text)));
        }, (_, _) => Task.FromResult(true), _ => { });
        Assert.True(await loop.RunAsync("Đọc file a.txt và xem ảnh", images: new() { image }));
        Assert.Equal(2, calls); Assert.DoesNotContain(image, new JsonArray(loop.History.Select(m => m.DeepClone()).ToArray()).ToJsonString());
    }

    [Fact]
    public async Task ImageOnlyMessageIsAllowedAndASecondPasteGetsANewIdentity()
    {
        using var dir = new TempDirectory(); var identities = new List<string?>();
        var loop = new AgentLoop(new ToolExecutor(dir.Root), (messages, _) =>
        {
            identities.Add(Prompt.ImageBatchId(messages)); return Task.FromResult(("Đã xem ảnh.", new List<ToolCall>()));
        }, (_, _) => Task.FromResult(true), _ => { });
        Assert.True(await loop.RunAsync("", images: new() { "data:image/png;base64,AQID" }));
        Assert.True(await loop.RunAsync("", images: new() { "data:image/png;base64,AQID" }));
        Assert.NotEqual(identities[0], identities[1]);
    }
}
