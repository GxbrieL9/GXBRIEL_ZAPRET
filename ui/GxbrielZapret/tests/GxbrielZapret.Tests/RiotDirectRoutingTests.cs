using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class RiotDirectRoutingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReferenceStrategiesProtectRiotTlsBeforeGeneralDesync(bool bypassAll, bool disableQuic)
    {
        foreach (var strategy in ReferenceStrategyCatalog.All)
        {
            var args = EngineService.BuildArguments(strategy.ToPreset(), null, bypassAll: bypassAll, disableQuic: disableQuic);
            int hosts = args.FindIndex(a => a.StartsWith("--hostlist-domains=riotgames.com,"));
            Assert.True(hosts >= 0, strategy.Id + ": Riot pass-through missing");
            int start = args.FindLastIndex(hosts, a => a == "--new") + 1;
            int end = args.FindIndex(hosts, a => a == "--new");
            var direct = args.GetRange(start, end - start);
            Assert.Contains("--filter-tcp=80,443,8443", direct);
            Assert.Contains("--filter-l7=http,tls", direct);
            Assert.DoesNotContain(direct, a => a.StartsWith("--lua-desync=") || a.StartsWith("--payload=") || a.StartsWith("--filter-udp="));
            Assert.True(end < args.FindIndex(a => a.StartsWith("--lua-desync=")), strategy.Id);
        }
    }
}
