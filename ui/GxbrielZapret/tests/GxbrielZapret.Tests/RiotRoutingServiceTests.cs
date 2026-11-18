using System;
using System.IO;
using System.Linq;
using GxbrielZapret.Services.Platform;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class RiotRoutingServiceTests
{
    private static readonly string[] ProhibitedSubstrings =
    {
        "riotgames.com",
        "riotcdn.net",
        "pvp.net",
        "leagueoflegends.com"
    };

    [Fact]
    public void Mappings_DoNotContainRiotOrLeagueDomains()
    {
        foreach (var (ip, domain) in RiotRoutingService.Mappings)
        {
            foreach (var prohibited in ProhibitedSubstrings)
            {
                Assert.DoesNotContain(prohibited, domain, StringComparison.OrdinalIgnoreCase);
            }
            Assert.NotEqual("104.16.120.50", ip);
        }
    }

    [Fact]
    public void Mappings_PreserveBlitzAndCrlDomains()
    {
        var domains = RiotRoutingService.Mappings.Select(m => m.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("blitz.gg", domains);
        Assert.Contains("blitz-desktop.blitz.gg", domains);
        Assert.Contains("yr2.c.lencr.org", domains);
        Assert.Contains("r3.c.lencr.org", domains);
        Assert.Contains("x1.c.lencr.org", domains);
    }

    [Fact]
    public void ApplyHostsFix_WritesBlitzAndCrlWithoutRiotDomains()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"hosts_test_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "127.0.0.1 localhost\n::1 localhost\n");

            bool ok = RiotRoutingService.ApplyHostsFix(tempFile, flushDns: false);
            Assert.True(ok);

            string content = File.ReadAllText(tempFile);
            Assert.Contains(RiotRoutingService.HeaderTag, content);
            Assert.Contains("blitz-desktop.blitz.gg", content);
            Assert.Contains("yr2.c.lencr.org", content);

            foreach (var prohibited in ProhibitedSubstrings)
            {
                Assert.DoesNotContain(prohibited, content, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain("104.16.120.50", content);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ApplyHostsFix_RemovesLegacyRiotBlockAndReplacesWithCleanBlock()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"hosts_test_legacy_{Guid.NewGuid():N}.txt");
        try
        {
            string legacyHosts = "127.0.0.1 localhost\n" +
                                 "# GXBRIEL_ZAPRET - Game Routing Fix (Riot & Blitz)\n" +
                                 "104.16.120.50   clientconfig.rpg.riotgames.com\n" +
                                 "104.16.120.50   data.riotgames.com\n" +
                                 "104.16.120.50   entitlements.auth.riotgames.com\n" +
                                 "185.111.111.155 blitz.gg\n" +
                                 "# END GXBRIEL_ZAPRET - Game Routing Fix\n";
            File.WriteAllText(tempFile, legacyHosts);

            bool ok = RiotRoutingService.ApplyHostsFix(tempFile, flushDns: false);
            Assert.True(ok);

            string updated = File.ReadAllText(tempFile);
            Assert.Contains("127.0.0.1 localhost", updated);
            Assert.Contains("blitz-desktop.blitz.gg", updated);
            Assert.Contains("yr2.c.lencr.org", updated);

            Assert.DoesNotContain("clientconfig.rpg.riotgames.com", updated);
            Assert.DoesNotContain("data.riotgames.com", updated);
            Assert.DoesNotContain("entitlements.auth.riotgames.com", updated);
            Assert.DoesNotContain("104.16.120.50", updated);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ApplyHostsFix_IsIdempotent()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"hosts_test_idempotent_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "127.0.0.1 localhost\n");

            Assert.True(RiotRoutingService.ApplyHostsFix(tempFile, flushDns: false));
            string firstRun = File.ReadAllText(tempFile);

            Assert.True(RiotRoutingService.ApplyHostsFix(tempFile, flushDns: false));
            string secondRun = File.ReadAllText(tempFile);

            Assert.Equal(firstRun, secondRun);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void RemoveHostsFix_RemovesBlockCleanly()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"hosts_test_remove_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "127.0.0.1 localhost\n");
            Assert.True(RiotRoutingService.ApplyHostsFix(tempFile, flushDns: false));

            Assert.True(RiotRoutingService.RemoveHostsFix(tempFile, flushDns: false));
            string content = File.ReadAllText(tempFile);

            Assert.Contains("127.0.0.1 localhost", content);
            Assert.DoesNotContain(RiotRoutingService.HeaderTag, content);
            Assert.DoesNotContain("blitz", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("lencr.org", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
