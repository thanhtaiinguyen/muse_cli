using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Muse2Api.Agent;

/// <summary>Two explicit roles on the existing connection; reviewer never mutates the project.</summary>
public sealed class ProjectReview
{
    public string Objective { get; set; } = "";
    public string Phase { get; set; } = "coder";
    public int Round { get; set; }
    public List<ReviewFinding> Findings { get; set; } = new();
    public string CandidateSummary { get; set; } = "";
    public string ReviewSummary { get; set; } = "";
    public int Revision { get; set; }
    public int LastRejectedRevision { get; set; } = -1;
    public string LastRejectedSignature { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool FreshRead { get; private set; }
    public bool IsReviewer => Phase == "reviewer";
    public void Resume() => FreshRead = false;

    public const string Instructions = """
        PROJECT REVIEW MODE: two roles, one existing conversation. Follow project_review.role.
        coder: implement the user's actual objective, fix the concrete findings, test again. Do not invent
        changes to satisfy a reviewer; if a finding is wrong, provide fresh file/verification evidence.
        reviewer: independently READ the relevant current project files/diff with local read tools before
        judging. Old summaries are not proof. Check correctness, acceptance criteria, tests, UI behavior,
        error handling and regressions. You may only inspect files/list/git diff; never edit, run commands
        or control windows as reviewer. Ask coder to perform missing checks. Cite exact paths/lines and
        concrete reproducible defects; avoid speculative cosmetic churn or unrelated feature demands.
        Return ONE json fence with {"status":"review","verdict":"approved","summary":"Vietnamese review","findings":[]}
        OR {"status":"review","verdict":"changes_requested","summary":"Vietnamese review","findings":[
        {"path":"relative/file","line":1,"severity":"high|medium|low","issue":"actual problem",
        "fix":"requested correction","verification":"check that proves the correction"}]}.
        At most 12 findings. Approval requires fresh inspection and no unresolved findings.
        Never claim perfection or missing tests passed. Preserve task scope, user data and permissions.
        """;

    public static bool CanInspect(ToolCall call) => call.Name is "read_file" or "read_files" or "search_files" or "list_files" or "git_status" or "git_diff";
    public void StartReview(string summary)
    {
        Phase = "reviewer"; Round++; FreshRead = false; CandidateSummary = Clip(summary, 4000);
    }
    public void Record(ToolCall call, string result, bool changed)
    {
        if (changed) Revision++;
        if (IsReviewer && !ToolOutcome.IsFailure(result) && call.Name is "read_file" or "read_files" or "git_diff") FreshRead = true;
    }
    public JsonObject Snapshot() => new()
    {
        ["role"] = IsReviewer ? "reviewer" : "coder", ["round"] = Round, ["objective"] = Objective,
        ["fresh_read"] = FreshRead, ["findings"] = System.Text.Json.JsonSerializer.SerializeToNode(Findings, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }),
        ["instructions"] = Instructions,
    };
    public string ApplyDecision(string text, string root)
    {
        if (!FreshRead) throw new InvalidOperationException("Phản biện cần đọc lại mã hiện tại trước khi kết luận.");
        var candidates = Regex.Matches(text, @"(?s)```(?:json)?\s*\n?(.*?)\n?```").Select(m => m.Groups[1].Value).Prepend(text.Trim());
        JsonObject? report = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var trimmed = candidate.Trim();
                if (JsonNode.Parse(trimmed) is JsonObject obj && obj["status"]?.ToString() == "review") { report = obj; break; }
            }
            catch (System.Text.Json.JsonException) { }
        }
        if (report is null)
        {
            var first = text.IndexOf('{');
            var last = text.LastIndexOf('}');
            if (first >= 0 && last > first)
            {
                try
                {
                    if (JsonNode.Parse(text[first..(last + 1)]) is JsonObject obj && obj["status"]?.ToString() == "review") report = obj;
                }
                catch (System.Text.Json.JsonException) { }
            }
        }
        if (report is null)
        {
            var lower = text.ToLowerInvariant();
            if (Regex.IsMatch(lower, @"(chap thuan|phe duyet|dong y|approved|dat yeu cau|khong co loi|chuan xac|hoan thanh tot)")
                && !Regex.IsMatch(lower, @"(can sua|yeu cau sua|loi|chua dat|that bai|changes_requested)"))
            {
                report = new JsonObject
                {
                    ["status"] = "review",
                    ["verdict"] = "approved",
                    ["summary"] = Clip(text.Trim(), 1000),
                    ["findings"] = new JsonArray()
                };
            }
        }
        if (report is null || report["findings"] is not JsonArray findings || findings.Count > 12)
            throw new InvalidOperationException("Cần báo cáo phản biện đúng JSON, gồm verdict, summary và findings (tối đa 12).");
        var verdict = report["verdict"]?.ToString();
        if (verdict is not ("approved" or "changes_requested") || verdict == "approved" && findings.Count != 0 || verdict == "changes_requested" && findings.Count == 0)
            throw new InvalidOperationException("Kết luận phản biện không khớp danh sách vấn đề.");
        var parsed = new List<ReviewFinding>();
        foreach (var finding in findings)
        {
            var path = finding?["path"]?.GetValue<string>() ?? "";
            var full = Path.GetFullPath(path, root);
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || !full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Đường dẫn phản biện phải nằm trong dự án.");
            var line = finding?["line"]?.GetValue<int>() ?? 1;
            var severity = finding?["severity"]?.ToString() ?? "";
            var issue = finding?["issue"]?.GetValue<string>() ?? "";
            var fix = finding?["fix"]?.GetValue<string>() ?? "";
            var check = finding?["verification"]?.GetValue<string>() ?? "";
            if (line < 1 || severity is not ("high" or "medium" or "low") || new[] { issue, fix, check }.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Vấn đề phản biện cần vị trí, mức độ, lỗi cụ thể, cách sửa và cách kiểm chứng.");
            parsed.Add(new(path, line, severity, Clip(issue, 500), Clip(fix, 500), Clip(check, 500)));
        }
        var summary = report["summary"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(summary)) throw new InvalidOperationException("Thiếu tóm tắt phản biện.");
        if (verdict == "changes_requested")
        {
            var signature = System.Text.Json.JsonSerializer.Serialize(parsed);
            if (LastRejectedRevision == Revision)
                throw new InvalidOperationException("Phản biện lặp lại cùng vấn đề mà mã chưa thay đổi; cần làm rõ trở ngại trước khi chạy tiếp.");
            LastRejectedRevision = Revision; LastRejectedSignature = signature;
        }
        Findings = parsed; ReviewSummary = Clip(summary, 2000); Phase = verdict == "approved" ? "accepted" : "coder";
        return verdict;
    }
    private static string Clip(string value, int length) => value[..Math.Min(length, value.Length)];
}

public sealed record ReviewFinding(string Path, int Line, string Severity, string Issue, string Fix, string Verification);
