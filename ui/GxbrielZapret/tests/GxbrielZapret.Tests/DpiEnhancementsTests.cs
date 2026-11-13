using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class DpiEnhancementsTests
{
    [Fact]
    public void BuildChromeTlsClientHello_GeneratesValidRecordAndExtensions()
    {
        string host = "gateway.discord.gg";
        byte[] ch = NetProbe.BuildChromeTlsClientHello(host);

        // TLS Record Header
        Assert.True(ch.Length > 200, "ClientHello should be a realistic multi-cipher record");
        Assert.Equal(0x16, ch[0]); // Handshake record
        Assert.Equal(0x03, ch[1]); // Major TLS version
        Assert.Equal(0x01, ch[2]); // Record layer TLS 1.0

        int recLen = (ch[3] << 8) | ch[4];
        Assert.Equal(ch.Length - 5, recLen);

        // Handshake Header
        Assert.Equal(0x01, ch[5]); // ClientHello
        int hsLen = (ch[6] << 16) | (ch[7] << 8) | ch[8];
        Assert.Equal(ch.Length - 9, hsLen);

        // Check SNI presence
        var hostBytes = Encoding.ASCII.GetBytes(host);
        bool foundSni = false;
        for (int i = 0; i <= ch.Length - hostBytes.Length; i++)
        {
            if (ch.AsSpan(i, hostBytes.Length).SequenceEqual(hostBytes))
            {
                foundSni = true;
                break;
            }
        }
        Assert.True(foundSni, "ClientHello must contain the target SNI");

        // Check ALPN h2 presence
        var h2Bytes = Encoding.ASCII.GetBytes("h2");
        bool foundH2 = false;
        for (int i = 0; i <= ch.Length - h2Bytes.Length; i++)
        {
            if (ch.AsSpan(i, h2Bytes.Length).SequenceEqual(h2Bytes))
            {
                foundH2 = true;
                break;
            }
        }
        Assert.True(foundH2, "ClientHello must contain ALPN h2");
    }

    [Fact]
    public async Task UdpStunProbe_HandlesUnresponsiveEndpointGracefully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // Use a loopback unassigned high port to test timeout/fail handling
        var result = await NetProbe.UdpStunProbeAsync("127.0.0.1", 59999, cts.Token, timeoutSec: 1);
        Assert.True(result is DiagStatus.Timeout or DiagStatus.Fail,
            $"Expected Timeout or Fail for unassigned UDP port, got {result}");
    }

    [Fact]
    public void DiagRow_UdpOnly_ConfiguresCorrectInitialState()
    {
        var row = new DiagRow
        {
            Group = "Discord",
            Name = "Voice (UDP/RTC)",
            Host = "discord.media",
            UdpOnly = true,
            UdpPort = 50001
        };

        row.Reset();

        Assert.Equal(DiagStatus.Pending, row.Http);
        Assert.Equal(DiagStatus.Skip, row.Tls12);
        Assert.Equal(DiagStatus.Skip, row.Tls13);
        Assert.Equal(DiagStatus.Pending, row.Ping);
        Assert.True(row.UdpOnly);
        Assert.Equal(50001, row.UdpPort);
    }

    [Fact]
    public async Task SupportBundleService_GeneratesValidSanitizedArchive()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "gxbriel_test_bundle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var zipPath = Path.Combine(tempRoot, "support.zip");

        try
        {
            var settingsSvc = new SettingsService();
            var service = new SupportBundleService(settingsSvc);

            var result = await service.CreateBundleAsync(zipPath, CancellationToken.None);

            Assert.True(File.Exists(result));
            using var zip = ZipFile.OpenRead(result);

            Assert.Contains(zip.Entries, e => e.FullName == "system-summary.json");
            Assert.Contains(zip.Entries, e => e.FullName == "conflicts.txt");
            Assert.Contains(zip.Entries, e => e.FullName == "engine-logs-tail.txt");

            // Verify summary JSON is well-formed
            var summaryEntry = zip.GetEntry("system-summary.json");
            Assert.NotNull(summaryEntry);
            using var reader = new StreamReader(summaryEntry.Open());
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("Settings", out var settingsProp));
            Assert.True(settingsProp.TryGetProperty("BypassDesired", out _));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void AppSettings_Defaults_FavorNonTechUsers()
    {
        var settings = new AppSettings();
        Assert.True(settings.SimpleMode, "SimpleMode should default to true for non-tech users");
        Assert.True(settings.AutoHeal, "AutoHeal should default to true for silent recovery");
        Assert.False(settings.BypassAllSites, "BypassAllSites must default to false (whitelist mode) to avoid breaking domestic traffic and games");
    }

    [Fact]
    public void AppSettings_BuildReset_PreservesSimpleMode_AndDefaultsAutoHeal()
    {
        var current = new AppSettings { SimpleMode = false };
        var reset = SettingsService.BuildReset(current);

        Assert.False(reset.SimpleMode, "BuildReset should preserve the user's active view mode");
        Assert.True(reset.AutoHeal, "BuildReset should set AutoHeal to default true");
    }
}
