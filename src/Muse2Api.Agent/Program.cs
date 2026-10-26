using System.Text.Json.Nodes;
using Muse2Api.Agent;

// muse-agent: a Codex-like coding agent for the terminal.
// Usage (cmd / PowerShell):
//   dotnet run --project src/Muse2Api.Agent -- "sửa lỗi test X trong thư mục này"
//   dotnet run --project src/Muse2Api.Agent -- --workspace C:\code\myapp --model muse-chat
// Env: MUSE_AGENT_BASE_URL (default http://127.0.0.1:18610/v1),
//      MUSE_AGENT_API_KEY, MUSE_AGENT_MODEL (default muse-chat),
//      MUSE_AGENT_WORKSPACE (default current directory).

var opts = ParseArgs(args);
if (opts.Help)
{
    Console.WriteLine("""
        muse-agent — coding agent chạy trong cmd / PowerShell.

        Cách dùng:
          muse-agent "nhiệm vụ..."            chạy 1 nhiệm vụ rồi thoát
          muse-agent                            chat tương tác (REPL)
          muse-agent --workspace C:\code\app    chọn thư mục làm việc

        Tùy chọn: --base-url URL --api-key KEY --model NAME
                  --workspace DIR --shell powershell|cmd|bash|auto
                  --yes (không hỏi xác nhận trước khi ghi file / chạy lệnh)
                  --max-steps N (mặc định 30)

        Lệnh trong REPL: /help /exit /clear /model <tên> /workspace <đường dẫn>
                         /yes (bật/tắt tự xác nhận)
        Agent chỉ được đọc/ghi file trong workspace, và chỉ chạy lệnh sau khi
        bạn xác nhận (trừ khi dùng --yes).
        """);
    return 0;
}

var workspace = opts.Workspace ?? Environment.GetEnvironmentVariable("MUSE_AGENT_WORKSPACE")
    ?? Directory.GetCurrentDirectory();
var client = new AgentClient(
    opts.BaseUrl ?? Environment.GetEnvironmentVariable("MUSE_AGENT_BASE_URL") ?? "http://127.0.0.1:18610/v1",
    opts.Model ?? Environment.GetEnvironmentVariable("MUSE_AGENT_MODEL") ?? "muse-chat",
    opts.ApiKey ?? Environment.GetEnvironmentVariable("MUSE_AGENT_API_KEY"));
var tools = new ToolExecutor(workspace, opts.Shell ?? "auto");
var autoApprove = opts.Yes;
var history = new List<JsonObject>();

Console.WriteLine("muse-agent — agent code chạy trong terminal (kiểu Codex).");
Console.WriteLine($"  workspace: {tools.Root}");
Console.WriteLine($"  model: {client.Model}   shell: {(OperatingSystem.IsWindows() ? "PowerShell (mặc định Windows)" : "bash")}");
Console.WriteLine("  Gõ /help để xem lệnh, /exit để thoát. Ghi file và chạy lệnh sẽ hỏi xác nhận trước.");
Console.WriteLine();

if (opts.Task is not null)
{
    await RunTaskAsync(opts.Task);
    return 0;
}

while (true)
{
    Console.Write("bạn> ");
    var line = Console.ReadLine();
    if (line is null) break;
    line = line.Trim();
    if (line.Length == 0) continue;
    if (line is "/exit" or "/quit") break;
    if (line == "/help")
    {
        Console.WriteLine("  /exit, /clear, /model <tên>, /workspace <đường dẫn>, /yes");
        continue;
    }
    if (line == "/clear") { history.Clear(); Console.WriteLine("  Đã xóa lịch sử chat."); continue; }
    if (line == "/yes") { autoApprove = !autoApprove; Console.WriteLine($"  Tự xác nhận: {(autoApprove ? "bật" : "tắt")}"); continue; }
    if (line.StartsWith("/model ", StringComparison.Ordinal))
    {
        Console.WriteLine($"  Model hiện tại là {client.Model}. Đổi model: thoát rồi chạy lại với --model <tên>.");
        continue;
    }
    if (line.StartsWith("/workspace ", StringComparison.Ordinal))
    {
        var dir = line["/workspace ".Length..].Trim();
        try
        {
            tools = new ToolExecutor(dir, opts.Shell ?? "auto");
            history.Clear();
            Console.WriteLine($"  Workspace mới: {tools.Root} (lịch sử chat đã xóa).");
        }
        catch (Exception ex) { Console.WriteLine($"  Lỗi: {ex.Message}"); }
        continue;
    }
    await RunTaskAsync(line);
}

return 0;

async Task RunTaskAsync(string prompt)
{
    var loop = new AgentLoop(tools,
        async messages =>
        {
            try { return await client.ChatAsync(messages); }
            catch (Exception ex)
            {
                Console.WriteLine($"agent> Lỗi gọi LLM: {ex.Message}");
                Console.WriteLine("  Kiểm tra gateway đang chạy và MUSE_AGENT_API_KEY / --api-key đã đúng chưa.");
                // Return an empty final answer so the loop ends instead of
                // retrying the same failing request for every step.
                return ("", new List<ToolCall>());
            }
        },
        call =>
        {
            if (autoApprove) return true;
            Console.Write("  Chạy bước này? [y/N] ");
            return Console.ReadLine()?.Trim().ToLowerInvariant() is ("y" or "yes");
        },
        Console.WriteLine,
        opts.MaxSteps);
    loop.History.AddRange(history.Select(m => (JsonObject)m.DeepClone()));
    await loop.RunAsync(prompt);
    history.Clear();
    history.AddRange(loop.History.Select(m => (JsonObject)m.DeepClone()));
}

static Options ParseArgs(string[] argv)
{
    string? task = null, baseUrl = null, apiKey = null, model = null, workspace = null, shell = null;
    var yes = false;
    var maxSteps = 30;
    var help = false;
    var positional = new List<string>();
    for (var i = 0; i < argv.Length; i++)
    {
        string? Next() => i + 1 < argv.Length ? argv[++i] : null;
        switch (argv[i])
        {
            case "--help" or "-h": help = true; break;
            case "--base-url": baseUrl = Next(); break;
            case "--api-key": apiKey = Next(); break;
            case "--model": model = Next(); break;
            case "--workspace": workspace = Next(); break;
            case "--shell": shell = Next(); break;
            case "--yes" or "-y": yes = true; break;
            case "--max-steps": if (int.TryParse(Next(), out var n)) maxSteps = n; break;
            default: positional.Add(argv[i]); break;
        }
    }
    if (positional.Count > 0) task = string.Join(' ', positional);
    return new Options(task, baseUrl, apiKey, model, workspace, shell, yes, maxSteps, help);
}
