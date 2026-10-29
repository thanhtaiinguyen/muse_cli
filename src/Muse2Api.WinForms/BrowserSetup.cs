using System.Diagnostics;
using System.Reflection;

namespace Muse2Api.WinForms;

internal static class BrowserSetup
{
    public static async Task<int> InstallAsync(CancellationToken cancel)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate application runtime");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--prepare-browser");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot prepare browser");
        async Task Drain(StreamReader reader)
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory(), cancel) > 0) { }
        }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancel);
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            throw;
        }
    }
}
