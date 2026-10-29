using System.Security.Cryptography;
using System.Text;

namespace Muse2Api;

/// <summary>
/// Encrypts local credentials on Windows with DPAPI CurrentUser. Existing plaintext
/// files remain readable for one-time, in-place migration; no profile is deleted.
/// Non-Windows gateway installs retain the existing plaintext behavior and must
/// protect their data directory with OS permissions.
/// </summary>
public static class PrivateDataProtection
{
    private const string Prefix = "muse-dpapi-v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Muse2Api/local-private-storage/v1");

    public static bool IsEncrypted(string contents) => contents.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows()) return plaintext;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string contents)
    {
        if (!IsEncrypted(contents)) return contents;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Muse credential file is Windows DPAPI-protected and requires the original Windows account.");
        var encrypted = Convert.FromBase64String(contents[Prefix.Length..].Trim());
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser));
    }

    public static string ReadFile(string path) => Unprotect(File.ReadAllText(path));

    /// <summary>Atomically replace a file only after encryption succeeded.</summary>
    public static void WriteFile(string path, string plaintext)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encrypted = Protect(plaintext);
        var temporary = path + ".muse-protect-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, encrypted, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Upgrade a valid legacy plaintext file after its owner has parsed it.
    /// Never replace an unreadable or encrypted file.
    /// </summary>
    public static void MigrateLegacyFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var raw = File.ReadAllText(path);
        if (!IsEncrypted(raw)) WriteFile(path, raw);
    }
}
