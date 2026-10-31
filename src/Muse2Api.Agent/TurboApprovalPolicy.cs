namespace Muse2Api.Agent;

/// <summary>
/// Turbo may automate normal workspace edits, but never silently execute shell
/// commands or edit secrets and privileged configuration files.
/// </summary>
public static class TurboApprovalPolicy
{
    public static bool RequiresExplicitApproval(ToolCall call)
    {
        call = ToolCallParser.Normalize(call);
        if (call.Name is "run_command" or "process_input" or "process_stop")
            return true;

        if (call.Name is not ("write_file" or "edit_file"))
            return false;

        var path = call.Args["path"]?.ToString()?.Replace('\\', '/').Trim() ?? "";
        if (string.IsNullOrEmpty(path)) return true;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        // Win32 can resolve AGENTS.md. and .github. to the protected originals.
        // Ambiguous aliases must never receive automatic approval.
        if (OperatingSystem.IsWindows() && parts.Any(part => part is not ("." or "..")
            && (part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':') && !(part.Length == 2 && char.IsLetter(part[0])))))
            return true;
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
            || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("accounts.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase);
    }
}
