using System.Diagnostics;
using System.IO;
using System.Windows;
using GxbrielZapret.Harness;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Startup;
using GxbrielZapret.Views;

namespace GxbrielZapret;

public partial class App : Application
{
    private SingleInstance? _instance;

    public static string StartupMode { get; private set; } = "Window";
    public static int MainWindowShowCount { get; internal set; } = 0;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (CommandLine.Value(e.Args, "--apply-app-update") is { } updateRequest)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(async () =>
            {
                try { await AppUpdateInstaller.RunAsync(updateRequest); Shutdown(0); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Обновление GXBRIEL_ZAPRET", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
            });
            return;
        }
        CrashLog.Install(this);
        CrashLog.RecordLifecycle($"START exe={Environment.ProcessPath}");
        AppPaths.EnsureCreated();
        try { ThirdPartyNotices.Ensure(); } catch (Exception ex) { CrashLog.Write(ex); }
        MigrationManager.EnsureMigrated();
        try
        {
            if (new SettingsService().Settings.GameRoutingHostsFix)
                RiotRoutingService.ApplyHostsFix();
            else
                RiotRoutingService.RemoveHostsFix();
        }
        catch { }
        AwaitPreviousCopy(e.Args);
        InitLanguage(e.Args);

        // Deliberately before the single-instance claim below: the headless modes are one-shots that
        // must still run alongside a live app, and each shuts the process down when it is done.
        if (RunHeadlessMode(e.Args)) return;

        _instance = SingleInstance.Claim(e.Args, SurfaceExistingWindow, ShutdownFromSignal);
        if (_instance is null)
        {
            Shutdown(0);   // another copy holds the slot and has been asked to surface
            return;
        }

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            bool startInTray = !CommandLine.Has(e.Args, "--window") && (CommandLine.Has(e.Args, "--tray")
                || (new SettingsService().Settings is { StartMinimized: true, MinimizeToTray: true }));
            StartupMode = startInTray ? "Tray" : "Window";
            if (startInTray)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window.InitializeForTrayStartup();
            }
            else
            {
                MainWindowShowCount++;
                window.Show();
            }
            if (CommandLine.Value(e.Args, "--update-health") is { } healthRequest)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => AppUpdateInstaller.SignalHealthy(healthRequest));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            MessageBox.Show(ex.ToString(), Loc.T("GXBRIEL_ZAPRET — критическая ошибка запуска"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>A later launch asked the copy already running to come to the front. Marshalled onto the
    /// UI thread: the signal arrives on the single-instance watcher thread.</summary>
    private void SurfaceExistingWindow() =>
        Dispatcher.BeginInvoke(() => (MainWindow as Views.MainWindow)?.SurfaceWindow());

    /// <summary>A newer build launch requested this running copy to cleanly exit.</summary>
    private void ShutdownFromSignal() =>
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is Views.MainWindow window) window.ExitApp();
            else Shutdown(0);
        });

    protected override void OnExit(ExitEventArgs e)
    {
        CrashLog.RecordLifecycle($"EXIT code={e.ApplicationExitCode}");
        // Hosts-based game routing is persistent with Settings.GameRoutingHostsFix and cleaned up when disabled or reset.
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Restart-to-apply for the language switch: start a fresh copy that waits for THIS one to exit, then
    /// shut down. <c>UseShellExecute=false</c> makes the child inherit our already-elevated token with no
    /// second UAC prompt; its <c>--awaitpid</c> gate holds it until our single-instance mutex is released
    /// on exit (otherwise the child would see the slot taken and just re-surface us). The language itself
    /// is persisted by the caller before this runs.
    /// </summary>
    public static void RelaunchSelf()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    Arguments = "--awaitpid " + Environment.ProcessId,
                });
        }
        catch (Exception ex) { CrashLog.Write(ex); }
        finally { Current.Shutdown(0); }
    }

    /// <summary>
    /// The relaunch gate (see <see cref="RelaunchSelf"/>): a copy started by the language switch carries
    /// <c>--awaitpid &lt;old&gt;</c> and waits for the previous copy to exit, so the single-instance mutex
    /// is free by the time it is claimed. Not a mode of its own — it just holds, then falls through to a
    /// normal startup in the newly-chosen language.
    /// </summary>
    private static void AwaitPreviousCopy(string[] args)
    {
        if (!CommandLine.TryValue(args, "--awaitpid", "", out string pid) || !int.TryParse(pid, out int id))
            return;
        try { using var prev = Process.GetProcessById(id); prev.WaitForExit(5000); }
        catch { /* already gone — nothing to wait for */ }
    }

    /// <summary>
    /// The UI language is chosen once, before any window is parsed: the XAML <c>{loc:Loc …}</c> extension
    /// resolves at parse time and switching is restart-to-apply, so a plain read is enough here. A
    /// <c>--lang ru|en</c> argument overrides the saved setting — that is how the screenshot harness
    /// renders either language without touching settings.json. Best-effort: a failed settings read just
    /// leaves the app on Russian.
    /// </summary>
    private static void InitLanguage(string[] args)
    {
        string? lang = CommandLine.Value(args, "--lang");
        try { Loc.Init(lang ?? new SettingsService().Settings.Language); } catch { Loc.Init(lang); }
    }

    /// <summary>
    /// The developer-facing launch modes, each implemented in its own file under Harness. They render or
    /// probe something and shut the process down themselves, so all this decides is whether a window
    /// should be opened at all. Returns true when one of them took the launch.
    /// </summary>
    private static bool RunHeadlessMode(string[] args)
    {
        if (CommandLine.TryValue(args, "--engine-lifecycle-report", Path.Combine(AppPaths.LogsDir, "engine-lifecycle.json"), out string lifecycleReport))
        {
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Current.Dispatcher.BeginInvoke(async () => await EngineLifecycleHarness.RunAsync(lifecycleReport));
            return true;
        }
        if (CommandLine.TryValue(args, "--antigravity-status", Path.Combine(AppPaths.LogsDir, "antigravity-status.json"), out string antigravityReport))
        {
            string? root = CommandLine.Value(args, "--antigravity-root");
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Current.Dispatcher.BeginInvoke(async () => await AntigravityReportHarness.RunAsync(antigravityReport, root));
            return true;
        }
        bool smoke = CommandLine.TryValue(args, "--blockcheck2-smoke", Path.Combine(AppPaths.LogsDir, "blockcheck2-smoke.json"), out string reportFile);
        if (smoke || CommandLine.TryValue(args, "--connectivity-report", Path.Combine(AppPaths.LogsDir, "connectivity-report.json"), out reportFile))
        {
            CommandLine.TryValue(args, "--blockcheck2-domain", "example.org", out string probeDomain);
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Current.Dispatcher.BeginInvoke(async () => await ConnectivityReportHarness.RunAsync(reportFile, smoke, probeDomain));
            return true;
        }
        if (CommandLine.TryValue(args, "--screenshot", Path.Combine(AppPaths.TempDir, "screenshots"), out string outDir))
        {
            string targetDir = Path.IsPathRooted(outDir) ? outDir : Path.Combine(AppPaths.TempDir, outDir);
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Current.Dispatcher.BeginInvoke(async () => await ScreenshotHarness.RunAsync(targetDir));
            return true;
        }
        if (CommandLine.TryValue(args, "--enginedump", Path.Combine(AppPaths.TempDir, "enginedump.txt"), out string dumpFile))
        {
            string targetDump = Path.IsPathRooted(dumpFile) ? dumpFile : Path.Combine(AppPaths.TempDir, dumpFile);
            EngineDump.Run(targetDump);
            return true;
        }
        if (CommandLine.TryValue(args, "--settings-migration-test", Path.Combine(AppPaths.TempDir, "settings-migration.json"), out string settingsOut) ||
            CommandLine.Has(args, "--settings-migration-test"))
        {
            string outJson = string.IsNullOrWhiteSpace(settingsOut)
                ? Path.Combine(AppPaths.TempDir, "settings-migration.json")
                : (Path.IsPathRooted(settingsOut) ? settingsOut : Path.Combine(AppPaths.TempDir, settingsOut));
            SettingsMigrationHarness.Run(outJson);
            return true;
        }
        if (CommandLine.TryValue(args, "--autoselect-test", Path.Combine(AppPaths.TempDir, "autoselect-test.log"), out string autoOut) ||
            CommandLine.Has(args, "--autoselect-test"))
        {
            string outLog = string.IsNullOrWhiteSpace(autoOut)
                ? Path.Combine(AppPaths.TempDir, "autoselect-test.log")
                : (Path.IsPathRooted(autoOut) ? autoOut : Path.Combine(AppPaths.TempDir, autoOut));
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Current.Dispatcher.BeginInvoke(async () => await AutoSelectHarness.RunAsync(outLog));
            return true;
        }
        if (CommandLine.TryValue(args, "--update-lists", Path.Combine(AppPaths.TempDir, "update-summary.log"), out string updateOut) ||
            CommandLine.TryValue(args, "--parse-blitzfix", Path.Combine(AppPaths.TempDir, "update-summary.log"), out updateOut) ||
            CommandLine.Has(args, "--update-lists") ||
            CommandLine.Has(args, "--parse-blitzfix"))
        {
            string outLog = string.IsNullOrWhiteSpace(updateOut)
                ? Path.Combine(AppPaths.TempDir, "update-summary.log")
                : (Path.IsPathRooted(updateOut) ? updateOut : Path.Combine(AppPaths.TempDir, updateOut));
            ListUpdateHarness.Run(outLog);
            return true;
        }
        if (CommandLine.Has(args, BlitzRepairService.HeadlessRepairArgument))
        {
            BlitzRepairService.RunElevatedRepair();
            Current.Shutdown(0);
            return true;
        }
        return false;
    }
}
