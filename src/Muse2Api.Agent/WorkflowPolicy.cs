using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Muse2Api.Agent;

/// <summary>Limit clear discussion/read-only turns before approving any batch.</summary>
public static class WorkflowPolicy
{
    private static string Fold(string text) => string.Concat(text.ToLowerInvariant().Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).Replace('đ', 'd');
    public static bool IsThanksOrGreeting(string text) => Regex.IsMatch(Fold(text).Trim(),
        @"^(?:(?:cam on|thanks?|thank you|xin chao|chao|hello|hi)(?:\s+(?:ban|ni|nhe|nha|nhieu|a|oi|rat nhieu))*[.!,:\s]*)+$");
    public static bool IsSmallTalk(string text) => IsThanksOrGreeting(text) || Regex.IsMatch(Fold(text).Trim(),
        @"^(?:ok(?:ay)?|oke|uh|u|duoc|tot|hay|tuyet)(?:\s+(?:roi|do|nhe|nha|ni|a))*[.!\s]*$");
    public static bool IsExplanation(string text) => Regex.IsMatch(Fold(text).Trim(),
        @"^(?:(?:hay|vui long|ban|ni)\s+)?(?:huong dan|lam the nao|giai thich|how to|show me how|co nen|theo ban)\b");
    public static bool IsProjectExplanation(string text) => Regex.IsMatch(Fold(text).Trim(),
        @"^(?:(?:hay|vui long|ban|ni)\s+)?giai thich\b") && Regex.IsMatch(Fold(text), @"\b(?:du an|project|repo|code|ma nguon|file|tep)\b");
    public static bool IsReadOnly(string text) => Regex.IsMatch(Fold(text),
        @"\bchi (?:can )?(?:doc|xem|tim hieu|phan tich|giai thich|review)\b")
        && !Regex.IsMatch(Fold(text), @"\b(?:roi|sau do|va) (?:hay )?(?:sua|chay|thuc hien|cap nhat)\b");
    public static bool IsContinue(string text) => Regex.IsMatch(Fold(text).Trim(),
        @"^(?:(?:hay|ban|ni)\s+)?(?:tiep tuc|continue|resume)(?:\s+(?:di|nhe|nha|ni|goal|cho den khi xong))*[.!\s]*$");
    public static bool IsPermissionQuestion(string text) => Regex.IsMatch(Fold(text),
        @"\b(?:ban (?:co )?muon (?:minh|toi) (?:thuc hien|chay|sua|viet|soan|tiep tuc|ap dung)|cho phep (?:minh|toi) (?:chay|sua|tiep tuc)|may i (?:run|edit|implement|continue))\b");
    public static bool Allows(string prompt, ToolCall call, bool goal)
    {
        if (goal && !IsReadOnly(prompt)) return true;
        if (IsSmallTalk(prompt)) return false;
        if (IsExplanation(prompt)) return IsProjectExplanation(prompt)
            && call.Name is "read_file" or "read_files" or "search_files" or "list_files" or "git_status" or "git_diff";
        var required = LocalTaskIntent.Required(prompt);
        var diagnosticRead = required == LocalWork.Read && Regex.IsMatch(Fold(prompt), @"\b(?:tai sao|vi sao|nguyen nhan|why)\b");
        if (IsReadOnly(prompt) || diagnosticRead)
            return call.Name is "read_file" or "read_files" or "search_files" or "list_files" or "git_status" or "git_diff"
                || required.HasFlag(LocalWork.Computer) && call.Name is "computer_inspect" or "computer_screenshot" or "computer_assert";
        return true;
    }
}
