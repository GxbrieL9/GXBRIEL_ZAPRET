using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class StrategyReliabilityTests
{
    [Fact]
    public void WorkForOneServiceDoesNotQualifyForAnother()
    {
        var preset = new Preset { Status = "WORK", ProbeResults = AutoScope.YouTube.GoalHosts().ToDictionary(h => h, _ => true) };
        Assert.True(preset.IsWorkFor(AutoScope.YouTube.GoalHosts()));
        Assert.False(preset.IsWorkFor(AutoScope.Discord.GoalHosts()));
        Assert.False(preset.IsWorkFor(AutoScope.Both.GoalHosts()));
        Assert.False(preset.IsWorkFor(Array.Empty<string>()));
    }

    [Fact]
    public void DiscoveryUsesNativeLuaAndDistinctFingerprints()
    {
        var candidates = DynamicDiscoveryEngine.CreateCandidates();
        Assert.NotEmpty(candidates);
        Assert.Equal(candidates.Count, candidates.Select(p => p.Fingerprint).Distinct().Count());
        foreach (var candidate in candidates)
        {
            Assert.Contains(candidate.Args, a => a.StartsWith("--lua-desync="));
            Assert.DoesNotContain(candidate.Args, a => a.StartsWith("--dpi-desync"));
            Assert.DoesNotContain(ReferenceStrategyCatalog.All, r => r.Fingerprint == candidate.Fingerprint);
        }
    }

    [Fact]
    public async Task EmptyTargetSetCannotProduceWork()
    {
        using var service = new Canonical5ProbeService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.EvaluateCandidateAsync(
            new Preset(), null, CancellationToken.None, targets: Array.Empty<string>()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeadersWithoutCompletedBodyAreNotSuccess(bool completeBody)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            using var stream = client.GetStream();
            var request = new byte[4096];
            int received = await stream.ReadAsync(request, stop.Token);
            Assert.True(received > 0);
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\n" + (completeBody ? "test" : "");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
            if (!completeBody)
                try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        });
        try
        {
            var result = await NetProbe.RunCurlAsync($"http://127.0.0.1:{port}/", null, CancellationToken.None, 1);
            Assert.Equal(completeBody, result.ok);
            if (!completeBody) Assert.Equal(28, result.exitCode);
        }
        finally { stop.Cancel(); await server; }
    }

    [Fact]
    public void CustomVerificationSurvivesReloadButNotNetworkChange()
    {
        WithIsolatedStorage(() =>
        {
            var service = new PresetService();
            service.LoadForNetwork("network-a");
            var preset = new Preset { Name = "custom regression", Args = new() { "--lua-desync=multisplit:pos=7" } };
            service.AddUser(preset);
            var probes = AutoScope.YouTube.GoalHosts().ToDictionary(h => h, _ => true);
            service.UpdateStrategyProbeState(preset, "WORK", "verified_YouTube_4_of_4", probes, 4, 4, 10, "network-a", FullScanServiceVerificationTests.Flows());
            service.LoadForNetwork("network-a");
            Assert.True(service.FindByFingerprint(preset.Fingerprint)!.IsWorkFor(AutoScope.YouTube.GoalHosts()));
            service.LoadForNetwork("network-b");
            Assert.True(service.FindByFingerprint(preset.Fingerprint)!.IsUntested);
        });
    }

    [Fact]
    public void EmptyEvidenceCannotRestoreWork()
    {
        WithIsolatedStorage(() =>
        {
            StrategyStateStore.SaveSingleState("network-a", new StrategyProbeState
            {
                Id = ReferenceStrategyCatalog.All[0].Id, Status = "WORK", Reason = "verified_5_of_5",
                PassCount = 5, TotalCount = 5
            });
            var service = new PresetService();
            service.LoadForNetwork("network-a");
            Assert.False(service.ReferencePresets[0].IsWork);
        });
    }

    private static void WithIsolatedStorage(Action action)
    {
        string root = Path.Combine(Path.GetTempPath(), "gx-reliability-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", root);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
