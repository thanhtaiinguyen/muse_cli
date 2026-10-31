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

    [Theory]
    [InlineData("AGENTS.md.")]
    [InlineData("private.pem.")]
    [InlineData(".github./workflows/build.yml")]
    [InlineData("config/secrets /token.txt")]
    [InlineData("file.txt:stream")]
    public void WindowsAliasesCannotBypassApproval(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(TurboApprovalPolicy.RequiresExplicitApproval(Call("write_file",path)));
    }

    [Theory]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    [InlineData("global.json")]
    [InlineData("accounts.json")]
    [InlineData("api_key")]
    public void BuildAndCredentialConfigurationRequiresApproval(string path)
        => Assert.True(TurboApprovalPolicy.RequiresExplicitApproval(Call("edit_file",path)));

    [Theory]
    [InlineData("run_cmd")]
    [InlineData("run_powershell")]
    [InlineData("runCommand")]
    public void ShellAliasesRequireApproval(string tool)
        => Assert.True(TurboApprovalPolicy.RequiresExplicitApproval(Call(tool)));

    [Theory]
    [InlineData("AGENTS.md","AGENTS.md.")]
    [InlineData("private.pem","private.pem.")]
    [InlineData(".github/workflows/test.yml",".github./workflows/test.yml")]
    public async Task PreparedChangesToProtectedAliasesRequireReview(string canonical,string alias)
    {
        if(!OperatingSystem.IsWindows())return;
        using var dir=new TempDirectory();var file=dir.File(canonical);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,"initial");
        var tools=new ToolExecutor(dir.Root);tools.BeginAgentTurn();await tools.ExecuteAsync(new("read_file",new(){["path"]=alias}));
        var prepared=await tools.PrepareForAgentAsync(new("write_file",new(){["path"]=alias,["content"]="modified"}));
        Assert.True(TurboApprovalPolicy.RequiresExplicitApproval(prepared.Call));Assert.Equal("initial",File.ReadAllText(file));
    }
}
