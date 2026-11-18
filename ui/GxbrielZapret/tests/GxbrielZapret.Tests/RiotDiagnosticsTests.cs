using System.Net.Security;
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
    public async Task ChecksOfficialLauncherAndAuthEndpointsWithoutGameplayClaim()
    {
        int tls = 0, http = 0;
        var service = new RiotDiagnosticsService((host, protocol, _) =>
        {
            Assert.Contains(host, new[] { "clientconfig.rpg.riotgames.com", "auth.riotgames.com", "entitlements.auth.riotgames.com" });
            Assert.True(protocol is SslProtocols.Tls12 or SslProtocols.Tls13);
            Interlocked.Increment(ref tls);
            return Task.FromResult(DiagStatus.Ok);
        }, (host, _) => { Assert.False(string.IsNullOrWhiteSpace(host)); Interlocked.Increment(ref http); return Task.FromResult(DiagStatus.Ok); });
        var report = await service.CheckAsync();
        Assert.True(report.AllReachable);
        Assert.Equal(3, report.TotalCount);
        Assert.Equal(6, tls);
        Assert.Equal(3, http);
    }

    [Fact]
    public async Task CancellationStopsBeforeAdditionalEndpointProbes()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var service = new RiotDiagnosticsService((_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) cts.Cancel();
            return Task.FromResult(DiagStatus.Ok);
        }, (_, _) => Task.FromResult(DiagStatus.Ok));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cts.Token));
    }
}
