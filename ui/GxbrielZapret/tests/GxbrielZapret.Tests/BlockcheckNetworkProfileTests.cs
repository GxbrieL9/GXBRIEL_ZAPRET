using System.IO;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class BlockcheckNetworkProfileTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
    private const string Options = "all=False;quicOff=False";
    public BlockcheckNetworkProfileTests() => Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", Path.Combine(Path.GetTempPath(), "gx-blockcheck-profiles-" + Guid.NewGuid().ToString("N")));
    public void Dispose() => Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _previous);

    private static Preset Candidate(int pos = 1) => new()
    {
        Name = "temporary", Provenance = "UPSTREAM_BLOCKCHECK2",
        Args = new() { "--wf-tcp-out=443", "--filter-tcp=443", "{HOSTLIST:youtube}", "--lua-desync=multisplit:pos=" + pos },
        Fingerprint = CanonicalProfile.ComputeFingerprint(new[] { "--wf-tcp-out=443", "--filter-tcp=443", "{HOSTLIST:youtube}", "--lua-desync=multisplit:pos=" + pos })
    };
    private static Canonical5ProbeResult Verified(Preset candidate, AutoScope scope = AutoScope.Both, bool? video = true, bool gateway = true) =>
        new(candidate.Name, candidate.Fingerprint, true, scope.GoalHosts().Length, scope.GoalHosts().Length, 10,
            scope.GoalHosts().ToDictionary(h => h, _ => true), "test",
            new(new(video, "video test", video == true ? ApplicationFlowProbe.VideoSampleBytes : 0), new(gateway, "gateway test")));

    [Fact]
    public void RepeatedSelectionUpdatesOneFileAndKeepsPreviousWorkingBackup()
    {
        for (int i = 1; i <= 20; i++)
        {
            var candidate = Candidate(i);
            BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, candidate, Verified(candidate), () => { });
        }
        string path = BlockcheckProfileStore.ProfilePath("network-a", AutoScope.Both);
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
        Assert.True(File.Exists(path + ".bak"));
        var loaded = Assert.Single(BlockcheckProfileStore.Load("network-a"));
        Assert.Contains("--lua-desync=multisplit:pos=20", loaded.Args);
        Assert.Equal("network-a", loaded.BoundNetworkFingerprint);
        Assert.Empty(Directory.GetFiles(AppPaths.AppDataRoot, "*.gxprofile.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void HttpsSuccessWithoutVideoProofCannotReplaceWorkingProfile(bool? video)
    {
        var first = Candidate();
        BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, first, Verified(first), () => { });
        string before = File.ReadAllText(BlockcheckProfileStore.ProfilePath("network-a", AutoScope.Both));
        var second = Candidate(2);
        Assert.Throws<InvalidOperationException>(() => BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, second, Verified(second, video: video), () => { }));
        Assert.Equal(before, File.ReadAllText(BlockcheckProfileStore.ProfilePath("network-a", AutoScope.Both)));
    }

    [Fact]
    public void MissingGatewayProofCannotBecomeCombinedWork()
    {
        var candidate = Candidate();
        Assert.Throws<InvalidOperationException>(() => BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, candidate, Verified(candidate, gateway: false), () => { }));
        Assert.Empty(BlockcheckProfileStore.Load("network-a"));
    }

    [Fact]
    public void CanceledCommitPreservesPreviousFileAndRemovesTemporaryData()
    {
        var first = Candidate();
        BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, first, Verified(first), () => { });
        string path = BlockcheckProfileStore.ProfilePath("network-a", AutoScope.Both);
        string before = File.ReadAllText(path);
        int checkpoints = 0;
        var second = Candidate(2);
        Assert.Throws<OperationCanceledException>(() => BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, second, Verified(second), () => { if (++checkpoints == 2) throw new OperationCanceledException(); }));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void ProfilesAreIsolatedByNetworkScopeAndRuntimeOptions()
    {
        var candidate = Candidate();
        BlockcheckProfileStore.SaveVerified("network-a", AutoScope.YouTube, Options, candidate, Verified(candidate, AutoScope.YouTube), () => { });
        BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, candidate, Verified(candidate), () => { });
        Assert.Equal(2, BlockcheckProfileStore.Load("network-a").Count);
        Assert.Empty(BlockcheckProfileStore.Load("network-b"));
        Assert.All(BlockcheckProfileStore.Load("network-a", "other-options"), p => Assert.True(p.IsUntested));
        var service = new PresetService();
        service.LoadForNetwork("network-a", Options);
        Assert.Equal(2, service.All.Count(p => p.BoundNetworkFingerprint is not null));
        Assert.Equal(47, service.ReferencePresets.Count);
        service.LoadForNetwork("network-b", Options);
        Assert.DoesNotContain(service.All, p => p.BoundNetworkFingerprint is not null);
    }

    [Fact]
    public void CorruptionFallsBackToBackupWithoutDestroyingItOnNextSave()
    {
        for (int i = 1; i <= 2; i++) { var p = Candidate(i); BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, p, Verified(p), () => { }); }
        string path = BlockcheckProfileStore.ProfilePath("network-a", AutoScope.Both);
        File.WriteAllText(path, "{broken");
        Assert.Contains("--lua-desync=multisplit:pos=1", Assert.Single(BlockcheckProfileStore.Load("network-a")).Args);
        var next = Candidate(3);
        BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, next, Verified(next), () => { });
        File.WriteAllText(path, "{broken again");
        Assert.Contains("--lua-desync=multisplit:pos=1", Assert.Single(BlockcheckProfileStore.Load("network-a")).Args);
    }

    [Fact]
    public void TemporaryUpstreamCandidatesCannotBeAddedAsUserProfiles()
    {
        var service = new PresetService();
        Assert.Throws<InvalidOperationException>(() => service.AddUser(Candidate()));
        Assert.Empty(Directory.GetFiles(AppPaths.StrategiesUserDir, "*.gxprofile.json"));
    }

    [Fact]
    public void GenericPoolUpdatesCannotOverwriteTheNetworkProfileDocument()
    {
        var candidate = Candidate();
        var saved = BlockcheckProfileStore.SaveVerified("network-a", AutoScope.Both, Options, candidate, Verified(candidate), () => { });
        string before = File.ReadAllText(saved.FilePath!);
        var service = new PresetService();
        Assert.Throws<InvalidOperationException>(() => service.MoveToStatus(saved, "WORK", "https_only"));
        Assert.Equal(before, File.ReadAllText(saved.FilePath!));
    }

    [Fact]
    public async Task NetworkChangeCancelsTheWholeSession()
    {
        string? current = "network-a";
        await using var run = new NetworkBoundRun(() => current, CancellationToken.None, TimeSpan.FromMilliseconds(10));
        current = "network-b";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(1000, run.Token));
        Assert.True(run.NetworkChanged);
        Assert.ThrowsAny<OperationCanceledException>(run.Checkpoint);
    }

    [Fact]
    public async Task UserCancellationIsNotMisreportedAsNetworkChange()
    {
        using var stop = new CancellationTokenSource();
        await using var run = new NetworkBoundRun(() => "network-a", stop.Token);
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(run.Checkpoint);
        Assert.False(run.NetworkChanged);
    }

    [Fact]
    public async Task FailedCombinedCandidatesNeverReachStorage()
    {
        const string report = "* COMMON\nhttps_tls12 ipv4 : winws2 --wf-tcp-out=443 --payload=tls_client_hello --lua-desync=multisplit:pos=1\nhttps_tls12 ipv4 : winws2 --wf-tcp-out=443 --payload=tls_client_hello --lua-desync=multisplit:pos=2";
        int attempts = 0;
        var result = await BlockcheckSelectionService.SelectAsync(report, AutoScope.Both, "network-a", Options, false, () => { },
            (candidate, _) => Task.FromResult(Verified(candidate, video: ++attempts == 2)), null, CancellationToken.None);
        Assert.Equal(2, result.Attempted);
        Assert.NotNull(result.Profile);
        Assert.Single(BlockcheckProfileStore.Load("network-a"));
        Assert.False(Directory.Exists(AppPaths.ProfilesBrokenDir));
    }
}
