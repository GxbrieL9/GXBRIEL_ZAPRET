using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GxbrielZapret.Models;

/// <summary>
/// Canonical strategy profile represented on disk as a .gxprofile.json manifest.
/// Pool location: profiles/work (for verified WORK) or profiles/broken (for all others).
/// </summary>
public sealed class CanonicalProfile
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "winws2";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("tagline")]
    public string Tagline { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("reference_id")]
    public string? ReferenceId { get; set; }

    [JsonPropertyName("provenance")]
    public string? Provenance { get; set; }

    [JsonPropertyName("target_services")]
    public string? TargetServices { get; set; }

    [JsonPropertyName("compatible_lists")]
    public List<string> CompatibleLists { get; set; } = new();

    [JsonPropertyName("lists")]
    public List<string> Lists { get; set; } = new();

    [JsonPropertyName("source")]
    public string Source { get; set; } = "zapret2ui_builtin";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "BROKEN";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "unproven";

    [JsonPropertyName("compatibility")]
    public string Compatibility { get; set; } = "";

    [JsonPropertyName("compatibility_notes")]
    public string CompatibilityNotes { get; set; } = "";

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("list_fingerprint")]
    public string ListFingerprint { get; set; } = "";

    [JsonPropertyName("last_test")]
    public string? LastTest { get; set; }

    [JsonPropertyName("is_builtin")]
    public bool IsBuiltIn { get; set; }

    [JsonPropertyName("is_recommended")]
    public bool IsRecommended { get; set; }

    [JsonPropertyName("is_generated")]
    public bool IsGenerated { get; set; }

    [JsonPropertyName("is_auto_leaderboard")]
    public bool IsAutoLeaderboard { get; set; }

    [JsonPropertyName("uses_hostlist")]
    public bool UsesHostlist { get; set; }

    [JsonIgnore]
    public string? FilePath { get; set; }

    /// <summary>
    /// Computes canonical SHA-256 fingerprint for winws2 arguments.
    /// Normalizes whitespace and case to ensure identical strategies match.
    /// </summary>
    public static string ComputeFingerprint(IEnumerable<string> args)
    {
        var normalized = args
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Select(a => Regex.Replace(a, @"\s+", " "))
            .Select(a => a.ToLowerInvariant());

        string joined = string.Join("\n", normalized);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes canonical SHA-256 fingerprint for referenced hostlists.
    /// </summary>
    public static string ComputeListFingerprint(IEnumerable<string> lists)
    {
        var sorted = lists
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .Distinct()
            .OrderBy(l => l, StringComparer.Ordinal);

        string joined = string.Join(",", sorted);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts list names referenced in args tokens like {HOSTLIST:name} or {EXCLUDE:name}.
    /// </summary>
    public static List<string> ExtractReferencedLists(IEnumerable<string> args)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in args)
        {
            var matches = Regex.Matches(a, @"\{(HOSTLIST|EXCLUDE|IPSET):([a-zA-Z0-9_\-\.]+)\}");
            foreach (Match m in matches)
            {
                if (m.Groups.Count >= 3)
                    result.Add(m.Groups[2].Value.ToLowerInvariant());
            }
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static CanonicalProfile FromPreset(Preset p, string status = "BROKEN", string reason = "unproven", string source = "zapret2ui_builtin")
    {
        var lists = ExtractReferencedLists(p.Args);
        string fp = ComputeFingerprint(p.Args);
        string listFp = ComputeListFingerprint(lists);

        string effectiveStatus = !string.IsNullOrWhiteSpace(p.Status) ? p.Status : status;
        string effectiveReason = !string.IsNullOrWhiteSpace(p.Reason) ? p.Reason : reason;

        return new CanonicalProfile
        {
            ReferenceId = p.ReferenceId,
            Provenance = p.Provenance,
            TargetServices = p.TargetServices,
            CompatibleLists = new List<string>(p.CompatibleLists),
            Name = p.Name,
            Description = p.Description,
            Tagline = p.Tagline,
            Args = new List<string>(p.Args),
            Lists = lists,
            Source = source,
            Status = effectiveStatus,
            Reason = effectiveReason,
            Compatibility = p.Compatibility,
            CompatibilityNotes = p.CompatibilityNotes,
            Fingerprint = fp,
            ListFingerprint = listFp,
            IsBuiltIn = p.IsBuiltIn,
            IsRecommended = p.IsRecommended,
            IsGenerated = p.IsGenerated,
            IsAutoLeaderboard = p.IsAutoLeaderboard,
            UsesHostlist = p.UsesHostlist,
            FilePath = p.FilePath
        };
    }

    public Preset ToPreset()
    {
        return new Preset
        {
            ReferenceId = ReferenceId,
            Provenance = Provenance,
            TargetServices = TargetServices,
            CompatibleLists = new List<string>(CompatibleLists),
            Name = Name,
            Description = Description,
            Tagline = Tagline,
            Args = new List<string>(Args),
            UsesHostlist = UsesHostlist,
            IsBuiltIn = IsBuiltIn,
            IsRecommended = IsRecommended,
            IsGenerated = IsGenerated,
            IsAutoLeaderboard = IsAutoLeaderboard,
            Status = Status,
            Reason = Reason,
            Compatibility = Compatibility,
            CompatibilityNotes = CompatibilityNotes,
            Fingerprint = Fingerprint,
            FilePath = FilePath
        };
    }

    public static CanonicalProfile? LoadFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            string json = File.ReadAllText(filePath);
            var profile = JsonSerializer.Deserialize<CanonicalProfile>(json, JsonOpts);
            if (profile is not null)
            {
                profile.FilePath = filePath;
                if (string.IsNullOrEmpty(profile.Fingerprint))
                    profile.Fingerprint = ComputeFingerprint(profile.Args);
                if (string.IsNullOrEmpty(profile.ListFingerprint))
                    profile.ListFingerprint = ComputeListFingerprint(profile.Lists);
            }
            return profile;
        }
        catch { return null; }
    }

    public string SaveToDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (string.IsNullOrEmpty(Fingerprint))
            Fingerprint = ComputeFingerprint(Args);
        if (string.IsNullOrEmpty(ListFingerprint))
            ListFingerprint = ComputeListFingerprint(Lists);

        string safeName = SanitizeFileName(Name);
        string shortFp = Fingerprint.Length >= 12 ? Fingerprint[..12] : Fingerprint;
        string fileName = $"{safeName}.{shortFp}.gxprofile.json";
        string targetPath = Path.Combine(directory, fileName);

        string json = JsonSerializer.Serialize(this, JsonOpts);
        string tmp = targetPath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, targetPath, overwrite: true);
        FilePath = targetPath;
        return targetPath;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (invalid.Contains(c) || c == ':' || c == '/' || c == '\\')
                sb.Append('_');
            else
                sb.Append(c);
        }
        string res = sb.ToString().Trim();
        return string.IsNullOrEmpty(res) ? "profile" : res;
    }
}
