using System.Text.Json.Nodes;
using Muse2Api;
using Xunit;

namespace Muse2Api.Tests;

public class PortTests
{
    [Fact]
    public void ResolveModel_AliasAndWrongKind()
    {
        Assert.Equal("muse-chat", ModelRegistry.Resolve("gpt-4o", "chat").Id);
        Assert.Equal("muse-chat", ModelRegistry.Resolve(null, "chat").Id);
        Assert.Equal("muse-chat", ModelRegistry.Resolve("unknown-model", "chat").Id);
        Assert.Throws<MuseException>(() => ModelRegistry.Resolve("muse-video", "chat"));
    }

    [Fact]
    public void Flatten_SingleUserTurnIsVerbatim()
    {
        var flat = Prompt.Flatten(new[]
        {
            new JsonObject { ["role"] = "user", ["content"] = "hello" },
        });
        Assert.Equal("hello", flat.Text);
    }

    [Fact]
    public void Flatten_MultiTurnAddsRoleHeaders()
    {
        var flat = Prompt.Flatten(new[]
        {
            new JsonObject { ["role"] = "system", ["content"] = "be brief" },
            new JsonObject { ["role"] = "user", ["content"] = "hi" },
        });
        Assert.Contains("[System]", flat.Text);
        Assert.Contains("[Instruction]", flat.Text);
    }

    [Fact]
    public async Task MockDriver_ChatAndImage()
    {
        var driver = new MockDriver(delayMs: 0);
        var account = new Account();
        var text = "";
        await foreach (var d in driver.ChatStream(account,
            new ChatRequest("hello", "muse-chat", new List<InputImage>(), null, new(), CancellationToken.None)))
            text += d;
        Assert.Equal("[mock:muse-chat] You said: hello", text);

        var images = await driver.GenerateImage(account,
            new ImageRequest("a fox", "muse-image", null, 1, new()));
        Assert.Single(images);
        // PNG signature
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, images[0].Data[..4]);
        var quota = await driver.Quota(account);
        Assert.Equal("free", quota["plan"]);
    }

    [Fact]
    public void AccountPool_LruAndMasking()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var pool = new AccountPool(Path.Combine(dir, "accounts.json"), "lru", 1, 120, 60);
        var a = new Account { Label = "a", Cookies = new() { ["hatch_sess"] = "secret123456" } };
        pool.Upsert(a);
        using var lease = pool.Acquire(Array.Empty<string>(), null);
        Assert.Equal(a.Id, lease.Account.Id);
        var pub = a.Public();
        Assert.Equal("secr...3456", pub["cookies"]!["hatch_sess"]!.GetValue<string>());
        Assert.DoesNotContain("secret123456", pub.ToJsonString());
    }

    [Fact]
    public void KeyStore_CreateVerifyAndMask()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new KeyStore(Path.Combine(dir, "keys.json"));
        var (key, plaintext) = store.Create("test", "");
        Assert.StartsWith("m2a-", plaintext);
        Assert.NotNull(store.Verify(plaintext));
        Assert.Null(store.Verify("wrong"));
        Assert.DoesNotContain(plaintext, key.Public().ToJsonString());
    }

    [Fact]
    public void MediaStore_SaveAndRejectTraversal()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "media");
        var store = new MediaStore(dir);
        var name = store.Save(new byte[] { 1, 2, 3 }, "image/png", "img");
        Assert.EndsWith(".png", name);
        Assert.True(File.Exists(store.PathOf(name)));
        Assert.Throws<MuseException>(() => store.PathOf("../evil.png"));
    }
}
