using System.Diagnostics;
using System.IO;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Platform;

internal static class EngineProcessOwnership
{
    internal static bool IsManagedPath(string? path, IEnumerable<string> runtimeRoots)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            return runtimeRoots.Any(root => new[] { "winws.exe", "winws2.exe" }.Any(name =>
                string.Equals(full, Path.GetFullPath(Path.Combine(root, name)), StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    internal static IEnumerable<Process> FindOwned()
    {
        string[] roots = [AppPaths.EngineDir, AppPaths.SystemRuntimeDir, Path.Combine(AppPaths.AppDataRoot, "runtime")];
        foreach (string name in new[] { "winws", "winws2" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                if (IsManagedPath(path, roots)) yield return process;
                else process.Dispose();
            }
    }

    internal static void StopOwned(int? excludePid = null)
    {
        foreach (var process in FindOwned())
            using (process)
            {
                if (process.Id == excludePid) continue;
                try
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(2000); }
                }
                catch (Exception ex) { Trace.WriteLine("[engine cleanup] " + ex.Message); }
            }
    }
}
