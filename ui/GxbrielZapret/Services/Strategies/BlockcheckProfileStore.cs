using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;

namespace GxbrielZapret.Services.Strategies;

public sealed class BlockcheckNetworkProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string NetworkFingerprint { get; set; } = "";
    public AutoScope Scope { get; set; }
    public string OptionsKey { get; set; } = "";
    public CanonicalProfile Profile { get; set; } = new();
    public StrategyProbeState Verification { get; set; } = new();
    public ApplicationFlowResult? Flows { get; set; }

    public Preset ToPreset(string path, string? optionsKey = null)
    {
        var preset = Profile.ToPreset();
        preset.FilePath = path;
        preset.BoundNetworkFingerprint = NetworkFingerprint;
        preset.BoundScope = Scope;
        preset.VerifiedFlowSummary = Flows?.Summary ?? "";
        preset.LastTestedAt = Verification.LastTestedAt;
        preset.ProbeResults = new(Verification.Probes, StringComparer.OrdinalIgnoreCase);
        preset.PassCount = Verification.PassCount;
        preset.TotalCount = Verification.TotalCount;
        preset.LatencyMs = Verification.LatencyMs;
        if (optionsKey is not null && optionsKey != OptionsKey)
        {
            preset.Status = "UNTESTED";
            preset.Reason = "runtime_options_changed_requires_recheck";
            preset.ProbeResults.Clear();
            preset.PassCount = 0;
            preset.VerifiedFlowSummary = "Настройки изменились; видео и Gateway требуют повторной проверки.";
        }
        return preset;
    }
}

public static class BlockcheckProfileStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string OptionsKey(bool bypassAll, bool disableQuic) => $"all={bypassAll};quicOff={disableQuic}";
    public static string ProfilePath(string network, AutoScope scope)
    {
        if (string.IsNullOrWhiteSpace(network) || network == "default" || !Enum.IsDefined(scope))
            throw new ArgumentException("Нужны определённая сеть и режим подбора.");
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(network)))[..24];
        return Path.Combine(AppPaths.NetworksDir, id, "blockcheck2-" + scope.ToString().ToLowerInvariant() + ".json");
    }

    public static Preset SaveVerified(string network, AutoScope scope, string optionsKey, Preset candidate,
        Canonical5ProbeResult result, Action checkpoint)
    {
        var goals = scope.GoalHosts();
        if (!result.IsWork || result.Flows?.Passes(scope) != true || result.TotalCount != goals.Length ||
            result.PassCount != goals.Length || result.Probes.Count != goals.Length ||
            !goals.All(host => result.Probes.TryGetValue(host, out bool ok) && ok) ||
            result.StrategyId != CanonicalProfile.ComputeFingerprint(candidate.Args))
            throw new InvalidOperationException("Профиль не прошёл полную проверку выбранных сервисов.");
        lock (Gate)
        {
            checkpoint();
            string path = ProfilePath(network, scope);
            var profile = CanonicalProfile.FromPreset(candidate);
            profile.Name = $"blockcheck2 — {scope.Title()} — {network}";
            profile.Tagline = scope.Title();
            profile.Description = "Единый профиль этой сети. " + result.Flows.Summary;
            profile.Provenance = "UPSTREAM_BLOCKCHECK2";
            profile.Status = "WORK";
            profile.Reason = $"manual_{scope.Title()}_{goals.Length}_of_{goals.Length}";
            var document = new BlockcheckNetworkProfile
            {
                NetworkFingerprint = network, Scope = scope, OptionsKey = optionsKey, Profile = profile, Flows = result.Flows,
                Verification = new StrategyProbeState
                {
                    ValidationVersion = StrategyProbeState.CurrentValidationVersion, Id = profile.Fingerprint,
                    Status = "WORK", Reason = profile.Reason, LastTestedAt = DateTime.UtcNow,
                    Probes = new(result.Probes, StringComparer.OrdinalIgnoreCase), PassCount = result.PassCount,
                    TotalCount = result.TotalCount, LatencyMs = result.LatencyMs, NetworkFingerprint = network
                }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, document, Json);
                    stream.Flush(true);
                }
                checkpoint();
                if (File.Exists(path)) File.Replace(temporary, path,
                    Read(path, network, scope) is null ? path + ".corrupt-" + Guid.NewGuid().ToString("N") : path + ".bak");
                else File.Move(temporary, path);
                return document.ToPreset(path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static IReadOnlyList<Preset> Load(string network, string? optionsKey = null)
    {
        if (string.IsNullOrWhiteSpace(network) || network == "default") return Array.Empty<Preset>();
        lock (Gate)
        {
            var result = new List<Preset>();
            foreach (var scope in Enum.GetValues<AutoScope>())
            {
                string path = ProfilePath(network, scope);
                var document = Read(path, network, scope) ?? Read(path + ".bak", network, scope);
                if (document is not null) result.Add(document.ToPreset(path, optionsKey));
            }
            return result;
        }
    }

    private static BlockcheckNetworkProfile? Read(string path, string network, AutoScope scope)
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) return null;
            var value = JsonSerializer.Deserialize<BlockcheckNetworkProfile>(File.ReadAllText(path), Json);
            var goals = scope.GoalHosts();
            return value is { SchemaVersion: 1, Profile: not null, Verification: not null } &&
                value.NetworkFingerprint == network && value.Scope == scope && value.Flows?.Passes(scope) == true &&
                value.Profile.Provenance == "UPSTREAM_BLOCKCHECK2" && value.Profile.Status == "WORK" &&
                value.Profile.Fingerprint == CanonicalProfile.ComputeFingerprint(value.Profile.Args) &&
                value.Verification.ValidationVersion == StrategyProbeState.CurrentValidationVersion &&
                value.Verification.NetworkFingerprint == network && value.Verification.Id == value.Profile.Fingerprint &&
                value.Verification.Status == "WORK" && value.Verification.PassCount == goals.Length && value.Verification.TotalCount == goals.Length &&
                value.Verification.Probes.Count == goals.Length && goals.All(h => value.Verification.Probes.TryGetValue(h, out bool ok) && ok)
                ? value : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NullReferenceException)
        { System.Diagnostics.Trace.WriteLine("[blockcheck2] Profile read: " + ex.Message); return null; }
    }
}
