using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Muse2Api.Agent;
using Muse2Api.Computer.Fixture;

namespace Muse2Api.Computer.Tests;

[CollectionDefinition("Desktop", DisableParallelization = true)] public sealed class DesktopCollection;

[Collection("Desktop")]
public class NativeComputerTests
{
    [Fact]
    public async Task DeniedAccess_IsAskedOnlyOncePerTurnWithoutOpeningAnyWindow()
    {
        var computer=new DesktopComputer(); var prompts=0;
        computer.RequestAuthorization=_=>{prompts++;return Task.FromResult(false);};
        Assert.StartsWith("error: user declined",(await computer.ExecuteAsync(new("computer_inspect",new()),default)).Text);
        Assert.StartsWith("error: user declined",(await computer.ExecuteAsync(new("computer_screenshot",new()),default)).Text);
        Assert.Equal(1,prompts);
        computer.BeginTurn(); await computer.ExecuteAsync(new("computer_inspect",new()),default); Assert.Equal(2,prompts);
    }
    [Fact]
    public async Task DisabledComputer_RejectsAccessBeforeInspectingAnyWindow()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DesktopComputer().ExecuteAsync(new("computer_inspect", new()), default));
    }

    [DesktopFact]
    public async Task NativeWindow_ReadSetInvokeAssertScreenshotAndStop()
    {
        await using var fixture = await TestWindow.Start();
        var computer = new DesktopComputer(); computer.Authorize(fixture.Window);
        var inspected = await computer.ExecuteAsync(new("computer_inspect", new()), default);
        var elements = JsonNode.Parse(inspected.Text)!["elements"]!.AsArray();
        string Id(string name) => elements.OfType<JsonObject>().Single(e => e["automation_id"]?.ToString() == name)["element_id"]!.ToString();
        Assert.DoesNotContain("not-for-automation", inspected.Text);
        Assert.DoesNotContain(elements.OfType<JsonObject>(), e => e["automation_id"]?.ToString() == "password");
        await computer.ExecuteAsync(new("computer_toggle", new() { ["element_id"] = Id("option"), ["state"] = "on" }), default);
        var toggled = await computer.ExecuteAsync(new("computer_assert", new() { ["element_id"] = Id("option"), ["property"] = "toggle", ["expected"] = "On" }), default);
        Assert.StartsWith("assertion_passed=true", toggled.Text);
        await computer.ExecuteAsync(new("computer_set_value", new() { ["element_id"] = Id("testInput"), ["text"] = "Xin chào Muse" }), default);
        await computer.ExecuteAsync(new("computer_invoke", new() { ["element_id"] = Id("apply") }), default);
        var check = await computer.ExecuteAsync(new("computer_assert", new() { ["element_id"] = Id("result"), ["property"] = "name", ["expected"] = "Saved: Xin chào Muse" }), default);
        Assert.StartsWith("assertion_passed=true", check.Text);
        Assert.Equal("Xin chào Muse", await File.ReadAllTextAsync(fixture.Output));
        var failed = await computer.ExecuteAsync(new("computer_assert", new() { ["element_id"] = Id("result"), ["property"] = "name", ["expected"] = "wrong" }), default);
        Assert.True(ToolOutcome.IsFailure(failed.Text));
        var capture = await computer.ExecuteAsync(new("computer_screenshot", new()), default);
        Assert.StartsWith("data:image/jpeg;base64,", capture.ImageDataUrl);
        Assert.True(Convert.FromBase64String(capture.ImageDataUrl!.Split(',')[1]).Length > 1000);
        computer.Disarm();
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ExecuteAsync(new("computer_invoke", new() { ["element_id"] = Id("apply") }), default));
    }

    [DesktopFact]
    public async Task NativeKeyboardAndCoordinateMouse_ActOnObservedFixtureAndVerifyOutput()
    {
        await using var fixture = await TestWindow.Start();
        var computer = new DesktopComputer(); computer.Authorize(fixture.Window);
        var tree = JsonNode.Parse((await computer.ExecuteAsync(new("computer_inspect", new()), default)).Text)!["elements"]!.AsArray();
        JsonObject Find(string name) => tree.OfType<JsonObject>().Single(e => e["automation_id"]?.ToString() == name);
        var input = Find("testInput")["element_id"]!.ToString();
        await computer.ExecuteAsync(new("computer_type", new() { ["element_id"] = input, ["text"] = "replace" }), default);
        await computer.ExecuteAsync(new("computer_key", new() { ["key"] = "CTRL+A" }), default);
        await computer.ExecuteAsync(new("computer_type", new() { ["element_id"] = input, ["text"] = "Thử chuột và phím" }), default);
        var capture = JsonNode.Parse((await computer.ExecuteAsync(new("computer_screenshot", new()), default)).Text)!;
        var bounds = Find("apply")["bounds"]!;
        await computer.ExecuteAsync(new("computer_click", new()
        {
            ["snapshot_id"] = capture["snapshot_id"]!.ToString(), ["x"] = (int)(bounds["x"]!.GetValue<double>() + bounds["width"]!.GetValue<double>() / 2),
            ["y"] = (int)(bounds["y"]!.GetValue<double>() + bounds["height"]!.GetValue<double>() / 2),
        }), default);
        var check = await computer.ExecuteAsync(new("computer_assert", new() { ["element_id"] = Find("result")["element_id"]!.ToString(), ["property"] = "name", ["expected"] = "Saved: Thử chuột và phím" }), default);
        Assert.StartsWith("assertion_passed=true", check.Text);
        Assert.Equal("Thử chuột và phím", await File.ReadAllTextAsync(fixture.Output));
        computer.Authorize(fixture.Window);
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ExecuteAsync(new("computer_click", new() { ["snapshot_id"] = capture["snapshot_id"]!.ToString(), ["x"] = 1, ["y"] = 1 }), default));
    }

    [DesktopFact]
    public async Task Coordinates_RejectStaleSnapshotAndCancellation()
    {
        await using var fixture = await TestWindow.Start();
        var computer = new DesktopComputer(); computer.Authorize(fixture.Window);
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ExecuteAsync(new("computer_click", new() { ["snapshot_id"] = "not-a-snapshot", ["x"] = 1, ["y"] = 1 }), default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => computer.ExecuteAsync(new("computer_inspect", new()), cancel.Token));
    }

    private sealed class TestWindow(Process process, DesktopWindow window, string output) : IAsyncDisposable
    {
        public DesktopWindow Window => window;
        public string Output => output;
        public static async Task<TestWindow> Start()
        {
            var output = Path.Combine(Path.GetTempPath(), "muse-native-test-" + Guid.NewGuid().ToString("N") + ".txt");
            var runtime = Path.ChangeExtension(typeof(NativeComputerTests).Assembly.Location, ".runtimeconfig.json");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "exec", "--runtimeconfig", runtime, typeof(FixtureMarker).Assembly.Location, output }) start.ArgumentList.Add(argument);
            var process = Process.Start(start)!;
            try
            {
                var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (!long.TryParse(ready, out var handle)) throw new InvalidOperationException("Fixture did not start: " + ready + " " + await process.StandardError.ReadToEndAsync());
                return new(process, new(handle, process.Id, "Muse test fixture"), output);
            }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); process.Dispose();
            if (File.Exists(output)) File.Delete(output);
        }
    }
}

internal sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute() { if (Environment.GetEnvironmentVariable("MUSE_COMPUTER_TESTS") != "1") Skip = "Set MUSE_COMPUTER_TESTS=1 to launch and control a dedicated fixture window"; }
}
