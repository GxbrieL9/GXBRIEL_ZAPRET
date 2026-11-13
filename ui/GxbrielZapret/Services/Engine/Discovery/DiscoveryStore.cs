using System.IO;
using System.Text;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Engine.Discovery;

/// <summary>
/// Source-of-truth persistent store for accepted discovered endpoints and compiler for runtime lists.
/// Enforces ADD / MERGE / DEDUP semantics: never destroys existing hostlists.
/// </summary>
public sealed class DiscoveryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly Dictionary<string, DiscoveredRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DiscoveredRecord> Records
    {
        get
        {
            lock (_lock)
            {
                return _records.Values.ToList();
            }
        }
    }

    public DiscoveryStore()
    {
        Load();
    }

    private static string StoreFilePath => Path.Combine(AppPaths.DiscoveryDir, "accepted_discovery.json");

    public void Load()
    {
        lock (_lock)
        {
            _records.Clear();
            string path = StoreFilePath;
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<DiscoveredRecord>>(json, JsonOpts);
                if (list is not null)
                {
                    foreach (var rec in list)
                    {
                        if (!string.IsNullOrWhiteSpace(rec.Key))
                        {
                            string mapKey = BuildRecordKey(rec.TargetSlug, rec.Key);
                            _records[mapKey] = rec;
                        }
                    }
                }
            }
            catch { /* non-fatal corrupted recovery */ }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                AppPaths.EnsureCreated();
                Directory.CreateDirectory(AppPaths.DiscoveryDir);

                string path = StoreFilePath;
                string tmpPath = path + ".tmp." + Guid.NewGuid().ToString("N");

                string json = JsonSerializer.Serialize(_records.Values.ToList(), JsonOpts);
                File.WriteAllText(tmpPath, json, Encoding.UTF8);

                // Atomic replacement
                File.Move(tmpPath, path, overwrite: true);
            }
            catch { /* non-fatal */ }
        }
    }

    private static string BuildRecordKey(string targetSlug, string canonicalValue) =>
        $"{targetSlug.Trim().ToLowerInvariant()}::{canonicalValue.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Ingests a raw endpoint through the full 9-stage pipeline.
    /// Returns the pipeline execution trace and indicates if an effective new endpoint was accepted.
    /// </summary>
    public PipelineResult IngestEndpoint(RawEndpointInput input)
    {
        var result = new PipelineResult
        {
            RawInput = input.RawValue ?? "",
            Source = input.Source,
            TargetSlug = NormalizeTargetSlug(input.TargetSlug),
            ObservedAt = input.Timestamp ?? DateTime.UtcNow
        };

        // STAGE 1: Observed
        result.RecordStage(DiscoveryStage.Observed, $"Raw input '{input.RawValue}' from {input.Source}");

        if (string.IsNullOrWhiteSpace(input.RawValue))
        {
            result.FilterReason = "Empty raw input";
            result.RecordStage(DiscoveryStage.Filtered, result.FilterReason);
            return result;
        }

        // STAGE 2: Parsed
        string raw = input.RawValue.Trim();
        result.RecordStage(DiscoveryStage.Parsed, $"Extracted raw value '{raw}' (process: {input.ProcessName ?? "none"}, port: {input.Port})");

        // STAGE 3: Normalized
        string? cidrErr = null;
        string? ipErr = null;
        string? domErr = null;

        if (raw.Contains('/') && (raw.Contains('.') || raw.Contains(':')) && EndpointNormalizer.TryNormalizeCidr(raw, out string canonicalCidr, out cidrErr))
        {
            result.CanonicalValue = canonicalCidr;
            result.Kind = EndpointKind.Cidr;
            result.RecordStage(DiscoveryStage.Normalized, $"CIDR canonical network: '{canonicalCidr}'");
        }
        else if (EndpointNormalizer.TryNormalizeIp(raw, out string canonicalIp, out ipErr))
        {
            result.CanonicalValue = canonicalIp;
            result.Kind = canonicalIp.Contains(':') ? EndpointKind.IPv6 : EndpointKind.IPv4;
            result.RecordStage(DiscoveryStage.Normalized, $"IP canonical address: '{canonicalIp}' ({result.Kind})");
        }
        else if (EndpointNormalizer.TryNormalizeDomain(raw, out string canonicalDomain, out domErr))
        {
            result.CanonicalValue = canonicalDomain;
            result.Kind = EndpointKind.Domain;
            result.RecordStage(DiscoveryStage.Normalized, $"Domain canonical representation: '{canonicalDomain}'");
        }
        else
        {
            result.FilterReason = $"Normalization failed: {domErr ?? ipErr ?? cidrErr ?? "unknown format"}";
            result.RecordStage(DiscoveryStage.Filtered, result.FilterReason);
            return result;
        }

        // STAGE 4: Candidate
        result.RecordStage(DiscoveryStage.Candidate, $"Candidate {result.Kind} '{result.CanonicalValue}' bound to target slug '{result.TargetSlug}'");

        // STAGE 5: Filtered (Garbage, noise, telemetry checks)
        if (result.Kind == EndpointKind.Domain)
        {
            if (NoiseFilter.IsDomainNoise(result.CanonicalValue))
            {
                result.FilterReason = "Telemetry, analytics or OS noise domain";
                result.RecordStage(DiscoveryStage.Filtered, result.FilterReason);
                return result;
            }
        }
        else if (result.Kind is EndpointKind.IPv4 or EndpointKind.IPv6)
        {
            if (NoiseFilter.IsIpNoise(result.CanonicalValue))
            {
                result.FilterReason = "Public DNS resolver or noise IP";
                result.RecordStage(DiscoveryStage.Filtered, result.FilterReason);
                return result;
            }
        }

        // STAGE 6: Accepted
        result.IsAccepted = true;
        result.Confidence = CandidateConfidence.High;
        result.RecordStage(DiscoveryStage.Accepted, $"Accepted {result.Kind} '{result.CanonicalValue}' for target '{result.TargetSlug}'");

        // STAGE 7: Persistent (Merge & Dedup)
        bool isNewEffective;
        lock (_lock)
        {
            string mapKey = BuildRecordKey(result.TargetSlug, result.CanonicalValue);
            if (_records.TryGetValue(mapKey, out var existing))
            {
                existing.SeenCount++;
                existing.LastSeen = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(input.Source)) existing.Sources.Add(input.Source);
                if (!string.IsNullOrWhiteSpace(input.ProcessName)) existing.ProcessName = input.ProcessName;
                if (input.Port > 0) existing.RemotePort = input.Port;
                isNewEffective = false;
                result.RecordStage(DiscoveryStage.Persistent, $"Updated existing record (SeenCount: {existing.SeenCount}, LastSeen: {existing.LastSeen:u})");
            }
            else
            {
                var newRecord = new DiscoveredRecord
                {
                    Key = result.CanonicalValue,
                    Kind = result.Kind,
                    TargetSlug = result.TargetSlug,
                    FirstSeen = result.ObservedAt,
                    LastSeen = DateTime.UtcNow,
                    SeenCount = 1,
                    Sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { input.Source },
                    Confidence = result.Confidence,
                    ProcessName = input.ProcessName,
                    RemotePort = input.Port
                };
                _records[mapKey] = newRecord;
                isNewEffective = true;
                result.RecordStage(DiscoveryStage.Persistent, $"Created new persistent record '{mapKey}'");
            }

            Save();
        }

        // STAGE 8: RuntimeMaterialized
        if (isNewEffective)
        {
            string materializedFile = MaterializeTargetList(result.TargetSlug);
            result.RecordStage(DiscoveryStage.RuntimeMaterialized, $"Materialized list to '{materializedFile}'");
        }

        return result;
    }

    /// <summary>
    /// Materializes the combined (base + user + accepted discovery) list into runtime TXT.
    /// Never destroys existing list items — merges with strict deduplication and deterministic sorting.
    /// </summary>
    public string MaterializeTargetList(string targetSlug)
    {
        string slug = NormalizeTargetSlug(targetSlug);
        AppPaths.EnsureCreated();

        // 1. Gather all existing domains from source files (base lists, user lists, known lists)
        var sourcePaths = new List<string>
        {
            Path.Combine(AppPaths.ListsDir, $"list-{slug}.txt"),
            Path.Combine(AppPaths.ListsDir, $"{slug}.txt"),
            Path.Combine(AppPaths.ListsKnownDir, $"list-{slug}.txt"),
            Path.Combine(AppPaths.ListsKnownDir, $"{slug}.txt"),
            Path.Combine(AppPaths.ListsUserDir, $"list-{slug}.txt"),
            Path.Combine(AppPaths.ListsUserDir, $"{slug}.txt"),
            Path.Combine(AppPaths.ListsDiscoveredDir, $"list-{slug}.txt"),
            Path.Combine(AppPaths.ListsDiscoveredDir, $"{slug}.txt")
        };

        var allDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string clean = line.Trim();
                    if (clean.Length == 0 || clean.StartsWith('#')) continue;

                    if (EndpointNormalizer.TryNormalizeDomain(clean, out string normDom, out _))
                        allDomains.Add(normDom);
                    else if (EndpointNormalizer.TryNormalizeIp(clean, out string normIp, out _))
                        allIps.Add(normIp);
                    else if (EndpointNormalizer.TryNormalizeCidr(clean, out string normCidr, out _))
                        allIps.Add(normCidr);
                }
            }
        }

        // 2. Union with persistent accepted discovery records for this slug
        lock (_lock)
        {
            foreach (var rec in _records.Values.Where(r => r.TargetSlug.Equals(slug, StringComparison.OrdinalIgnoreCase)))
            {
                if (rec.Kind == EndpointKind.Domain)
                    allDomains.Add(rec.Key);
                else
                    allIps.Add(rec.Key);
            }
        }

        // 3. Write merged source-of-truth back to ListsDiscoveredDir (preserving persistence)
        Directory.CreateDirectory(AppPaths.ListsDiscoveredDir);
        if (allDomains.Count > 0)
        {
            string discoveredSource = Path.Combine(AppPaths.ListsDiscoveredDir, $"list-{slug}.txt");
            WriteDeterministicList(discoveredSource, allDomains);
            string altSource = Path.Combine(AppPaths.ListsDiscoveredDir, $"{slug}.txt");
            WriteDeterministicList(altSource, allDomains);
        }

        if (allIps.Count > 0)
        {
            string discoveredIpsetSource = Path.Combine(AppPaths.ListsDiscoveredDir, $"ipset-{slug}.txt");
            WriteDeterministicList(discoveredIpsetSource, allIps);
        }

        // 4. Materialize to Runtime directory
        Directory.CreateDirectory(AppPaths.RuntimeListsDir);
        string runtimeTarget = Path.Combine(AppPaths.RuntimeListsDir, $"list-{slug}.txt");
        WriteDeterministicList(runtimeTarget, allDomains);
        string altRuntimeTarget = Path.Combine(AppPaths.RuntimeListsDir, $"{slug}.txt");
        WriteDeterministicList(altRuntimeTarget, allDomains);

        if (allIps.Count > 0)
        {
            string runtimeIpset = Path.Combine(AppPaths.RuntimeListsDir, $"ipset-{slug}.txt");
            WriteDeterministicList(runtimeIpset, allIps);
        }

        return runtimeTarget;
    }

    /// <summary>
    /// Self-heals all runtime lists from persistent sources + discovery store if runtime was wiped.
    /// </summary>
    public void SelfHealAllRuntimeLists()
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(AppPaths.RuntimeListsDir);

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "discord", "youtube", "exclude", "general", "lol", "rockstar"
        };

        lock (_lock)
        {
            foreach (var rec in _records.Values)
                slugs.Add(rec.TargetSlug);
        }

        foreach (var slug in slugs)
        {
            MaterializeTargetList(slug);
        }
    }

    private static readonly object _fileWriteLock = new();

    /// <summary>
    /// Atomically writes a deterministic, sorted, duplicate-free list file with no dev paths.
    /// Thread-safe and resilient against concurrent write collisions.
    /// </summary>
    public static void WriteDeterministicList(string targetPath, IEnumerable<string> items)
    {
        var sorted = items
            .Where(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith('#'))
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => !s.Contains("!vs_my_project") && !s.Contains("gxbriel_zapret\\ui"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        lock (_fileWriteLock)
        {
            string tmp = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllLines(tmp, sorted, Encoding.UTF8);

                // Atomic replace with retry for Windows file lock release
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        File.Move(tmp, targetPath, overwrite: true);
                        break;
                    }
                    catch (Exception) when (attempt < 5)
                    {
                        Thread.Sleep(20 * attempt);
                    }
                }
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
        }
    }

    public static string NormalizeTargetSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "general";
        string s = slug.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        if (s.StartsWith("list-")) s = s["list-".Length..];
        if (s == "league-of-legends" || s == "riot-client" || s == "blitz") return "lol";
        if (s == "rockstar-games") return "rockstar";
        return s;
    }
}
