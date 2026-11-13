using System.IO;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Strategies;
using GxbrielZapret.ViewModels;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class PersistenceAndInitializationTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
    public PersistenceAndInitializationTests() => Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT",
        Path.Combine(Path.GetTempPath(), "gx-persistence-" + Guid.NewGuid().ToString("N")));
    public void Dispose() => Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _previous);

    [Fact]
    public void CorruptCatalogRecoversLastAtomicBackup()
    {
        Assert.True(StrategyStateStore.SaveSingleState("NET-A", new() { Id = "GX-B01", Status = "WORK" }));
        Assert.True(StrategyStateStore.SaveSingleState("NET-A", new() { Id = "GX-B02", Status = "BROKEN" }));
        File.WriteAllText(StrategyStateStore.StateFilePath, "{incomplete");
        var restored = StrategyStateStore.GetForNetwork("net-a");
        Assert.Equal("WORK", restored["gx-b01"].Status);
        Assert.True(File.Exists(StrategyStateStore.BackupFilePath));
        Assert.Single(Directory.GetFiles(AppPaths.BackupsDir, "strategy-catalog-corrupt-*.json"));
        Assert.NotEmpty(StrategyStateStore.Load().Networks);
    }

    [Fact]
    public void CaseOnlyDuplicateProbeKeysDoNotEraseCatalog()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(StrategyStateStore.StateFilePath, """
            {"Networks":{"NET":{"GX-B01":{"Id":"GX-B01","Probes":{"YouTube.com":true,"youtube.com":false}}}}}
            """);
        var state = StrategyStateStore.GetForNetwork("net")["gx-b01"];
        Assert.Single(state.Probes);
        Assert.False(state.Probes["YOUTUBE.COM"]);
    }

    [Fact]
    public void FailedAtomicWritePreservesPreviousCatalogAndRemovesTemporaryFile()
    {
        Assert.True(StrategyStateStore.SaveSingleState("net", new() { Id = "GX-B01", Status = "WORK" }));
        string previous = File.ReadAllText(StrategyStateStore.StateFilePath);
        using (File.Open(StrategyStateStore.StateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(StrategyStateStore.SaveSingleState("net", new() { Id = "GX-B02", Status = "WORK" }));
        Assert.Equal(previous, File.ReadAllText(StrategyStateStore.StateFilePath));
        Assert.Empty(Directory.GetFiles(AppPaths.ConfigDir, "*.tmp"));
    }

    [Fact]
    public void LegacyYouTubeTransportResultsDoNotRemainGreen()
    {
        Assert.True(StrategyStateStore.SaveSingleState("net", new() { Id = "GX-B01", Status = "WORK", Reason = "verified_YouTube_1_of_1",
            Probes = new() { ["www.youtube.com"] = true }, PassCount = 1, TotalCount = 1, LastTestedAt = DateTime.UtcNow }));
        var service = new PresetService();
        service.LoadForNetwork("net");
        var preset = service.ReferencePresets.Single(p => p.ReferenceId == "GX-B01");
        Assert.False(preset.IsWork);
        Assert.Equal("UNTESTED", preset.Status);
        Assert.Contains("recheck", preset.Reason);
    }

    [Fact]
    public void FailedSaveDoesNotPaintInMemoryStrategyGreen()
    {
        var service = new PresetService();
        service.LoadForNetwork("net");
        var preset = service.ReferencePresets.First();
        Assert.True(StrategyStateStore.SaveSingleState("net", new() { Id = preset.ReferenceId! }));
        using (File.Open(StrategyStateStore.StateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => service.UpdateStrategyProbeState(preset, "WORK", "manual_YouTube_1_of_1", new() { ["www.youtube.com"] = true }, 1, 1, 10, "net", FullScanServiceVerificationTests.Flows()));
        Assert.False(preset.IsWork);
    }

    [Fact]
    public void EmptyStrategyIdentityIsNotPersisted()
    {
        Assert.False(StrategyStateStore.SaveSingleState("net", new()));
        Assert.False(File.Exists(StrategyStateStore.StateFilePath));
    }

    [Fact]
    public async Task RepeatedInitializationSharesOneInFlightOperation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var vm = new MainViewModel(() => { calls++; return release.Task; });
        var first = vm.InitializeAsync();
        var second = vm.InitializeAsync();
        Assert.True(vm.IsInitializing);
        Assert.False(vm.IsInitialized);
        Assert.False(vm.CanStart);
        Assert.Equal(1, calls);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(vm.IsInitialized);
        Assert.False(vm.IsInitializing);
        await vm.InitializeAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedInitializationCanBeRetried()
    {
        int calls = 0;
        var vm = new MainViewModel(() => ++calls == 1 ? Task.FromException(new IOException("test failure")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => vm.InitializeAsync());
        Assert.False(vm.IsInitialized);
        Assert.False(vm.IsInitializing);
        await vm.InitializeAsync();
        Assert.True(vm.IsInitialized);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CanceledDiagnosticsAreNotReportedAsNetworkTimeouts()
    {
        var rows = DiagnosticsService.BuildRows();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DiagnosticsService().RunAsync(rows, stop.Token));
        Assert.All(rows, row => Assert.Equal(DiagStatus.Canceled, row.Ping));
        Assert.DoesNotContain(rows, row => row.Http == DiagStatus.Timeout || row.Tls12 == DiagStatus.Timeout);
        Assert.All(rows.Where(row => row.PingOnly), row => Assert.Equal(DiagStatus.Skip, row.Http));
    }
}
