using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GxbrielZapret.Services.Platform;

internal static class AntigravityProcesses
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal static bool BelongsTo(string? executable, IEnumerable<string> componentPaths)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        string full = Path.GetFullPath(executable);
        foreach (string path in componentPaths)
        {
            if (string.Equals(full, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return true;
            string? install = AntigravityDiscovery.InstallRootFor(path);
            if (install is not null && full.StartsWith(install.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static List<Process> Find(string? root)
    {
        var result = new List<Process>();
        var paths = AntigravityDiscovery.Find(root).Values.ToList();
        string? manualInstall = string.IsNullOrWhiteSpace(root) ? null : AntigravityDiscovery.InstallRootFor(root);
        if (manualInstall is not null)
            paths.AddRange(new[] { "Antigravity.exe", "Antigravity IDE.exe" }.Select(n => Path.Combine(manualInstall, n)).Where(File.Exists));
        foreach (string name in new[] { "Antigravity", "Antigravity IDE", "language_server", "agy", "webm_encoder" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (BelongsTo(process.MainModule?.FileName, paths)) { result.Add(process); continue; }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                process.Dispose();
            }
        return result;
    }

    internal static IReadOnlyList<string> Describe(string? root)
    {
        var result = new List<string>();
        foreach (var process in Find(root))
            using (process)
                try { result.Add(process.ProcessName + " (" + process.Id + ")"); } catch (InvalidOperationException) { }
        return result;
    }

    internal static Task<IReadOnlyList<string>> RequestCloseAsync(string? root, CancellationToken ct)
        => CloseAsync(root, false, ct);

    internal static Task<IReadOnlyList<string>> TerminateAsync(string? root, CancellationToken ct)
        => CloseAsync(root, true, ct);

    private static async Task<IReadOnlyList<string>> CloseAsync(string? root, bool force, CancellationToken ct)
    {
        var processes = await Task.Run(() => Find(root), ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                foreach (var process in processes)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (!process.HasExited)
                        {
                            // Each process has a verified installation path. Do not kill unverified child trees.
                            if (force) process.Kill();
                            else
                            {
                                IntPtr window = process.MainWindowHandle;
                                if (window != IntPtr.Zero && !PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero))
                                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                            }
                        }
                    }
                    catch (InvalidOperationException) { }
                }
            }, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                foreach (var process in processes)
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() => Describe(root), ct).ConfigureAwait(false);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
