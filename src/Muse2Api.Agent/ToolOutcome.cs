using System.Text.RegularExpressions;

namespace Muse2Api.Agent;

public static class ToolOutcome
{
    private static string Metadata(string result)
    {
        var boundary = Regex.Match(result, @"(?m)^(?:stdout:|stderr:|output \()");
        return boundary.Success ? result[..boundary.Index] : result;
    }
    public static bool IsRunning(string result) => Regex.IsMatch(Metadata(result), @"(?im)^running=true\s*$");
    public static bool ExitedSuccessfully(string result) => Regex.IsMatch(Metadata(result), @"(?m)^exit_code=0\s*$") && !IsFailure(result);
    public static bool IsFailure(string result)
    {
        if (result.StartsWith("stopped=true\n", StringComparison.Ordinal)) return false;
        if (result.StartsWith("error:", StringComparison.OrdinalIgnoreCase)) return true;
        var code = Regex.Match(Metadata(result), @"(?m)^exit_code=(-?\d+)\s*$");
        return code.Success && code.Groups[1].Value != "0" || Metadata(result).Contains("timed_out=true", StringComparison.Ordinal);
    }
}
