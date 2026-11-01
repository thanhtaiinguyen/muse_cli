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
    public async Task HiddenOwnedDialogIsInScopeButAnotherWindowOfTheSameProcessIsNot()
    {
        await using var fixture = await TestWindow.Start(offscreen: true);
        Assert.True(NativeDesktop.InScope((nint)fixture.Window.Handle, (nint)fixture.Owned));
        Assert.False(NativeDesktop.InScope((nint)fixture.Window.Handle, (nint)fixture.Separate));
        Assert.False(NativeDesktop.IsWindowVisible((nint)fixture.Owned));
        Assert.False(NativeDesktop.IsWindowVisible((nint)fixture.Separate));
    }
    [Fact]
    public async Task ClosingTheHostCancelsEvenAnUnresponsivePermissionCallback()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var computer = new DesktopComputer { RequestAuthorization = _ => pending.Task };
        var task = computer.ExecuteAsync(new("computer_inspect", new()), default);
        computer.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => computer.ExecuteAsync(new("computer_inspect", new()), default));
    }

    [Fact]
    public async Task InvalidArgumentsDoNotOpenAPermissionDialog()
    {
        var prompts = 0; using var computer = new DesktopComputer { RequestAuthorization = _ => { prompts++; return Task.FromResult(false); } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ExecuteAsync(new("computer_key", new() { ["key"] = "ALT+TAB" }), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ExecuteAsync(new("computer_click", new() { ["x"] = 1, ["y"] = 2 }), default));
        Assert.Equal(0, prompts);
    }
    [Fact]
    public async Task HiddenWindow_InspectUsesFreshIdsAndRejectsInteractionWithoutMovingFocus()
    {
        await using var fixture = await TestWindow.Start(offscreen: true);
        Assert.False(NativeDesktop.IsWindowVisible((nint)fixture.Window.Handle));
        var foreground = NativeDesktop.GetForegroundWindow();
        using var computer = new DesktopComputer(); computer.Authorize(fixture.Window);
        var first = JsonNode.Parse((await computer.ExecuteAsync(new("computer_inspect", new()), default)).Text)!;
        var oldId = first["elements"]!.AsArray().First()!["element_id"]!.ToString();
        var second = JsonNode.Parse((await computer.ExecuteAsync(new("computer_inspect", new()), default)).Text)!;
        var id = second["elements"]!.AsArray().First()!["element_id"]!.ToString();
        Assert.NotEqual(oldId, id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ExecuteAsync(new("computer_assert", new() { ["element_id"] = oldId, ["property"] = "name", ["expected"] = "test" }), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ExecuteAsync(new("computer_invoke", new() { ["element_id"] = id }), default));
        Assert.Equal(foreground, NativeDesktop.GetForegroundWindow());
        Assert.False(NativeDesktop.IsWindowVisible((nint)fixture.Window.Handle));
    }

    [Fact]
    public async Task HiddenWindow_InspectFilteringIsBoundedAndCancelledCallsDoNotAskForAccess()
    {
        await using var fixture = await TestWindow.Start(offscreen: true);
        using var computer = new DesktopComputer(); computer.Authorize(fixture.Window);
        var result = JsonNode.Parse((await computer.ExecuteAsync(new("computer_inspect", new() { ["query"] = "NO_MATCH_71", ["limit"] = 1 }), default)).Text)!;
        Assert.Empty(result["elements"]!.AsArray());
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var prompts = 0;
        computer.RequestAuthorization = _ => { prompts++; return Task.FromResult(false); };
        computer.Disarm();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => computer.ExecuteAsync(new("computer_inspect", new()), cancel.Token));
        Assert.Equal(0, prompts);
        Assert.False(NativeDesktop.IsWindowVisible((nint)fixture.Window.Handle));
    }
    [Fact]
    public async Task DeniedAccess_IsAskedOnlyOncePerTurnWithoutOpeningAnyWindow()
    {
        var computer=new DesktopComputer(); var prompts=0;
        computer.WindowSource=()=>Array.Empty<DesktopWindow>();
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
    [Fact]
    public async Task AutoSelectIsDelegatedToTheHostSoPermissionsAndEmergencyStopAreApplied()
    {
        await using var fixture = await TestWindow.Start(offscreen: true);
        using var computer = new DesktopComputer();
        var prompts = 0;
        computer.WindowSource = () => [fixture.Window];
        computer.RequestAuthorization = _ => { prompts++; return Task.FromResult(computer.TryAutoAuthorize()); };
        var result = await computer.ExecuteAsync(new("computer_inspect", new()), default);
        Assert.Contains("Muse test fixture", result.Text);
        Assert.Equal(1, prompts);
        Assert.Equal(fixture.Window.Handle, computer.Target!.Handle);
    }

    [Fact]
    public async Task AnAvailableAutoCandidateCannotBypassADeniedHostPolicy()
    {
        await using var fixture=await TestWindow.Start(offscreen:true);
        using var computer=new DesktopComputer {WindowSource=()=>[fixture.Window]};var prompts=0;
        computer.RequestAuthorization=_=>{prompts++;return Task.FromResult(false);};
        Assert.StartsWith("error: user declined",(await computer.ExecuteAsync(new("computer_inspect",new()),default)).Text);
        Assert.Null(computer.Target);Assert.Equal(1,prompts);
        Assert.False(NativeDesktop.IsWindowVisible((nint)fixture.Window.Handle));
    }

    [Fact]
    public async Task RevokingScreenControlCannotAutomaticallySelectTheWindowAgainInTheSameTurn()
    {
        await using var fixture=await TestWindow.Start(offscreen:true);
        using var computer=new DesktopComputer {WindowSource=()=>[fixture.Window]};var prompts=0;
        computer.RequestAuthorization=_=>{prompts++;return Task.FromResult(computer.TryAutoAuthorize());};
        await computer.ExecuteAsync(new("computer_inspect",new()),default);computer.Disarm();
        var result=await computer.ExecuteAsync(new("computer_inspect",new()),default);
        Assert.StartsWith("error: screen_control_stopped",result.Text);Assert.Null(computer.Target);Assert.Equal(1,prompts);
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

    [DesktopFact]
    public async Task NativePointer_RightDoubleDragAndWheelAreVerifiedOnTheOwnedFixture()
    {
        await using var fixture = await TestWindow.Start(); using var computer = new DesktopComputer(); computer.Authorize(fixture.Window);
        var tree = JsonNode.Parse((await computer.ExecuteAsync(new("computer_inspect",new()),default)).Text)!["elements"]!.AsArray().OfType<JsonObject>().ToList();
        var surface=tree.Single(e=>e["automation_id"]?.ToString()=="pointer");var result=tree.Single(e=>e["automation_id"]?.ToString()=="result")["element_id"]!.ToString();
        await computer.ExecuteAsync(new("computer_focus",new(){["element_id"]=surface["element_id"]!.ToString()}),default);
        async Task<JsonObject> Capture()
        {
            var image=JsonNode.Parse((await computer.ExecuteAsync(new("computer_screenshot",new()),default)).Text)!;
            var x=(int)((surface["bounds"]!["x"]!.GetValue<double>()+70)*image["image_scale"]!["x"]!.GetValue<double>());
            var y=(int)((surface["bounds"]!["y"]!.GetValue<double>()+40)*image["image_scale"]!["y"]!.GetValue<double>());
            return new(){["snapshot_id"]=image["snapshot_id"]!.ToString(),["x"]=x,["y"]=y};
        }
        async Task Verify(string expected)=>Assert.StartsWith("assertion_passed=true",(await computer.ExecuteAsync(new("computer_assert",new(){["element_id"]=result,["property"]="name",["expected"]=expected}),default)).Text);
        var right=await Capture();right["button"]="right";await computer.ExecuteAsync(new("computer_click",right),default);await Verify("RightClick");
        var twice=await Capture();twice["double_click"]=true;await computer.ExecuteAsync(new("computer_click",twice),default);await Verify("DoubleClick");
        var drag=await Capture();drag["to_x"]=drag["x"]!.GetValue<int>()+90;drag["to_y"]=drag["y"]!.GetValue<int>();
        await computer.ExecuteAsync(new("computer_drag",drag),default);await Verify("DragDone");
        var wheel=await Capture();wheel["direction"]="down";await computer.ExecuteAsync(new("computer_scroll",wheel),default);await Verify("Wheel:down");
    }

    private sealed class TestWindow(Process process, DesktopWindow window, string output, long owned = 0, long separate = 0) : IAsyncDisposable
    {
        public DesktopWindow Window => window;
        public string Output => output;
        public long Owned => owned;
        public long Separate => separate;
        public static async Task<TestWindow> Start(bool offscreen = false)
        {
            var output = Path.Combine(Path.GetTempPath(), "muse-native-test-" + Guid.NewGuid().ToString("N") + ".txt");
            var runtime = Path.ChangeExtension(typeof(NativeComputerTests).Assembly.Location, ".runtimeconfig.json");
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (string.IsNullOrEmpty(host) && string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) host = Environment.ProcessPath;
            var start = new ProcessStartInfo(host ?? "dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "exec", "--runtimeconfig", runtime, typeof(FixtureMarker).Assembly.Location, output }) start.ArgumentList.Add(argument);
            if (offscreen) start.ArgumentList.Add("--offscreen");
            var process = Process.Start(start)!;
            try
            {
                var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (!long.TryParse(ready, out var handle)) throw new InvalidOperationException("Fixture did not start: " + ready + " " + await process.StandardError.ReadToEndAsync());
                if (offscreen)
                {
                    var others = (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Split(' ');
                    return new(process, new(handle, process.Id, "Muse test fixture"), output, long.Parse(others[0]), long.Parse(others[1]));
                }
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
