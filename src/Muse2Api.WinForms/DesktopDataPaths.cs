namespace Muse2Api.WinForms;

/// <summary>
/// A separate profile root can be selected for branch testing without touching
/// production login cookies, conversation histories or WebView2 state.
/// </summary>
internal static class DesktopDataPaths
{
    public static bool IsOverride => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUSE_DESKTOP_PROFILE_ROOT"));

    public static string Root
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("MUSE_DESKTOP_PROFILE_ROOT");
            if (string.IsNullOrWhiteSpace(custom))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Muse2Api.WinForms");
            if (!Path.IsPathFullyQualified(custom))
                throw new InvalidOperationException("MUSE_DESKTOP_PROFILE_ROOT must be an absolute path.");
            return Path.GetFullPath(custom);
        }
    }
}
