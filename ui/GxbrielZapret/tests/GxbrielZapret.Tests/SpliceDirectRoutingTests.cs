using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class SpliceDirectRoutingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EveryReferenceStrategyKeepsSpliceDirectBeforeDesync(bool bypassAll, bool disableQuic)
    {
        foreach (var strategy in ReferenceStrategyCatalog.All)
        {
            var args = EngineService.BuildArguments(strategy.ToPreset(), null, bypassAll: bypassAll, disableQuic: disableQuic);
            int hostsIdx = args.FindIndex(a => a.StartsWith("--hostlist-domains=") && a.Contains("splice.com"));
            Assert.True(hostsIdx >= 0, $"{strategy.Id}: Splice pass-through missing");

            int start = args.FindLastIndex(hostsIdx, a => a == "--new") + 1;
            int end = args.FindIndex(hostsIdx, a => a == "--new");
            var direct = args.GetRange(start, end - start);

            Assert.Contains(direct, a => a.StartsWith("--filter-tcp="));
            Assert.Contains(direct, a => a.StartsWith("--filter-l7=") && a.Contains("tls"));
            Assert.DoesNotContain(direct, a => a.StartsWith("--lua-desync=") || a.StartsWith("--dpi-desync=") || a.StartsWith("--payload="));

            string domains = Assert.Single(direct, a => a.StartsWith("--hostlist-domains="));
            string[] hosts = domains["--hostlist-domains=".Length..].Split(',');
            Assert.Contains("splice.com", hosts);
            Assert.Contains("api.splice.com", hosts);
            Assert.Contains("desktop.splice.com", hosts);
            Assert.Contains("spliceblob.splice.com", hosts);
            Assert.Contains("spliceproduction.s3.us-west-1.amazonaws.com", hosts);

            int firstDesync = args.FindIndex(a => a.StartsWith("--lua-desync="));
            Assert.True(firstDesync < 0 || end < firstDesync, $"{strategy.Id}: Splice direct rule must precede any desync profile");
        }
    }
}
