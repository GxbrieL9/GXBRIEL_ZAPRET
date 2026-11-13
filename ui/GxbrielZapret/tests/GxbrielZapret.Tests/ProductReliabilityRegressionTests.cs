using GxbrielZapret.Models;
using GxbrielZapret.Mvvm;
using GxbrielZapret.Services.Network;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class ProductReliabilityRegressionTests
{
    [Fact]
    public async Task RepeatedClicksCannotRunTheSameCommandTwice()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        var command = new RelayCommand(async () => { runs++; await release.Task; });
        var first = command.ExecuteAsync();
        Assert.True(command.IsExecuting);
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, runs);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task DisabledCommandsAreIgnoredEvenWhenInvokedDirectly()
    {
        int runs = 0;
        var command = new RelayCommand(() => runs++, () => false);
        command.Execute(null);
        await command.ExecuteAsync();
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task FailedCommandsCanBeRetried()
    {
        var command = new RelayCommand((Func<Task>)(() => Task.FromException(new InvalidOperationException("test"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync());
        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task OldProbeCannotRestoreGreenStateAfterReset()
    {
        var release = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new ServiceHealthMonitor(_ => release.Task, _ => release.Task);
        var probe = monitor.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(ServiceHealthState.Checking, monitor.GetSnapshot().Discord);
        monitor.SetTelegramState(ServiceHealthState.Working);
        monitor.Reset();
        long resetRevision = monitor.GetSnapshot().Revision;
        release.SetResult((true, "old success"));
        await probe.WaitAsync(TimeSpan.FromSeconds(2));
        var snapshot = monitor.GetSnapshot();
        Assert.Equal(resetRevision, snapshot.Revision);
        Assert.Equal(ServiceHealthState.NotChecked, snapshot.Discord);
        Assert.Equal(ServiceHealthState.NotChecked, snapshot.YouTube);
        Assert.Equal(ServiceHealthState.Working, snapshot.Telegram);
        Assert.Equal(default, snapshot.Timestamp);
    }

    [Fact]
    public async Task ResetCancelsInFlightNetworkRequests()
    {
        CancellationToken captured = default;
        async Task<(bool, string)> Probe(CancellationToken ct)
        {
            captured = ct;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return (true, "unreachable");
        }
        using var monitor = new ServiceHealthMonitor(Probe, Probe);
        var probe = monitor.ProbeAllAsync(CancellationToken.None);
        monitor.Reset();
        await probe.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(captured.IsCancellationRequested);
        Assert.False(monitor.GetSnapshot().IsFullyHealthy);
    }

    [Fact]
    public async Task CancellationLeavesNoForeverCheckingState()
    {
        async Task<(bool, string)> Probe(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return (true, "unreachable");
        }
        using var monitor = new ServiceHealthMonitor(Probe, Probe);
        using var cancellation = new CancellationTokenSource();
        var probe = monitor.ProbeAllAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(ServiceHealthState.NotChecked, monitor.GetSnapshot().Discord);
    }

    [Fact]
    public async Task DisposeDoesNotRaceWithSemaphoreRelease()
    {
        var release = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new ServiceHealthMonitor(_ => release.Task, _ => release.Task);
        var probe = monitor.ProbeAllAsync(CancellationToken.None);
        monitor.Dispose();
        release.SetResult((true, "late result"));
        await probe.WaitAsync(TimeSpan.FromSeconds(2));
        monitor.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.ProbeAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProbeExceptionIsInconclusiveAndCanBeRetried()
    {
        bool fail = true;
        Task<(bool, string)> Probe(CancellationToken _) => fail
            ? Task.FromException<(bool, string)>(new InvalidOperationException("network failure"))
            : Task.FromResult((true, "HTTPS success"));
        using var monitor = new ServiceHealthMonitor(Probe, Probe);
        var failed = await monitor.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(ServiceHealthState.Inconclusive, failed.Discord);
        Assert.Contains("network failure", failed.DiscordDetails);
        fail = false;
        var recovered = await monitor.ProbeAllAsync(CancellationToken.None);
        Assert.True(recovered.IsFullyHealthy);
        Assert.NotEqual(default, recovered.Timestamp);
        Assert.True(recovered.Revision > failed.Revision);
    }

    [Fact]
    public void WatchdogUsesOnlyTheSelectedServices()
    {
        var snapshot = new ServiceHealthSnapshot(ServiceHealthState.Failed, ServiceHealthState.Working,
            ServiceHealthState.NotChecked, DateTime.UtcNow);
        Assert.True(snapshot.IsHealthyFor(AutoScope.YouTube));
        Assert.False(snapshot.IsHealthyFor(AutoScope.Discord));
        Assert.False(snapshot.IsHealthyFor(AutoScope.Both));
    }

    [Fact]
    public void StartingTheSameWatchdogTwiceKeepsItRunning()
    {
        using var monitor = new ServiceHealthMonitor();
        monitor.StartWatchdog(AutoScope.YouTube);
        monitor.StartWatchdog(AutoScope.YouTube);
        Assert.True(monitor.IsWatchdogRunning);
        monitor.StopWatchdog();
        Assert.False(monitor.IsWatchdogRunning);
    }
}
