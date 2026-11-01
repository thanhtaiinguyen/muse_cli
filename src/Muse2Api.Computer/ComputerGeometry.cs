using Muse2Api.Agent;

namespace Muse2Api.Computer;

internal sealed record ScreenshotMap(NativeDesktop.Rect Bounds, int ImageWidth, int ImageHeight)
{
    public NativeDesktop.Point ToScreen(int x, int y)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || ImageWidth <= 0 || ImageHeight <= 0
            || x < 0 || y < 0 || x >= ImageWidth || y >= ImageHeight)
            throw new InvalidOperationException("Tọa độ nằm ngoài ảnh chụp. Hãy chụp lại cửa sổ.");
        return new() { X = checked(Bounds.Left + (int)((x + 0.5) * Bounds.Width / ImageWidth)),
            Y = checked(Bounds.Top + (int)((y + 0.5) * Bounds.Height / ImageHeight)) };
    }
}

internal static class ComputerActionPolicy
{
    public static bool Mutates(string name) => name is "computer_focus" or "computer_invoke" or "computer_set_value" or "computer_type" or "computer_key"
        or "computer_toggle" or "computer_select" or "computer_expand" or "computer_scroll" or "computer_click" or "computer_drag";

    public static ushort[] Keys(string chord) => chord.Trim().ToUpperInvariant() switch
    {
        "ENTER" => [13], "TAB" => [9], "SHIFT+TAB" => [16, 9], "SPACE" => [32],
        "CTRL+A" => [17, 65], "CTRL+C" => [17, 67], "CTRL+V" => [17, 86], "CTRL+Z" => [17, 90],
        "CTRL+S" => [17, 83], "CTRL+F" => [17, 70], "CTRL+L" => [17, 76], "CTRL+HOME" => [17, 36], "CTRL+END" => [17, 35],
        "BACKSPACE" => [8], "DELETE" => [46], "LEFT" => [37], "RIGHT" => [39], "UP" => [38], "DOWN" => [40],
        "HOME" => [36], "END" => [35], "PAGEUP" => [33], "PAGEDOWN" => [34], "F5" => [116],
        _ => throw new InvalidOperationException("Phím chưa được hỗ trợ. Esc dành riêng cho bạn dừng điều khiển; không dùng phím chuyển ứng dụng/hệ thống."),
    };

    public static void Validate(ToolCall call)
    {
        string S(string key) => call.Args[key]?.ToString() ?? "";
        if (!call.Name.StartsWith("computer_", StringComparison.Ordinal) || ToolCallParser.Validate(call).Name == "__parse_error__") throw new InvalidOperationException("Công cụ điều khiển giao diện không hợp lệ.");
        if (call.Name is "computer_invoke" or "computer_set_value" or "computer_assert" or "computer_toggle" or "computer_select" or "computer_expand"
            && string.IsNullOrWhiteSpace(S("element_id"))) throw new InvalidOperationException("Cần element_id từ lần đọc giao diện mới nhất.");
        if (call.Name == "computer_inspect")
        {
            foreach (var (key, fallback, min, max) in new[] { ("offset", 0, 0, 10000), ("limit", 80, 1, 180), ("max_depth", 12, 1, 30) })
                if (call.Args[key] is { } raw && (raw is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<int>(out var number) || number < min || number > max))
                    throw new InvalidOperationException($"{key} phải là số nguyên từ {min} đến {max}.");
            if (S("query").Length > 200) throw new InvalidOperationException("Từ khóa tìm giao diện quá dài (tối đa 200 ký tự).");
        }
        if (call.Name == "computer_key") _ = Keys(S("key"));
        if (call.Name == "computer_click" && S("button") is not ("" or "left" or "right")) throw new InvalidOperationException("button phải là left hoặc right.");
        foreach (var key in new[] { "double_click", "expanded" })
            if (call.Args[key] is { } raw && (raw is not System.Text.Json.Nodes.JsonValue flag || !flag.TryGetValue<bool>(out _))) throw new InvalidOperationException($"{key} phải là true hoặc false.");
        if (call.Name == "computer_scroll" && S("direction") is not ("up" or "down" or "left" or "right")) throw new InvalidOperationException("direction phải là up/down/left/right.");
        if (call.Name is "computer_click" or "computer_drag" || call.Name == "computer_scroll" && S("element_id").Length == 0)
        {
            if (string.IsNullOrWhiteSpace(S("snapshot_id"))) throw new InvalidOperationException("Cần chụp ảnh mới và cung cấp snapshot_id trước khi dùng tọa độ.");
            foreach (var key in call.Name == "computer_drag" ? new[] { "x", "y", "to_x", "to_y" } : new[] { "x", "y" })
                if (call.Args[key] is not System.Text.Json.Nodes.JsonValue v || !v.TryGetValue<int>(out var value) || value < 0)
                    throw new InvalidOperationException($"{key} phải là tọa độ pixel nguyên không âm trong ảnh mới.");
        }
        if (call.Name is "computer_type" or "computer_set_value")
        {
            if (call.Args["text"] is not System.Text.Json.Nodes.JsonValue raw || !raw.TryGetValue<string>(out _)) throw new InvalidOperationException("Cần cung cấp text dạng chuỗi; muốn xóa ô nhập hãy dùng text rỗng.");
            var limit = call.Name == "computer_type" ? 4000 : 16000;
            if (S("text").Length > limit || S("text").Contains('\0') || call.Name == "computer_type" && S("text").Length == 0)
                throw new InvalidOperationException($"Nội dung nhập không hợp lệ (tối đa {limit} ký tự, không chứa NUL).");
        }
        if (call.Name == "computer_assert" && S("property") is not ("name" or "value" or "toggle" or "enabled" or "offscreen" or "selected")) throw new InvalidOperationException("property chưa được hỗ trợ cho kiểm tra giao diện.");
        if (call.Name == "computer_assert")
        {
            if (call.Args["expected"] is not System.Text.Json.Nodes.JsonValue) throw new InvalidOperationException("Cần expected để xác minh kết quả, không thể coi thiếu dữ liệu là test đạt.");
            if (S("match") is not ("" or "equals" or "contains")) throw new InvalidOperationException("match phải là equals hoặc contains.");
            if (call.Args["wait_ms"] is { } raw && (raw is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<int>(out var wait) || wait < 0 || wait > 5000)) throw new InvalidOperationException("wait_ms phải từ 0 đến 5000.");
        }
    }
}
