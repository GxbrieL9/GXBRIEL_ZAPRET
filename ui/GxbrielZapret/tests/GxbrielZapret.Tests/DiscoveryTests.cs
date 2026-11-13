using System.IO;
using Xunit;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Tests;

public class DiscoveryTests
{
    [Fact]
    public void BuiltInTargets_AreSeededWithExpectedDefaults()
    {
        var builtIns = DiscoveryService.CreateBuiltInTargets();

        Assert.NotEmpty(builtIns);

        // Core enabled profiles by default (preserving existing user behavior)
        var discord = builtIns.FirstOrDefault(t => t.Id == "discord");
        Assert.NotNull(discord);
        Assert.True(discord.Enabled);
        Assert.Equal(TargetCategory.Programs, discord.Category);

        var lol = builtIns.FirstOrDefault(t => t.Id == "league_of_legends");
        Assert.NotNull(lol);
        Assert.True(lol.Enabled);
        Assert.Equal(TargetCategory.Games, lol.Category);

        var rockstar = builtIns.FirstOrDefault(t => t.Id == "rockstar_games");
        Assert.NotNull(rockstar);
        Assert.True(rockstar.Enabled);

        // New game profiles (disabled by default so update doesn't suddenly monitor 10 extra games)
        var dbd = builtIns.FirstOrDefault(t => t.Id == "dead_by_daylight");
        Assert.NotNull(dbd);
        Assert.False(dbd.Enabled);
        Assert.Contains("DeadByDaylight-Win64-Shipping.exe", dbd.Executables);

        var roblox = builtIns.FirstOrDefault(t => t.Id == "roblox");
        Assert.NotNull(roblox);
        Assert.False(roblox.Enabled);

        var fortnite = builtIns.FirstOrDefault(t => t.Id == "fortnite");
        Assert.NotNull(fortnite);
        Assert.False(fortnite.Enabled);

        // SoundCloud MUST be a DomainTarget (Web service) without any fake .exe
        var soundcloud = builtIns.FirstOrDefault(t => t.Id == "soundcloud");
        Assert.NotNull(soundcloud);
        Assert.False(soundcloud.Enabled);
        Assert.Equal(TargetType.Domain, soundcloud.Type);
        Assert.Equal(TargetCategory.WebServices, soundcloud.Category);
        Assert.Empty(soundcloud.Executables); // Executables MUST be empty!
        Assert.Contains("soundcloud.com", soundcloud.SeedDomains);
        Assert.Contains("sndcdn.com", soundcloud.SeedDomains);
        Assert.True(soundcloud.IsDomainTarget);
        Assert.False(soundcloud.IsProcessTarget);
    }

    [Theory]
    [InlineData("DeadByDaylight-Win64-Shipping.exe", "dead_by_daylight", true)]
    [InlineData("DeadByDaylight.exe", "dead_by_daylight", true)]
    [InlineData("deadbydaylight-shipping", "dead_by_daylight", true)]
    [InlineData("Discord.exe", "discord", true)]
    [InlineData("discord", "discord", true)]
    [InlineData("DiscordCanary.exe", "discord", true)]
    [InlineData("LeagueClientUx.exe", "league_of_legends", true)]
    [InlineData("RobloxPlayerBeta.exe", "roblox", true)]
    [InlineData("FortniteClient-Win64-Shipping.exe", "fortnite", true)]
    [InlineData("Pioneer-Win64-Shipping.exe", "arc_raiders", true)]
    [InlineData("notepad.exe", "discord", false)]
    public void MatchesTargetName_AccuratelyCorrelatesProcess(string procName, string targetId, bool expectedMatch)
    {
        var builtIns = DiscoveryService.CreateBuiltInTargets();
        var target = builtIns.First(t => t.Id == targetId);

        bool actualMatch = DiscoveryService.MatchesTargetName(target, procName);
        Assert.Equal(expectedMatch, actualMatch);
    }

    [Fact]
    public void CustomTarget_FromProcess_CreatesAndMatchesCorrectly()
    {
        var svc = new DiscoveryService();
        var snapshot = new ProcessSnapshotItem
        {
            Pid = 12345,
            ProcessName = "MyCustomGame",
            WindowTitle = "My Custom Game v1.0",
            Description = "Custom Unreal Engine Game",
            ExecutablePath = @"C:\Games\MyGame\MyCustomGame.exe",
            IsSystem = false
        };

        var target = svc.AddCustomTargetFromProcess(snapshot);

        Assert.NotNull(target);
        Assert.StartsWith("custom_", target.Id);
        Assert.Equal(TargetCategory.Custom, target.Category);
        Assert.True(target.Enabled);
        Assert.True(target.TrackChildProcesses);
        Assert.Equal("Custom Unreal Engine Game", target.DisplayName);

        // Verification of matching
        Assert.True(DiscoveryService.MatchesTargetName(target, "MyCustomGame.exe"));
        Assert.True(DiscoveryService.MatchesTargetName(target, "MyCustomGame"));

        // Cleanup
        svc.RemoveTarget(target.Id);
    }

    [Fact]
    public void CaptureScope_WithTargets_IncludesEnabledTargetsOnly()
    {
        var targets = new List<DiscoveryTarget>
        {
            new()
            {
                Id = "t1",
                DisplayName = "Target One",
                Enabled = true,
                Executables = new List<string> { "game1.exe" }
            },
            new()
            {
                Id = "t2",
                DisplayName = "Target Two",
                Enabled = false,
                Executables = new List<string> { "game2.exe" }
            }
        };

        var scope = new LiveCaptureService.CaptureScope(Targets: targets);

        Assert.True(scope.Includes("game1.exe"));
        Assert.False(scope.Includes("game2.exe"));
        Assert.False(scope.Includes("unknown.exe"));
        Assert.Contains("Target One", scope.Label);
        Assert.DoesNotContain("Target Two", scope.Label);
    }

    [Fact]
    public void NoiseFilter_CorrectlyFiltersDnsAndTelemetry()
    {
        // DNS resolvers must be filtered as noise
        Assert.True(NoiseFilter.IsIpNoise("1.1.1.1"));
        Assert.True(NoiseFilter.IsIpNoise("8.8.8.8"));
        Assert.True(NoiseFilter.IsIpNoise("77.88.8.8"));

        // Private / non-routable IPs must be noise
        Assert.True(NoiseFilter.IsIpNoise("192.168.1.1"));
        Assert.True(NoiseFilter.IsIpNoise("127.0.0.1"));
        Assert.True(NoiseFilter.IsIpNoise("10.0.0.5"));

        // Valid public IP must not be noise
        Assert.False(NoiseFilter.IsIpNoise("162.159.130.233"));
        Assert.False(NoiseFilter.IsIpNoise("185.199.108.153"));

        // Telemetry domains must be noise
        Assert.True(NoiseFilter.IsDomainNoise("v10.events.data.microsoft.com"));
        Assert.True(NoiseFilter.IsDomainNoise("watson.telemetry.microsoft.com"));
        Assert.True(NoiseFilter.IsDomainNoise("settings-win.data.microsoft.com"));
        Assert.True(NoiseFilter.IsDomainNoise("app-measurement.com"));
        Assert.True(NoiseFilter.IsDomainNoise("google-analytics.com"));

        // Valid game/service domains must not be noise
        Assert.False(NoiseFilter.IsDomainNoise("gateway.discord.gg"));
        Assert.False(NoiseFilter.IsDomainNoise("deadbydaylight.com"));
        Assert.False(NoiseFilter.IsDomainNoise("epicgames.com"));
    }

    [Fact]
    public void IngestCapturedEndpoints_PopulatesCandidatesAndFiltersNoise()
    {
        var svc = new DiscoveryService();
        var discord = svc.Targets.First(t => t.Id == "discord");
        svc.ClearCandidates(discord.Id);

        var endpoints = new List<LiveCaptureService.CapturedEndpoint>
        {
            new(
                processName: "Discord.exe",
                pid: 1234,
                protocol: "TCP",
                state: "ESTABLISHED",
                localAddress: "192.168.1.50",
                localPort: 54321,
                remoteAddress: "162.159.130.233",
                remotePort: 443,
                remoteKind: "public_ipv4",
                domainCandidates: new List<string> { "gateway.discord.gg", "telemetry.microsoft.com" },
                sources: new List<string> { "test" },
                targetName: "Discord"
            ),
            new(
                processName: "Discord.exe",
                pid: 1234,
                protocol: "TCP",
                state: "ESTABLISHED",
                localAddress: "192.168.1.50",
                localPort: 54322,
                remoteAddress: "8.8.8.8", // DNS noise IP
                remotePort: 53,
                remoteKind: "public_ipv4",
                domainCandidates: new List<string> { "cdn.discordapp.com" },
                sources: new List<string> { "test" },
                targetName: "Discord"
            )
        };

        int added = svc.IngestCapturedEndpoints(endpoints);
        Assert.True(added >= 3); // 2 domains + 1 IP (8.8.8.8 and telemetry filtered out)

        var updatedDiscord = svc.Targets.First(t => t.Id == "discord");
        Assert.True(updatedDiscord.HasCandidates);

        var candValues = updatedDiscord.DiscoveredCandidates.Select(c => c.Value).ToList();
        Assert.Contains("gateway.discord.gg", candValues);
        Assert.Contains("cdn.discordapp.com", candValues);
        Assert.Contains("162.159.130.233", candValues);

        // Noise items must NOT be present
        Assert.DoesNotContain("telemetry.microsoft.com", candValues);
        Assert.DoesNotContain("8.8.8.8", candValues);

        // Clean up
        svc.ClearCandidates(discord.Id);
    }

    [Fact]
    public void ExportCandidates_WritesDiscoveredSourcesAndCompiledRuntimeLists()
    {
        var svc = new DiscoveryService();
        var target = svc.AddCustomTargetFromProcess(new ProcessSnapshotItem
        {
            Pid = 4242,
            ProcessName = "StageBExport",
            WindowTitle = "Stage B Export",
            Description = "Stage B Export",
            ExecutablePath = @"C:\Games\StageBExport.exe"
        });

        string slug = target.Id.Replace('_', '-');
        string discoveredHostlist = Path.Combine(AppPaths.ListsDiscoveredDir, $"{slug}.txt");
        string discoveredIpset = Path.Combine(AppPaths.ListsDiscoveredDir, $"ipset-{slug}.txt");
        string runtimeHostlist = Path.Combine(AppPaths.RuntimeListsDir, $"{slug}.txt");
        string runtimeIpset = Path.Combine(AppPaths.RuntimeListsDir, $"ipset-{slug}.txt");

        try
        {
            svc.ExportCandidates(target.Id, new[]
            {
                new DiscoveredItem { Value = "Voice.StageB.Example", IsIp = false, IsSelected = true },
                new DiscoveredItem { Value = "voice.stageb.example", IsIp = false, IsSelected = true },
                new DiscoveredItem { Value = "203.0.113.41", IsIp = true, IsSelected = true },
                new DiscoveredItem { Value = "203.0.113.42", IsIp = true, IsSelected = true }
            }, out int addedDomains, out int addedIps);

            Assert.Equal(1, addedDomains);
            Assert.Equal(2, addedIps);
            Assert.True(File.Exists(discoveredHostlist));
            Assert.True(File.Exists(discoveredIpset));
            Assert.True(File.Exists(runtimeHostlist));
            Assert.True(File.Exists(runtimeIpset));
            Assert.Equal(new[] { "voice.stageb.example" }, File.ReadAllLines(runtimeHostlist));
            Assert.Contains("203.0.113.0/24", File.ReadAllLines(runtimeIpset));
        }
        finally
        {
            svc.RemoveTarget(target.Id);
            foreach (var path in new[] { discoveredHostlist, discoveredIpset, runtimeHostlist, runtimeIpset })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }

    [Fact]
    public void DomainTarget_NeverMatchesProcessNames_EvenBrowserProcesses()
    {
        var soundcloud = new DiscoveryTarget
        {
            Id = "soundcloud",
            DisplayName = "SoundCloud",
            Type = TargetType.Domain,
            Category = TargetCategory.WebServices,
            SeedDomains = new List<string> { "soundcloud.com", "sndcdn.com" }
        };

        // Even if chrome/firefox is running, MatchesTargetName must NEVER return true for DomainTarget
        Assert.False(DiscoveryService.MatchesTargetName(soundcloud, "chrome.exe"));
        Assert.False(DiscoveryService.MatchesTargetName(soundcloud, "chrome"));
        Assert.False(DiscoveryService.MatchesTargetName(soundcloud, "msedge.exe"));
        Assert.False(DiscoveryService.MatchesTargetName(soundcloud, "firefox.exe"));
        Assert.False(DiscoveryService.MatchesTargetName(soundcloud, "SoundCloud.exe"));
    }

    [Theory]
    [InlineData("https://soundcloud.com/", "soundcloud.com", "Soundcloud")]
    [InlineData("http://api-v2.soundcloud.com/tracks?id=123", "api-v2.soundcloud.com", "Api-v2.soundcloud.com")]
    [InlineData("custom-radio.org:8080/stream", "custom-radio.org", "Custom-radio.org")]
    public void AddCustomDomainTarget_ExtractsHostAndCreatesDomainTarget(string url, string expectedDomain, string expectedTitle)
    {
        var svc = new DiscoveryService();
        var target = svc.AddCustomDomainTarget(url, expectedTitle);

        Assert.NotNull(target);
        Assert.Equal(TargetType.Domain, target.Type);
        Assert.Equal(TargetCategory.Custom, target.Category);
        Assert.True(target.Enabled);
        Assert.Empty(target.Executables);
        Assert.Contains(expectedDomain, target.SeedDomains);
        Assert.Equal(expectedTitle, target.DisplayName);

        // Cleanup
        svc.RemoveTarget(target.Id);
    }

    [Fact]
    public void CandidateScoring_ClassifiesHighMediumLowCorrectly()
    {
        var seedDomains = new List<string> { "soundcloud.com", "sndcdn.com" };
        var sessionStart = DateTime.UtcNow.AddMinutes(-2);

        // 1. Exact seed domain or subdomain -> HIGH
        var (c1, r1) = WebsiteLearningService.ScoreDomainCandidate("soundcloud.com", seedDomains, 1, DateTime.UtcNow, sessionStart);
        Assert.Equal(CandidateConfidence.High, c1);

        var (c2, r2) = WebsiteLearningService.ScoreDomainCandidate("api-v2.soundcloud.com", seedDomains, 2, DateTime.UtcNow, sessionStart);
        Assert.Equal(CandidateConfidence.High, c2);

        var (c3, r3) = WebsiteLearningService.ScoreDomainCandidate("cf-media.sndcdn.com", seedDomains, 4, DateTime.UtcNow, sessionStart);
        Assert.Equal(CandidateConfidence.High, c3);

        // 2. Generic CDN edge node -> MEDIUM
        var (c4, r4) = WebsiteLearningService.ScoreDomainCandidate("d1234.cloudfront.net", seedDomains, 1, DateTime.UtcNow, sessionStart);
        Assert.Equal(CandidateConfidence.Medium, c4);

        // 3. Analytics / Telemetry -> LOW
        var (c5, r5) = WebsiteLearningService.ScoreDomainCandidate("google-analytics.com", seedDomains, 5, DateTime.UtcNow, sessionStart);
        Assert.Equal(CandidateConfidence.Low, c5);

        var (c6, r6) = WebsiteLearningService.ScoreDomainCandidate("telemetry.microsoft.com", seedDomains, 1, DateTime.UtcNow, sessionStart);
        Assert.Equal(CandidateConfidence.Low, c6);
    }
}
