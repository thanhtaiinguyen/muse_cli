namespace Muse2Api.WinForms;

/// <summary>
/// A separate profile root can be selected for branch testing without touching
/// production login cookies, conversation histories or WebView2 state.
/// </summary>
internal static class DesktopDataPaths
{
    public const string PreviewMarker = "muse-preview.profile";
    public const string PreviewProfileName = "Muse2Api.WinForms-Preview-Net10";
    public static bool IsPreview => File.Exists(Path.Combine(AppContext.BaseDirectory, PreviewMarker));
    public static bool IsOverride => IsPreview || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUSE_DESKTOP_PROFILE_ROOT"));

    public static string Root
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("MUSE_DESKTOP_PROFILE_ROOT");
            if (string.IsNullOrWhiteSpace(custom))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), IsPreview ? PreviewProfileName : "Muse2Api.WinForms");
            if (!Path.IsPathFullyQualified(custom))
                throw new InvalidOperationException("MUSE_DESKTOP_PROFILE_ROOT must be an absolute path.");
            return Path.GetFullPath(custom);
        }
    }
}
