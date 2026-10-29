using System.Net;
using System.Text.Json.Nodes;

namespace Muse2Api.Tests;

public class InputRegressionTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("100::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::1")]
    public void Media_BlocksNonPublicAddresses(string address)
        => Assert.True(MediaHelp.IsNonPublicIp(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Media_AllowsPublicAddresses(string address)
        => Assert.False(MediaHelp.IsNonPublicIp(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://127.0.0.1/test.png")]
    [InlineData("http://[::1]/test.png")]
    [InlineData("http://10.0.0.1/test.png")]
    [InlineData("http://[invalid")]
    [InlineData("data:image/png,AAAA")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("data:image/png;base64,")]
    [InlineData("data:image/png;base64,bad")]
    public async Task Media_RejectsInvalidReferencesWith400(string reference)
    {
        var error = await Assert.ThrowsAsync<MuseException>(() => MediaHelp.LoadImageRef(reference));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task Media_AcceptsDataUrlAndRawBase64()
    {
        Assert.Equal(new byte[] { 1, 2, 3 }, (await MediaHelp.LoadImageRef("data:image/png;base64,AQID")).Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await MediaHelp.LoadImageRef("AQID")).Data);
    }

    [Fact]
    public void Settings_RejectsUnknownDriverAndInvalidTimeout()
    {
        Assert.Throws<InvalidOperationException>(() => new Settings { Driver = "typo" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new Settings { ChatTimeout = double.NaN }.Validate());
        Assert.Throws<InvalidOperationException>(() => new Settings { Port = -1 }.Validate());
    }

    [Theory]
    [InlineData("Usage\nPro Plan\n12.5% used\nWeekly limit resets on Friday\nLanguage", 12.5)]
    [InlineData("用量\n專業方案\n已使用 25%\n語言", 25)]
    public void Quota_ParsesUsageInEnglishAndChinese(string text, double used)
        => Assert.Equal(used, Dom.ParseUsageText(text)["weekly_used_pct"]);

    [Fact]
    public void Prompt_RejectsEmptyRoleInsteadOfThrowingIndexError()
    {
        var error = Assert.Throws<MuseException>(() => Prompt.Flatten(new[] { new JsonObject { ["role"] = "", ["content"] = "test" } }));
        Assert.Equal(400, error.StatusCode);
    }
}
