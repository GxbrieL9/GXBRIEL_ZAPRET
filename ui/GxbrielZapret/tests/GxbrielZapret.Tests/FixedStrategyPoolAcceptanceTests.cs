using System.IO;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Strategies;
using GxbrielZapret.ViewModels;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class FixedStrategyPoolAcceptanceTests : IDisposable
{
    private readonly string _testConfigDir;
    private readonly string _testProfilesDir;
    private readonly string? _previousDataRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
    private readonly string? _previousSystemRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT");

    public FixedStrategyPoolAcceptanceTests()
    {
        _testConfigDir = Path.Combine(Path.GetTempPath(), "gxbriel_test_config_" + Guid.NewGuid().ToString("N"));
        _testProfilesDir = Path.Combine(Path.GetTempPath(), "gxbriel_test_profiles_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testConfigDir);
        Directory.CreateDirectory(_testProfilesDir);
        Directory.CreateDirectory(Path.Combine(_testProfilesDir, "work"));
        Directory.CreateDirectory(Path.Combine(_testProfilesDir, "broken"));
        Directory.CreateDirectory(Path.Combine(_testProfilesDir, "user"));
        Directory.CreateDirectory(Path.Combine(_testProfilesDir, "generated"));
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _testConfigDir);
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", Path.Combine(_testConfigDir, "system"));
        AppPaths.ResetEngineDirCache();
        AppPaths.ResetSystemRuntimeDirCache();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _previousDataRoot);
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _previousSystemRoot);
        AppPaths.ResetEngineDirCache();
        AppPaths.ResetSystemRuntimeDirCache();
        try { if (Directory.Exists(_testConfigDir)) Directory.Delete(_testConfigDir, true); } catch { }
        try { if (Directory.Exists(_testProfilesDir)) Directory.Delete(_testProfilesDir, true); } catch { }
    }

    [Fact]
    public void Catalog_ContainsExactly39ImmutableReferenceStrategies()
    {
        var allRef = ReferenceStrategyCatalog.All;
        Assert.Equal(47, allRef.Count);
        Assert.Equal(39, ReferenceStrategyCatalog.Baseline.Count);
        Assert.Equal(8, ReferenceStrategyCatalog.Extensions.Count);

        var bList = allRef.Where(r => r.Id.StartsWith("GX-B")).ToList();
        var cList = allRef.Where(r => r.Id.StartsWith("GX-C")).ToList();

        Assert.Equal(9, bList.Count);
        Assert.Equal(30, cList.Count);

        // Distinct IDs and fingerprints
        Assert.Equal(47, allRef.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(47, allRef.Select(r => r.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void InitialState_All39AreUntested_SumEquals39()
    {
        var presetService = new PresetService();
        var presets = presetService.ReferencePresets;

        Assert.Equal(47, presets.Count);

        int workCount = presets.Count(p => p.IsWork);
        int brokenCount = presets.Count(p => p.IsFailed);
        int untestedCount = presets.Count(p => p.IsUntested);

        Assert.Equal(47, workCount + brokenCount + untestedCount);
        Assert.Equal(0, workCount);
    }

    [Fact]
    public void FullScan_PromotesPassingCandidatesToWork_AndFailingToBroken()
    {
        var presetService = new PresetService();
        string testNetwork = "test_net_full_scan_" + Guid.NewGuid().ToString("N");
        presetService.LoadForNetwork(testNetwork);

        var presets = presetService.ReferencePresets.ToList();
        Assert.Equal(47, presets.Count);

        // Simulate Full Scan: 5 strategies pass 5/5, 34 fail
        var passingIds = new HashSet<string> { "GX-B01", "GX-B03", "GX-B05", "GX-C01", "GX-C10" };

        foreach (var p in presets)
        {
            string id = p.ReferenceId ?? "";
            bool passes = passingIds.Contains(id);
            int passCount = passes ? 5 : 3;
            string status = passes ? "WORK" : "BROKEN";
            string reason = passes ? "verified_5_of_5" : "failed_3_of_5";

            var probes = new Dictionary<string, bool>
            {
                ["www.youtube.com"] = passes,
                ["redirector.googlevideo.com"] = passes,
                ["i.ytimg.com"] = true,
                ["discord.com"] = true,
                ["gateway.discord.gg"] = true
            };

            presetService.UpdateStrategyProbeState(p, status, reason, probes, passCount, 5, 45, testNetwork, FullScanServiceVerificationTests.Flows());
        }

        presetService.LoadForNetwork(testNetwork);
        var reloaded = presetService.ReferencePresets;

        int workCount = reloaded.Count(p => p.IsWork);
        int brokenCount = reloaded.Count(p => p.IsFailed);
        int untestedCount = reloaded.Count(p => p.IsUntested);

        Assert.Equal(5, workCount);
        Assert.Equal(42, brokenCount);
        Assert.Equal(0, untestedCount);
        Assert.Equal(47, workCount + brokenCount + untestedCount);
    }

    [Fact]
    public void RecheckWork_OnlyTestsWorkPool_AndDemotesFailingCandidateToBroken()
    {
        var presetService = new PresetService();
        string testNetwork = "test_net_recheck_" + Guid.NewGuid().ToString("N");
        presetService.LoadForNetwork(testNetwork);

        var presets = presetService.ReferencePresets.ToList();

        // 3 strategies initially WORK
        var initialWorkIds = new[] { "GX-B01", "GX-B02", "GX-B03" };
        foreach (var p in presets)
        {
            bool isInitialWork = initialWorkIds.Contains(p.ReferenceId);
            string status = isInitialWork ? "WORK" : "BROKEN";
            presetService.UpdateStrategyProbeState(
                p,
                status,
                isInitialWork ? "verified_5_of_5" : "unproven",
                Canonical5ProbeService.CanonicalTargets.ToDictionary(h => h, _ => isInitialWork),
                isInitialWork ? 5 : 0,
                5,
                30,
                testNetwork, FullScanServiceVerificationTests.Flows());
        }

        presetService.LoadForNetwork(testNetwork);
        var workPool = presetService.ReferencePresets.Where(p => p.IsWork).ToList();
        Assert.Equal(3, workPool.Count);

        // Recheck WORK: artificially fail GX-B02 (e.g. 4/5 pass instead of 5/5)
        foreach (var p in workPool)
        {
            bool stillWorks = p.ReferenceId != "GX-B02";
            string status = stillWorks ? "WORK" : "BROKEN";
            int passCount = stillWorks ? 5 : 4;

            presetService.UpdateStrategyProbeState(
                p,
                status,
                stillWorks ? "verified_5_of_5" : "failed_4_of_5",
                Canonical5ProbeService.CanonicalTargets.ToDictionary(h => h, h => stillWorks || h != "discord.com"),
                passCount,
                5,
                35,
                testNetwork, FullScanServiceVerificationTests.Flows());
        }

        presetService.LoadForNetwork(testNetwork);
        var afterRecheckWork = presetService.ReferencePresets.Where(p => p.IsWork).ToList();
        var demotedCandidate = presetService.ReferencePresets.First(p => p.ReferenceId == "GX-B02");

        Assert.Equal(2, afterRecheckWork.Count);
        Assert.False(demotedCandidate.IsWork);
        Assert.True(demotedCandidate.IsFailed);
        Assert.Equal(1, presetService.ReferencePresets.Count(p => p.IsFailed));
        Assert.Equal(44, presetService.ReferencePresets.Count(p => p.IsUntested));
        Assert.Equal(47, presetService.ReferencePresets.Count(p => p.IsWork) + presetService.ReferencePresets.Count(p => p.IsFailed) + presetService.ReferencePresets.Count(p => p.IsUntested));
    }

    [Fact]
    public void FullScan_AllowsBrokenToTransitionBackToWork()
    {
        var presetService = new PresetService();
        string testNetwork = "test_net_recovery_" + Guid.NewGuid().ToString("N");
        presetService.LoadForNetwork(testNetwork);

        var candidate = presetService.ReferencePresets.First(p => p.ReferenceId == "GX-B02");

        // 1. Mark as BROKEN
        presetService.UpdateStrategyProbeState(candidate, "BROKEN", "failed_4_of_5", new Dictionary<string, bool>(), 4, 5, 50, testNetwork);
        presetService.LoadForNetwork(testNetwork);
        Assert.True(presetService.ReferencePresets.First(p => p.ReferenceId == "GX-B02").IsFailed);

        // 2. Full scan re-tests and passes 5/5
        presetService.UpdateStrategyProbeState(candidate, "WORK", "verified_5_of_5", Canonical5ProbeService.CanonicalTargets.ToDictionary(h => h, _ => true), 5, 5, 25, testNetwork, FullScanServiceVerificationTests.Flows());
        presetService.LoadForNetwork(testNetwork);

        var recovered = presetService.ReferencePresets.First(p => p.ReferenceId == "GX-B02");
        Assert.True(recovered.IsWork);
        Assert.False(recovered.IsFailed);
        Assert.Equal("5/5", recovered.ProbeScore);
    }

    [Theory]
    [InlineData(40, 20, "GX-C05")]
    [InlineData(20, 20, "GX-B03")]
    [InlineData(0, 20, "GX-C05")]
    [InlineData(20, 0, "GX-B03")]
    public void AutoSelect_PicksOnlyFromWorkPool_ByLatencyThenCatalog(long latencyB, long latencyC, string expected)
    {
        var presetService = new PresetService();
        string testNetwork = "test_net_autoselect_" + Guid.NewGuid().ToString("N");
        presetService.LoadForNetwork(testNetwork);

        var presets = presetService.ReferencePresets.ToList();

        // Mark GX-C05 and GX-B03 as WORK
        var pB03 = presets.First(p => p.ReferenceId == "GX-B03");
        var pC05 = presets.First(p => p.ReferenceId == "GX-C05");

        var probes = AutoScope.Both.GoalHosts().ToDictionary(h => h, _ => true);
        string optionsKey = new ProbeLaunchOptions().VerificationKey(false, true);
        presetService.UpdateStrategyProbeState(pB03, "WORK", "verified_Discord + YouTube_9_of_9", probes, probes.Count, probes.Count, latencyB, testNetwork, FullScanServiceVerificationTests.Flows(), optionsKey);
        presetService.UpdateStrategyProbeState(pC05, "WORK", "verified_Discord + YouTube_9_of_9", probes, probes.Count, probes.Count, latencyC, testNetwork, FullScanServiceVerificationTests.Flows(), optionsKey);

        presetService.LoadForNetwork(testNetwork);

        var vm = new MainViewModel();
        vm.ReloadPresets(testNetwork);
        var best = vm.GetBestWorkPreset();

        Assert.NotNull(best);
        Assert.True(best.IsWork);
        // A later catalog entry wins when its verified worst-request latency is lower.
        Assert.Equal(expected, best.ReferenceId);
    }

    [Fact]
    public void AutoSelect_PrefersMeasuredVideoRateOverLowerRequestLatency()
    {
        var service = new PresetService();
        string network = "video-rank-" + Guid.NewGuid().ToString("N");
        service.LoadForNetwork(network);
        var candidates = service.ReferencePresets.Take(2).ToArray();
        var probes = AutoScope.Both.GoalHosts().ToDictionary(h => h, _ => true);
        string key = new ProbeLaunchOptions().VerificationKey(false, true);
        for (int i = 0; i < candidates.Length; i++)
        {
            var flows = FullScanServiceVerificationTests.Flows();
            flows = flows with { YouTubeVideo = flows.YouTubeVideo with { MegabitsPerSecond = i == 0 ? 2 : 20 } };
            service.UpdateStrategyProbeState(candidates[i], "WORK", "verified_Discord + YouTube_9_of_9",
                probes, probes.Count, probes.Count, i == 0 ? 10 : 500, network, flows, key);
        }
        var vm = new MainViewModel();
        vm.ReloadPresets(network);
        var best = vm.GetBestWorkPreset();
        Assert.Equal(candidates[1].ReferenceId, best?.ReferenceId);
        Assert.Equal(20, best?.VerifiedVideoMbps);
    }

    [Fact]
    public void Persistence_StoresAndRestoresPerNetworkFingerprint()
    {
        string netA = "network_alpha_" + Guid.NewGuid().ToString("N");
        string netB = "network_beta_" + Guid.NewGuid().ToString("N");

        var serviceA = new PresetService();
        serviceA.LoadForNetwork(netA);

        var target = serviceA.ReferencePresets.First(p => p.ReferenceId == "GX-B01");
        serviceA.UpdateStrategyProbeState(target, "WORK", "verified_5_of_5", new Dictionary<string, bool>
        {
            ["www.youtube.com"] = true,
            ["redirector.googlevideo.com"] = true,
            ["i.ytimg.com"] = true,
            ["discord.com"] = true,
            ["gateway.discord.gg"] = true
        }, 5, 5, 33, netA, FullScanServiceVerificationTests.Flows());

        // Verify Net A restores WORK
        var reloadedA = new PresetService();
        reloadedA.LoadForNetwork(netA);
        var restoredA = reloadedA.ReferencePresets.First(p => p.ReferenceId == "GX-B01");
        Assert.True(restoredA.IsWork);
        Assert.Equal(5, restoredA.PassCount);
        Assert.Equal(33, restoredA.LatencyMs);

        // Verify Net B loads as UNTESTED
        var serviceB = new PresetService();
        serviceB.LoadForNetwork(netB);
        var targetB = serviceB.ReferencePresets.First(p => p.ReferenceId == "GX-B01");
        Assert.True(targetB.IsUntested);
        Assert.False(targetB.IsWork);
    }

    [Fact]
    public void ScopeCatchAllToTargets_PreservesCandidateTlsRules_AndPinsToAllowListInListMode()
    {
        var c01 = ReferenceStrategyCatalog.CatalogCandidates.First(c => c.Id == "GX-C01").ToPreset();
        var argsAllowList = GxbrielZapret.Services.Engine.EngineService.BuildArguments(
            c01, hostlistPath: null, gameFilter: false, bypassAll: false, forLaunch: false);

        // Prove that the TLS profile (--filter-l7=tls) is NEVER stripped in allow-list mode
        Assert.Contains(argsAllowList, a => a.StartsWith("--filter-l7=tls", StringComparison.Ordinal));
        Assert.Contains(argsAllowList, a => a.StartsWith("--hostlist=", StringComparison.Ordinal));
        Assert.Contains(argsAllowList, a => a.StartsWith("--lua-desync=", StringComparison.Ordinal));

        // Prove that GX-B01 preserves its third (fallback) profile pinned to a hostlist in allow-list mode
        var b01 = ReferenceStrategyCatalog.BuiltIns.First(b => b.Id == "GX-B01").ToPreset();
        var argsB01 = GxbrielZapret.Services.Engine.EngineService.BuildArguments(
            b01, hostlistPath: null, gameFilter: false, bypassAll: false, forLaunch: false);

        int hostlistCount = argsB01.Count(a => a.StartsWith("--hostlist=", StringComparison.Ordinal));
        Assert.True(hostlistCount >= 3, $"GX-B01 must retain all 3 TLS hostlists (Discord, YouTube, and General fallback), got {hostlistCount}");
    }

    [Fact]
    public void DisableQuic_DefaultsToTrue_AndReplacesQuicDesyncWithDrop()
    {
        var settings = new AppSettings();
        Assert.True(settings.DisableQuic, "DisableQuic must default to true to prevent browser stalling on TSPU");

        var b01 = ReferenceStrategyCatalog.BuiltIns.First(b => b.Id == "GX-B01").ToPreset();
        var args = EngineService.BuildArguments(b01, hostlistPath: null, gameFilter: false, bypassAll: false, disableQuic: true, forLaunch: false);

        // All QUIC rules must use --lua-desync=drop
        Assert.Contains("--lua-desync=drop", args);
        Assert.DoesNotContain("--lua-desync=fake:blob=quic_google:repeats=11", args);

        // Discord STUN/Voice must NOT be dropped
        Assert.Contains(args, a => a.Contains("discord,stun"));
        Assert.Contains(args, a => a.Contains("quic_google:ip_autottl"));

        // Verify GX-B04 uses pos=2 matching Flowseal ALT11 spec
        var b04 = ReferenceStrategyCatalog.BuiltIns.First(b => b.Id == "GX-B04");
        Assert.Contains(b04.Args, a => a.Contains("multisplit:pos=2:seqovl=681"));
        Assert.DoesNotContain(b04.Args, a => a.Contains("pos=1,midsld"));
    }
}
