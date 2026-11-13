using System.Diagnostics;
using System.IO;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Startup;

/// <summary>
/// The one-running-copy lock with automatic stale-build replacement detection.
/// A second launch (shortcut, autostart, Explorer) will either:
/// 1. Ask the running tray instance to surface if it's the exact same build.
/// 2. Gracefully terminate and replace the running instance if a newer build/executable is launched
///    or if <c>--restart</c> / <c>--force</c> is specified.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\GxbrielZapret.SingleInstance";
    private const string SurfaceEventName = @"Global\GxbrielZapret.SurfaceWindow";
    private const string ShutdownEventName = @"Global\GxbrielZapret.ShutdownRequest";

    private Mutex? _mutex;
    private EventWaitHandle? _surfaceSignal;
    private EventWaitHandle? _shutdownSignal;

    private SingleInstance() { }

    /// <summary>
    /// Try to become the one running copy. Returns the claim when we are it,
    /// or <c>null</c> when another copy holds the slot and has been asked to surface.
    /// </summary>
    internal static SingleInstance? Claim(string[] args, Action onSurfaceRequested, Action? onShutdownRequested = null)
    {
        var claim = new SingleInstance();
        try
        {
            return claim.TryClaim(args, onSurfaceRequested, onShutdownRequested) ? claim : null;
        }
        catch (Exception ex)
        {
            Log("single-instance ОШИБКА, запускаюсь обычным образом: " + ex.Message);
            return claim;
        }
    }

    private bool TryClaim(string[] args, Action onSurfaceRequested, Action? onShutdownRequested)
    {
        var surfaceSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SurfaceEventName);
        var shutdownSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        _surfaceSignal = surfaceSignal;
        _shutdownSignal = shutdownSignal;

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isPrimary);

        if (!isPrimary)
        {
            bool forceRestart = CommandLine.Has(args, "--restart") || CommandLine.Has(args, "--force");
            bool isNewerBuild = false;

            try
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    var fileWriteTime = File.GetLastWriteTimeUtc(exePath);
                    int currentPid = Environment.ProcessId;
                    var otherProcesses = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)
                        .Where(p => p.Id != currentPid)
                        .ToList();

                    foreach (var p in otherProcesses)
                    {
                        try
                        {
                            if (fileWriteTime > p.StartTime.ToUniversalTime())
                            {
                                isNewerBuild = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (forceRestart || isNewerBuild)
            {
                Log($"обнаружен новый бинарник/перезапуск (force={forceRestart}, newer={isNewerBuild}) — завершаю старую копию");
                try
                {
                    shutdownSignal.Set();
                    int currentPid = Environment.ProcessId;
                    var otherProcesses = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)
                        .Where(p => p.Id != currentPid)
                        .ToList();

                    foreach (var p in otherProcesses)
                    {
                        try
                        {
                            if (!p.WaitForExit(2500))
                            {
                                p.Kill(entireProcessTree: true);
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Try claiming primary mutex again after old copy exited
                _mutex?.Dispose();
                _mutex = new Mutex(initiallyOwned: true, MutexName, out isPrimary);
                if (isPrimary)
                {
                    StartWatcher(surfaceSignal, shutdownSignal, onSurfaceRequested, onShutdownRequested);
                    Log("первая копия (после автозамены старого билда) — работаю");
                    return true;
                }
            }

            // Normal second launch -> bring window forward
            bool trayStart = CommandLine.Has(args, "--tray");
            if (!trayStart)
            {
                try { surfaceSignal.Set(); } catch { }
            }
            Log(trayStart
                ? "вторая копия (--tray) — выхожу тихо"
                : "вторая копия — сигналю первой развернуться и выхожу");
            return false;
        }

        StartWatcher(surfaceSignal, shutdownSignal, onSurfaceRequested, onShutdownRequested);
        Log("первая копия — работаю, слушаю сигнал разворота");
        return true;
    }

    private static void StartWatcher(
        EventWaitHandle surfaceSignal,
        EventWaitHandle shutdownSignal,
        Action onSurfaceRequested,
        Action? onShutdownRequested)
    {
        var waiter = new Thread(() =>
        {
            var handles = new WaitHandle[] { surfaceSignal, shutdownSignal };
            while (true)
            {
                try
                {
                    int index = WaitHandle.WaitAny(handles);
                    if (index == 0)
                    {
                        onSurfaceRequested();
                    }
                    else if (index == 1)
                    {
                        onShutdownRequested?.Invoke();
                        break;
                    }
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-watch",
        };
        waiter.Start();
    }

    public void Dispose()
    {
        _surfaceSignal?.Dispose();
        _shutdownSignal?.Dispose();
        _mutex?.Dispose();
    }

    private static void Log(string msg)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "startup.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] pid {Environment.ProcessId}: {msg}\n");
        }
        catch { }
    }
}
