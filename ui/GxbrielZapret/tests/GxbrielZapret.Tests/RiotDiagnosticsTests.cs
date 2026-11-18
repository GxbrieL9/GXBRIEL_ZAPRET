using System;
using System.Linq;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Network;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class RiotDiagnosticsTests
{
    [Fact]
    public void Endpoints_TargetLegitimateOfficialDomainsAndExcludeRdevs()
    {
        var hosts = RiotDiagnosticsService.Endpoints.Select(e => e.Host).ToList();

        // Must target legitimate official Riot endpoints
        Assert.Contains("clientconfig.rpg.riotgames.com", hosts);
        Assert.Contains("auth.riotgames.com", hosts);
        Assert.Contains("entitlements.auth.riotgames.com", hosts);

        // Strict requirement: do NOT assume or use unverified clientconfig.rdevs.com
        Assert.DoesNotContain("clientconfig.rdevs.com", hosts);
        foreach (var host in hosts)
        {
            Assert.DoesNotContain("rdevs.com", host, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BuildRiotRows_ProducesCorrectGroupAndHosts()
    {
        var rows = RiotDiagnosticsService.BuildRiotRows();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(RiotDiagnosticsService.GroupName, r.Group);
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.False(string.IsNullOrWhiteSpace(r.Host));
        });
    }

    [Fact]
    public async Task CheckAsync_WhenAllEndpointsReachable_ReturnsAllReachableReport()
    {
        var service = new RiotDiagnosticsService(
            tlsProbe: (host, proto, ct) => Task.FromResult(DiagStatus.Ok),
            reachProbe: (host, ct) => Task.FromResult(DiagStatus.Ok));

        var report = await service.CheckAsync(CancellationToken.None);

        Assert.True(report.AllReachable);
        Assert.Equal(3, report.PassedCount);
        Assert.Equal(3, report.TotalCount);
        Assert.All(report.Endpoints, ep => Assert.True(ep.Passes));
    }

    [Fact]
    public async Task CheckAsync_WhenAuthFails_ReportsPartialAvailability()
    {
        var service = new RiotDiagnosticsService(
            tlsProbe: (host, proto, ct) =>
                Task.FromResult(host == "auth.riotgames.com" ? DiagStatus.Fail : DiagStatus.Ok),
            reachProbe: (host, ct) =>
                Task.FromResult(host == "auth.riotgames.com" ? DiagStatus.Fail : DiagStatus.Ok));

        var report = await service.CheckAsync(CancellationToken.None);

        Assert.False(report.AllReachable);
        Assert.Equal(2, report.PassedCount);
        Assert.Equal(3, report.TotalCount);

        var auth = report.Endpoints.First(e => e.Host == "auth.riotgames.com");
        Assert.False(auth.Passes);
        Assert.Equal(DiagStatus.Fail, auth.Http);
    }

    [Fact]
    public async Task CheckAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = new RiotDiagnosticsService(
            tlsProbe: (host, proto, ct) => Task.FromResult(DiagStatus.Ok),
            reachProbe: (host, ct) => Task.FromResult(DiagStatus.Ok));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cts.Token));
    }

    [Fact]
    public async Task ProbeRowAsync_UpdatesRowCellsCorrectly()
    {
        var service = new RiotDiagnosticsService(
            tlsProbe: (host, proto, ct) => Task.FromResult(proto == SslProtocols.Tls13 ? DiagStatus.Ok : DiagStatus.Fail),
            reachProbe: (host, ct) => Task.FromResult(DiagStatus.Ok),
            pingProbe: (host, ct) => Task.FromResult((true, 25L)));

        var row = new DiagRow
        {
            Group = RiotDiagnosticsService.GroupName,
            Name = "Client Config",
            Host = "clientconfig.rpg.riotgames.com"
        };
        row.Reset();

        await service.ProbeRowAsync(row, CancellationToken.None);

        Assert.Equal(DiagStatus.Ok, row.Ping);
        Assert.Equal(DiagStatus.Ok, row.Http);
        Assert.Equal(DiagStatus.Fail, row.Tls12);
        Assert.Equal(DiagStatus.Ok, row.Tls13);
        Assert.Contains("25", row.PingText);
    }
}
