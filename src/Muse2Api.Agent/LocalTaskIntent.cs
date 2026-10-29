using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;

namespace Muse2Api.Agent;

[Flags]
public enum LocalWork { None = 0, Read = 1, Edit = 2, Command = 4, Browse = 8, Computer = 16, ComputerInteract = 32, ComputerAssert = 64, ComputerClick = 128, ComputerInput = 256, CommandVerified = 512, ComputerScreenshot = 1024 }

/// <summary>Completion checks only: this never executes or approves an operation.</summary>
public static class LocalTaskIntent
{
    public const LocalWork ComputerWork = LocalWork.Computer | LocalWork.ComputerInteract | LocalWork.ComputerAssert | LocalWork.ComputerClick | LocalWork.ComputerInput | LocalWork.ComputerScreenshot;
    public static LocalWork Required(string prompt)
    {
        var text = Fold(prompt);
        if (Regex.IsMatch(text, @"^(?:hay |vui long )?(?:huong dan|lam the nao|giai thich cach|how to|show me how)\b")) return LocalWork.None;
        var result = LocalWork.None;
        if (Regex.IsMatch(text, @"\b(doc|xem (?:file|tep|du an|ma|git)|tim (?:file|tep|loi|kiem)|kiem tra (?:du an|ma|thay doi)|tim hieu du an|giai thich du an|read|inspect|search files|review project)\b")) result |= LocalWork.Read;
        if (Regex.IsMatch(text, @"\b(liet ke|cau truc|list files|list directory)\b")) result |= LocalWork.Browse;
        if (Regex.IsMatch(text, @"\b(sua|chinh sua|cap nhat (?:file|tep|ma|ui)|tao (?:file|tep)|them (?:tinh nang|chuc nang|nut|che do|dark|light)|doi (?:theme|mau|giao dien)|toi uu|cai thien|hoan thien|thiet ke lai|nang cap|phat trien|edit|modify|replace|fix|implement|optimize|create (?:a )?file|add (?:a )?(?:button|feature|dark|light|theme))\b")) result |= LocalWork.Edit;
        if (Regex.IsMatch(text, @"\b(?:toi|minh) muon (?:phan cai dat|giao dien|theme|dark|light)\b")) result |= LocalWork.Edit;
        if (Regex.IsMatch(text, @"\b(chay|thuc thi|run|execute)\b")) result |= LocalWork.Command;
        if (Regex.IsMatch(text, @"^(?:hay |ban )?(?:thuc hien|lam)(?: luon| di| ngay| giup| cho minh| cho toi)+[.! ]*$")) result |= LocalWork.Edit;
        if (Regex.IsMatch(text, @"\b(dieu khien (?:man hinh|cua so)|kiem thu giao dien|test ui|test giao dien|click|bam nut|chup (?:man hinh|cua so))\b")) result |= LocalWork.Computer;
        if (Regex.IsMatch(text, @"\b(kiem thu giao dien|test ui|test giao dien)\b")) result |= LocalWork.ComputerAssert;
        if (Regex.IsMatch(text, @"\bchup (?:man hinh|cua so)\b")) result |= LocalWork.ComputerScreenshot;
        if (Regex.IsMatch(text, @"\b(click|bam nut)\b")) result |= LocalWork.ComputerClick;
        if ((result & ComputerWork) != 0 && Regex.IsMatch(text, @"\b(nhap|dien|go)\b.*\b(o|input|textbox|truong)\b")) result |= LocalWork.ComputerInput;
        if (result.HasFlag(LocalWork.Command) && Regex.IsMatch(text, @"\b(kiem tra|kiem thu|test|build|bien dich)\b")) result |= LocalWork.CommandVerified;
        return result;
    }

    public static LocalWork Evidence(string tool) => tool switch
    {
        "read_file" or "read_files" or "search_files" or "git_diff" => LocalWork.Read,
        "list_files" or "git_status" => LocalWork.Browse,
        "write_file" or "edit_file" => LocalWork.Edit,
        "run_command" or "process_status" => LocalWork.Command,
        "computer_inspect" => LocalWork.Computer,
        "computer_screenshot" => LocalWork.Computer | LocalWork.ComputerScreenshot,
        "computer_assert" => LocalWork.Computer | LocalWork.ComputerAssert,
        "computer_invoke" or "computer_click" => LocalWork.ComputerInteract | LocalWork.ComputerClick,
        "computer_set_value" or "computer_type" => LocalWork.ComputerInteract | LocalWork.ComputerInput,
        "computer_key" or "computer_toggle" or "computer_select" or "computer_expand" or "computer_scroll" => LocalWork.ComputerInteract,
        _ => LocalWork.None,
    };

    public static bool IsEnvironmentConfusion(string text)
    {
        var value = Fold(text);
        return Regex.IsMatch(value, @"\b(vm linux|may ao|tren web|web muse|muse tren web|ban windows|ban copy tren vm)\b")
            && Regex.IsMatch(value, @"\b(khong (?:the|tu|truy cap|sua|phai)|chua sua|ban dan|chi la chu|push|gui (?:file|noi dung))\b");
    }

    public static bool IsClarification(string text)
        => text.TrimEnd().EndsWith('?') && !IsEnvironmentConfusion(text)
            && !Regex.IsMatch(Fold(text), @"\b(da (?:sua|chay|doc|tao)|completed|executed|modified|neu ban muon|ban (?:co )?muon (?:minh )?(?:thuc hien|bam|chay|sua|viet|soan|tiep tuc|ap dung))\b");

    public static bool ExplainsNoChange(string text)
        => Regex.IsMatch(Fold(text), @"\b(khong can (?:sua|thay doi)|khong co thay doi|noi dung da khop|giu nguyen|no changes? needed|already (?:matches|correct))\b");

    private static string Fold(string text)
        => string.Concat(text.ToLowerInvariant().Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
            .Replace('đ', 'd').Normalize(NormalizationForm.FormC);
}
