using System.Text;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class FileSearchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchAcceptsAnIndividualSourceFile(bool absolute)
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("src/Muse2Api.WinForms"));
        const string relative = "src/Muse2Api.WinForms/AgentWorkspaceControl.cs";
        File.WriteAllText(dir.File(relative), "// workspace\r\nDesktopTheme.Apply(this);\r\n");
        var tools = new ToolExecutor(dir.Root);
        var result = await tools.ExecuteAsync(new("search_files", new()
        {
            ["path"] = absolute ? dir.File(relative) : relative, ["query"] = "DesktopTheme"
        }));
        Assert.Contains(relative + ":2: DesktopTheme.Apply(this);", result);
        Assert.Contains("scanned 1 files", result);
        Assert.False(ToolOutcome.IsFailure(result), result);
    }

    [Fact]
    public async Task FileScopeDoesNotSearchSiblingFilesAndHonorsCaseAndUnicode()
    {
        using var dir = new TempDirectory();
        const string path = "giao diện.cs";
        File.WriteAllText(dir.File(path), "// CÀI ĐẶT\n// cài đặt");
        File.WriteAllText(dir.File("sibling.cs"), "// cài đặt");
        var tools = new ToolExecutor(dir.Root);
        var call = new ToolCall("search_files", new() { ["path"] = path, ["query"] = "cài đặt" });
        var result = await tools.ExecuteAsync(call);
        Assert.Contains("2 matches", result);
        Assert.DoesNotContain("sibling.cs", result);
        call.Args["case_sensitive"] = true;
        result = await tools.ExecuteAsync(call);
        Assert.DoesNotContain(path + ":1:", result);
        Assert.Contains(path + ":2:", result);
        call.Args["query"] = "missing";
        Assert.Equal("No matches (scanned 1 files).", await tools.ExecuteAsync(call));
    }

    [Theory]
    [InlineData("missing.cs")]
    [InlineData("missing/directory")]
    public async Task MissingPathReportsAnActionableError(string path)
    {
        using var dir = new TempDirectory();
        var result = await new ToolExecutor(dir.Root).ExecuteAsync(new("search_files", new()
        { ["path"] = path, ["query"] = "DesktopTheme" }));
        Assert.StartsWith("error: search path not found:", result);
        Assert.Contains(path, result);
        Assert.DoesNotContain("parameter is incorrect", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectorySearchStillRecursesAndSkipsGeneratedAndBinaryFiles()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("src/nested"));
        Directory.CreateDirectory(dir.File("src/obj"));
        File.WriteAllText(dir.File("src/nested/a.cs"), "DesktopTheme");
        File.WriteAllText(dir.File("src/obj/generated.cs"), "DesktopTheme");
        File.WriteAllBytes(dir.File("src/image.bin"), new byte[] { 0xff, 0xfe, 0, 0 });
        var result = await new ToolExecutor(dir.Root).ExecuteAsync(new("search_files", new()
        { ["path"] = "src", ["query"] = "DesktopTheme" }));
        Assert.Contains("src/nested/a.cs:1: DesktopTheme", result);
        Assert.DoesNotContain("generated.cs", result);
        Assert.False(ToolOutcome.IsFailure(result), result);
    }

    [Fact]
    public async Task UnreadableIndividualFileReportsErrorInsteadOfNoMatches()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.File("binary.dat"), new byte[] { 0, 1, 2, 3 });
        var result = await new ToolExecutor(dir.Root).ExecuteAsync(new("search_files", new()
        { ["path"] = "binary.dat", ["query"] = "anything" }));
        Assert.StartsWith("error:", result);
        Assert.Contains("binary", result);
    }

    [Fact]
    public async Task FileSearchCanLeadToAReadAndTargetedAgentEdit()
    {
        using var dir = new TempDirectory();
        const string path = "MainForm.cs";
        const string original = "// keep this\r\nDesktopTheme.Old(this);\r\n// keep this too\r\n";
        File.WriteAllText(dir.File(path), original, new UTF8Encoding(true));
        var tools = new ToolExecutor(dir.Root);
        tools.BeginAgentTurn();
        var found = await tools.ExecuteAsync(new("search_files", new()
        { ["path"] = path, ["query"] = "DesktopTheme" }));
        Assert.False(ToolOutcome.IsFailure(found), found);
        await tools.ExecuteAsync(new("read_file", new() { ["path"] = path, ["details"] = true }));
        var edit = await tools.PrepareForAgentAsync(new("edit_file", new()
        { ["path"] = path, ["old_text"] = "DesktopTheme.Old(this);", ["new_text"] = "DesktopTheme.Apply(this);" }));
        Assert.StartsWith("edited", await tools.ExecutePreparedAsync(edit));
        Assert.Equal(original.Replace(".Old(", ".Apply("), File.ReadAllText(dir.File(path)));
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, File.ReadAllBytes(dir.File(path)).Take(3));
        Assert.Single(tools.Changes);
    }

    [Fact]
    public async Task FileScopeCannotEscapeTheWorkspace()
    {
        using var dir = new TempDirectory();
        using var outside = new TempDirectory();
        File.WriteAllText(outside.File("private.cs"), "secret needle");
        var result = await new ToolExecutor(dir.Root).ExecuteAsync(new("search_files", new()
        { ["path"] = outside.File("private.cs"), ["query"] = "needle" }));
        Assert.StartsWith("error:", result);
        Assert.Contains("outside the workspace", result);
        Assert.DoesNotContain("secret needle", result);
    }
}
