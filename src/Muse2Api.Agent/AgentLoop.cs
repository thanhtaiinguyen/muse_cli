using System.Text.Json.Nodes;

namespace Muse2Api.Agent;

public sealed record AgentEvent(string Kind, string Text, string? Tool = null, string? Path = null);
public sealed record AgentCheckpoint(List<JsonObject> History, GoalRun? Goal, ProjectReview? Review);

/// <summary>Shared, cancellable chat/tool loop for the desktop and terminal.</summary>
public sealed class AgentLoop
{
    private readonly ToolExecutor _tools;
    private readonly Func<List<JsonObject>, CancellationToken, Task<(string Text, List<ToolCall> Calls)>> _chat;
    private readonly Func<ToolApproval, CancellationToken, Task<bool>> _confirm;
    private readonly Action<AgentEvent> _observe;
    private readonly int? _maxSteps;
    private int _running;
    private AgentEvent? _turnOutcome;
    public List<JsonObject> History { get; } = new();
    public JsonArray? RetrievedContext { get; set; }
    public GoalRun? Goal { get; set; }
    public bool ProjectReviewEnabled { get; set; }
    public ProjectReview? Review { get; set; }
    public Func<AgentCheckpoint, CancellationToken, Task>? SaveCheckpoint { get; set; }
    /// <summary>Directory of user-installed skills. When the prompt mentions
    /// a skill with @alias, its content is added to this turn's instructions.</summary>
    public string? SkillsDirectory { get; set; }

    public AgentLoop(ToolExecutor tools, Func<List<JsonObject>, Task<(string, List<ToolCall>)>> chat,
        Func<ToolCall, bool> confirm, Action<string> output, int? maxSteps = null)
        : this(tools, (messages, _) => chat(messages), (approval, _) => Task.FromResult(confirm(approval.Call)),
            e => { if (e.Kind is not ("assistant_start" or "command_output")) output(e.Kind == "assistant" ? $"agent> {e.Text}" : e.Text); }, maxSteps)
    { }

    public AgentLoop(ToolExecutor tools,
        Func<List<JsonObject>, CancellationToken, Task<(string Text, List<ToolCall> Calls)>> chat,
        Func<ToolApproval, CancellationToken, Task<bool>> confirm, Action<AgentEvent> observe, int? maxSteps = null)
    {
        if (maxSteps is < 1) throw new ArgumentOutOfRangeException(nameof(maxSteps));
        _tools = tools; _chat = chat; _confirm = confirm; _maxSteps = maxSteps;
        _observe = item =>
        {
            if (item.Kind is "completed" or "incomplete" or "cancelled" or "declined" or "waiting_input") _turnOutcome = item;
            observe(item);
        };
    }

    public async Task<bool> RunAsync(string prompt, CancellationToken cancel = default, List<string>? images = null)
    {
        if (string.IsNullOrWhiteSpace(prompt) && images is not { Count: > 0 }) throw new ArgumentException("Nội dung không được để trống.");
        if (prompt.Length > 32000) throw new ArgumentException("Yêu cầu quá dài (tối đa 32000 ký tự).");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Agent is already running");
        _turnOutcome = null;
        _tools.BeginAgentTurn();
        var messages = new List<JsonObject>();
        string? pendingTool = null;
        GoalVerification? verification = null;
        ProjectReview? review = null;
        var userPrompt = prompt;
        try
        {
            var goalFollowUp = Goal?.AcceptsFollowUp(prompt) == true;
            if (GoalRun.IsCommand(prompt) || goalFollowUp)
            {
                var argument = goalFollowUp ? "" : prompt[5..].Trim();
                if (argument.Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    _observe(new("assistant", Goal is null ? "Chưa có goal. Nhập /goal kèm mục tiêu để bắt đầu." : $"Mục tiêu: {Goal.Objective}\nTrạng thái: {GoalRun.DescribeStatus(Goal.Status)}\n{Goal.Summary}"));
                    _observe(new("completed", "Đã xem trạng thái goal.")); return true;
                }
                if (argument.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (Goal is not null) { Goal.Status = "paused"; Goal.PendingQuestion = null; Goal.Summary = "Đã tạm dừng. Nhập /goal để tiếp tục và kiểm chứng lại."; Goal.UpdatedAt = DateTimeOffset.UtcNow; }
                    _observe(new("assistant", "Đã tạm dừng goal. Mục tiêu và thay đổi được giữ lại."));
                    _observe(new("completed", "Đã tạm dừng goal.")); return true;
                }
                if (argument.Length > 0) Goal = new() { Objective = argument };
                if (Goal is null || string.IsNullOrWhiteSpace(Goal.Objective))
                {
                    _observe(new("assistant", "Nhập /goal kèm mục tiêu, ví dụ: /goal Hoàn thiện màn hình đăng nhập và kiểm thử toàn bộ luồng."));
                    _observe(new("waiting_input", "Cần mục tiêu để bắt đầu.")); return false;
                }
                if (goalFollowUp && !WorkflowPolicy.IsContinue(userPrompt)) Goal.AddFollowUp(userPrompt);
                Goal.Status = "running"; Goal.Summary = "Đang xây dựng và kiểm chứng mục tiêu.";
                Goal.PendingQuestion = null;
                var requiresUi = (LocalTaskIntent.Required(Goal.Objective) & LocalTaskIntent.ComputerWork) != 0;
                verification = new(requiresUi);
                prompt = Goal.TaskPrompt;
                _observe(new("goal", "Đang theo đuổi mục tiêu: " + prompt));
            }
            if (ProjectReviewEnabled)
            {
                if (verification is null && Review is { Phase: not "accepted" } savedReview && WorkflowPolicy.IsContinue(userPrompt))
                    prompt = savedReview.Objective;
                if (!WorkflowPolicy.IsSmallTalk(prompt) && !WorkflowPolicy.IsExplanation(prompt)
                    && (verification is not null || LocalTaskIntent.Required(prompt).HasFlag(LocalWork.Edit) || LocalTaskIntent.Required(prompt).HasFlag(LocalWork.Read) || prompt.Trim().Length > 0))
                {
                    var objective = Goal is not null && verification is not null ? Goal.Objective : prompt;
                    Review = review = Review is { Phase: not "accepted" } existing && existing.Objective == objective ? existing : new() { Objective = objective };
                    review.Resume();
                    _observe(new("review", review.IsReviewer ? "Đang tiếp tục phản biện dự án…" : "Đang viết mã — sẽ phản biện sau khi có kết quả kiểm thử."));
                }
            }
            var context = await _tools.WorkspaceContextAsync(cancel);
            var skillBlock = "";
            if (SkillsDirectory is not null)
            {
                try
                {
                    skillBlock = new SkillStore(SkillsDirectory).Expand(prompt, out var appliedSkills);
                    if (appliedSkills.Count > 0) _observe(new("skill", "Đang áp dụng kỹ năng: " + string.Join(", ", appliedSkills.Select(a => "@" + a))));
                }
                catch (Exception ex) { _observe(new("skill", "Không nạp được kỹ năng: " + ex.Message)); }
            }
            messages.Add(new() { ["role"] = "system", ["content"] = AgentClient.SystemPrompt + "\n\n" + context + (skillBlock.Length == 0 ? "" : "\n\n" + skillBlock) + "\n\nCURRENT LOCAL TASK (keep working on this):\n" + prompt
                + (verification is null ? "" : "\n\n" + GoalRun.Instructions) });
            messages.AddRange(History.Select(m =>
            {
                var copy = (JsonObject)m.DeepClone();
                if (copy["role"]?.ToString() == "assistant")
                {
                    var previous = copy["content"]?.ToString() ?? "";
                    if (ToolCallParser.Parse(previous).Any(c => c.Name == "__parse_error__"))
                        copy["content"] = "[Invalid plan: rejected before execution.]";
                    else if (LocalTaskIntent.IsEnvironmentConfusion(previous))
                        copy["content"] = "[Previous reply confused the web VM with the local desktop. It is not evidence about this workspace. Use the local executor receipts.]";
                }
                return copy;
            }));
            JsonNode userMessageContent = JsonValue.Create(userPrompt)!;
            if (images is { Count: > 0 })
            {
                var imageBatch = Guid.NewGuid().ToString("N");
                var parts = new JsonArray();
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = userPrompt });
                foreach (var url in images) parts.Add(new JsonObject { ["type"] = "image_url", ["attachment_id"] = imageBatch, ["image_url"] = new JsonObject { ["url"] = url } });
                userMessageContent = parts;
            }
            messages.Add(new() { ["role"] = "user", ["content"] = userMessageContent });
            var failures = new Dictionary<string, int>();
            var required = LocalTaskIntent.Required(prompt);
            if (verification is not null)
            {
                required |= LocalWork.Read | LocalWork.CommandVerified;
                if ((LocalTaskIntent.Required(prompt) & LocalTaskIntent.ComputerWork) != 0)
                    required |= LocalWork.ComputerInteract | LocalWork.ComputerAssert | LocalWork.ComputerScreenshot;
            }
            var evidence = LocalWork.None;
            var protocolRetries = 0;
            var malformedReplies = 0;
            var scopeRetries = 0;
            var declined = false;
            var verifiedNoChange = false;
            string? previousObservation = null;
            var repeatedObservations = 0;
            for (long step = 0; !_maxSteps.HasValue || step < _maxSteps.Value; step++)
            {
                cancel.ThrowIfCancellationRequested();
                // Summarize before evicting raw history; retain that memory in
                // the session and attach it to EVERY existing web request.
                if (ConversationContext.Compact(messages, _tools.Root, protectedPrefix: 1))
                    _observe(new("context_compacted", "Đã nén ngữ cảnh cũ trên máy; giữ mục tiêu, lời dặn và kết quả gần đây. Không tạo thêm lượt chat web."));
                _observe(new("assistant_start", "Đang suy nghĩ…"));
                if (SaveCheckpoint is not null)
                {
                    var checkpoint = new AgentCheckpoint(messages.Skip(1).Select(WithoutImage).ToList(),
                        Goal is null ? null : System.Text.Json.JsonSerializer.Deserialize<GoalRun>(System.Text.Json.JsonSerializer.Serialize(Goal)),
                        Review is null ? null : System.Text.Json.JsonSerializer.Deserialize<ProjectReview>(System.Text.Json.JsonSerializer.Serialize(Review)));
                    await SaveCheckpoint(checkpoint, cancel);
                }
                var request = FitContext(messages);
                // Requirements apply to this user turn only. A discussion after
                // a coding task must not inherit the earlier task's operations.
                request.Add(new()
                {
                    ["role"] = "user",
                    ["content"] = "APP_STATE:\n" + new JsonObject
                    {
                        ["goal"] = prompt, ["workspace"] = _tools.Root, ["observed_operations"] = evidence.ToString(),
                        ["conversation_context"] = ConversationContext.Snapshot(messages.Skip(1), _tools.Root),
                        ["retrieved_context"] = RetrievedContext?.DeepClone(),
                        ["active_skills"] = skillBlock.Length == 0 ? null : JsonValue.Create(skillBlock),
                        ["project_review"] = review?.Snapshot(),
                        ["goal_session"] = Goal is null ? null : new JsonObject { ["objective"] = Goal.Objective,
                            ["status"] = Goal.Status, ["pending_question"] = Goal.PendingQuestion },
                        ["goal_mode"] = verification is null ? null : new JsonObject { ["objective"] = Goal!.Objective,
                            ["instructions"] = GoalRun.Instructions, ["verification"] = verification.Snapshot() },
                        ["remaining"] = (required & ~evidence).ToString(), ["computer_scope"] = _tools.Computer?.Context ?? "unavailable",
                        ["owned_processes"] = _tools.OwnedProcessContext(),
                        ["next"] = required == LocalWork.None
                            ? "Respond to the current user message in Vietnamese. For discussion, advice or a comment, return status done with the actual answer in summary and no actions. Resolve references such as continue using conversation_context, but only propose actions requested by the current user; do not execute historical tasks for a casual comment. Do not claim unobserved execution."
                            : "Generate the next operation as JSON data for the app. Continue after reading; ask only for information genuinely missing. Do not claim native execution of supplied observations.",
                    }.ToJsonString(),
                });
                var (text, calls) = await _chat(request, cancel);
                cancel.ThrowIfCancellationRequested();
                calls = calls.Select(ToolCallParser.Validate).ToList();
                messages.Add(new() { ["role"] = "assistant", ["content"] = text });
                if (calls.Any(call => call.Name == "__parse_error__"))
                {
                    // Formatting errors are not failed OS operations. Never run a
                    // valid prefix, and never feed the broken code back as a plan.
                    messages[^1]["content"] = "[Invalid plan: rejected before execution.]\n" + text;
                    var errors = string.Join("\n", calls.Where(c => c.Name == "__parse_error__").Select(c => c.Args["error"]?.ToString()));
                    _observe(new("protocol_error", errors));
                    if (++malformedReplies >= 3)
                    {
                        _observe(new("incomplete", "Muse chưa gửi được thao tác đúng định dạng sau 3 lần phản hồi. Không thực hiện thao tác nào từ các phản hồi lỗi; những bước đã thành công trước đó vẫn được giữ lại."));
                        return false;
                    }
                    messages.Add(new()
                    {
                        ["role"] = "user", ["content"] = "APP_PROTOCOL_REPAIR:\n" + new JsonObject
                        {
                            ["error"] = errors, ["max_actions"] = 1, ["executed_from_rejected_reply"] = false,
                            ["instruction"] = "Resend only ONE small action in a json code fence. JSON-escape code quotes, backslashes and newlines. Do not repeat the rejected batch. No source code has been inferred or repaired by the app.",
                        }.ToJsonString(),
                    });
                    _observe(new("protocol_retry", "Muse đang gửi lại thao tác đúng định dạng. Chưa thực hiện thay đổi từ phản hồi lỗi…"));
                    continue;
                }
                malformedReplies = 0;
                if (calls.Count > 0 && (verification is null && review is null && Goal?.Status == "completed" && WorkflowPolicy.IsContinue(userPrompt)
                    || calls.Any(call => !WorkflowPolicy.Allows(prompt, call, verification is not null) || review?.IsReviewer == true && !ProjectReview.CanInspect(call))))
                {
                    messages[^1]["content"] = "[Unverified reply: action batch exceeded the current user's request.]";
                    _observe(new("protocol_retry", "Đã bỏ qua thao tác không thuộc yêu cầu hiện tại. Đang điều chỉnh phản hồi…"));
                    if (++scopeRetries >= 2)
                    { _observe(new("incomplete", "Muse đề xuất thao tác ngoài yêu cầu nên app đã chặn toàn bộ, chưa thay đổi dự án. Hãy gửi lại yêu cầu rõ hơn.")); return false; }
                    messages.Add(new() { ["role"] = "user", ["content"] = review?.IsReviewer == true
                        ? "LOCAL EXECUTOR: Reviewer is READ-ONLY. This entire batch was rejected before execution. Inspect current files/diff and report concrete findings; ask coder to fix/run tests. " + ProjectReview.Instructions
                        : "LOCAL EXECUTOR: The entire action batch was rejected BEFORE execution. This turn is discussion/advice or explicitly read-only. Respect the current user request; return an answer in summary, or read-only file inspection when requested. Do not run commands, edit files or control the screen. Do not resume historical work." });
                    continue;
                }
                var visible = ToolCallParser.PlannerReply(ToolCallParser.StripToolBlocks(text));
                var needsInput = ToolCallParser.NeedsInput(text);
                if ((verification is not null || review is not null) && WorkflowPolicy.IsPermissionQuestion(visible)) needsInput = false;
                if (calls.Count == 0)
                {
                    if (review?.IsReviewer == true && !needsInput)
                    {
                        try
                        {
                            var decision = review.ApplyDecision(text, _tools.Root);
                            _observe(new("review", decision == "approved" ? "Phản biện đã chấp thuận sau khi đọc mã hiện tại." : $"Phản biện yêu cầu sửa {review.Findings.Count} vấn đề. Đang chuyển lại cho vai trò viết mã…"));
                            _observe(new("review_verdict", decision == "approved" ? "Phản biện dự án đã chấp thuận sau khi kiểm tra mã nguồn." : $"Phản biện yêu cầu sửa {review.Findings.Count} vấn đề: {review.ReviewSummary}", decision));
                            if (decision == "changes_requested")
                            {
                                protocolRetries = scopeRetries = 0;
                                messages.Add(new() { ["role"] = "user", ["content"] = "LOCAL EXECUTOR: Switch to CODER. Address the reviewer findings using current files; rerun checks after edits. " + review.Snapshot().ToJsonString() });
                                continue;
                            }
                            if (verification is not null && verification.Missing.Count > 0)
                            {
                                review.Phase = "coder";
                                messages.Add(new() { ["role"] = "user", ["content"] = "LOCAL EXECUTOR: Review approved, but goal verification is incomplete. Continue CODER checks: " + string.Join("; ", verification.Missing) });
                                continue;
                            }
                            _observe(new("assistant", review.CandidateSummary + "\n\nPhản biện dự án: " + review.ReviewSummary
                                + (verification?.LastTestSummary is { Length: > 0 } testSummary ? "\n" + testSummary : "")));
                            _observe(new("completed", "Đã hoàn tất viết mã, kiểm chứng và phản biện dự án.")); return true;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException or FormatException)
                        {
                            messages[^1]["content"] = "[Unverified reply: invalid project review.]";
                            if (protocolRetries++ >= 2) { _observe(new("incomplete", "Phản biện chưa đủ bằng chứng: " + ex.Message)); return false; }
                            messages.Add(new() { ["role"] = "user", ["content"] = "LOCAL EXECUTOR: Review rejected: " + ex.Message + " " + ProjectReview.Instructions });
                            _observe(new("protocol_retry", "Đang bổ sung bằng chứng phản biện…")); continue;
                        }
                    }
                    if (verification is not null && verification.Missing.Count > 0 && !needsInput)
                    {
                        messages[^1]["content"] = "[Unverified reply: goal checks are still missing.]\n" + text;
                        if (protocolRetries++ >= 2)
                        {
                            _observe(new("incomplete", "Goal chưa hoàn tất. " + string.Join("; ", verification.Missing)
                                + ". Mục tiêu đã được lưu; nhập /goal để tiếp tục.")); return false;
                        }
                        _observe(new("protocol_retry", "Đang tiếp tục kiểm chứng goal…"));
                        messages.Add(new() { ["role"] = "user", ["content"] = "LOCAL EXECUTOR: Goal completion rejected. Missing verified checks: "
                            + string.Join("; ", verification.Missing) + ". Continue implementing/testing/fixing using local actions. If genuinely blocked, ask a concrete question and pause. " + GoalRun.Instructions });
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Model trả về câu trả lời rỗng.");
                    var missing = required & ~evidence;
                    if (missing == LocalWork.Edit && verifiedNoChange && LocalTaskIntent.ExplainsNoChange(visible)) missing = LocalWork.None;
                    if (!declined && (missing != LocalWork.None && !needsInput
                        || required != LocalWork.None && LocalTaskIntent.IsEnvironmentConfusion(visible)))
                    {
                        messages[^1]["content"] = "[Unverified reply: the required local operations did not run.]\n" + text;
                        _observe(new("assistant", ""));
                        if (protocolRetries++ >= 2)
                        {
                            _observe(new("incomplete", "Muse chưa thực hiện đủ thao tác trên dự án này. Chưa thể xác nhận hoàn tất; xem Terminal để biết các bước đã chạy."));
                            return false;
                        }
                        _observe(new("protocol_retry", "Đang yêu cầu Muse chuyển câu trả lời thành thao tác trên dự án…"));
                        messages.Add(new()
                        {
                            ["role"] = "user",
                            ["content"] = $"LOCAL EXECUTOR: The requested local work is still pending ({missing}). Your built-in VM is not the user's machine. Do not claim completion or ask for uploaded files. "
                                + (missing.HasFlag(LocalWork.Edit) && evidence.HasFlag(LocalWork.Read)
                                    ? "You have read local files. Now emit edit_file/write_file requests for the actual requested changes; do not stop at a plan. "
                                    : (missing & (LocalWork.Command | LocalWork.CommandVerified)) != 0 ? "Emit run_command for the required check, or process_status until an already started check exits successfully. "
                                    : (missing & LocalTaskIntent.ComputerWork) != 0 ? "Complete the remaining UI operations: enter the requested text, invoke/click the requested button, then computer_assert the requested resulting label/value. An inspect or input alone does not complete a UI test. Inspect again if element IDs expired. "
                                    : "Emit read_file/list_files to inspect the actual project, or summarize verified receipts if all requested work is already done. ")
                                + $"Use a JSON code block with actions in the application schema. Wait for the external local result. Current task: {prompt}",
                        });
                        continue;
                    }
                    if (ProjectReviewEnabled && !needsInput && (review is not null || _tools.LastFileChangeApplied || evidence != LocalWork.None))
                    {
                        if (review is null)
                        {
                            review = Review = new() { Objective = Goal?.Objective ?? prompt };
                            review.Resume();
                        }
                        if (review.Phase == "coder")
                        {
                            review.StartReview(visible); protocolRetries = scopeRetries = 0;
                            messages[^1]["content"] = "[Unverified reply: awaiting independent project review.]\n" + visible;
                            messages.Add(new() { ["role"] = "user", ["content"] = "LOCAL EXECUTOR: Switch to REVIEWER role.\nCODER candidate is ready. Switch to REVIEWER, read the current project files/diff and independently assess acceptance criteria and actual verification receipts. " + ProjectReview.Instructions });
                            _observe(new("review", "Đang phản biện — đọc lại mã và đối chiếu kết quả kiểm thử…"));
                            _observe(new("review_switch", "Chuyển sang vai trò Phản biện dự án: Đang đọc lại mã và đối chiếu kết quả kiểm thử…", "reviewer"));
                            continue;
                        }
                    }
                    _observe(new("assistant", verification is null || needsInput ? visible
                        : visible + "\n\nGoal đã được kiểm chứng bằng test tự động và thao tác/xác nhận/ảnh giao diện.\n" + verification.LastTestSummary));
                    if (needsInput)
                    {
                        if (verification is not null && Goal is not null) Goal.PendingQuestion = visible[..Math.Min(2000, visible.Length)];
                        _observe(new("waiting_input", "Muse cần thêm thông tin. Bạn có thể trả lời ngay trong ô chat.")); return false;
                    }
                    _observe(new("completed", "Hoàn thành lượt chat."));
                    return true;
                }
                _observe(new("assistant", visible));
                if (calls.Count > 16) throw new InvalidOperationException("Model requested too many tool calls in one step (limit 16)");
                foreach (var call in calls)
                {
                    cancel.ThrowIfCancellationRequested();
                    pendingTool = call.Name;
                    var path = call.Args["path"] is JsonValue value && value.TryGetValue<string>(out var filePath) ? filePath : null;
                    _observe(new("tool_start", _tools.Describe(call), call.Name, path));
                    string result;
                    try
                    {
                        var prepared = await _tools.PrepareForAgentAsync(call, cancel);
                        if (_tools.NeedsConfirmation(call) && !await _confirm(prepared, cancel))
                        {
                            declined = true;
                            result = "error: user declined this step. Do not retry this action unless the user explicitly requests it.";
                        }
                        else result = await _tools.ExecutePreparedAsync(prepared, cancel,
                            output => _observe(new("command_output", output, call.Name)));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result = "error: " + (string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name + ": không đọc được phần tử giao diện hiện tại; hãy quan sát lại trước khi thao tác." : ex.Message); }
                    var filesChanged = call.Name is "write_file" or "edit_file" ? _tools.LastFileChangeApplied && !ToolOutcome.IsFailure(result)
                        : call.Name == "run_command" && _tools.LastCommandChangedFiles.Count > 0;
                    protocolRetries = 0;
                    verification?.Record(call, result, filesChanged,
                        imageCaptured: call.Name == "computer_screenshot" && _tools.LastComputerImage is not null);
                    if (ProjectReviewEnabled && review is null && filesChanged)
                    {
                        review = Review = new() { Objective = Goal?.Objective ?? prompt };
                        review.Resume();
                    }
                    review?.Record(call, result, filesChanged);
                    if (verification is not null && filesChanged)
                    {
                        // A fix starts another real verification attempt. The
                        // same failing check after a new edit is not a blind retry.
                        failures.Clear(); protocolRetries = 0;
                        previousObservation = null; repeatedObservations = 0;
                    }
                    var receipt = $"Tool result for {call.Name}:\n{result}";
                    messages.Add(new() { ["role"] = "user", ["muse_tool"] = call.Name, ["muse_path"] = path,
                        ["muse_command"] = call.Name == "run_command" && call.Args["command"]?.ToString() is { } commandText
                            ? commandText[..Math.Min(500, commandText.Length)] : null,
                        ["content"] = call.Name.StartsWith("computer_") && _tools.LastComputerImage is { } image
                        ? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = receipt }, new JsonObject { ["type"] = "image_url", ["attachment_id"] = Guid.NewGuid().ToString("N"), ["image_url"] = new JsonObject { ["url"] = image } })
                        : JsonValue.Create(receipt) });
                    _observe(new("tool_result", result, call.Name, path));
                    // Stop a repeated observation loop, not productive long tasks
                    // or a running command that legitimately has no new output.
                    var observation = ObservationKey(call, result);
                    repeatedObservations = observation == previousObservation ? repeatedObservations + 1 : 1;
                    previousObservation = observation;
                    if (!ToolOutcome.IsFailure(result) && !ToolOutcome.IsRunning(result) && repeatedObservations >= 3)
                    {
                        _observe(new("incomplete", "Đã dừng vì Muse lặp lại cùng thao tác và kết quả, không có tiến triển. Những thay đổi đã thực hiện vẫn được giữ lại; xem Terminal để biết chi tiết."));
                        return false;
                    }
                    if (result.StartsWith("error: user declined", StringComparison.OrdinalIgnoreCase))
                    {
                        _observe(new("declined", "Đã dừng phần việc bạn chưa cho phép. Những thao tác đã hoàn tất trước đó vẫn được giữ lại."));
                        return false;
                    }
                    if (!ToolOutcome.IsFailure(result))
                    {
                        if (call.Name is "write_file" or "edit_file" && result.StartsWith("unchanged:")) verifiedNoChange = true;
                        var previousEvidence = evidence;
                        var operationEvidence = LocalTaskIntent.Evidence(call.Name);
                        if (operationEvidence.HasFlag(LocalWork.ComputerInteract)) evidence &= ~(LocalWork.ComputerAssert | LocalWork.ComputerScreenshot);
                        if (call.Name is "edit_file" or "write_file" && _tools.LastFileChangeApplied) evidence &= ~LocalWork.CommandVerified;
                        if (call.Name is not ("edit_file" or "write_file") || _tools.LastFileChangeApplied)
                            evidence |= operationEvidence;
                        if (ToolOutcome.ExitedSuccessfully(result)) evidence |= LocalWork.CommandVerified;
                        if (call.Name == "run_command" && _tools.LastCommandChangedFiles.Count > 0) evidence |= LocalWork.Edit;
                        if (evidence != previousEvidence) protocolRetries = 0;
                    }
                    pendingTool = null;
                    if (ToolOutcome.IsFailure(result))
                    {
                        var key = call.Name + call.Args.ToJsonString();
                        failures[key] = failures.GetValueOrDefault(key) + 1;
                        if (failures.Count > 256) failures.Remove(failures.Keys.First());
                        if (failures[key] >= 2)
                        {
                            _observe(new("incomplete", "Đã dừng vì cùng một thao tác thất bại hai lần. Hãy xem kết quả và điều chỉnh yêu cầu."));
                            return false;
                        }
                    }
                }
            }
            _observe(new("incomplete", $"Đã hết {_maxSteps} bước; nhiệm vụ chưa hoàn thành."));
            return false;
        }
        catch (OperationCanceledException)
        {
            messages.Add(new() { ["role"] = "user", ["content"] = pendingTool is null ? "The user stopped this turn." : $"Tool result for {pendingTool}: cancelled by user; inspect the current workspace before continuing." });
            _observe(new("cancelled", "Đã dừng theo yêu cầu."));
            throw;
        }
        finally
        {
            if (verification is not null && Goal is not null)
            {
                Goal.Status = _turnOutcome?.Kind == "completed" && verification.Missing.Count == 0 ? "completed"
                    : _turnOutcome?.Kind == "waiting_input" ? "waiting_input" : "paused";
                Goal.Summary = Goal.Status == "completed" ? "Đã kiểm chứng test tự động và giao diện.\n" + verification.LastTestSummary
                    : (_turnOutcome?.Text ?? "Lượt xử lý bị gián đoạn.") + "\nCòn cần: " + string.Join("; ", verification.Missing);
                Goal.UpdatedAt = DateTimeOffset.UtcNow;
            }
            if (messages.Count > 1)
            {
                messages[^1]["muse_turn_outcome"] = new JsonObject
                {
                    ["goal"] = prompt[..Math.Min(1000, prompt.Length)], ["status"] = _turnOutcome?.Kind ?? "interrupted",
                    ["summary"] = _turnOutcome?.Text ?? "Lượt chat bị gián đoạn; cần kiểm tra kết quả trước khi tiếp tục.",
                };
                ConversationContext.Compact(messages, _tools.Root, protectedPrefix: 1);
                History.Clear();
                History.AddRange(messages.Skip(1).Select(m => WithoutImage(m)));
            }
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static List<JsonObject> FitContext(List<JsonObject> messages)
    {
        var selected = new List<JsonObject>();
        var size = messages[0].ToJsonString().Length;
        var keptImage = false;
        foreach (var original in messages.Skip(1).Reverse())
        {
            // Already represented by the fresh conversation_context packet.
            if (original["role"]?.ToString() == "system" && original["content"]?.ToString().StartsWith(ConversationContext.ArchivePrefix, StringComparison.Ordinal) == true) continue;
            var hasImage = original["content"] is JsonArray parts && parts.OfType<JsonObject>().Any(p => p["type"]?.ToString() == "image_url");
            var message = hasImage && keptImage ? WithoutImage(original) : original;
            var length = hasImage ? WithoutImage(message).ToJsonString().Length + 2048 : message.ToJsonString().Length;
            if (size + length > 96000 && selected.Count > 0) break;
            size += length;
            selected.Add(message);
            if (hasImage) keptImage = true;
        }
        selected.Reverse();
        selected.Insert(0, (JsonObject)messages[0].DeepClone());
        if (selected.Count < messages.Count)
            selected.Insert(1, new() { ["role"] = "system", ["content"] = "Older messages were omitted for context limits. Re-read project files when needed; do not assume their contents." });
        return selected;
    }

    private static string ObservationKey(ToolCall call, string result)
    {
        // Fresh element/capture IDs are required for safe input, but are not
        // evidence of progress. Otherwise an unchanged UI can loop forever.
        if (call.Name is "computer_inspect" or "computer_screenshot")
        {
            try
            {
                if (JsonNode.Parse(result) is JsonObject state)
                {
                    if (call.Name == "computer_screenshot" && string.IsNullOrEmpty(state["image_fingerprint"]?.ToString()))
                        return call.Name + call.Args.ToJsonString() + result;
                    state.Remove("snapshot_id"); state.Remove("inspection_id");
                    if (state["elements"] is JsonArray elements)
                        foreach (var element in elements.OfType<JsonObject>()) element.Remove("element_id");
                    var args = (JsonObject)call.Args.DeepClone(); args.Remove("element_id"); args.Remove("snapshot_id");
                    return call.Name + args.ToJsonString() + state.ToJsonString();
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        return call.Name + call.Args.ToJsonString() + result;
    }

    private static JsonObject WithoutImage(JsonObject message)
    {
        var copy = (JsonObject)message.DeepClone();
        if (copy["content"] is JsonArray parts)
            copy["content"] = string.Join("\n", parts.OfType<JsonObject>().Select(p => p["type"]?.ToString() == "image_url"
                ? "[An image was attached in this turn. Reattach it if the original image is needed again.]" : p["text"]?.ToString() ?? ""));
        return copy;
    }
}
