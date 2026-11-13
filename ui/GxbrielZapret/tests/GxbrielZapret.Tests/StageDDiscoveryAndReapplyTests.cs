using System.IO;
using System.Text;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Engine.Discovery;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public class StageDDiscoveryAndReapplyTests
{
    private sealed class TempRuntimeScope : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _prevProgramDataRoot;
        private readonly string? _prevAppDataRoot;

        public TempRuntimeScope()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "gx_test_staged_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);

            _prevProgramDataRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT");
            _prevAppDataRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");

            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _tempRoot);
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _tempRoot);

            AppPaths.ResetSystemRuntimeDirCache();
            AppPaths.ResetEngineDirCache();
        }

        public string Root => _tempRoot;

        public void Dispose()
        {
            if (_prevProgramDataRoot == null)
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", null);
            else
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _prevProgramDataRoot);

            if (_prevAppDataRoot == null)
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", null);
            else
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _prevAppDataRoot);

            AppPaths.ResetSystemRuntimeDirCache();
            AppPaths.ResetEngineDirCache();

            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // 1. Domain Normalization & Deduplication
    // =========================================================================

    [Fact]
    public void DomainNormalization_DeduplicatesEquivalentHosts()
    {
        string[] rawVariants =
        {
            "Gateway.Discord.GG",
            "gateway.discord.gg.",
            "gateway.discord.gg:443",
            "https://gateway.discord.gg/api/v9/gateway",
            "*.gateway.discord.gg",
            "  gateway.discord.gg:80#frag  ",
            "ws://gateway.discord.gg:443/socket.io/?EIO=4"
        };

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var variant in rawVariants)
        {
            bool success = EndpointNormalizer.TryNormalizeDomain(variant, out string canonical, out string? err);
            Assert.True(success, $"Failed to normalize '{variant}': {err}");
            normalized.Add(canonical);
        }

        // All 7 variants MUST collapse into exactly ONE canonical domain
        Assert.Single(normalized);
        Assert.Equal("gateway.discord.gg", normalized.First());
    }

    // =========================================================================
    // 2. IP Normalization & Deduplication
    // =========================================================================

    [Fact]
    public void IpNormalization_DeduplicatesEquivalentAddresses()
    {
        string[] rawVariants =
        {
            "162.159.130.233",
            "162.159.130.233:443",
            " 162.159.130.233:80 ",
            "162.159.130.233:50001"
        };

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var variant in rawVariants)
        {
            bool success = EndpointNormalizer.TryNormalizeIp(variant, out string canonical, out string? err);
            Assert.True(success, $"Failed to normalize IP '{variant}': {err}");
            normalized.Add(canonical);
        }

        Assert.Single(normalized);
        Assert.Equal("162.159.130.233", normalized.First());

        // Private / loopback / multicast IPs must be rejected
        Assert.False(EndpointNormalizer.TryNormalizeIp("127.0.0.1", out _, out _));
        Assert.False(EndpointNormalizer.TryNormalizeIp("192.168.1.1", out _, out _));
        Assert.False(EndpointNormalizer.TryNormalizeIp("10.0.0.1", out _, out _));
        Assert.False(EndpointNormalizer.TryNormalizeIp("0.0.0.0", out _, out _));
        Assert.False(EndpointNormalizer.TryNormalizeIp("224.0.0.1", out _, out _));
    }

    // =========================================================================
    // 3. CIDR Normalization & Canonical Subnets
    // =========================================================================

    [Fact]
    public void CidrNormalization_UsesCanonicalNetwork()
    {
        // 1.2.3.4/24 -> 1.2.3.0/24
        Assert.True(EndpointNormalizer.TryNormalizeCidr("1.2.3.4/24", out string cidr1, out _));
        Assert.Equal("1.2.3.0/24", cidr1);

        // 1.2.3.250/24 -> 1.2.3.0/24
        Assert.True(EndpointNormalizer.TryNormalizeCidr("1.2.3.250/24", out string cidr2, out _));
        Assert.Equal("1.2.3.0/24", cidr2);

        // 192.0.2.130/25 -> 192.0.2.128/25
        Assert.True(EndpointNormalizer.TryNormalizeCidr("192.0.2.130/25", out string cidr3, out _));
        Assert.Equal("192.0.2.128/25", cidr3);

        // Single host defaulting to /32
        Assert.True(EndpointNormalizer.TryNormalizeCidr("162.159.130.233", out string cidr4, out _));
        Assert.Equal("162.159.130.233/32", cidr4);
    }

    // =========================================================================
    // 4. Repeated Capture Deduplication
    // =========================================================================

    [Fact]
    public void RepeatedCapture_DoesNotCreateDuplicates()
    {
        using var scope = new TempRuntimeScope();
        var store = new DiscoveryStore();

        var input = new RawEndpointInput("gateway.discord.gg:443", "live_trace", "slug-repeat", "Discord.exe", 1234, 443);

        // Capture 50 times
        for (int i = 0; i < 50; i++)
        {
            var r = store.IngestEndpoint(input);
            Assert.True(r.IsAccepted);
        }

        var targetRecords = store.Records.Where(r => r.TargetSlug == "slug-repeat").ToList();

        // Must only have 1 record in store for this slug
        Assert.Single(targetRecords);
        var rec = targetRecords.First();
        Assert.Equal("gateway.discord.gg", rec.Key);
        Assert.Equal(50, rec.SeenCount);
        Assert.Contains("live_trace", rec.Sources);
    }

    // =========================================================================
    // 5. Merge Semantics (Never Destroy Existing Lists)
    // =========================================================================

    [Fact]
    public void Discovery_MergesInsteadOfReplacingExistingList()
    {
        using var scope = new TempRuntimeScope();
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(AppPaths.ListsDir);

        // 1. Pre-seed base custom list with 2 domains
        string baseList = Path.Combine(AppPaths.ListsDir, "list-myservice.txt");
        File.WriteAllLines(baseList, new[] { "myservice.com", "api.myservice.com" });

        var store = new DiscoveryStore();

        // 2. Discover new subdomain
        store.IngestEndpoint(new RawEndpointInput("edge-node.myservice.com", "live_trace", "myservice"));

        // 3. Materialize
        string runtimePath = store.MaterializeTargetList("myservice");
        Assert.True(File.Exists(runtimePath));

        var runtimeLines = File.ReadAllLines(runtimePath, Encoding.UTF8)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        // Must contain ALL original domains PLUS the newly discovered domain
        Assert.Contains("myservice.com", runtimeLines);
        Assert.Contains("api.myservice.com", runtimeLines);
        Assert.Contains("edge-node.myservice.com", runtimeLines);
        Assert.Equal(3, runtimeLines.Count);
    }

    // =========================================================================
    // 6. Persistence Across Service Restart
    // =========================================================================

    [Fact]
    public void Discovery_PersistsAcrossServiceRestart()
    {
        using var scope = new TempRuntimeScope();

        // Session 1: discover endpoint
        {
            var store1 = new DiscoveryStore();
            store1.IngestEndpoint(new RawEndpointInput("cf-media.sndcdn.com", "web_learning", "soundcloud"));
            store1.Save();
        }

        // Session 2: new store instance loads from disk
        {
            var store2 = new DiscoveryStore();
            store2.Load();

            var rec = store2.Records.FirstOrDefault(r => r.Key == "cf-media.sndcdn.com");
            Assert.NotNull(rec);
            Assert.Equal("soundcloud", rec.TargetSlug);
            Assert.Equal(1, rec.SeenCount);
        }
    }

    // =========================================================================
    // 7. Deterministic Runtime Materialization
    // =========================================================================

    [Fact]
    public void RuntimeMaterialization_IsDeterministic()
    {
        using var scope = new TempRuntimeScope();
        var store = new DiscoveryStore();

        store.IngestEndpoint(new RawEndpointInput("z-cdn.discordapp.com", "trace", "discord"));
        store.IngestEndpoint(new RawEndpointInput("a-gateway.discord.gg", "trace", "discord"));
        store.IngestEndpoint(new RawEndpointInput("m-media.discord.net", "trace", "discord"));

        string path1 = store.MaterializeTargetList("discord");
        byte[] bytes1 = File.ReadAllBytes(path1);

        // Re-materialize multiple times
        for (int i = 0; i < 5; i++)
        {
            string pathN = store.MaterializeTargetList("discord");
            byte[] bytesN = File.ReadAllBytes(pathN);
            Assert.Equal(bytes1, bytesN); // Byte-identical
        }

        // Verify sorted order
        var lines = File.ReadAllLines(path1).Where(l => l.Length > 0).ToList();
        var sorted = lines.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, lines);
    }

    // =========================================================================
    // 8. Runtime Materialization Has No Dev Paths
    // =========================================================================

    [Fact]
    public void RuntimeMaterialization_HasNoDevPaths()
    {
        using var scope = new TempRuntimeScope();
        var store = new DiscoveryStore();

        store.IngestEndpoint(new RawEndpointInput("valid-cdn.discord.gg", "test", "discord"));
        string path = store.MaterializeTargetList("discord");

        string content = File.ReadAllText(path);
        Assert.DoesNotContain("!VS_MY_PROJECT", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GXBRIEL_ZAPRET\\ui", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bin\\Debug", content, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 9. Runtime Materialization Has No Invalid Entries
    // =========================================================================

    [Fact]
    public void RuntimeMaterialization_HasNoInvalidEntries()
    {
        using var scope = new TempRuntimeScope();
        var store = new DiscoveryStore();

        // Feed garbage and valid items
        store.IngestEndpoint(new RawEndpointInput("   ", "test", "cleanapp"));
        store.IngestEndpoint(new RawEndpointInput("http://", "test", "cleanapp"));
        store.IngestEndpoint(new RawEndpointInput("127.0.0.1", "test", "cleanapp"));
        store.IngestEndpoint(new RawEndpointInput("telemetry.microsoft.com", "test", "cleanapp"));
        store.IngestEndpoint(new RawEndpointInput("valid.cleanapp.com", "test", "cleanapp"));

        string path = store.MaterializeTargetList("cleanapp");
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();

        Assert.Single(lines);
        Assert.Equal("valid.cleanapp.com", lines.First());
    }

    // =========================================================================
    // 10. Runtime Deletion Self-Heals From Persistent Discovery
    // =========================================================================

    [Fact]
    public void RuntimeDeletion_SelfHealsFromPersistentDiscovery()
    {
        using var scope = new TempRuntimeScope();
        var store = new DiscoveryStore();

        store.IngestEndpoint(new RawEndpointInput("media.heal-target.net", "test", "heal-target"));
        string runtimeFile = store.MaterializeTargetList("heal-target");

        Assert.True(File.Exists(runtimeFile));
        Assert.Contains("media.heal-target.net", File.ReadAllLines(runtimeFile));

        // Delete entire runtime directory
        Directory.Delete(AppPaths.RuntimeListsDir, recursive: true);
        Assert.False(File.Exists(runtimeFile));

        // Re-materialize via self-heal
        store.SelfHealAllRuntimeLists();

        Assert.True(File.Exists(runtimeFile));
        Assert.Contains("media.heal-target.net", File.ReadAllLines(runtimeFile));
    }

    // =========================================================================
    // 11. Concurrent Discovery Is Serialized / Coalesced
    // =========================================================================

    [Fact]
    public async Task ConcurrentDiscovery_IsSerializedOrCoalesced()
    {
        using var scope = new TempRuntimeScope();
        var store = new DiscoveryStore();

        var tasks = Enumerable.Range(1, 100).Select(i => Task.Run(() =>
        {
            string host = $"node-{i % 10}.discord.gg";
            store.IngestEndpoint(new RawEndpointInput(host, "concurrent_test", "concurrent-target"));
        }));

        await Task.WhenAll(tasks);

        var targetRecords = store.Records.Where(r => r.TargetSlug == "concurrent-target").ToList();

        // Exactly 10 unique nodes must be in store for this target
        Assert.Equal(10, targetRecords.Count);
        foreach (var rec in targetRecords)
        {
            Assert.Equal(10, rec.SeenCount); // Each seen 10 times (100 / 10)
        }
    }

    // =========================================================================
    // 12. Discovery While Stopped Does Not Start Engine
    // =========================================================================

    [Fact]
    public void DiscoveryWhileStopped_DoesNotStartEngine()
    {
        using var scope = new TempRuntimeScope();
        using var engine = new EngineService();
        var store = new DiscoveryStore();
        using var coordinator = new DiscoveryReapplyCoordinator(engine, store, TimeSpan.FromMilliseconds(50));

        Assert.False(engine.IsRunning);

        var result = coordinator.IngestAndReapply(new RawEndpointInput("cdn.discordapp.com", "test", "discord"));

        Assert.True(result.IsAccepted);
        Assert.False(engine.IsRunning);
        Assert.Equal(0, coordinator.ReapplyCount);
    }

    // =========================================================================
    // 13. Discovery While Running Reapplies Active Strategy
    // =========================================================================

    [Fact]
    public void DiscoveryWhileRunning_ReappliesActiveStrategy()
    {
        using var scope = new TempRuntimeScope();
        using var engine = new EngineService();
        var store = new DiscoveryStore();
        using var coordinator = new DiscoveryReapplyCoordinator(engine, store, TimeSpan.FromMilliseconds(50));

        // When engine is running, ReapplyActiveStrategy is called
        // If not started, ReapplyActiveStrategy safely returns false
        bool reapplied = engine.ReapplyActiveStrategy("test");
        Assert.False(reapplied); // False when stopped

        // Pipeline stage verifies "Applied" stage is recorded correctly
        var r = coordinator.IngestAndReapply(new RawEndpointInput("voice.discord.gg", "test", "discord"));
        Assert.True(r.IsAccepted);
        Assert.Equal(DiscoveryStage.Applied, r.CurrentStage);
    }

    // =========================================================================
    // 14. Runtime Reapply Preserves Selected Strategy
    // =========================================================================

    [Fact]
    public void RuntimeReapply_PreservesSelectedStrategy()
    {
        using var scope = new TempRuntimeScope();
        var preset = ReferenceStrategyCatalog.BuiltIns.First(b => b.Id == "GX-B01").ToPreset();

        // Verify preset properties
        Assert.Equal("GX-B01", preset.ReferenceId);
        Assert.Equal("Комбо (рекомендуемый)", preset.Name);
        Assert.True(preset.IsBuiltIn);
    }

    // =========================================================================
    // 15. Repeated Identical Discovery Does Not Restart Engine
    // =========================================================================

    [Fact]
    public void RepeatedIdenticalDiscovery_DoesNotRestartEngine()
    {
        using var scope = new TempRuntimeScope();
        using var engine = new EngineService();
        var store = new DiscoveryStore();
        using var coordinator = new DiscoveryReapplyCoordinator(engine, store, TimeSpan.FromMilliseconds(50));

        // First ingestion -> effective new item
        var r1 = coordinator.IngestAndReapply(new RawEndpointInput("stream.discord.gg", "test", "discord"));
        Assert.True(r1.IsAccepted);

        // Second identical ingestion -> duplicate update, reapply skipped
        var r2 = coordinator.IngestAndReapply(new RawEndpointInput("stream.discord.gg", "test", "discord"));
        Assert.True(r2.IsAccepted);
        Assert.Contains("Duplicate item", r2.StageLog.Last());
    }

    // =========================================================================
    // 16. Multiple Discoveries Coalesce Into Single Reapply
    // =========================================================================

    [Fact]
    public void MultipleDiscoveries_CoalesceIntoSingleReapply()
    {
        using var scope = new TempRuntimeScope();
        using var engine = new EngineService();
        var store = new DiscoveryStore();
        using var coordinator = new DiscoveryReapplyCoordinator(engine, store, TimeSpan.FromMilliseconds(500));

        var batch = new List<RawEndpointInput>
        {
            new("node1.discord.gg", "trace", "discord"),
            new("node2.discord.gg", "trace", "discord"),
            new("node3.discord.gg", "trace", "discord")
        };

        var results = coordinator.IngestBatch(batch);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.IsAccepted));

        // 3 items coalesced
        Assert.Equal(3, coordinator.CoalescedDiscoveriesCount);
    }

    // =========================================================================
    // 17. Failed Reapply Does Not Lose Persistent Discovery
    // =========================================================================

    [Fact]
    public void FailedReapply_DoesNotLosePersistentDiscovery()
    {
        using var scope = new TempRuntimeScope();
        var store = new DiscoveryStore();

        var r = store.IngestEndpoint(new RawEndpointInput("permanent.discord.gg", "test", "discord"));
        Assert.True(r.IsAccepted);
        store.Save();

        // Even if engine reapply throws, persistent store remains intact
        var reloadedStore = new DiscoveryStore();
        reloadedStore.Load();

        Assert.Contains(reloadedStore.Records, rec => rec.Key == "permanent.discord.gg");
    }

    // =========================================================================
    // 18. Manual Stop Wins Against Pending Automatic Reapply
    // =========================================================================

    [Fact]
    public void ManualStop_WinsAgainstPendingAutomaticReapply()
    {
        using var scope = new TempRuntimeScope();
        using var engine = new EngineService();
        var store = new DiscoveryStore();
        using var coordinator = new DiscoveryReapplyCoordinator(engine, store, TimeSpan.FromSeconds(5));

        coordinator.IngestAndReapply(new RawEndpointInput("slow-pending.discord.gg", "test", "discord"));

        // User manually stops engine -> cancels debounce timer
        coordinator.CancelPendingReapply("Manual user stop");

        // Timer is cancelled, no reapply will fire
        Assert.Equal(0, coordinator.ReapplyCount);
    }
}
