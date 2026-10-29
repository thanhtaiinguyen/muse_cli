namespace Muse2Api;

/// <summary>Private desktop host: credentials belong to the application, never to its setup flow.</summary>
public static class DesktopProfile
{
    public static bool HasSavedLogin(Account account)
        => account.Enabled && account.Status != AccountStatus.Invalid && Upstream.MissingSessionCookies(account.Cookies).Count == 0;

    public static Settings Create(string dataDirectory, string? browserPath = null)
    {
        var settings = new Settings
        {
            Host = "127.0.0.1", Port = 18610, Driver = "browser",
            DataDir = Path.GetFullPath(dataDirectory), ApiKey = "", AdminKey = "",
            PublicBase = "", ChromiumPath = browserPath ?? (File.Exists(Path.Combine(dataDirectory, "bundled_browser")) ? "" : FindBrowser()),
            KeepaliveEnabled = true, StreamHeartbeatInterval = 10,
            FirstTokenTimeout = 120, ChatTimeout = 600,
        };
        settings.ResolveApiKey();
        return settings;
    }

    public static int InstallBrowser() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

    public static string FindBrowser()
    {
        var configured = Environment.GetEnvironmentVariable("MUSE2API_CHROMIUM_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var directory in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        })
        {
            if (string.IsNullOrEmpty(directory)) continue;
            foreach (var relative in new[] { "Microsoft/Edge/Application/msedge.exe", "Google/Chrome/Application/chrome.exe" })
            {
                var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path)) return path;
            }
        }
        return ""; // Playwright's bundled Chromium is the next option.
    }
}
