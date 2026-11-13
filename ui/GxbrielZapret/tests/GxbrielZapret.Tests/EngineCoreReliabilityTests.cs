using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Platform;
using Xunit;
using System.Diagnostics;
using System.Reflection;

namespace GxbrielZapret.Tests;

public sealed class EngineCoreReliabilityTests
{
    [Theory]
    [InlineData("windivert initialized. capture is started.", true)]
    [InlineData("CAPTURE IS STARTED", true)]
    [InlineData("windivert initialized", false)]
    [InlineData("JIT: initialized", false)]
    [InlineData("ready", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyExplicitCaptureMessageConfirmsReadiness(string? line, bool expected) =>
        Assert.Equal(expected, EngineReadinessSignal.IsCaptureReady(line));

    [Fact]
    public async Task SilentLivingProcessTimesOutWithoutConfirmation()
    {
        var signal = new EngineReadinessSignal();
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(15), CancellationToken.None));
        Assert.False(signal.Confirmed);
        signal.ObserveExit();
        signal.ObserveExit();
        signal.ObserveOutput("capture is started");
        Assert.False(signal.Confirmed);
    }

    [Fact]
    public async Task LateExitAndOutputCallbacksAfterSuccessfulStartupAreSafe()
    {
        var signal = new EngineReadinessSignal();
        signal.ObserveOutput("capture is started");
        Assert.True(signal.Wait(100));
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        await Task.Run(() => { signal.ObserveExit(); signal.ObserveOutput("capture is started"); signal.ObserveExit(); });
        Assert.True(signal.Confirmed);
    }

    [Fact]
    public async Task ReadinessWaitIsCancelable()
    {
        var signal = new EngineReadinessSignal();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signal.WaitAsync(TimeSpan.FromSeconds(1), cancel.Token));
        Assert.False(signal.Confirmed);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\GXBRIEL_ZAPRET\runtime\winws2.exe", true)]
    [InlineData(@"C:\ProgramData\GXBRIEL_ZAPRET\runtime\winws.exe", true)]
    [InlineData(@"c:\programdata\gx briel\winws2.exe", false)]
    [InlineData(@"C:\ProgramData\GXBRIEL_ZAPRET\runtime-other\winws2.exe", false)]
    [InlineData(@"C:\ProgramData\GXBRIEL_ZAPRET\runtime\other\winws2.exe", false)]
    [InlineData(@"C:\Flowseal\winws2.exe", false)]
    [InlineData(@"C:\ProgramData\GXBRIEL_ZAPRET\runtime\goodbyedpi.exe", false)]
    [InlineData(null, false)]
    public void OwnershipRequiresExactExecutableInsideKnownRuntime(string? path, bool expected) =>
        Assert.Equal(expected, EngineProcessOwnership.IsManagedPath(path, [@"C:\ProgramData\GXBRIEL_ZAPRET\runtime"]));

    [Fact]
    public async Task ConflictCheckNeverClaimsCleanOrStopsForeignTasks()
    {
        int ownCleanup = 0;
        var cleanup = new BypassCleanupService(() => ownCleanup++, () => ["foreign winws2 PID 123"]);
        var result = await cleanup.StopAndVerifyCleanAsync(CancellationToken.None);
        Assert.Equal(1, ownCleanup);
        Assert.False(result.Clean);
        Assert.Single(result.Remaining);
        Assert.Contains("вручную", result.Message);
        Assert.False(BypassCleanupService.CanStopScheduledTask("Flowseal"));
        Assert.False(BypassCleanupService.CanStopScheduledTask("GXBRIEL_ZAPRET Autostart"));
    }

    [Fact]
    public async Task CancellationBeforeCleanupDoesNotTouchProcesses()
    {
        var cleanup = new BypassCleanupService(() => throw new InvalidOperationException("must not run"), () => []);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup.StopAndVerifyCleanAsync(cancel.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProbeExceptionDoesNotOverwriteTheOtherServicesResult(bool discordFails)
    {
        Task<(bool? ok, string details)> Good(CancellationToken _) => Task.FromResult<(bool?, string)>((true, "verified flow"));
        Task<(bool? ok, string details)> Broken(CancellationToken _) => throw new InvalidOperationException("parser failed");
        using var monitor = new ServiceHealthMonitor(discordFails ? Broken : Good, discordFails ? Good : Broken);
        var result = await monitor.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(ServiceHealthState.Working, discordFails ? result.YouTube : result.Discord);
        Assert.Equal(ServiceHealthState.Inconclusive, discordFails ? result.Discord : result.YouTube);
        Assert.False(result.IsConclusiveFor(AutoScope.Both));
        Assert.True(result.IsHealthyFor(discordFails ? AutoScope.YouTube : AutoScope.Discord));
    }

    [Fact]
    public async Task ConfirmedNetworkFailureIsStillFailureRatherThanUnknown()
    {
        using var monitor = new ServiceHealthMonitor(
            _ => Task.FromResult<(bool?, string)>((false, "connection refused")),
            _ => Task.FromResult<(bool?, string)>((true, "video sample read")));
        var result = await monitor.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(ServiceHealthState.Failed, result.Discord);
        Assert.Equal(ServiceHealthState.Working, result.YouTube);
        Assert.True(result.IsConclusiveFor(AutoScope.Both));
    }

    [Fact]
    public void DisposedEngineCannotBeStartedAgain()
    {
        var engine = new EngineService(); engine.Dispose(); engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.Start(new Preset(), null));
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task ActualChildExitAfterReadyWaitDoesNotAccessDisposedHandles()
    {
        var signal = new EngineReadinessSignal();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                ArgumentList = { "/D", "/C", "echo capture is started & ping -n 2 127.0.0.1 >nul" }
            },
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += (_, e) => signal.ObserveOutput(e.Data);
        process.Exited += (_, _) => signal.ObserveExit();
        try
        {
            process.Start(); process.BeginOutputReadLine();
            Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            signal.ObserveExit();
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Fact]
    public void LateExitForOldProcessCannotClearNewProcessState()
    {
        using var engine = new EngineService();
        using var oldProcess = new Process();
        using var newerProcess = new Process();
        var field = typeof(EngineService).GetField("_proc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var finalize = typeof(EngineService).GetMethod("FinalizeStoppedProcess", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            field.SetValue(engine, newerProcess);
            finalize.Invoke(engine, [oldProcess]);
            Assert.Same(newerProcess, field.GetValue(engine));
        }
        finally { field.SetValue(engine, null); }
    }
}
