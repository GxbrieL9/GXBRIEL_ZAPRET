using System.IO;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Strategies;
using GxbrielZapret.ViewModels;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class FullScanServiceVerificationTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
    public FullScanServiceVerificationTests() => Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT",
        Path.Combine(Path.GetTempPath(), "gx-fullscan-proof-" + Guid.NewGuid().ToString("N")));
    public void Dispose() => Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _previous);

    internal static ApplicationFlowResult Flows(bool? video = true, bool gateway = true) =>
        new(new(video, "video evidence", video == true ? ApplicationFlowProbe.VideoSampleBytes : 0),
            new(gateway, "gateway evidence"));

    [Fact]
    public async Task DiscordTransportFailureStillMeasuresYouTubeForEveryRepeat()
    {
        int samples = 0;
        using var service = new Canonical5ProbeService(new FakeRunner(),
            (host, _) => Task.FromResult(host != "discord.com"),
            (_, _) => { samples++; return Task.FromResult(Flows()); });
        var result = await service.EvaluateCandidateAsync(ReferenceStrategyCatalog.All[0].ToPreset(), null,
            CancellationToken.None, targets: AutoScope.Both.GoalHosts());
        Assert.Equal(3, samples);
        Assert.NotNull(result.Flows);
        Assert.True(result.Flows.YouTubeVideo.Passed);
        Assert.False(result.IsWork);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(null, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task NineHttpsSuccessesRequireApplicationProofEvenWhenCallerOmitsScope(bool? video, bool gateway, bool expected)
    {
        var runner = new FakeRunner();
        AutoScope? checkedScope = null;
        using var service = new Canonical5ProbeService(runner, (_, _) => Task.FromResult(true), (scope, _) =>
        {
            checkedScope = scope;
            return Task.FromResult(Flows(video, gateway));
        });
        var result = await service.EvaluateCandidateAsync(ReferenceStrategyCatalog.All[0].ToPreset(), null,
            CancellationToken.None, targets: AutoScope.Both.GoalHosts());
        Assert.Equal(9, result.PassCount);
        Assert.Equal(AutoScope.Both, checkedScope);
        Assert.Equal(expected, result.IsWork);
        Assert.Equal(video is null && gateway, result.IsInconclusive);
        Assert.False(runner.IsAlive);
        var score = MainViewModel.ScoreFromCanonicalResult(result);
        Assert.Equal(expected, score.IsStrictWork);
        if (!expected) Assert.DoesNotContain("всё прошло", score.Detail);
    }

    [Fact]
    public async Task YoutubeOnlyDoesNotRequireUnselectedDiscord()
    {
        using var service = new Canonical5ProbeService(new FakeRunner(), (_, _) => Task.FromResult(true), (scope, _) =>
        {
            Assert.Equal(AutoScope.YouTube, scope);
            return Task.FromResult(Flows(gateway: false));
        });
        Assert.True((await service.EvaluateCandidateAsync(new Preset(), null, CancellationToken.None,
            targets: AutoScope.YouTube.GoalHosts())).IsWork);
    }

    [Fact]
    public async Task CancellationDuringVideoCheckStopsCandidateAndCannotProduceWork()
    {
        var runner = new FakeRunner();
        using var stop = new CancellationTokenSource();
        using var service = new Canonical5ProbeService(runner, (_, _) => Task.FromResult(true), (_, token) =>
        {
            stop.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Flows());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EvaluateCandidateAsync(
            new Preset(), null, stop.Token));
        Assert.False(runner.IsAlive);
    }

    [Fact]
    public async Task ProbeUsesTheSameHostlistAndCaptureOptionsAsNormalLaunch()
    {
        Directory.CreateDirectory(AppPaths.AppDataRoot);
        string hostlist = Path.Combine(AppPaths.AppDataRoot, "selected.txt");
        File.WriteAllText(hostlist, "youtube.com\ndiscord.com\n");
        var options = new ProbeLaunchOptions(hostlist, GameFilter: true);
        var preset = ReferenceStrategyCatalog.All[0].ToPreset();
        var expected = EngineService.BuildArguments(preset, hostlist, gameFilter: true, disableQuic: true, forLaunch: true);
        var runner = new FakeRunner();
        using var service = new Canonical5ProbeService(runner, (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(Flows()));
        await service.EvaluateCandidateAsync(preset, null, CancellationToken.None, disableQuic: true, launchOptions: options);
        Assert.Equal(expected, runner.Arguments);
        Assert.DoesNotContain(runner.Arguments, arg => arg.Contains("probe-targets-"));
    }

    [Theory]
    [InlineData("discord.com", "403", false)]
    [InlineData("discord.com", "429", false)]
    [InlineData("discord.com", "503", false)]
    [InlineData("redirector.googlevideo.com", "403", false)]
    [InlineData("gateway.discord.gg", "503", false)]
    [InlineData("updates.discord.com", "503", false)]
    [InlineData("discord.com", "200", true)]
    [InlineData("cdn.discordapp.com", "403", true)]
    [InlineData("i.ytimg.com", "404", true)]
    public void TransportResponseIsNotAutomaticallyServiceSuccess(string host, string code, bool expected) =>
        Assert.Equal(expected, Canonical5ProbeService.IsExpectedTargetResponse(host, code));

    [Fact]
    public void HttpsOnlyCannotBeWrittenAsWork()
    {
        var service = new PresetService();
        service.LoadForNetwork("net");
        var preset = service.ReferencePresets[0];
        Assert.Throws<InvalidOperationException>(() => service.UpdateStrategyProbeState(preset, "WORK",
            "verified_Discord + YouTube_9_of_9", AutoScope.Both.GoalHosts().ToDictionary(h => h, _ => true), 9, 9, 10, "net"));
        Assert.False(preset.IsWork);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void OlderWorkIsInvalidatedWithoutErasingHistoricalResult(int version)
    {
        StrategyStateStore.SaveSingleState("net", new()
        {
            Id = "GX-B01", Status = "WORK", Reason = "verified_Discord + YouTube_9_of_9", ValidationVersion = version,
            PassCount = 9, TotalCount = 9, Probes = AutoScope.Both.GoalHosts().ToDictionary(h => h, _ => true)
        });
        string before = File.ReadAllText(StrategyStateStore.StateFilePath);
        var service = new PresetService();
        service.LoadForNetwork("net");
        Assert.True(service.ReferencePresets[0].IsUntested);
        Assert.Equal(before, File.ReadAllText(StrategyStateStore.StateFilePath));
    }

    [Fact]
    public void VerifiedFlowsSurviveReloadButChangedLaunchSettingsRequireRecheck()
    {
        var service = new PresetService();
        service.LoadForNetwork("net", referenceOptionsKey: "same-launch");
        service.UpdateStrategyProbeState(service.ReferencePresets[0], "WORK", "verified_Discord + YouTube_9_of_9",
            AutoScope.Both.GoalHosts().ToDictionary(h => h, _ => true), 9, 9, 10, "net", Flows(), "same-launch");
        service.LoadForNetwork("net", referenceOptionsKey: "same-launch");
        Assert.True(service.ReferencePresets[0].IsWork);
        Assert.Contains("video evidence", service.ReferencePresets[0].VerifiedFlowSummary);
        service.LoadForNetwork("net", referenceOptionsKey: "changed-launch");
        Assert.True(service.ReferencePresets[0].IsUntested);
        service.LoadForNetwork("net", referenceOptionsKey: "same-launch");
        Assert.True(service.ReferencePresets[0].IsWork);
    }

    [Fact]
    public void UnknownVideoSurvivesReloadAsNeitherWorkNorBroken()
    {
        var service = new PresetService();
        service.LoadForNetwork("net");
        service.UpdateStrategyProbeState(service.ReferencePresets[0], "INCONCLUSIVE", "inconclusive_Both_9_of_9",
            AutoScope.Both.GoalHosts().ToDictionary(h => h, _ => true), 9, 9, 20, "net", Flows(null));
        service.LoadForNetwork("net");
        var preset = service.ReferencePresets[0];
        Assert.True(preset.IsInconclusive);
        Assert.True(preset.IsUntested);
        Assert.False(preset.IsWork);
        Assert.False(preset.IsFailed);
        Assert.Contains("video evidence", preset.PoolStatusHint);
    }

    [Fact]
    public void LegacyFalseBrokenIsReclassifiedWithoutChangingHistoricalFile()
    {
        StrategyStateStore.SaveSingleState("net", new()
        {
            Id = "GX-B01", Status = "BROKEN", ValidationVersion = 3, PassCount = 9, TotalCount = 9,
            Probes = AutoScope.Both.GoalHosts().ToDictionary(h => h, _ => true), Flows = Flows(null)
        });
        string before = File.ReadAllText(StrategyStateStore.StateFilePath);
        var service = new PresetService();
        service.LoadForNetwork("net");
        Assert.True(service.ReferencePresets[0].IsInconclusive);
        Assert.Equal(before, File.ReadAllText(StrategyStateStore.StateFilePath));
    }

    [Fact]
    public void ProbeLogRedactsSignedMediaUrls()
    {
        string detail = ScanProbeLog.Redact("failed https://rr.googlevideo.com/video?sig=secret and wss://gateway.discord.gg/?token=secret end");
        Assert.DoesNotContain("secret", detail);
        Assert.Contains("end", detail);
    }

    [Fact]
    public async Task EngineReadinessFailureCannotClassifyTheStrategyAsBroken()
    {
        var runner = new FakeRunner(ready: false);
        using var service = new Canonical5ProbeService(runner,
            (_, _) => throw new Exception("No network probe is allowed before readiness"),
            (_, _) => throw new Exception("No flow probe is allowed before readiness"));
        var result = await service.EvaluateCandidateAsync(ReferenceStrategyCatalog.All[0].ToPreset(), null, CancellationToken.None);
        Assert.True(result.CheckUnavailable);
        Assert.Equal("INCONCLUSIVE", result.Status);
        Assert.False(runner.IsAlive);
    }

    [Fact]
    public async Task InitialFailureCannotBeHiddenByTwoSuccessfulRepeats()
    {
        int calls = 0, flowCalls = 0;
        var runner = new FakeRunner();
        using var service = new Canonical5ProbeService(runner,
            (_, _) => Task.FromResult(++calls != 1),
            (_, _) => { flowCalls++; return Task.FromResult(Flows()); });
        var result = await service.EvaluateCandidateAsync(ReferenceStrategyCatalog.All[0].ToPreset(), null,
            CancellationToken.None, targets: new[] { "www.youtube.com" });
        Assert.Equal(3, calls);
        Assert.Equal(3, flowCalls);
        Assert.False(result.IsWork);
        Assert.False(runner.IsAlive);
        Assert.Contains("1/3", result.TargetDetails!["www.youtube.com"]);
    }

    [Fact]
    public async Task RepeatedVideoFailureRejectsOtherwiseWorkingTransport()
    {
        int flowCalls = 0;
        using var service = new Canonical5ProbeService(new FakeRunner(), (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(Flows(video: ++flowCalls != 2)));
        var result = await service.EvaluateCandidateAsync(ReferenceStrategyCatalog.All[0].ToPreset(), null,
            CancellationToken.None, targets: new[] { "www.youtube.com" });
        Assert.Equal(3, flowCalls);
        Assert.False(result.IsWork);
        Assert.False(result.Flows!.YouTubeVideo.Passed);
    }

    [Fact]
    public async Task LatencyExcludesEngineStartupAndDoesNotDivideByTargetCount()
    {
        using var service = new Canonical5ProbeService(new FakeRunner(startupMs: 500),
            async (_, ct) => { await Task.Delay(20, ct); return true; },
            (_, _) => Task.FromResult(Flows()));
        var result = await service.EvaluateCandidateAsync(ReferenceStrategyCatalog.All[0].ToPreset(), null,
            CancellationToken.None, targets: AutoScope.YouTube.GoalHosts());
        Assert.True(result.IsWork);
        Assert.InRange(result.LatencyMs, 15, 450);
    }

    [Fact]
    public void FastPickOrderPrioritizesRecommendedComboAndHostfakesplit()
    {
        var all = ReferenceStrategyCatalog.All.Select(r => r.ToPreset()).ToList();
        var ordered = MainViewModel.BuildFastPickOrder(all, AutoScope.Both.GoalHosts());
        Assert.Equal("GX-B01", ordered[0].ReferenceId);
        Assert.Equal("GX-C16", ordered[1].ReferenceId);
        Assert.Equal("GX-C15", ordered[2].ReferenceId);
    }

    [Fact]
    public void ScopeCatchAllToTargetsPreservesHostlistDomainsProfiles()
    {
        var preset = new Preset
        {
            Name = "domain-scoped",
            Args = new()
            {
                "--wf-tcp-out=443", "--wf-udp-out=443",
                "--filter-tcp=443", "--filter-l7=tls",
                "--hostlist-domains=googlevideo.com", "--hostlist-exclude-domains=redirector.googlevideo.com",
                "--payload=tls_client_hello", "--lua-desync=wssize:wsize=1:scale=6"
            }
        };
        var args = EngineService.BuildArguments(preset, null, bypassAll: false, disableQuic: true, forLaunch: false);
        int domIdx = args.IndexOf("--hostlist-domains=googlevideo.com");
        Assert.True(domIdx >= 0);
        int start = args.FindLastIndex(domIdx, a => a == "--new") + 1;
        int end = args.FindIndex(domIdx, a => a == "--new");
        var segment = args.GetRange(start, (end < 0 ? args.Count : end) - start);
        Assert.DoesNotContain(segment, a => a.StartsWith("--hostlist=", StringComparison.Ordinal));
    }

    private sealed class FakeRunner(bool ready = true, int startupMs = 0) : IProbeEngineRunner
    {
        public bool IsAlive { get; private set; }
        public bool IsReady => IsAlive && ready;
        public List<string> Arguments { get; private set; } = new();
        public void Start(List<string> args) { Arguments = args; IsAlive = true; }
        public Task WaitReadyAsync(CancellationToken ct) => Task.Delay(startupMs, ct);
        public void Stop() => IsAlive = false;
        public void Dispose() => Stop();
    }
}
