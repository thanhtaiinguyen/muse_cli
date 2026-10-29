namespace Muse2Api.Tests;

public class PrivateDataProtectionTests
{
    [Fact]
    public void NewWindowsSecretsAreEncryptedAndCanBeReadBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        var file = dir.File("api_key");
        const string value = "m2a-sensitive-secret-test-fixture";
        PrivateDataProtection.WriteFile(file, value);
        Assert.True(PrivateDataProtection.IsEncrypted(File.ReadAllText(file)));
        Assert.DoesNotContain(value, File.ReadAllText(file));
        Assert.Equal(value, PrivateDataProtection.ReadFile(file));
    }

    [Fact]
    public void LegacyPlaintextIsMigratedWithoutRegeneratingTheKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        var file = dir.File("api_key");
        const string value = "m2a-legacy-key-that-must-not-change";
        File.WriteAllText(file, value);
        var settings = new Settings { DataDir = dir.Root };
        settings.ResolveApiKey();
        Assert.Equal(value, settings.ApiKey);
        Assert.Equal(value, PrivateDataProtection.ReadFile(file));
        Assert.True(PrivateDataProtection.IsEncrypted(File.ReadAllText(file)));
        var next = new Settings { DataDir = dir.Root };
        next.ResolveApiKey();
        Assert.Equal(value, next.ApiKey);
    }

    [Fact]
    public void InvalidEncryptedCredentialsFailClosedRatherThanBeingReplaced()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        var file = dir.File("api_key");
        File.WriteAllText(file, "muse-dpapi-v1:not-valid-base64");
        var settings = new Settings { DataDir = dir.Root };
        Assert.Throws<FormatException>(() => settings.ResolveApiKey());
        Assert.Equal("muse-dpapi-v1:not-valid-base64", File.ReadAllText(file));
    }
}
