using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class AntigravityRoutingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EveryReferenceStrategyKeepsAntigravityDirectBeforeDesyncAndQuicDrop(bool bypassAll, bool disableQuic)
    {
        foreach (var strategy in ReferenceStrategyCatalog.All)
        {
            var args = EngineService.BuildArguments(strategy.ToPreset(), null, bypassAll: bypassAll, disableQuic: disableQuic);
            int end = args.IndexOf("--new");
            Assert.True(end > 0, strategy.Id);
            var direct = args.Take(end).ToList();
            string domains = Assert.Single(direct, a => a.StartsWith("--hostlist-domains="));
            Assert.Contains("--filter-l7=http,tls,quic", direct);
            Assert.Contains("--filter-tcp=80,443-65535", direct);
            Assert.Contains("--filter-udp=443-65535", direct);
            Assert.DoesNotContain(direct, a => a.StartsWith("--lua-desync=") || a.StartsWith("--dpi-desync=") || a.StartsWith("--payload="));
            string[] hosts = domains["--hostlist-domains=".Length..].Split(',');
            Assert.Contains("daily-cloudcode-pa.googleapis.com", hosts);
            Assert.Contains("www.googleapis.com", hosts);
            Assert.Contains("lh3.googleusercontent.com", hosts);
            Assert.Contains("www.gstatic.com", hosts);
            Assert.DoesNotContain("googleusercontent.com", hosts);
            Assert.DoesNotContain("googleapis.com", hosts);
            Assert.DoesNotContain("gstatic.com", hosts);
            Assert.DoesNotContain("googlevideo.com", hosts);
            Assert.DoesNotContain("yt3.googleusercontent.com", hosts);
            Assert.Contains(args.Skip(end + 1), a => a.StartsWith("--lua-desync="));
        }
    }
}
