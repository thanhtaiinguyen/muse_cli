using System.Diagnostics;

namespace Muse2Api.Tests;

public class ReleaseSwapTests
{
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
