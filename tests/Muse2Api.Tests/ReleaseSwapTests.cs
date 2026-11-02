using System.Diagnostics;

namespace Muse2Api.Tests;

public class ReleaseSwapTests
{
    [Theory]
    [InlineData("none",true)] [InlineData("done",true)] [InlineData("busy",false)] [InlineData("foreign-task",false)]
    public async Task ScheduledSwapRemovesOnlyItsOwnTaskWhenNoWorkRemains(string state,bool shouldRemove)
    {
        if(!OperatingSystem.IsWindows())return;
        using var dir=new TempDirectory();var dist=dir.File("dist");Directory.CreateDirectory(dist);var flag=dir.File("unregistered.txt");
        var stage=Path.Combine(dist,"Muse.stage."+Guid.NewGuid().ToString("N"));
        if(state is "done" or "busy"){Directory.CreateDirectory(stage);File.WriteAllText(Path.Combine(stage,"Muse.exe"),"verified fixture");}
        var taskLauncher=state=="foreign-task"?"C:\\another-project\\scripts\\Run-Swap-Hidden.vbs":dir.File("scripts\\Run-Swap-Hidden.vbs");
        var script="$ErrorActionPreference='Stop'\n. "+Quote(Path.Combine(Scripts(),"Release-Validation.ps1"))+"\n"
            +"function Get-ScheduledTask { param($TaskName,$TaskPath,$ErrorAction) [pscustomobject]@{Actions=@([pscustomobject]@{Execute='wscript.exe';Arguments="+Quote("//B //NoLogo \""+taskLauncher+"\"")+"})} }\n"
            +"function Unregister-ScheduledTask { param($TaskName,$TaskPath,[switch]$Confirm,$ErrorAction) [IO.File]::WriteAllText("+Quote(flag)+",'removed') }\n";
        if(state is "done" or "busy")script+="Write-MuseReleaseValidation "+Quote(stage)+" "+Quote(dist)+" '9.9.9'\n";
        script+="function Get-Process { param($Name,$ErrorAction) "+(state=="busy"?"[pscustomobject]@{Path="+Quote(Path.Combine(dist,"Muse","Muse.exe"))+"}":"")+" }\n";
        script+="& "+Quote(Path.Combine(Scripts(),"Swap-Release.ps1"))+" -WorkspaceRoot "+Quote(dir.Root)+" -ScheduledRun\n";
        await Run(script,dir.File("test.ps1"));
        Assert.Equal(shouldRemove,File.Exists(flag));
        if(state=="busy")Assert.True(Directory.Exists(stage));
        if(state=="done")Assert.True(File.Exists(Path.Combine(dist,"Muse","Muse.exe")));
    }

    [Fact]
    public async Task HiddenLauncherForwardsScheduledRunWithoutAVisibleConsole()
    {
        if(!OperatingSystem.IsWindows())return;
        using var dir=new TempDirectory();var folder=dir.File("ẩn có khoảng trắng");Directory.CreateDirectory(folder);
        var launcher=Path.Combine(folder,"Run-Swap-Hidden.vbs");File.Copy(Path.Combine(Scripts(),"Run-Swap-Hidden.vbs"),launcher);
        var flag=Path.Combine(folder,"visible.txt");
        var script="""
            param([switch]$ScheduledRun)
            $ErrorActionPreference='Stop'
            if (!$ScheduledRun) { exit 17 }
            Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class WindowProbe { [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle); }'
            """+"\n[IO.File]::WriteAllText("+Quote(flag)+",[WindowProbe]::IsWindowVisible([WindowProbe]::GetConsoleWindow()).ToString())\n";
        await File.WriteAllTextAsync(Path.Combine(folder,"Swap-Release.ps1"),script,new System.Text.UTF8Encoding(true));
        using var process=new Process {StartInfo=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","wscript.exe")){UseShellExecute=false,CreateNoWindow=true}};
        foreach(var arg in new[]{"//B","//NoLogo",launcher})process.StartInfo.ArgumentList.Add(arg);
        process.Start();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(entireProcessTree:true);throw;}
        Assert.Equal(0,process.ExitCode);Assert.Equal("False",await File.ReadAllTextAsync(flag));
    }

    private static string Scripts()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"scripts","Swap-Release.ps1"))) return Path.Combine(directory.FullName,"scripts");
        throw new InvalidOperationException("Repository scripts not found.");
    }
    private static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
    private static async Task Run(string script,string scriptPath)
    {
        await File.WriteAllTextAsync(scriptPath,script,new System.Text.UTF8Encoding(true));
        using var process=new Process {StartInfo=new("powershell.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",scriptPath})process.StartInfo.ArgumentList.Add(arg);
        process.Start();var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(cancel.Token); }
        catch { process.Kill(entireProcessTree:true);throw; }
        Assert.True(process.ExitCode==0,await output+"\n"+await error);
    }

    [Theory]
    [InlineData("missing",false)] [InlineData("valid",true)] [InlineData("changed",false)]
    [InlineData("extra",false)] [InlineData("personal-current",false)] [InlineData("personal-stage",false)]
    public async Task SwapsOnlyAnUnchangedVerifiedReleaseAndPreservesPersonalData(string scenario,bool shouldSwap)
    {
        if(!OperatingSystem.IsWindows())return;
        using var dir=new TempDirectory();var dist=dir.File("dist");var current=Path.Combine(dist,"Muse");
        var stage=Path.Combine(dist,"Muse.stage."+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(current);Directory.CreateDirectory(stage);
        await File.WriteAllTextAsync(Path.Combine(current,"Muse.exe"),"old verified app");
        await File.WriteAllTextAsync(Path.Combine(stage,"Muse.exe"),"new app");
        await File.WriteAllTextAsync(Path.Combine(stage,"library.dll"),"original library");
        if(scenario=="personal-current")await File.WriteAllTextAsync(Path.Combine(current,"accounts.json"),"keep user data");
        if(scenario=="personal-stage")await File.WriteAllTextAsync(Path.Combine(stage,"accounts.json"),"do not package");
        var script="$ErrorActionPreference='Stop'\n. "+Quote(Path.Combine(Scripts(),"Release-Validation.ps1"))+"\n";
        if(scenario!="missing")script+="Write-MuseReleaseValidation "+Quote(stage)+" "+Quote(dist)+" '9.9.9'\n";
        if(scenario=="changed")script+="Set-Content -LiteralPath "+Quote(Path.Combine(stage,"library.dll"))+" -Value 'changed after validation'\n";
        if(scenario=="extra")script+="Set-Content -LiteralPath "+Quote(Path.Combine(stage,"extra.dll"))+" -Value 'not validated'\n";
        script+="& "+Quote(Path.Combine(Scripts(),"Swap-Release.ps1"))+" -WorkspaceRoot "+Quote(dir.Root)+"\n";
        await Run(script,dir.File("scenario.ps1"));
        Assert.Equal(shouldSwap?"new app":"old verified app",await File.ReadAllTextAsync(Path.Combine(current,"Muse.exe")));
        Assert.Equal(!shouldSwap,Directory.Exists(stage));
        if(scenario=="personal-current")Assert.Equal("keep user data",await File.ReadAllTextAsync(Path.Combine(current,"accounts.json")));
        Assert.Empty(Directory.EnumerateDirectories(dist,"Muse.old.*"));
    }

    [Fact]
    public async Task NewerUnvalidatedStageCannotReplaceAnOlderVerifiedStage()
    {
        if(!OperatingSystem.IsWindows())return;
        using var dir=new TempDirectory();var dist=dir.File("dist");Directory.CreateDirectory(dist);
        var valid=Path.Combine(dist,"Muse.stage."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(valid);
        var invalid=Path.Combine(dist,"Muse.stage."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(invalid);
        await File.WriteAllTextAsync(Path.Combine(valid,"Muse.exe"),"verified");await File.WriteAllTextAsync(Path.Combine(invalid,"Muse.exe"),"unfinished");
        await Run("$ErrorActionPreference='Stop'\n. "+Quote(Path.Combine(Scripts(),"Release-Validation.ps1"))+"\nWrite-MuseReleaseValidation "+Quote(valid)+" "+Quote(dist)+" '9.9.9'\n"
            +"(Get-Item -LiteralPath "+Quote(invalid)+").LastWriteTime=(Get-Date).AddMinutes(1)\n& "+Quote(Path.Combine(Scripts(),"Swap-Release.ps1"))+" -WorkspaceRoot "+Quote(dir.Root),dir.File("scenario.ps1"));
        Assert.Equal("verified",await File.ReadAllTextAsync(Path.Combine(dist,"Muse","Muse.exe")));Assert.True(Directory.Exists(invalid));
    }
}
