using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Telegram;
using GxbrielZapret.ViewModels;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class LifecycleResilienceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gx-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldAppData = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
    private readonly string? _oldProgramData = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT");

    public LifecycleResilienceTests()
    {
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _root);
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", Path.Combine(_root, "system"));
        AppPaths.ResetEngineDirCache();
        AppPaths.ResetSystemRuntimeDirCache();
    }

    public void Dispose()
    {
        NetworkReadinessGate.SetTestProbe(null);
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _oldAppData);
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _oldProgramData);
        AppPaths.ResetEngineDirCache();
        AppPaths.ResetSystemRuntimeDirCache();
    }

    [Fact]
    public void FailedSettingsWriteKeepsPreviousFileAndReportsUntilSuccessfulRetry()
    {
        var store = new SettingsService();
        store.Settings.Language = "ru";
        Assert.True(store.Save());
        string previous = File.ReadAllText(AppPaths.SettingsFile);
        var notifications = new List<string?>();
        store.SaveStatusChanged += notifications.Add;
        using (File.Open(AppPaths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Settings.Language = "en";
            Assert.False(store.Save());
            Assert.False(string.IsNullOrWhiteSpace(store.LastSaveError));
            Assert.Equal(previous, File.ReadAllText(AppPaths.SettingsFile));
            Assert.Empty(Directory.GetFiles(AppPaths.ConfigDir, "*.tmp"));
        }
        Assert.True(store.Save());
        Assert.Null(store.LastSaveError);
        Assert.Equal("en", new SettingsService().Settings.Language);
        Assert.NotNull(notifications.First());
        Assert.Null(notifications.Last());
    }

    [Fact]
    public async Task ConcurrentSettingsWritersLeaveACompleteDocumentAndNoTemporaryFiles()
    {
        var first = new SettingsService();
        var second = new SettingsService();
        first.Settings.Language = "ru";
        second.Settings.Language = "en";
        var results = await Task.WhenAll(Task.Run(first.Save), Task.Run(second.Save));
        Assert.All(results, Assert.True);
        using var json = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
        Assert.Contains(json.RootElement.GetProperty("Language").GetString(), new[] { "ru", "en" });
        Assert.Empty(Directory.GetFiles(AppPaths.ConfigDir, "*.tmp"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void StartupUsesExplicitAutostartOrPersistedIntent(bool autostart, bool desired, bool expected)
    {
        Assert.Equal(expected, RecoveryCoordinator.ShouldRestoreBypass(new AppSettings
        {
            AutostartEngine = autostart, DesiredBypassEnabled = desired
        }));
    }

    [Fact]
    public async Task BlockingNativeRecoveryStartRunsOffTheCallerAndKeepsDesiredAfterFailure()
    {
        var store = new SettingsService();
        store.Settings.AutostartEngine = true;
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(true));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new EngineService();
        var proxy = new TelegramProxyService();
        int syncCalls = 0;
        var coordinator = new RecoveryCoordinator(store, engine, proxy,
            startEngine: (_, _) =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("The caller was blocked");
                throw new IOException("Injected native startup failure");
            }, syncAutostart: _ => syncCalls++);
        var clock = Stopwatch.StartNew();
        Task<RecoveryResult>? recovery = null;
        try
        {
            recovery = coordinator.ExecuteRecoveryAsync([new Preset { Name = "test" }]);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "Recovery blocked its caller instead of awaiting native startup.");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(recovery.IsCompleted);
        }
        finally { release.Set(); }
        var result = await recovery!;
        Assert.False(result.BypassRestored);
        Assert.True(result.BypassDesired);
        Assert.True(new SettingsService().Settings.DesiredBypassEnabled);
        Assert.Equal(1, syncCalls);
    }

    [Fact]
    public async Task CancellationDuringStartupDoesNotRunASecondRecoveryAttempt()
    {
        var store = new SettingsService();
        store.Settings.DesiredBypassEnabled = true;
        NetworkReadinessGate.SetTestProbe(_ => Task.FromResult(true));
        using var cancel = new CancellationTokenSource();
        using var engine = new EngineService();
        int calls = 0;
        var coordinator = new RecoveryCoordinator(store, engine, new TelegramProxyService(),
            startEngine: (_, _) => { calls++; cancel.Cancel(); throw new OperationCanceledException(cancel.Token); },
            syncAutostart: _ => { });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ExecuteRecoveryAsync([new Preset { Name = "test" }], ct: cancel.Token));
        Assert.Equal(1, calls);
        Assert.True(store.Settings.DesiredBypassEnabled);
    }

    [Fact]
    public async Task FailedStrategyAndFailedRollbackKeepIntentAndPreviousStrategyForNextStartup()
    {
        var vm = new MainViewModel();
        try
        {
            Assert.StartsWith(_root, AppPaths.WinwsExe, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(AppPaths.WinwsExe))
                File.Move(AppPaths.WinwsExe, AppPaths.WinwsExe + ".unavailable", overwrite: true);
            Assert.False(File.Exists(AppPaths.WinwsExe)); // Never execute the real engine in this test.
            var previous = new Preset { Name = "previous-working" };
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.RunningPreset))!.SetValue(vm, previous);
            vm.Settings.DesiredBypassEnabled = true;
            vm.Settings.ActivePresetId = "failed-new";
            var restart = typeof(MainViewModel).GetMethod("RestartEngineForStrategyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Assert.ThrowsAnyAsync<Exception>(() => (Task)restart.Invoke(vm, [new Preset { Name = "failed-new" }])!);
            Assert.True(vm.Settings.DesiredBypassEnabled);
            Assert.Equal("previous-working", vm.Settings.ActivePresetId);
            Assert.Equal("ERROR", vm.Settings.BypassActual);
            Assert.Equal(EngineState.Stopped, vm.State);
            var persisted = new SettingsService().Settings;
            Assert.True(persisted.DesiredBypassEnabled);
            Assert.Equal("previous-working", persisted.ActivePresetId);
        }
        finally { vm.Shutdown(); }
    }

    [Theory]
    [InlineData(EngineState.Running, false, 1)]
    [InlineData(EngineState.Running, true, 0)]
    [InlineData(EngineState.Starting, false, 0)]
    public void OnlyUnexpectedExitOfReadyProcessTriggersRecovery(EngineState state, bool requested, int expected)
    {
        using var engine = new EngineService();
        using var process = new Process();
        var previous = new Preset { Name = "last-running" };
        typeof(EngineService).GetField("_proc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, process);
        typeof(EngineService).GetField("_stopRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, requested);
        typeof(EngineService).GetProperty(nameof(EngineService.State))!.SetValue(engine, state);
        typeof(EngineService).GetProperty(nameof(EngineService.ActivePreset))!.SetValue(engine, previous);
        var exits = new List<EngineProcessExit>();
        engine.UnexpectedExit += exits.Add;
        typeof(EngineService).GetMethod("FinalizeStoppedProcess", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [process]);
        Assert.Equal(expected, exits.Count);
        if (expected != 0) Assert.Same(previous, exits.Single().Preset);
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task RepeatedCrashesShareRetryBudgetEvenWhenRestartsSucceed()
    {
        var clock = new Clock();
        int attempts = 0;
        var reports = new List<string>();
        using var recovery = new UnexpectedEngineRecovery(() => true,
            _ => { attempts++; return Task.FromResult(true); }, reports.Add,
            [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero], clock);
        for (int i = 0; i < 4; i++) await recovery.NotifyUnexpectedExitAsync();
        Assert.Equal(3, attempts);
        Assert.Single(reports);
        clock.Now += TimeSpan.FromMinutes(3);
        await recovery.NotifyUnexpectedExitAsync();
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task ExitDuringRestartCannotBeLostBehindItsLateSuccess()
    {
        int attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var recovery = new UnexpectedEngineRecovery(() => true, async _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { entered.SetResult(); await release.Task; }
            return true;
        }, delays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var first = recovery.NotifyUnexpectedExitAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = recovery.NotifyUnexpectedExitAsync();
        Assert.Same(first, second);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CancelingPendingRecoveryNeverRestartsTheEngine()
    {
        int attempts = 0;
        using var recovery = new UnexpectedEngineRecovery(() => true,
            _ => { attempts++; return Task.FromResult(true); }, delays: [TimeSpan.FromHours(1)]);
        var pending = recovery.NotifyUnexpectedExitAsync();
        recovery.Cancel();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task RepeatedStartupFailuresExhaustExactlyThreeAttempts()
    {
        int attempts = 0;
        using var recovery = new UnexpectedEngineRecovery(() => true, _ =>
        {
            attempts++;
            throw new IOException("Injected engine failure");
        }, delays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        await recovery.NotifyUnexpectedExitAsync();
        await recovery.NotifyUnexpectedExitAsync();
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ShutdownCancelsPendingRecoveryAndRejectsNewExitNotifications()
    {
        int attempts = 0;
        var recovery = new UnexpectedEngineRecovery(() => true,
            _ => { attempts++; return Task.FromResult(true); }, delays: [TimeSpan.FromHours(1)]);
        var pending = recovery.NotifyUnexpectedExitAsync();
        recovery.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await recovery.NotifyUnexpectedExitAsync();
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task ManualStopDuringAttemptPreventsFurtherRecovery()
    {
        bool desired = true;
        int attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var recovery = new UnexpectedEngineRecovery(() => desired, async ct =>
        {
            attempts++;
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        }, delays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var pending = recovery.NotifyUnexpectedExitAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        desired = false;
        recovery.Cancel();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await recovery.NotifyUnexpectedExitAsync();
        Assert.Equal(1, attempts);
    }
}
