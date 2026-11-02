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
        if (WorkflowPolicy.IsProjectExplanation(prompt)) return LocalWork.Read;
        if (WorkflowPolicy.IsSmallTalk(prompt) || WorkflowPolicy.IsExplanation(prompt)) return LocalWork.None;
        if (Regex.IsMatch(text, @"^(?:hay |vui long )?(?:huong dan|lam the nao|giai thich cach|how to|show me how)\b")) return LocalWork.None;
        // A diagnostic why-question about the code/project asks for an
        // explanation, even when it mentions screen control. With no edit
        // request in it, it is read-only project work, never a UI operation
        // on a real window.
        if (Regex.IsMatch(text, @"\b(?:tai sao|vi sao|nguyen nhan|why)\b") && Regex.IsMatch(text, @"\b(?:code|ma nguon|project|repo)\b")
            && !Regex.IsMatch(text, @"\b(?:sua|chinh sua|fix|implement)\b")) return LocalWork.Read;
        var result = LocalWork.None;
        // “độ hoàn thiện” is a property to assess, not an instruction to edit.
        if (Regex.IsMatch(text, @"\b(?:kiem tra|danh gia|xem) (?:muc )?do hoan thien\b")) result |= LocalWork.Read;
        var editText = Regex.Replace(text, @"\b(?:muc )?do hoan thien\b", "muc do day du");
        if (Regex.IsMatch(text, @"\b(doc|xem (?:file|tep|du an|ma|git)|tim (?:file|tep|loi|kiem)|kiem tra (?:du an|ma|thay doi|code)|tim hieu du an|giai thich du an|read|inspect|search files|review project)\b")) result |= LocalWork.Read;
        if (Regex.IsMatch(text, @"\b(liet ke|cau truc|list files|list directory)\b")) result |= LocalWork.Browse;
        if (Regex.IsMatch(editText, @"\b(sua|chinh sua|cap nhat (?:file|tep|ma|ui)|tao (?:file|tep)|them (?:tinh nang|chuc nang|nut|che do|dark|light)|doi (?:theme|mau|giao dien)|toi uu|cai thien|hoan thien|thiet ke lai|nang cap|phat trien|edit|modify|replace|fix|implement|optimize|create (?:a )?file|add (?:a )?(?:button|feature|dark|light|theme))\b")) result |= LocalWork.Edit;
        if (Regex.IsMatch(text, @"\b(?:toi|minh) muon (?:phan cai dat|giao dien|theme|dark|light)\b")) result |= LocalWork.Edit;
        if (RequestsCommand(text)) result |= LocalWork.Command;
        if (Regex.IsMatch(text, @"^(?:hay |ban )?(?:thuc hien|lam)(?: luon| di| ngay| giup| cho minh| cho toi)+[.! ]*$")) result |= LocalWork.Edit;
        if (Regex.IsMatch(text, @"\b(dieu khien (?:man hinh|cua so)|kiem thu giao dien|test ui|test giao dien|click|bam nut|chup (?:man hinh|cua so)|render 3d|game 3d|app 3d|giao dien (?:3d|app)|ung dung (?:3d|desktop)|electron|app chu khong phai (?:la )?web)\b")) result |= LocalWork.Computer;
        if (Regex.IsMatch(text, @"\b(kiem thu giao dien|test ui|test giao dien|render 3d|game 3d|app 3d|giao dien (?:3d|app)|ung dung (?:3d|desktop)|electron|app chu khong phai (?:la )?web)\b")) result |= LocalWork.ComputerAssert;
        if (Regex.IsMatch(text, @"\b(chup (?:man hinh|cua so)|render 3d|game 3d|app 3d|giao dien (?:3d|app)|ung dung (?:3d|desktop)|electron|app chu khong phai (?:la )?web)\b")) result |= LocalWork.ComputerScreenshot;
        if (Regex.IsMatch(text, @"\b(click|bam nut)\b")) result |= LocalWork.ComputerClick;
        if (Regex.IsMatch(text, @"\b(cuon (?:xuong|len|sang|trai|phai)|keo tha|bam (?:chuot phai|dup)|nhap dup|doc (?:giao dien|man hinh|cua so)|(?:nhap|dien|go)\b[^\n.!?]{0,100}\b(?:vao o|textbox|truong nhap))\b")) result |= LocalWork.Computer;
        if (Regex.IsMatch(text, @"\b(?:keo tha|cuon (?:xuong|len|sang|trai|phai))\b")) result |= LocalWork.ComputerInteract;
        if (Regex.IsMatch(text, @"\b(?:bam (?:chuot phai|dup)|nhap dup)\b")) result |= LocalWork.ComputerClick;
        if ((result & ComputerWork) != 0 && Regex.IsMatch(text, @"\b(nhap|dien|go)\b.*\b(o|input|textbox|truong)\b")) result |= LocalWork.ComputerInput;
        if (result.HasFlag(LocalWork.Command) && Regex.IsMatch(text, @"\b(kiem tra|kiem thu|tests?|build|bien dich)\b")) result |= LocalWork.CommandVerified;
        if ((result & ComputerWork) != 0)
        {
            // UI labels such as "ô tìm kiếm" and "đọc giao diện" are not file
            // work. Likewise "chạy test UI" need not invoke a shell command.
            var explicitProjectRead = Regex.IsMatch(text, @"\b(?:doc|xem|read|inspect|tim|kiem tra)\b[^\n!?]{0,60}\b(?:file|tep|du an|project|ma nguon|code|thu muc|repository)\b")
                || Regex.IsMatch(text, @"\b(?:doc|read)\b[^\n!?]{0,60}\.[a-z0-9]{1,8}\b");
            if (!explicitProjectRead && !Regex.IsMatch(text, @"\b(render 3d|game 3d|ung dung desktop|electron|app chu khong phai)\b")) result &= ~LocalWork.Read;
            if (!explicitProjectRead) result &= ~LocalWork.Read;
            if (!Regex.IsMatch(text, @"\b(?:liet ke (?:file|tep|thu muc)|list files|list directory)\b")) result &= ~LocalWork.Browse;
            if (Regex.IsMatch(text, @"\b(?:kiem thu giao dien|test ui|test giao dien)\b")
                && !Regex.IsMatch(text, @"\b(?:cmd|powershell|terminal|dotnet|npm|pnpm|yarn|python|bash|lenh|shell)\b")) result &= ~(LocalWork.Command | LocalWork.CommandVerified);
        }
        if (WorkflowPolicy.IsReadOnly(prompt))
        {
            result &= ~(LocalWork.Edit | LocalWork.Command | LocalWork.CommandVerified | LocalWork.ComputerInteract | LocalWork.ComputerClick | LocalWork.ComputerInput);
            if (result == LocalWork.None) result = LocalWork.Read;
        }
        return result;
    }

    private static bool RequestsCommand(string text)
    {
        // Completion requirements need a request, not just a mention of running.
        // For example, "khi chạy test bị lỗi" describes a problem; it does not
        // authorize a shell command. Keep this separate from model tool selection.
        foreach (Match match in Regex.Matches(text, @"\b(chay|thuc thi|run|execute)\b"))
        {
            var before = text[..match.Index];
            var after = text[(match.Index + match.Length)..];
            var clause = Regex.Split(before, @"[\r\n,;.!?]")[^1].Trim();
            var tail = Regex.Split(after, @"[\r\n;!?]")[0];
            if (Regex.IsMatch(clause, @"\b(dung|cho|khong|chua|do not|don't|never)(?: can)?(?: tu)?(?: dong)?$")
                || Regex.IsMatch(tail, @"^.*\b(co can|tai sao|vi sao|nghia la gi|de lam gi|why|what happens)\b")) continue;

            if (clause.Length == 0
                || Regex.IsMatch(clause, @"\b(hay|vui long|lam on|nho|please|then|roi|sau do|va)$")
                || Regex.IsMatch(clause, @"^(?:ban|ni)$|\b(?:(?:toi|tui|minh) (?:muon|can)|(?:ban|ni) (?:hay|co the)|(?:giup|cho) (?:toi|tui|minh))(?: (?:ban|ni))?$")
                || Regex.IsMatch(tail, @"\b(?:giup|cho|ho) (?:toi|tui|minh)\b")) return true;
        }
        return false;
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
        "computer_invoke" or "computer_click" or "computer_toggle" or "computer_select" or "computer_expand" => LocalWork.ComputerInteract | LocalWork.ComputerClick,
        "computer_set_value" or "computer_type" => LocalWork.ComputerInteract | LocalWork.ComputerInput,
        "computer_key" or "computer_scroll" or "computer_drag" or "computer_focus" => LocalWork.Computer | LocalWork.ComputerInteract,
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
