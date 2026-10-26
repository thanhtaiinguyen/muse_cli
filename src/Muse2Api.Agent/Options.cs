namespace Muse2Api.Agent;

public sealed record Options(string? Task, string? BaseUrl, string? ApiKey, string? Model,
    string? Workspace, string? Shell, bool Yes, int MaxSteps, bool Help);
