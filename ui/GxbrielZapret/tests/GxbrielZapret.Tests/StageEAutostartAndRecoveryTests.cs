using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Engine.Discovery;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Services.Strategies;
using GxbrielZapret.Services.Telegram;
using GxbrielZapret.ViewModels;
using Xunit;

namespace GxbrielZapret.Tests;

public class StageEAutostartAndRecoveryTests
{
    private sealed class TempEnvironmentScope : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _prevAppDataRoot;
        private readonly string? _prevProgramDataRoot;

        public TempEnvironmentScope()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "gx_test_stage_e_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);

            _prevAppDataRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
            _prevProgramDataRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT");

            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _tempRoot);
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _tempRoot);

            AppPaths.ResetSystemRuntimeDirCache();
            AppPaths.ResetEngineDirCache();
            AppPaths.EnsureCreated();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                    Directory.Delete(_tempRoot, recursive: true);
            }
            catch { }
            finally
            {
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _prevAppDataRoot);
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _prevProgramDataRoot);
                AppPaths.ResetSystemRuntimeDirCache();
                AppPaths.ResetEngineDirCache();
                NetworkReadinessGate.SetTestProbe(null);
            }
        }
    }

    // =========================================================================
    // 1. Desired State vs Runtime State Separation & Crash Recovery
    // =========================================================================

    [Fact]
    public void DesiredState_SeparatedFromRuntimeState_EngineCrashPreservesDesiredOn()
    {
        using var scope = new TempEnvironmentScope();
        var settingsSvc = new SettingsService();

        // User intentionally turns on bypass
        settingsSvc.Settings.DesiredBypassEnabled = true;
        settingsSvc.Settings.ActivePresetId = "r-discord-general";
        settingsSvc.Settings.BypassActual = "RUNNING";
        settingsSvc.Save();

        // Simulate engine crash (state becomes STOPPED, but user intent is untouched)
        settingsSvc.Settings.BypassActual = "STOPPED";
        settingsSvc.Save();

        // Reload fresh from disk
        var reloaded = new SettingsService();
        Assert.True(reloaded.Settings.DesiredBypassEnabled);
        Assert.Equal("r-discord-general", reloaded.Settings.ActivePresetId);
        Assert.Equal("STOPPED", reloaded.Settings.BypassActual);
    }

    [Fact]
    public async Task DesiredState_ManualStopSetsDesiredOff_DoesNotResurrectOnRecovery()
    {
        using var scope = new TempEnvironmentScope();
        var settingsSvc = new SettingsService();

        // User intentionally turns on bypass
        settingsSvc.Settings.DesiredBypassEnabled = true;
        settingsSvc.Settings.ActivePresetId = "r-discord-general";
        settingsSvc.Save();

        // User intentionally clicks STOP
        settingsSvc.Settings.DesiredBypassEnabled = false;
        settingsSvc.Settings.BypassActual = "STOPPED";
        settingsSvc.Save();

        // Verify recovery does NOT restore bypass
        var engineSvc = new EngineService();
        var tgSvc = new TelegramProxyService();
        var recovery = new RecoveryCoordinator(settingsSvc, engineSvc, tgSvc);

        var presets = new PresetService().All;
        var result = await recovery.ExecuteRecoveryAsync(presets);

        Assert.False(result.BypassDesired);
        Assert.False(result.BypassRestored);
        Assert.False(engineSvc.IsRunning);
    }

    // =========================================================================
    // 2. Four-State Bypass / Telegram Recovery Matrix
    // =========================================================================

    [Fact]
    public async Task FourStateRecoveryMatrix_BypassOff_TelegramOff()
    {
        using var scope = new TempEnvironmentScope();
        var settingsSvc = new SettingsService();
        settingsSvc.Settings.DesiredBypassEnabled = false;
        settingsSvc.Settings.DesiredTelegramEnabled = false;
        settingsSvc.Save();

        var engineSvc = new EngineService();
        var tgSvc = new TelegramProxyService();
        var recovery = new RecoveryCoordinator(settingsSvc, engineSvc, tgSvc);

        var result = await recovery.ExecuteRecoveryAsync(new PresetService().All);

        Assert.False(result.BypassDesired);
        Assert.False(result.BypassRestored);
        Assert.False(result.TelegramDesired);
        Assert.False(result.TelegramRestored);
        Assert.False(engineSvc.IsRunning);
        Assert.False(tgSvc.IsRunning);
    }

    [Fact]
    public async Task FourStateRecoveryMatrix_BypassOn_TelegramOff()
    {
        using var scope = new TempEnvironmentScope();
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(true));

        var settingsSvc = new SettingsService();
        settingsSvc.Settings.DesiredBypassEnabled = true;
        settingsSvc.Settings.ActivePresetId = "GX-B08";
        settingsSvc.Settings.DesiredTelegramEnabled = false;
        settingsSvc.Save();

        var engineSvc = new EngineService();
        var tgSvc = new TelegramProxyService();
        var recovery = new RecoveryCoordinator(settingsSvc, engineSvc, tgSvc);

        var presets = new PresetService().All;
        var result = await recovery.ExecuteRecoveryAsync(presets);

        Assert.True(result.BypassDesired);
        Assert.Equal("Discord — голос (QUIC-фейк)", result.BypassPresetName);
        Assert.False(result.TelegramDesired);
        Assert.False(result.TelegramRestored);
        Assert.False(tgSvc.IsRunning);
    }

    [Fact]
    public async Task FourStateRecoveryMatrix_BypassOff_TelegramOn()
    {
        using var scope = new TempEnvironmentScope();
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(true));

        var settingsSvc = new SettingsService();
        settingsSvc.Settings.DesiredBypassEnabled = false;
        settingsSvc.Settings.DesiredTelegramEnabled = true;
        settingsSvc.Settings.TgProxyPort = 19443;
        settingsSvc.Save();

        var engineSvc = new EngineService();
        var tgSvc = new TelegramProxyService();
        var recovery = new RecoveryCoordinator(settingsSvc, engineSvc, tgSvc);

        try
        {
            var result = await recovery.ExecuteRecoveryAsync(new PresetService().All);

            Assert.False(result.BypassDesired);
            Assert.False(result.BypassRestored);
            Assert.True(result.TelegramDesired);
            Assert.True(result.TelegramRestored);
            Assert.True(tgSvc.IsRunning);
        }
        finally
        {
            tgSvc.Stop();
        }
    }

    [Fact]
    public async Task FourStateRecoveryMatrix_BypassOn_TelegramOn()
    {
        using var scope = new TempEnvironmentScope();
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(true));

        var settingsSvc = new SettingsService();
        settingsSvc.Settings.DesiredBypassEnabled = true;
        settingsSvc.Settings.ActivePresetId = "GX-B01";
        settingsSvc.Settings.DesiredTelegramEnabled = true;
        settingsSvc.Settings.TgProxyPort = 19444;
        settingsSvc.Save();

        var engineSvc = new EngineService();
        var tgSvc = new TelegramProxyService();
        var recovery = new RecoveryCoordinator(settingsSvc, engineSvc, tgSvc);

        try
        {
            var result = await recovery.ExecuteRecoveryAsync(new PresetService().All);

            Assert.True(result.BypassDesired);
            Assert.Equal("Комбо (рекомендуемый)", result.BypassPresetName);
            Assert.True(result.TelegramDesired);
            Assert.True(result.TelegramRestored);
            Assert.True(tgSvc.IsRunning);
        }
        finally
        {
            tgSvc.Stop();
        }
    }

    // =========================================================================
    // 3. Network Readiness Gate & Polling
    // =========================================================================

    [Fact]
    public async Task NetworkReadinessGate_WaitsForNetworkReady_WithPollingAndBackoff()
    {
        using var scope = new TempEnvironmentScope();
        int probeAttempts = 0;

        NetworkReadinessGate.SetTestProbe(async ct =>
        {
            probeAttempts++;
            await Task.Delay(10, ct);
            return probeAttempts >= 3; // Becomes ready on 3rd check
        });

        var reports = new List<string>();
        var progress = new Progress<string>(reports.Add);

        bool ready = await NetworkReadinessGate.WaitForNetworkReadyAsync(
            TimeSpan.FromSeconds(5),
            initialPollInterval: TimeSpan.FromMilliseconds(50),
            progress: progress);

        Assert.True(ready);
        Assert.True(probeAttempts >= 3);
        Assert.Contains(reports, r => r.Contains("Сеть готова") || r.Contains("Ожидание"));
    }

    [Fact]
    public async Task NetworkReadinessGate_TimeoutHandling_ReturnsFalseWithoutHanging()
    {
        using var scope = new TempEnvironmentScope();
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(false));

        var sw = Stopwatch.StartNew();
        bool ready = await NetworkReadinessGate.WaitForNetworkReadyAsync(
            TimeSpan.FromMilliseconds(200),
            initialPollInterval: TimeSpan.FromMilliseconds(50));

        sw.Stop();
        Assert.False(ready);
        Assert.True(sw.ElapsedMilliseconds >= 150);
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public async Task NetworkReadinessGate_Cancellation_AbortsImmediately()
    {
        using var scope = new TempEnvironmentScope();
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(false));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();

        bool ready = await NetworkReadinessGate.WaitForNetworkReadyAsync(
            TimeSpan.FromSeconds(10),
            initialPollInterval: TimeSpan.FromMilliseconds(50),
            ct: cts.Token);

        sw.Stop();
        Assert.False(ready);
        Assert.True(sw.ElapsedMilliseconds < 1500);
    }

    // =========================================================================
    // 4. Task Scheduler XML & Working Directory & Stale Detection
    // =========================================================================

    [Fact]
    public void AutostartService_BuildTaskXml_IncludesWorkingDirectory_AndHighestPrivileges()
    {
        string fakeExe = @"C:\ProductionApp\GXBRIEL_ZAPRET.exe";
        string xml = AutostartService.BuildTaskXml(fakeExe, "--tray", 5);

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var runLevel = doc.Descendants(ns + "RunLevel").FirstOrDefault()?.Value;
        Assert.Equal("HighestAvailable", runLevel);

        var cmd = doc.Descendants(ns + "Command").FirstOrDefault()?.Value;
        Assert.Equal(fakeExe, cmd);

        var workDir = doc.Descendants(ns + "WorkingDirectory").FirstOrDefault()?.Value;
        Assert.Equal(@"C:\ProductionApp", workDir);

        var args = doc.Descendants(ns + "Arguments").FirstOrDefault()?.Value;
        Assert.Equal("--tray", args);

        var delay = doc.Descendants(ns + "Delay").FirstOrDefault()?.Value;
        Assert.Equal("PT5S", delay);

        var execLimit = doc.Descendants(ns + "ExecutionTimeLimit").FirstOrDefault()?.Value;
        Assert.Equal("PT0S", execLimit);
    }

    [Fact]
    public void AutostartService_BuildTaskXml_HasNoDevPaths()
    {
        string prodExe = @"C:\Users\fixture\AppData\Local\Programs\GXBRIEL_ZAPRET\GXBRIEL_ZAPRET.exe";
        string xml = AutostartService.BuildTaskXml(prodExe, "--tray");

        Assert.DoesNotContain("!VS_MY_PROJECT", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GXBRIEL_ZAPRET\\ui", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bin\\Debug", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bin\\Release", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoryResetService_Removes_All_Local_State_And_Runtime()
    {
        using var scope = new TempEnvironmentScope();

        Directory.CreateDirectory(AppPaths.ConfigDir);
        Directory.CreateDirectory(AppPaths.StrategiesUserDir);
        Directory.CreateDirectory(AppPaths.ListsUserDir);
        Directory.CreateDirectory(AppPaths.LogsDir);
        Directory.CreateDirectory(AppPaths.CacheDir);
        Directory.CreateDirectory(AppPaths.TelegramDir);
        string engineDir = AppPaths.EngineDir;
        Directory.CreateDirectory(engineDir);

        File.WriteAllText(AppPaths.SettingsFile, "{}");
        File.WriteAllText(Path.Combine(AppPaths.StrategiesUserDir, "custom.json"), "{}");
        File.WriteAllText(Path.Combine(AppPaths.ListsUserDir, "custom.txt"), "example.com");
        File.WriteAllText(Path.Combine(AppPaths.LogsDir, "engine-test.log"), "log");
        File.WriteAllText(Path.Combine(AppPaths.CacheDir, "cache.bin"), "cache");
        File.WriteAllText(Path.Combine(AppPaths.TelegramDir, "state.json"), "{}");
        File.WriteAllText(Path.Combine(engineDir, "winws2.exe"), "runtime");

        var result = new FactoryResetService().Reset();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.False(Directory.Exists(AppPaths.ConfigDir));
        Assert.False(Directory.Exists(AppPaths.StrategiesDir));
        Assert.False(Directory.Exists(AppPaths.ListsDir));
        Assert.False(Directory.Exists(AppPaths.LogsDir));
        Assert.False(Directory.Exists(AppPaths.CacheDir));
        Assert.False(Directory.Exists(AppPaths.TelegramDir));
        Assert.False(Directory.Exists(engineDir));
    }

    // =========================================================================
    // 5. Self-Healing Runtime Lists on Recovery
    // =========================================================================

    [Fact]
    public async Task RecoveryCoordinator_SelfHealsRuntimeLists_BeforeRestore()
    {
        using var scope = new TempEnvironmentScope();
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(true));

        var settingsSvc = new SettingsService();
        settingsSvc.Settings.DesiredBypassEnabled = true;
        settingsSvc.Settings.ActivePresetId = "r-discord-general";
        settingsSvc.Save();

        // Ingest a discovered item
        var store = new DiscoveryStore();
        store.IngestEndpoint(new RawEndpointInput("custom-voice.discord.gg", "trace", "discord"));

        // Delete runtime directory completely
        if (Directory.Exists(AppPaths.RuntimeListsDir))
            Directory.Delete(AppPaths.RuntimeListsDir, recursive: true);

        Assert.False(File.Exists(Path.Combine(AppPaths.RuntimeListsDir, "list-discord.txt")));

        // Recovery executes
        var engineSvc = new EngineService();
        var tgSvc = new TelegramProxyService();
        var recovery = new RecoveryCoordinator(settingsSvc, engineSvc, tgSvc, store);

        await recovery.ExecuteRecoveryAsync(new PresetService().All);

        // Verify runtime directory and hostlist were regenerated
        string runtimeList = Path.Combine(AppPaths.RuntimeListsDir, "list-discord.txt");
        Assert.True(File.Exists(runtimeList));
        var content = File.ReadAllText(runtimeList);
        Assert.Contains("custom-voice.discord.gg", content);
    }

    // =========================================================================
    // 6. Idempotency & Failure Safety
    // =========================================================================

    [Fact]
    public async Task RecoveryCoordinator_DuplicateRuns_AreIdempotent()
    {
        using var scope = new TempEnvironmentScope();
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(true));

        var settingsSvc = new SettingsService();
        settingsSvc.Settings.DesiredBypassEnabled = false;
        settingsSvc.Settings.DesiredTelegramEnabled = false;
        settingsSvc.Save();

        var engineSvc = new EngineService();
        var tgSvc = new TelegramProxyService();
        var recovery = new RecoveryCoordinator(settingsSvc, engineSvc, tgSvc);

        var res1 = await recovery.ExecuteRecoveryAsync(new PresetService().All);
        var res2 = await recovery.ExecuteRecoveryAsync(new PresetService().All);

        Assert.False(res1.BypassRestored);
        Assert.False(res2.BypassRestored);
        Assert.False(engineSvc.IsRunning);
    }

    [Fact]
    public async Task RecoveryCoordinator_FailureDoesNotCorruptDesiredSettings()
    {
        using var scope = new TempEnvironmentScope();
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(true));

        var settingsSvc = new SettingsService();
        settingsSvc.Settings.DesiredBypassEnabled = true;
        settingsSvc.Settings.ActivePresetId = "non_existent_fake_preset";
        settingsSvc.Save();

        var engineSvc = new EngineService();
        var tgSvc = new TelegramProxyService();
        var recovery = new RecoveryCoordinator(settingsSvc, engineSvc, tgSvc);

        // Available presets list is empty -> restoration will fail
        var res = await recovery.ExecuteRecoveryAsync(Array.Empty<Preset>());

        Assert.False(res.BypassRestored);

        // Desired intent must remain TRUE despite runtime failure
        var freshSettings = new SettingsService();
        Assert.True(freshSettings.Settings.DesiredBypassEnabled);
        Assert.Equal("ERROR", freshSettings.Settings.BypassActual);
    }

    // =========================================================================
    // 7. Silent Startup Telemetry Marker
    // =========================================================================

    [Fact]
    public void SilentStartup_ShowCountTelemetry_InitializedCorrectly()
    {
        Assert.Equal(0, App.MainWindowShowCount);
    }
}
