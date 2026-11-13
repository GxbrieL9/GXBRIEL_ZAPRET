using System.IO;
using GxbrielZapret.Services.Platform;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class AntigravityUpdateMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gx-ag-monitor-" + Guid.NewGuid().ToString("N"));
    private int _scans;
    private const string Original = "let a=(resetIsTierGCPTos(),this.account.isGoogleInternal);";
    private const string Patched = "let a=(resetIsTierGCPTos(),true);";
    private string StateDirectory => Path.Combine(_root, "checkpoint");
    private string Component => Path.Combine(_root, "main.js");
    public AntigravityUpdateMonitorTests() { Directory.CreateDirectory(_root); File.WriteAllText(Component, Patched); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    private AntigravityUpdateMonitor Monitor(Func<string?, CancellationToken, Task<AntigravityStatusResult>>? inspect = null) =>
        new(StateDirectory, inspect ?? (async (root, ct) => { _scans++; return await AntigravityPatcherService.GetStatusAsync(ct, root); }));

    [Fact]
    public async Task AutomaticPatchWaitsForClosedAppAndDoesNotRepeatSuccessfulWrite()
    {
        bool running = true, patched = false;
        int writes = 0;
        AntigravityStatusResult Status() => new()
        {
            Succeeded = true,
            LanguageServer = new() { Key = "LanguageServer", Path = Component, State = patched ? "Patched" : "Unpatched" }
        };
        var monitor = new AntigravityUpdateMonitor(StateDirectory, (_, _) => Task.FromResult(Status()),
            (_, _) => { writes++; patched = true; return Task.FromResult(Status()); }, _ => running);
        Assert.True((await monitor.CheckAsync(_root, autoPatch: true))!.Pending);
        Assert.Equal(0, writes);
        running = false;
        var applied = await monitor.CheckAsync(_root, autoPatch: true);
        Assert.True(applied!.Status!.LanguageServer.IsPatched);
        Assert.True(applied.Notify);
        Assert.Equal(1, writes);
        Assert.Null(await monitor.CheckAsync(_root, autoPatch: true));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task CliSchemaUpgradeAutoPatchesEvenWhenLanguageServerIsAlreadyPatched()
    {
        bool patched = false;
        int writes = 0;
        AntigravityStatusResult Status() => new() { Succeeded = true,
            LanguageServer = new() { Key = "LanguageServer", Path = Component, State = "Patched" },
            Agy = new() { Key = "Agy", Path = Component, State = patched ? "Patched" : "Partial", PatchRule = "cli-eligibility-schema-v2" } };
        var monitor = new AntigravityUpdateMonitor(StateDirectory, (_, _) => Task.FromResult(Status()),
            (_, _) => { writes++; patched = true; return Task.FromResult(Status()); }, _ => false);
        var result = await monitor.CheckAsync(_root, autoPatch: true);
        Assert.True(result!.Status!.Agy.IsPatched);
        Assert.Contains("CLI agy", result.Message);
        Assert.Equal(1, writes);
        Assert.Null(await monitor.CheckAsync(_root, autoPatch: true));
    }

    [Fact]
    public async Task HubInstallerAutoPatchWorksWithoutALanguageServerAndVerifiesTheCorrectComponent()
    {
        bool patched = false;
        int writes = 0;
        AntigravityStatusResult Status() => new() { Succeeded = true,
            VsCode = new() { Key = "VsCode", Path = Component, State = patched ? "Patched" : "Unpatched", PatchRule = "hub-installer-1.11-v1" } };
        var monitor = new AntigravityUpdateMonitor(StateDirectory, (_, _) => Task.FromResult(Status()),
            (_, _) => { writes++; patched = true; return Task.FromResult(Status()); }, _ => false);
        var result = await monitor.CheckAsync(_root, autoPatch: true);
        Assert.True(result!.Status!.VsCode.IsPatched);
        Assert.False(result.Status.LanguageServer.IsFound);
        Assert.Contains("установщика Hub", result.Message);
        Assert.Equal(1, writes);
        Assert.Null(await monitor.CheckAsync(_root, autoPatch: true));
    }

    [Fact]
    public async Task AmbiguousDesktopDoesNotBlockStandaloneCliAndScopesProcessChecksToCliDirectory()
    {
        string cliDirectory = Path.Combine(_root, "cli-only");
        Directory.CreateDirectory(cliDirectory);
        string cli = Path.Combine(cliDirectory, "agy.exe");
        File.WriteAllText(cli, "fixture");
        bool patched = false;
        int writes = 0;
        AntigravityStatusResult Status() => new() { Succeeded = true, SelectionRequired = true,
            LanguageServer = new() { Key = "LanguageServer", Path = Component, State = "Unpatched" },
            Agy = new() { Key = "Agy", Path = cli, State = patched ? "Patched" : "Unpatched", PatchRule = "cli-eligibility-schema-v2" } };
        var monitor = new AntigravityUpdateMonitor(StateDirectory, (_, _) => Task.FromResult(Status()),
            (scope, _) => { Assert.Equal(cliDirectory, scope); writes++; patched = true; return Task.FromResult(Status()); },
            scope => { Assert.Equal(cliDirectory, scope); return false; });
        var result = await monitor.CheckAsync(_root, autoPatch: true);
        Assert.True(result!.Status!.Agy.IsPatched);
        Assert.True(result.Status.LanguageServer.CanPatch);
        Assert.Contains("CLI agy", result.Message);
        Assert.True(result.Notify);
        Assert.Equal(1, writes);
        Assert.Null(await monitor.CheckAsync(_root, autoPatch: true));
    }

    [Fact]
    public async Task AmbiguousInstallationNeverAutoPatchesOrChecksRunningProcesses()
    {
        var status = new AntigravityStatusResult { Succeeded = true, SelectionRequired = true,
            LanguageServer = new() { Key = "LanguageServer", Path = Component, State = "Unpatched" } };
        var monitor = new AntigravityUpdateMonitor(StateDirectory, (_, _) => Task.FromResult(status),
            (_, _) => throw new InvalidOperationException("Must not apply"),
            _ => throw new InvalidOperationException("Must not inspect processes"));
        var result = await monitor.CheckAsync(_root, force: true, autoPatch: true);
        Assert.False(result!.Pending);
        Assert.Contains("несколько установок", result.Message);
        Assert.Null(await monitor.CheckAsync(_root, autoPatch: true));
    }

    [Fact]
    public async Task UnknownOrDisabledAutomaticPatchNeverWrites()
    {
        int writes = 0;
        string state = "Unknown";
        var monitor = new AntigravityUpdateMonitor(StateDirectory,
            (_, _) => Task.FromResult(new AntigravityStatusResult { Succeeded = true,
                LanguageServer = new() { Key = "LanguageServer", Path = Component, State = state } }),
            (_, _) => { writes++; throw new InvalidOperationException("Must not write"); }, _ => false);
        await monitor.CheckAsync(_root, force: true, autoPatch: true);
        state = "Unpatched";
        await monitor.CheckAsync(_root, force: true, autoPatch: false);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task IdlePollingDoesNotRescanWriteOrNotify()
    {
        var monitor = Monitor();
        var first = await monitor.CheckAsync(_root);
        Assert.False(first!.Notify);
        Assert.True(first.Status!.Ide.IsPatched);
        string saved = File.ReadAllText(Directory.GetFiles(StateDirectory).Single());
        for (int i = 0; i < 10; i++) Assert.Null(await monitor.CheckAsync(_root));
        Assert.Equal(1, _scans);
        Assert.Equal(saved, File.ReadAllText(Directory.GetFiles(StateDirectory).Single()));
        Assert.Equal(Patched, File.ReadAllText(Component));
    }

    [Fact]
    public async Task UpdateWaitsForStableFilesThenNotifiesOnceWithoutWritingTarget()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(_root);
        File.WriteAllText(Component, Original);
        Assert.True((await monitor.CheckAsync(_root))!.Pending);
        Assert.Equal(1, _scans);
        var changed = await monitor.CheckAsync(_root);
        Assert.True(changed!.Notify);
        Assert.True(changed.Status!.Ide.CanPatch);
        Assert.Contains("патч больше не подтверждён", changed.Message);
        Assert.Null(await monitor.CheckAsync(_root));
        Assert.Equal(Original, File.ReadAllText(Component));
    }

    [Fact]
    public async Task UpdateWhileAppWasClosedIsDetectedAndWarningSurvivesRestart()
    {
        await Monitor().CheckAsync(_root);
        File.WriteAllText(Component, "unknown upstream v999");
        var restarted = Monitor();
        Assert.True((await restarted.CheckAsync(_root))!.Pending);
        var changed = await restarted.CheckAsync(_root);
        Assert.True(changed!.Notify);
        Assert.Equal("Unknown", changed.Status!.Ide.State);
        Assert.False(changed.Status.Ide.CanPatch);
        Assert.Contains("неизвестная версия", changed.Message);
        var again = await Monitor().CheckAsync(_root);
        Assert.False(again!.Notify);
        Assert.Equal(changed.Message, again.Message);
    }

    [Fact]
    public async Task ChangedFilesDuringScanCannotCommitStalePatchedStatus()
    {
        await Monitor().CheckAsync(_root);
        var racing = Monitor(async (root, ct) =>
        {
            var status = await AntigravityPatcherService.GetStatusAsync(ct, root);
            File.WriteAllText(Component, Original);
            return status;
        });
        var result = await racing.CheckAsync(_root, force: true);
        Assert.True(result!.Pending);
        Assert.Null(result.Status);
        var next = await Monitor().CheckAsync(_root, force: true);
        Assert.True(next!.Notify);
        Assert.Equal("Unpatched", next.Status!.Ide.State);
    }

    [Fact]
    public async Task ContinuousUpdatesKeepDeferringFullScan()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(_root);
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Component, "updating-" + new string('x', i));
            Assert.True((await monitor.CheckAsync(_root))!.Pending);
        }
        Assert.Equal(1, _scans);
    }

    [Fact]
    public async Task RevertedPendingUpdateRestoresVerifiedUiStatus()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(_root);
        DateTime written = File.GetLastWriteTimeUtc(Component);
        File.WriteAllText(Component, Original);
        Assert.True((await monitor.CheckAsync(_root))!.Pending);
        File.WriteAllText(Component, Patched);
        File.SetLastWriteTimeUtc(Component, written);
        var result = await monitor.CheckAsync(_root);
        Assert.True(result!.Status!.Ide.IsPatched);
        Assert.False(result.Notify);
    }

    [Fact]
    public async Task RemovedComponentInvalidatesPreviousPatch()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(_root);
        File.Delete(Component);
        Assert.True((await monitor.CheckAsync(_root))!.Pending);
        var result = await monitor.CheckAsync(_root);
        Assert.True(result!.Notify);
        Assert.False(result.Status!.Ide.IsFound);
        Assert.False(result.Status.Ide.CanPatch);
    }

    [Fact]
    public async Task RemovedManualInstallationCanRecoverWhenReinstalled()
    {
        string install = Path.Combine(_root, "install"); Directory.CreateDirectory(install);
        string path = Path.Combine(install, "main.js"); File.WriteAllText(path, Patched);
        var monitor = Monitor();
        await monitor.CheckAsync(install);
        Directory.Delete(install, true);
        Assert.True((await monitor.CheckAsync(install))!.Pending);
        var missing = await monitor.CheckAsync(install);
        Assert.True(missing!.Notify);
        Assert.False(missing.Status!.Ide.IsFound);
        Directory.CreateDirectory(install); File.WriteAllText(path, Original);
        Assert.True((await monitor.CheckAsync(install))!.Pending);
        Assert.True((await monitor.CheckAsync(install))!.Status!.Ide.CanPatch);
    }

    [Fact]
    public async Task ExplicitPatchOrRestoreCheckpointDoesNotRaiseAnUpdateAlarm()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(_root);
        File.WriteAllText(Component, Original);
        var result = await monitor.CheckAsync(_root, force: true, acknowledge: true);
        Assert.False(result!.Notify);
        Assert.Empty(result.Message);
        Assert.Null(await monitor.CheckAsync(_root));
    }

    [Fact]
    public async Task InspectionErrorIsRetriedEvenWhenMetadataIsUnchanged()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(_root);
        bool fail = true;
        var retrying = Monitor(async (root, ct) => fail ? new AntigravityStatusResult() : await AntigravityPatcherService.GetStatusAsync(ct, root));
        Assert.Contains("повторена", (await retrying.CheckAsync(_root))!.Message);
        fail = false;
        Assert.True((await retrying.CheckAsync(_root))!.Status!.Ide.IsPatched);
    }

    [Fact]
    public async Task RootSelectionNeverComparesDifferentInstallations()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(_root);
        string other = Path.Combine(_root, "other"); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "main.js"), Original);
        var result = await monitor.CheckAsync(other);
        Assert.False(result!.Notify);
        Assert.Equal("Unpatched", result.Status!.Ide.State);
        Assert.True((await monitor.CheckAsync(_root))!.Status!.Ide.IsPatched);
    }

    [Fact]
    public async Task CorruptCheckpointCannotClaimAnInstalledPatch()
    {
        await Monitor().CheckAsync(_root);
        string file = Directory.GetFiles(StateDirectory).Single();
        File.WriteAllText(file, "{invalid");
        File.WriteAllText(Component, Original);
        var result = await Monitor().CheckAsync(_root);
        Assert.False(result!.Notify);
        Assert.True(result.Status!.Ide.CanPatch);
    }

    [Fact]
    public async Task CancellationLeavesCheckpointAndTargetUntouched()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(_root);
        string file = Directory.GetFiles(StateDirectory).Single();
        string before = File.ReadAllText(file);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.CheckAsync(_root, cts.Token));
        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal(Patched, File.ReadAllText(Component));
    }

    [Fact]
    public async Task ConcurrentPollsShareOneInspection()
    {
        var monitor = Monitor();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => monitor.CheckAsync(_root)));
        Assert.Single(results, r => r is not null);
        Assert.Equal(1, _scans);
    }

    [Fact]
    public async Task ExceptionAfterGoodCheckpointDoesNotSuppressRecovery()
    {
        bool fail = false;
        var monitor = Monitor(async (root, ct) =>
        {
            if (fail) throw new IOException("Installer locked the file");
            return await AntigravityPatcherService.GetStatusAsync(ct, root);
        });
        await monitor.CheckAsync(_root);
        fail = true;
        await Assert.ThrowsAsync<IOException>(() => monitor.CheckAsync(_root, force: true));
        fail = false;
        Assert.True((await monitor.CheckAsync(_root))!.Status!.Ide.IsPatched);
    }
}
