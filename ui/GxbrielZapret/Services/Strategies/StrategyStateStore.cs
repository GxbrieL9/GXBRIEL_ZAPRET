using System.IO;
using System.Diagnostics;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;

namespace GxbrielZapret.Services.Strategies;

public sealed class StrategyProbeState
{
    public const int CurrentValidationVersion = 6;
    public int ValidationVersion { get; set; }
    public string Id { get; set; } = "";
    public string Status { get; set; } = "UNTESTED"; // UNTESTED, TESTING, WORK, BROKEN, INCONCLUSIVE
    public string Reason { get; set; } = "unproven";
    public DateTime? LastTestedAt { get; set; }
    public Dictionary<string, bool> Probes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int PassCount { get; set; }
    public int TotalCount { get; set; } = 5;
    public long LatencyMs { get; set; }
    public string NetworkFingerprint { get; set; } = "";
    public ApplicationFlowResult? Flows { get; set; }
    public string? OptionsKey { get; set; }
}

public sealed class StrategyCatalogStateDocument
{
    public Dictionary<string, Dictionary<string, StrategyProbeState>> Networks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class StrategyStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly object FileLock = new();

    public static string StateFilePath => Path.Combine(AppPaths.ConfigDir, "strategy_catalog_state.json");
    public static string BackupFilePath => StateFilePath + ".bak";

    public static StrategyCatalogStateDocument Load()
    {
        lock (FileLock)
        {
            if (TryRead(StateFilePath, out var current)) return current!;
            if (TryRead(BackupFilePath, out var backup))
            {
                try
                {
                    if (File.Exists(StateFilePath))
                    {
                        Directory.CreateDirectory(AppPaths.BackupsDir);
                        File.Copy(StateFilePath, Path.Combine(AppPaths.BackupsDir, $"strategy-catalog-corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json"));
                    }
                    File.Copy(BackupFilePath, StateFilePath, overwrite: true);
                }
                catch (Exception ex) { Trace.WriteLine($"[StrategyStateStore] Restore failed: {ex.Message}"); }
                return backup!;
            }
            return new StrategyCatalogStateDocument();
        }
    }

    private static bool TryRead(string path, out StrategyCatalogStateDocument? document)
    {
        document = null;
        if (!File.Exists(path)) return false;
        try
        {
            var loaded = JsonSerializer.Deserialize<StrategyCatalogStateDocument>(File.ReadAllText(path), JsonOpts);
            if (loaded?.Networks is null) return false;
            document = new StrategyCatalogStateDocument();
            foreach (var network in loaded.Networks)
            {
                if (string.IsNullOrWhiteSpace(network.Key) || network.Value is null) continue;
                if (!document.Networks.TryGetValue(network.Key, out var states))
                    document.Networks[network.Key] = states = new(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in network.Value)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null) continue;
                    var state = entry.Value;
                    var probes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (var probe in state.Probes ?? new()) probes[probe.Key] = probe.Value;
                    state.Probes = probes;
                    states[entry.Key] = state;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[StrategyStateStore] Read failed: {ex.Message}");
            return false;
        }
    }

    public static Dictionary<string, StrategyProbeState> GetForNetwork(string networkFp)
    {
        if (string.IsNullOrWhiteSpace(networkFp)) networkFp = "default";
        var doc = Load();
        if (doc.Networks.TryGetValue(networkFp, out var states))
            return states;

        return new Dictionary<string, StrategyProbeState>(StringComparer.OrdinalIgnoreCase);
    }

    public static bool SaveNetworkStates(string networkFp, IReadOnlyCollection<StrategyProbeState> states)
    {
        if (string.IsNullOrWhiteSpace(networkFp)) networkFp = "default";
        lock (FileLock)
        {
            try
            {
                var doc = Load();
                var netMap = new Dictionary<string, StrategyProbeState>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in states)
                {
                    if (string.IsNullOrWhiteSpace(s.Id)) return false;
                    s.NetworkFingerprint = networkFp;
                    netMap[s.Id] = s;
                }
                doc.Networks[networkFp] = netMap;

                return SaveDocument(doc);
            }
            catch (Exception ex) { Trace.WriteLine($"[StrategyStateStore] Save failed: {ex.Message}"); return false; }
        }
    }

    public static bool SaveSingleState(string networkFp, StrategyProbeState state)
    {
        if (string.IsNullOrWhiteSpace(state.Id)) return false;
        if (string.IsNullOrWhiteSpace(networkFp)) networkFp = "default";
        lock (FileLock)
        {
            try
            {
                var doc = Load();
                if (!doc.Networks.TryGetValue(networkFp, out var netMap))
                {
                    netMap = new Dictionary<string, StrategyProbeState>(StringComparer.OrdinalIgnoreCase);
                    doc.Networks[networkFp] = netMap;
                }
                state.NetworkFingerprint = networkFp;
                netMap[state.Id] = state;

                return SaveDocument(doc);
            }
            catch (Exception ex) { Trace.WriteLine($"[StrategyStateStore] Save failed: {ex.Message}"); return false; }
        }
    }

    private static bool SaveDocument(StrategyCatalogStateDocument document)
    {
        string temporary = StateFilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOpts);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(StateFilePath)) File.Replace(temporary, StateFilePath, BackupFilePath);
            else File.Move(temporary, StateFilePath);
            return true;
        }
        catch (Exception ex) { Trace.WriteLine($"[StrategyStateStore] Save failed: {ex.Message}"); return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { Trace.WriteLine($"[StrategyStateStore] Cleanup failed: {ex.Message}"); }
        }
    }
}
