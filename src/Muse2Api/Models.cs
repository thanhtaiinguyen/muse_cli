using System.Text.Json.Nodes;

namespace Muse2Api;

public sealed record ModelSpec(string Id, string Kind, string Description = "", string OwnedBy = "muse");

/// <summary>Model registry, ported 1:1 from Python <c>core/models.py</c>.</summary>
public static class ModelRegistry
{
    public static readonly IReadOnlyDictionary<string, ModelSpec> Models =
        new Dictionary<string, ModelSpec>
        {
            ["muse-chat"] = new("muse-chat", "chat", "Default muse.ai assistant"),
            ["muse-image"] = new("muse-image", "image", "Text-to-image / image editing"),
            ["muse-video"] = new("muse-video", "video", "Text-to-video / first-frame image-to-video"),
        };

    public static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>
        {
            ["gpt-4o"] = "muse-chat", ["gpt-4o-mini"] = "muse-chat", ["gpt-4.1"] = "muse-chat",
            ["gpt-5"] = "muse-chat", ["claude-sonnet-4"] = "muse-chat", ["deepseek-chat"] = "muse-chat",
            ["dall-e-3"] = "muse-image", ["gpt-image-1"] = "muse-image", ["sora"] = "muse-video",
        };

    private static readonly Dictionary<string, string> DefaultByKind = new()
    {
        ["chat"] = "muse-chat", ["image"] = "muse-image", ["video"] = "muse-video",
    };

    public static ModelSpec Resolve(string? name, string kind)
    {
        if (string.IsNullOrEmpty(name)) return Models[DefaultByKind[kind]];
        var key = Aliases.TryGetValue(name, out var alias) ? alias : name;
        if (!Models.TryGetValue(key, out var spec)) return Models[DefaultByKind[kind]];
        if (spec.Kind != kind)
            throw MuseException.Invalid($"model '{name}' is a {spec.Kind} model, expected {kind}");
        return spec;
    }
}

/// <summary>Converts OpenAI-style message lists into one prompt for the muse
/// web chat, ported from Python <c>core/prompt.py</c>.</summary>
public static class Prompt
{
    public sealed record FlatPrompt(string Text, List<string> Images);

    private static readonly Dictionary<string, string> RoleLabels = new()
    {
        ["system"] = "System", ["developer"] = "System", ["user"] = "User",
        ["assistant"] = "Assistant", ["tool"] = "Tool",
    };

    public static string ContentToText(JsonNode? content, List<string> images)
    {
        if (content is null) return "";
        if (content is JsonValue v)
        {
            try { return v.GetValue<string>(); } catch { return content.ToJsonString(); }
        }
        if (content is JsonArray arr)
        {
            var parts = new List<string>();
            foreach (var part in arr)
            {
                if (part is not JsonObject obj) { parts.Add(part?.ToJsonString() ?? ""); continue; }
                var type = obj["type"]?.GetValue<string>();
                if (type is "text" or "input_text" or "output_text")
                    parts.Add(obj["text"]?.GetValue<string>() ?? "");
                else if (type is "image_url" or "input_image")
                {
                    var urlNode = obj["image_url"];
                    string? url = urlNode is JsonObject uo
                        ? uo["url"]?.GetValue<string>()
                        : urlNode?.GetValue<string>();
                    if (!string.IsNullOrEmpty(url)) images.Add(url);
                }
            }
            return string.Join("\n", parts.Where(p => p.Length > 0));
        }
        return content.ToJsonString();
    }

    public static List<(string Role, string Text)> MessageTurns(IEnumerable<JsonObject> messages)
    {
        var turns = new List<(string, string)>();
        foreach (var msg in messages)
        {
            var text = ContentToText(msg["content"], new List<string>()).Trim();
            if (text.Length > 0)
                turns.Add((msg["role"]?.GetValue<string>() ?? "user", text));
        }
        return turns;
    }

    public static FlatPrompt Flatten(IEnumerable<JsonObject> messages)
    {
        var images = new List<string>();
        var turns = new List<(string Role, string Text)>();
        foreach (var msg in messages)
        {
            var role = msg["role"]?.GetValue<string>() ?? "user";
            var text = ContentToText(msg["content"], images).Trim();
            if (text.Length > 0) turns.Add((role, text));
        }
        if (turns.Count == 0) return new FlatPrompt("", images);
        if (turns.Count == 1 && turns[0].Role == "user")
            return new FlatPrompt(turns[0].Text, images);
        var blocks = turns.Select(t =>
            $"[{(RoleLabels.TryGetValue(t.Role, out var l) ? l : char.ToUpper(t.Role[0]) + t.Role[1..])}]\n{t.Text}")
            .ToList();
        blocks.Add("[Instruction]\nContinue the conversation above and reply as the Assistant to the last "
            + "User message. Output only the reply.");
        return new FlatPrompt(string.Join("\n\n", blocks), images);
    }

    public static string? FollowupText(List<(string Role, string Text)> previous,
        List<(string Role, string Text)> incoming)
    {
        if (previous.Count == 0 || incoming.Count <= previous.Count) return null;
        if (!incoming.Take(previous.Count).SequenceEqual(previous)) return null;
        var extra = incoming.Skip(previous.Count).ToList();
        while (extra.Count > 0 && extra[0].Role is "assistant" or "tool") extra.RemoveAt(0);
        var userText = new List<string>();
        foreach (var (role, text) in extra)
        {
            if (role != "user") return null;
            userText.Add(text);
        }
        return userText.Count == 0 ? null : string.Join("\n\n", userText);
    }
}
