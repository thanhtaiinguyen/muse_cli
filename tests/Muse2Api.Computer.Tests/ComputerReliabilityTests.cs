using System.Text.Json.Nodes;
using Muse2Api.Agent;

namespace Muse2Api.Computer.Tests;

public class ComputerReliabilityTests
{
    [Fact]
    public async Task PreviewNeverRequestsDesktopAuthorization()
    {
        using var computer=new DesktopComputer();var prompts=0;
        computer.RequestAuthorization=_=>{prompts++;return Task.FromResult(false);};
        Assert.Null(await computer.PreviewAsync(default));Assert.Equal(0,prompts);
    }

    [Fact]
    public async Task PreviewSkipsBusyWorkerWithoutQueueingOrRevokingAccess()
    {
        using var release=new ManualResetEventSlim();using var started=new ManualResetEventSlim();using var worker=new ComputerWorker();var stopped=0;
        var running=worker.RunAsync(()=>{started.Set();release.Wait(TimeSpan.FromSeconds(5));return "done";},()=>stopped++,default);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            var preview=await worker.RunAsync(()=>"should not run",()=>stopped++,default,skipWhenBusy:true);
            Assert.Null(preview);Assert.Equal(0,stopped);
        }
        finally {release.Set();await running;}
    }

    [Theory]
    [InlineData(-1920, 0, 1920, 1080, 1600, 900, 0, 0, -1920, 0)]
    [InlineData(100, 200, 2000, 1001, 1600, 800, 1599, 799, 2099, 1200)]
    [InlineData(-300, -200, 800, 600, 800, 600, 799, 599, 499, 399)]
    [InlineData(0, 0, 4000, 2000, 1600, 800, 800, 400, 2001, 1001)]
    public void ScreenshotPixelsMapUsingBothRealAxesIncludingNegativeMonitors(int left,int top,int width,int height,int imageWidth,int imageHeight,int x,int y,int expectedX,int expectedY)
    {
        var map = new ScreenshotMap(new() { Left=left,Top=top,Right=left+width,Bottom=top+height },imageWidth,imageHeight);
        var point=map.ToScreen(x,y);
        Assert.Equal(expectedX,point.X);Assert.Equal(expectedY,point.Y);
    }

    [Theory]
    [InlineData(-1,0)] [InlineData(0,-1)] [InlineData(1600,0)] [InlineData(0,800)] [InlineData(int.MaxValue,int.MaxValue)]
    public void RejectsCoordinatesOutsideTheActualRoundedImage(int x,int y)
    {
        var map=new ScreenshotMap(new(){Right=2401,Bottom=1201},1600,800);
        Assert.Throws<InvalidOperationException>(()=>map.ToScreen(x,y));
    }

    [Theory]
    [InlineData("ESC")] [InlineData("ALT+TAB")] [InlineData("WIN+R")] [InlineData("ALT+F4")]
    public void GlobalSystemKeysCannotEscapeTheAuthorizedWindow(string key)
        =>Assert.Throws<InvalidOperationException>(()=>ComputerActionPolicy.Keys(key));

    [Theory]
    [InlineData("CTRL+S",17,83)] [InlineData("SHIFT+TAB",16,9)] [InlineData("CTRL+HOME",17,36)]
    public void AppShortcutsHaveExplicitKeySequences(string key,int first,int second)
        =>Assert.Equal(new ushort[]{(ushort)first,(ushort)second},ComputerActionPolicy.Keys(key));

    [Theory]
    [InlineData("computer_focus")] [InlineData("computer_drag")] [InlineData("computer_click")]
    [InlineData("computer_type")] [InlineData("computer_scroll")] [InlineData("computer_toggle")]
    public void MutationsInvalidateThePreviousCapture(string name)=>Assert.True(ComputerActionPolicy.Mutates(name));

    [Theory]
    [InlineData("computer_inspect")] [InlineData("computer_assert")] [InlineData("computer_screenshot")]
    public void ObservationsDoNotInvalidateACapture(string name)=>Assert.False(ComputerActionPolicy.Mutates(name));

    [Theory]
    [InlineData("computer_click","{\"snapshot_id\":\"s1\",\"x\":-1,\"y\":0}")]
    [InlineData("computer_click","{\"snapshot_id\":\"s1\",\"x\":1.5,\"y\":0}")]
    [InlineData("computer_drag","{\"snapshot_id\":\"s1\",\"x\":0,\"y\":0}")]
    [InlineData("computer_scroll","{\"direction\":\"down\"}")]
    [InlineData("computer_assert","{\"element_id\":\"e1\",\"property\":\"name\"}")]
    [InlineData("computer_set_value","{\"element_id\":\"e1\"}")]
    [InlineData("computer_inspect","{\"offset\":-1}")]
    public void InvalidArgumentsAreRejectedBeforePermissionOrInput(string name,string args)
        =>Assert.Throws<InvalidOperationException>(()=>ComputerActionPolicy.Validate(new(name,JsonNode.Parse(args)!.AsObject())));

    [Fact]
    public async Task ProviderTimeoutRevokesAccessAndNextCallHasABoundedWait()
    {
        using var release=new ManualResetEventSlim();using var started=new ManualResetEventSlim();var stopped=0;
        using var worker=new ComputerWorker(TimeSpan.FromMilliseconds(150),TimeSpan.FromMilliseconds(60));
        try
        {
            var task=worker.RunAsync(()=>{started.Set();release.Wait(TimeSpan.FromSeconds(10));return 1;},()=>Interlocked.Increment(ref stopped),default);
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>task);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>worker.RunAsync(()=>2,()=>Interlocked.Increment(ref stopped),default));
            Assert.Equal(2,stopped);
        }
        finally {release.Set();}
    }

    [Fact]
    public async Task CancellingBeforeAQueuedOperationNeverExecutesIt()
    {
        using var worker=new ComputerWorker();using var cancel=new CancellationTokenSource();cancel.Cancel();var executed=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>worker.RunAsync(()=>++executed,()=>{},cancel.Token));
        Assert.Equal(0,executed);
    }

    [Fact]
    public async Task UiaCallsUseOneLongLivedMtaThreadAndFaultsDoNotPoisonIt()
    {
        using var worker=new ComputerWorker();
        var first=await worker.RunAsync(()=> (Environment.CurrentManagedThreadId,Thread.CurrentThread.GetApartmentState()),()=>{},default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>worker.RunAsync<int>(()=>throw new InvalidOperationException("fixture"),()=>{},default));
        var second=await worker.RunAsync(()=> (Environment.CurrentManagedThreadId,Thread.CurrentThread.GetApartmentState()),()=>{},default);
        Assert.Equal(first,second);Assert.Equal(ApartmentState.MTA,first.Item2);
    }

    private static DesktopWindow Win(long handle,int pid,string title,string app="")=>new(handle,pid,title,0,app);

    [Fact]
    public void AutoPickPrefersTheForegroundWindow()
        =>Assert.Equal(200,WindowAutoPicker.Pick([Win(100,10,"A - app1","app1"),Win(200,20,"B - app2","app2")],200,999)!.Handle);

    [Fact]
    public void AutoPickFallsBackToTopmostWhenForegroundIsMuseItself()
        =>Assert.Equal(200,WindowAutoPicker.Pick([Win(900,999,"Muse"),Win(200,20,"B - app2","app2"),Win(100,10,"A - app1","app1")],900,999)!.Handle);

    [Fact]
    public void AutoPickNeverSelectsMuseOrShellOrUntitledWindows()
    {
        Assert.Null(WindowAutoPicker.Pick([Win(900,999,"Muse")],900,999));
        Assert.Null(WindowAutoPicker.Pick([Win(300,30,"Start","StartMenuExperienceHost")],300,999));
        Assert.Null(WindowAutoPicker.Pick([Win(300,30,"Windows Input Experience","SomeHost")],300,999));
        Assert.Null(WindowAutoPicker.Pick([Win(300,30,"Windows Input Experience")],300,999));
        Assert.Null(WindowAutoPicker.Pick([Win(300,30,"  ")],300,999));
        Assert.Null(WindowAutoPicker.Pick([],0,999));
    }

    [Fact]
    public void AutoPickSkipsTheInputExperienceOverlayForTheAppBelowIt()
        =>Assert.Equal(200,WindowAutoPicker.Pick([Win(300,30,"Windows Input Experience","TextInputHost"),Win(200,20,"B - app2","app2")],300,999)!.Handle);

    [Fact]
    public void AutoPickHonoursNameHintBeforeForeground()
        =>Assert.Equal(100,WindowAutoPicker.Pick([Win(100,10,"Tài liệu - Word","WINWORD"),Win(200,20,"Web - Chrome","chrome")],200,999,"word")!.Handle);

    [Fact]
    public void AutoPickIgnoresUnknownHintAndUsesForeground()
        =>Assert.Equal(200,WindowAutoPicker.Pick([Win(100,10,"A - app1","app1"),Win(200,20,"B - app2","app2")],200,999,"khong-co")!.Handle);
}
