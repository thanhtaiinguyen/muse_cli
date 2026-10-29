using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class TurboApprovalPolicyTests
{
    private static ToolCall Call(string name, string? path = null)
    {
        var args = new JsonObject();
        if (path is not null) args["path"] = path;
        return new ToolCall(name, args);
    }

    [Theory]
    [InlineData("run_command")]
    [InlineData("process_input")]
    [InlineData("process_stop")]
    public void ShellAndProcessToolsAlwaysRequireApproval(string tool)
        => Assert.True(TurboApprovalPolicy.RequiresExplicitApproval(Call(tool)));

    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env.production")]
    [InlineData("security/private.key")]
    [InlineData(".github/workflows/build.yml")]
    [InlineData("AGENTS.md")]
    [InlineData("src/Muse2Api/Muse2Api.csproj")]
    [InlineData("folder\\secrets\\token.txt")]
    public void SensitiveFileEditsRequireApproval(string file)
        => Assert.True(TurboApprovalPolicy.RequiresExplicitApproval(Call("edit_file", file)));

    [Theory]
    [InlineData("src/Example.cs")]
    [InlineData("docs/README.md")]
    public void NormalWorkspaceEditsCanBeAutoApproved(string file)
        => Assert.False(TurboApprovalPolicy.RequiresExplicitApproval(Call("write_file", file)));
}
