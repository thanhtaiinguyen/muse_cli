using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Muse2Api.Agent;

/// <summary>Wait for owned descendants too; Process.WaitForExit only waits for the root.</summary>
internal static class ProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, Id;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool First(SafeFileHandle snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Next(SafeFileHandle snapshot, ref Entry entry);

    private static List<Process> Descendants(int root)
    {
        var result = new List<Process>();
        if (!OperatingSystem.IsWindows()) return result;
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return result;
        var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
        var parents = new Dictionary<int, int>();
        if (First(snapshot, ref entry)) do { parents[(int)entry.Id] = (int)entry.Parent; } while (Next(snapshot, ref entry));
        var known = new HashSet<int> { root };
        while (true)
        {
            var children = parents.Where(p => !known.Contains(p.Key) && known.Contains(p.Value)).Select(p => p.Key).ToArray();
            if (children.Length == 0) break;
            foreach (var id in children)
            {
                known.Add(id);
                try { var process = Process.GetProcessById(id); _ = process.Handle; result.Add(process); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return result;
    }

    public static async Task StopAsync(Process root)
    {
        var children = Descendants(root.Id);
        try
        {
            try { if (!root.HasExited) root.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await root.WaitForExitAsync();
            foreach (var child in children)
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await Task.WhenAll(children.Select(async child =>
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (InvalidOperationException) { }
            }));
        }
        finally { foreach (var child in children) child.Dispose(); }
    }
}
