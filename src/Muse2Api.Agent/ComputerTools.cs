namespace Muse2Api.Agent;

public sealed record ComputerToolResult(string Text, string? ImageDataUrl = null);

public interface IComputerTools
{
    string Context { get; }
    Task<ComputerToolResult> ExecuteAsync(ToolCall call, CancellationToken cancel);
}

public sealed partial class ToolExecutor
{
    public IComputerTools? Computer { get; set; }
    public string? LastComputerImage { get; private set; }

    private async Task<string> ComputerAsync(ToolCall call, CancellationToken cancel)
    {
        LastComputerImage = null;
        if (Computer is null) return "error: Computer tools are unavailable in this host. Use the Muse desktop application.";
        var result = await Computer.ExecuteAsync(call, cancel);
        LastComputerImage = result.ImageDataUrl;
        return result.Text;
    }
}
