using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Muse2Api.Agent;

/// <summary>Persist the objective, never desktop permissions or stale test evidence.</summary>
public sealed class GoalRun
{
    public string Objective { get; set; } = "";
    public string Status { get; set; } = "paused";
    public string Summary { get; set; } = "Chưa kiểm chứng.";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? PendingQuestion { get; set; }
    public List<string> FollowUps { get; set; } = new();
    public static string DescribeStatus(string status) => status switch
    {
        "running" => "Đang thực hiện", "completed" => "Đã kiểm chứng và hoàn tất", "waiting_input" => "Chờ bạn bổ sung thông tin", _ => "Tạm dừng",
    };

    public static bool IsCommand(string text) => text.Length >= 5 && text.StartsWith("/goal", StringComparison.OrdinalIgnoreCase)
        && (text.Length == 5 || char.IsWhiteSpace(text[5]));
    public static bool IsLocalCommand(string text) => IsCommand(text) && text[5..].Trim().ToLowerInvariant() is "status" or "stop";

    public bool AcceptsFollowUp(string text) => !IsCommand(text)
        && ((Status == "waiting_input" && !string.IsNullOrWhiteSpace(PendingQuestion)
            && !WorkflowPolicy.IsThanksOrGreeting(text) && !WorkflowPolicy.IsExplanation(text))
            || Status == "paused" && WorkflowPolicy.IsContinue(text));

    public void AddFollowUp(string text)
    {
        FollowUps ??= new();
        FollowUps.Add(text[..Math.Min(2000, text.Length)]);
        FollowUps = FollowUps.TakeLast(8).ToList();
    }

    public string TaskPrompt => Objective + (FollowUps is { Count: > 0 }
        ? "\n\nThông tin bổ sung của người dùng cho mục tiêu hiện tại:\n" + string.Join("\n", FollowUps) : "");

    public const string Instructions = """
        GOAL MODE: Keep implementing the objective until verified, without inventing a step budget.
        Inspect the repository and establish a concise acceptance/test checklist. Implement, run ALL relevant
        automated test suites, fix failures, and repeat. Use real test runners (dotnet test, pytest, npm test,
        cargo test, go test, etc.), not echo/printed claims. Discover all relevant suites; a filtered subset
        does not establish completion. Add meaningful missing tests for the requested behavior.
        Launch the resulting application using run_command background:true when needed. Then use the local
        computer tools to inspect the user-authorized test window, perform the actual feature workflows,
        assert expected results with computer_assert and capture computer_screenshot AFTER the final action.
        The local host shows a live preview of the authorized window; never bypass its window chooser.
        After any source edit rerun checks. Use process_status wait_ms:30000 while a check is running;
        starting a process is not a passing test. Report actual test counts, failures and remaining issues.
        Do not mark done based on a plan, old receipts, empty/zero tests, or skipped/cancelled tests.
        If access, credentials, screen consent or requirements are missing, ask a concrete question and pause.
        Do not retry network/rate-limit errors, start extra conversations, or claim perfect/bug-free software.
        Brief Vietnamese progress only; detailed logs belong in the local Terminal.
        """;
}

/// <summary>Fresh receipts gate completion; model text cannot set a check to passed.</summary>
public sealed class GoalVerification
{
    private int _revision;
    private bool _tests, _interacted, _asserted, _captured;
    private readonly Dictionary<string, (string Command, string Workdir, int Revision)> _processes = new();
    private readonly HashSet<string> _failedChecks = new();
    private readonly HashSet<string> _failedAssertions = new();
    public string? LastTestSummary { get; private set; }

    public IReadOnlyList<string> Missing
    {
        get
        {
            var missing = new List<string>();
            if (!_tests) missing.Add("Chạy toàn bộ bộ kiểm thử liên quan: có test thực sự và exit_code=0 sau lần sửa cuối");
            if (_failedChecks.Count > 0) missing.Add("Chạy lại và sửa các lệnh kiểm thử còn thất bại/chưa xác minh");
            if (_processes.Values.Any(p => IsTestCommand(p.Command))) missing.Add("Chờ các tiến trình kiểm thử kết thúc");
            if (!_interacted) missing.Add("Thao tác kiểm thử trực tiếp trên cửa sổ đã cho phép");
            if (!_asserted || _failedAssertions.Count > 0) missing.Add("Xác nhận kết quả giao diện bằng computer_assert sau thao tác cuối");
            if (!_captured) missing.Add("Chụp kết quả giao diện sau thao tác cuối");
            return missing;
        }
    }

    public JsonObject Snapshot() => new()
    {
        ["source_revision"] = _revision, ["automated_tests_passed"] = _tests,
        ["test_summary"] = LastTestSummary, ["ui_interacted"] = _interacted,
        ["ui_asserted"] = _asserted, ["ui_captured"] = _captured,
        ["failed_checks"] = new JsonArray(_failedChecks.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        ["missing"] = new JsonArray(Missing.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
    };

    public void Record(ToolCall call, string result, bool filesChanged = false, bool imageCaptured = false)
    {
        if (filesChanged)
        {
            _revision++; _tests = _interacted = _asserted = _captured = false;
            LastTestSummary = null;
        }
        var failed = ToolOutcome.IsFailure(result);
        var command = call.Name == "run_command" ? call.Args["command"]?.ToString() : null;
        var workdir = call.Args["workdir"]?.ToString() ?? ".";
        var revision = _revision;
        var idMatch = Regex.Match(result, @"(?m)^process_id=([a-f0-9]+)\s*$");
        var id = idMatch.Success ? idMatch.Groups[1].Value : call.Args["process_id"]?.ToString();
        if (call.Name == "run_command" && id is not null && ToolOutcome.IsRunning(result))
            _processes[id] = (command ?? "", workdir, revision);
        else if (call.Name is "process_status" or "process_input" or "process_stop" && id is not null
            && _processes.TryGetValue(id, out var pending))
        {
            command = pending.Command; workdir = pending.Workdir; revision = pending.Revision;
            if (!ToolOutcome.IsRunning(result)) _processes.Remove(id);
        }
        if (command is not null && IsTestCommand(command) && !ToolOutcome.IsRunning(result))
        {
            var key = Regex.Replace(command, """(?i)\s+(?:--filter|-k|-t|--testNamePattern)(?:=|\s+)(?:"[^"]*"|'[^']*'|\S+)""", "")
                .Trim() + "\ncwd=" + workdir;
            var filtered = Regex.IsMatch(command, @"(?i)(?:^|\s)(?:--filter|-k|-t|--testNamePattern)(?:\s|=|$)");
            // Record failed/unrecognized output even when another suite passed.
            if (call.Name != "process_stop" && revision == _revision && ToolOutcome.ExitedSuccessfully(result)
                && HasPassingTests(result) && !filtered)
            {
                _tests = true; _failedChecks.Remove(key);
                LastTestSummary = ExtractTestSummary(result);
            }
            else if (!filtered || failed) { _tests = false; _failedChecks.Add(key); }
        }
        if ((LocalTaskIntent.Evidence(call.Name) & LocalWork.ComputerInteract) != 0)
        {
            _asserted = _captured = false;
            if (!failed) _interacted = true;
        }
        if (call.Name == "computer_assert")
        {
            var assertion = new JsonObject();
            foreach (var item in call.Args.Where(item => item.Key is not ("element_id" or "wait_ms")).OrderBy(item => item.Key, StringComparer.Ordinal))
                assertion[item.Key] = item.Value?.DeepClone();
            var key = assertion.ToJsonString();
            if (failed || !result.StartsWith("assertion_passed=true", StringComparison.Ordinal)) { _asserted = false; _failedAssertions.Add(key); }
            else { _asserted = true; _failedAssertions.Remove(key); }
        }
        if (call.Name == "computer_screenshot" && !failed && imageCaptured) _captured = true;
    }

    // Match an invoked runner at a command boundary, not a runner name quoted by echo.
    public static bool IsTestCommand(string command) => Regex.IsMatch(command,
        """(?im)(?:^|[;\n]|&&|\|\|)\s*(?:&\s*)?(?:["'](?:[^"'\r\n]*[\\/])?|[^\s"'\r\n]*[\\/])?(?:dotnet(?:\.exe)?["']?\s+test\b|(?:npm|pnpm|yarn)(?:\.cmd)?["']?\s+(?:run\s+)?test\b|(?:python(?:3|\.exe)?["']?\s+-m\s+)?pytest["']?\b|cargo["']?\s+test\b|go["']?\s+test\b|(?:mvn|gradle|gradlew)(?:\.cmd|\.bat)?["']?\s+test\b|(?:npx\s+)?(?:vitest|jest|playwright)["']?\s+(?:run|test)\b)""",
        RegexOptions.None, TimeSpan.FromMilliseconds(100));

    public static bool HasPassingTests(string output)
    {
        if (Regex.IsMatch(output, @"(?i)\b(?:failed|failures|errors|skipped|cancelled|canceled)\s*[:=]?\s*[1-9]\d*|\b[1-9]\d*\s+(?:failed|failures|errors|skipped|cancelled|canceled)\b|\bfail\s+[1-9]\d*|\bFAIL(?:ED)?\b\s+(?!\d)[\w/]")) return false;
        return Regex.IsMatch(output, @"(?i)\bpassed\s*[:=]\s*[1-9]\d*|\b[1-9]\d*\s+(?:passed|passing)\b|\bTests:\s*[1-9]\d*\s+passed|(?m)^ok\s+\S+\s+[\d.]+s|\bTests run:\s*[1-9]\d*,\s*Failures:\s*0,\s*Errors:\s*0|(?m)^[#ℹ]\s+pass [1-9]\d*\s*$");
    }

    private static string ExtractTestSummary(string result)
        => string.Join("\n", result.Split('\n').Where(line => Regex.IsMatch(line, @"(?i)passed|passing|Tests run:|^ok\s|^[#ℹ]\s+pass "))
            .TakeLast(8).Select(line => line[..Math.Min(250, line.Length)]));
}
