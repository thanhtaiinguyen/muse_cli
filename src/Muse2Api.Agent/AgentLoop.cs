using System.Text.Json.Nodes;

namespace Muse2Api.Agent;

/// <summary>The chat → tool → result loop, separated from the HTTP client
/// so it can be driven by a real LLM (AgentClient) or by canned responses
/// in tests. A turn ends when the model replies with no tool calls; that
/// reply is the final answer.</summary>
public sealed class AgentLoop
{
    private readonly ToolExecutor _tools;
    private readonly Func<List<JsonObject>, Task<(string Text, List<ToolCall> Calls)>> _chat;
    private readonly Func<ToolCall, bool> _confirm;
    private readonly Action<string> _out;
    private readonly int _maxSteps;

    public List<JsonObject> History { get; } = new();

    public AgentLoop(ToolExecutor tools,
        Func<List<JsonObject>, Task<(string, List<ToolCall>)>> chat,
        Func<ToolCall, bool> confirm, Action<string> output, int maxSteps = 30)
    {
        _tools = tools;
        _chat = chat;
        _confirm = confirm;
        _out = output;
        _maxSteps = maxSteps;
    }

    public async Task RunAsync(string prompt)
    {
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = AgentClient.SystemPrompt },
        };
        messages.AddRange(History.Select(m => (JsonObject)m.DeepClone()));
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });

        for (var step = 0; step < _maxSteps; step++)
        {
            var (text, calls) = await _chat(messages);
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });
            var visible = ToolCallParser.StripToolBlocks(text);
            if (!string.IsNullOrWhiteSpace(visible)) _out($"agent> {visible}");
            if (calls.Count == 0)
            {
                History.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
                History.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });
                return;
            }
            foreach (var call in calls)
            {
                _out($"  🔧 {_tools.Describe(call)}");
                if (_tools.NeedsConfirmation(call) && !_confirm(call))
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = $"Tool result for {call.Name}: error: user declined this step.",
                    });
                    continue;
                }
                var result = await _tools.ExecuteAsync(call);
                _out($"  ↳ {FirstLine(result)}");
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"Tool result for {call.Name}:\n{result}",
                });
            }
        }
        _out($"agent> Đã hết {_maxSteps} bước mà nhiệm vụ chưa xong. Hãy chia nhỏ nhiệm vụ và thử lại.");
    }

    private static string FirstLine(string s)
    {
        var first = s.Split('\n')[0];
        return first.Length <= 200 ? first : first[..200] + "...";
    }
}
