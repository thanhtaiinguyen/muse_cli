namespace Muse2Api.Agent;

public sealed record Options(string? Task, string? BaseUrl, string? ApiKey, string? Model,
    string? Workspace, string? Shell, bool Yes, int? MaxSteps, bool Help)
{
    public static Options Parse(string[] args)
    {
        string? baseUrl = null, key = null, model = null, workspace = null, shell = null;
        var yes = false;
        var help = false;
        int? maxSteps = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            string Next()
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {option}");
                return args[++i];
            }
            switch (option)
            {
                case "--": positional.AddRange(args.Skip(i + 1)); i = args.Length; break;
                case "--help" or "-h": help = true; break;
                case "--base-url": baseUrl = Next(); break;
                case "--api-key": key = Next(); break;
                case "--model": model = Next(); break;
                case "--workspace": workspace = Next(); break;
                case "--shell": shell = Next(); break;
                case "--yes" or "-y": yes = true; break;
                case "--max-steps":
                    if (!int.TryParse(Next(), out var limit) || limit < 1) throw new ArgumentException("--max-steps must be a positive integer");
                    maxSteps = limit;
                    break;
                default:
                    if (option.StartsWith('-')) throw new ArgumentException($"Unknown option: {option}");
                    positional.Add(option);
                    break;
            }
        }
        if (shell is not (null or "auto" or "powershell" or "cmd" or "bash")) throw new ArgumentException("invalid --shell");
        if (baseUrl is not null && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("--base-url must be an http(s) URL");
        return new(positional.Count == 0 ? null : string.Join(' ', positional), baseUrl, key, model, workspace, shell, yes, maxSteps, help);
    }
}
