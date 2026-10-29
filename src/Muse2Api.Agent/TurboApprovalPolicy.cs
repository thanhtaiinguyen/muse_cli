namespace Muse2Api.Agent;

/// <summary>
/// Turbo may automate normal workspace edits, but never silently execute shell
/// commands or edit secrets and privileged configuration files.
/// </summary>
public static class TurboApprovalPolicy
{
    public static bool RequiresExplicitApproval(ToolCall call)
    {
        if (call.Name is "run_command" or "process_input" or "process_stop")
            return true;

        if (call.Name is not ("write_file" or "edit_file"))
            return false;

        var path = call.Args["path"]?.ToString()?.Replace('\\', '/').Trim() ?? "";
        if (string.IsNullOrEmpty(path)) return true;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || part.Equals(".github", StringComparison.OrdinalIgnoreCase)
            || part.Equals("secrets", StringComparison.OrdinalIgnoreCase)
            || part.Equals("credentials", StringComparison.OrdinalIgnoreCase)
            || part.Equals("browser-sessions", StringComparison.OrdinalIgnoreCase)))
            return true;

        var name = parts[^1];
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase);
    }
}
