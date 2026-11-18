using System;
using System.IO;
using GxbrielZapret.Services.Platform;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class RiotRoutingMigrationTests
{
    [Fact]
    public void ApplyHostsFix_RemovesManagedRiotPinsAndKeepsOtherRoutes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gx_hosts_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "127.0.0.1 localhost\r\n" +
                "# GXBRIEL_ZAPRET - Game Routing Fix (Riot & Blitz)\r\n" +
                "104.16.120.50 clientconfig.rpg.riotgames.com\r\n" +
                "104.16.120.50 auth.riotgames.com\r\n" +
                "185.111.111.155 blitz.gg\r\n" +
                "# END GXBRIEL_ZAPRET - Game Routing Fix\r\n");
            Assert.True(RiotRoutingService.ApplyHostsFix(path, flushDns: false, ensureVanguard: false));
            var updated = File.ReadAllText(path);
            Assert.Contains("localhost", updated);
            Assert.Contains("blitz.gg", updated);
            Assert.Contains("yr2.c.lencr.org", updated);
            Assert.DoesNotContain("riotgames.com", updated, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("104.16.120.50", updated);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ApplyAndRemove_AreIdempotentAndPreserveUnmanagedContent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gx_hosts_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "127.0.0.1 localhost\r\n# user's unrelated entry\r\n");
            Assert.True(RiotRoutingService.ApplyHostsFix(path, flushDns: false, ensureVanguard: false));
            var first = File.ReadAllText(path);
            Assert.True(RiotRoutingService.ApplyHostsFix(path, flushDns: false, ensureVanguard: false));
            Assert.Equal(first, File.ReadAllText(path));
            Assert.True(RiotRoutingService.RemoveHostsFix(path, flushDns: false));
            var removed = File.ReadAllText(path);
            Assert.Contains("localhost", removed);
            Assert.Contains("unrelated entry", removed);
            Assert.DoesNotContain("blitz.gg", removed);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
