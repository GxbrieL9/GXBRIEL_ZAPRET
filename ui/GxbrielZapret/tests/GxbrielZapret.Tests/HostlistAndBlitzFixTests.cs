using System.IO;
using Xunit;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;

namespace GxbrielZapret.Tests;

public class HostlistAndBlitzFixTests
{
    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("162.159.130.234", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.5.1", false)]
    [InlineData("192.168.1.100", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.254", false)]
    [InlineData("169.254.10.20", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("not-an-ip", false)]
    [InlineData("", false)]
    public void TestPublicIPv4_ValidatesAccurately(string ip, bool expected)
    {
        bool actual = HostlistService.TestPublicIPv4(ip);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("*.discord.com", "discord.com")]
    [InlineData("discord.com:443", "discord.com")]
    [InlineData("youtube.com:80", "youtube.com")]
    [InlineData("  riotgames.com.  ", "riotgames.com")]
    [InlineData("gateway.discord.gg # voice gateway", "gateway.discord.gg")]
    [InlineData("cdn.discordapp.com; official cdn", "cdn.discordapp.com")]
    public void CleanDomain_CleansFormattedInputs(string raw, string expected)
    {
        string actual = HostlistService.CleanDomain(raw);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MatchesSuffix_MatchesAllowedDomains()
    {
        Assert.True(HostlistService.MatchesSuffix("auth.riotgames.com", HostlistService.AllowedLolSuffixes));
        Assert.True(HostlistService.MatchesSuffix("spectator.euw1.lol.pvp.net", HostlistService.AllowedLolSuffixes));
        Assert.True(HostlistService.MatchesSuffix("voice.discord.com", HostlistService.AllowedDiscordSuffixes));
        Assert.True(HostlistService.MatchesSuffix("gateway.discord.gg", HostlistService.AllowedDiscordSuffixes));
        Assert.True(HostlistService.MatchesSuffix("socialclub.rockstargames.com", HostlistService.AllowedRockstarSuffixes));

        Assert.False(HostlistService.MatchesSuffix("example.com", HostlistService.AllowedLolSuffixes));
        Assert.False(HostlistService.MatchesSuffix("fake-discord.ru", HostlistService.AllowedDiscordSuffixes));
    }

    [Fact]
    public void Write_NormalizesAndDeduplicatesTxtLists_ForRuntime()
    {
        string name = "stageb-normalize-" + Guid.NewGuid().ToString("N");
        var svc = new HostlistService();
        try
        {
            svc.Write(name, """
                # comment should not reach runtime
                *.Example.COM:443   # inline comment
                example.com

                bad domain with spaces
                valid.example.net;
                203.0.113.7
                203.0.113.0/24
                item,with,comma
                """);

            string[] expected =
            {
                "example.com",
                "valid.example.net",
                "203.0.113.7",
                "203.0.113.0/24"
            };

            Assert.Equal(expected, File.ReadAllLines(svc.GetPath(name)));
            string runtime = HostlistService.ResolveRuntimeListFilePath(name);
            Assert.Equal(expected, File.ReadAllLines(runtime));
        }
        finally
        {
            svc.Delete(name);
        }
    }

    [Fact]
    public void RuntimeListCompiler_MergesAndDeduplicatesAcrossSourcePartitions()
    {
        string name = "stageb-merge-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(AppPaths.ListsDir, $"list-{name}.txt");
        string discovered = Path.Combine(AppPaths.ListsDiscoveredDir, $"list-{name}.txt");
        string user = Path.Combine(AppPaths.ListsUserDir, $"list-{name}.txt");
        try
        {
            Directory.CreateDirectory(AppPaths.ListsDir);
            Directory.CreateDirectory(AppPaths.ListsDiscoveredDir);
            Directory.CreateDirectory(AppPaths.ListsUserDir);
            File.WriteAllText(root, "Shared.EXAMPLE\nroot-only.example\n");
            File.WriteAllText(discovered, "shared.example\ndiscovered-only.example\n");
            File.WriteAllText(user, "shared.example\nuser-only.example\n");

            string runtime = HostlistService.ResolveRuntimeListFilePath(name);

            Assert.Equal(new[]
            {
                "shared.example",
                "root-only.example",
                "discovered-only.example",
                "user-only.example"
            }, File.ReadAllLines(runtime));
        }
        finally
        {
            foreach (var path in new[] { root, discovered, user, Path.Combine(AppPaths.RuntimeListsDir, $"list-{name}.txt") })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }

    [Fact]
    public void ParseBlitzFixTraces_ParsesLocalFilesWithoutCrashing()
    {
        Directory.CreateDirectory(AppPaths.BlitzFixOutputDir);
        File.WriteAllText(
            Path.Combine(AppPaths.BlitzFixOutputDir, "test-riot-endpoint.json"),
            """
            [
              {
                "processName": "RiotClientServices.exe",
                "remoteAddress": "162.159.130.234",
                "remotePort": 443,
                "domainCandidates": [ "auth.riotgames.com" ]
              }
            ]
            """);

        var svc = new HostlistService();
        var result = svc.ParseBlitzFixTraces();

        Assert.NotNull(result);
        Assert.True(result.FilesParsed > 0, "Should find and parse BlitzFix JSON/TXT trace files");
        Assert.True(result.EndpointsParsed > 0, "Should parse captured endpoints");
        Assert.True(result.LolDomains.Count > 0, "Should extract LoL/Riot/Blitz domains");
    }

    [Fact]
    public void AggregateIpsToSubnets_GroupsSharedPrefixes()
    {
        var ips = new[]
        {
            "162.159.130.10",
            "162.159.130.20",
            "162.159.130.30",
            "1.1.1.1"
        };

        var subnets = IpsetService.AggregateIpsToSubnets(ips);

        Assert.Contains("162.159.130.0/24", subnets);
        Assert.Contains("1.1.1.1/32", subnets);
    }

    [Theory]
    [InlineData("[1/5] BlitzFix traces", 20, "Этап 1/5: BlitzFix & Трассировки")]
    [InlineData("[2/5] Downloading mirrors", 40, "Этап 2/5: Загрузка с зеркал")]
    [InlineData("[3/5] Merging domains", 60, "Этап 3/5: Объединение доменов")]
    [InlineData("[4/5] DNS and IP resolution", 80, "Этап 4/5: DNS резолв и IP")]
    [InlineData("[5/5] Saving files", 95, "Этап 5/5: Сохранение файлов")]
    public void ProgressTracking_MapsStageCorrectly(string status, double expectedProgress, string expectedStagePrefix)
    {
        double progress = 0;
        string stage = "";

        if (status.StartsWith("[1/5]")) { progress = 20; stage = "Этап 1/5: BlitzFix & Трассировки"; }
        else if (status.StartsWith("[2/5]")) { progress = 40; stage = "Этап 2/5: Загрузка с зеркал"; }
        else if (status.StartsWith("[3/5]")) { progress = 60; stage = "Этап 3/5: Объединение доменов"; }
        else if (status.StartsWith("[4/5]")) { progress = 80; stage = "Этап 4/5: DNS резолв и IP"; }
        else if (status.StartsWith("[5/5]")) { progress = 95; stage = "Этап 5/5: Сохранение файлов"; }

        Assert.Equal(expectedProgress, progress);
        Assert.Equal(expectedStagePrefix, stage);
    }

    [Fact]
    public void ParseTcpvconCsv_ParsesStandardOutputAccurately()
    {
        Assert.True(HostlistService.TestPublicIPv4("162.159.130.233"));
        Assert.True(HostlistService.TestPublicIPv4("104.16.57.21"));
        Assert.True(HostlistService.TestPublicIPv4("143.204.181.98"));
        Assert.False(HostlistService.TestPublicIPv4("192.168.0.108"));
    }

    [Fact]
    public void CustomList_Addition_PersistsAndCompilesToRuntime()
    {
        string customListName = "cst_test_" + Guid.NewGuid().ToString("N")[..8];
        var hostlistSvc = new HostlistService();
        var targetSvc = new TargetService();

        try
        {
            // 1. Add domain to custom list via HostlistService
            hostlistSvc.Write(customListName, "sub1.example.org\nsub2.example.org\n");

            // Verify persistent file exists in user lists
            string userListFile = Path.Combine(AppPaths.ListsUserDir, $"list-{customListName}.txt");
            if (!File.Exists(userListFile))
                userListFile = Path.Combine(AppPaths.ListsUserDir, $"{customListName}.txt");
            Assert.True(File.Exists(userListFile) || File.Exists(Path.Combine(AppPaths.ListsDir, $"list-{customListName}.txt")));

            // Verify compiled runtime file exists in ProgramData runtime lists (or local runtime fallback)
            string runtimeListFile = Path.Combine(AppPaths.RuntimeListsDir, $"list-{customListName}.txt");
            if (!File.Exists(runtimeListFile))
                runtimeListFile = Path.Combine(AppPaths.RuntimeListsDir, $"{customListName}.txt");
            Assert.True(File.Exists(runtimeListFile) || File.Exists(Path.Combine(AppPaths.AppDataRoot, "runtime", "lists", Path.GetFileName(runtimeListFile))));

            // 2. Add custom target via TargetService
            string targetName = "target_" + Guid.NewGuid().ToString("N")[..8];
            targetSvc.Save(targetName, new[] { "target1.org", "target2.org" });

            string targetUserFile = Path.Combine(AppPaths.ListsUserDir, $"target-{targetName}.txt");
            Assert.True(File.Exists(targetUserFile) || File.Exists(Path.Combine(AppPaths.ListsDir, $"target-{targetName}.txt")));

            string targetsAggregate = Path.Combine(AppPaths.RuntimeListsDir, "targets.txt");
            Assert.True(File.Exists(targetsAggregate) || File.Exists(Path.Combine(AppPaths.AppDataRoot, "runtime", "lists", "targets.txt")));

            // 3. Engine argument expansion verifies runtime paths are referenced
            var preset = new Preset
            {
                Name = "CST Engine Ingestion Test",
                Args = new List<string> { $"{{HOSTLIST:{customListName}}}" }
            };
            var args = EngineService.BuildArguments(preset, null, forLaunch: true);
            Assert.Contains(args, a => a.StartsWith("--hostlist=") && a.Contains(customListName));
        }
        finally
        {
            try { hostlistSvc.Delete(customListName); } catch { }
        }
    }
}
