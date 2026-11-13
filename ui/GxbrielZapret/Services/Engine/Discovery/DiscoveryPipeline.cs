using System.Text.Json.Serialization;
using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Engine.Discovery;

public enum DiscoveryStage
{
    Observed,
    Parsed,
    Normalized,
    Candidate,
    Filtered,
    Accepted,
    Persistent,
    RuntimeMaterialized,
    Applied
}

public enum EndpointKind
{
    Domain,
    IPv4,
    IPv6,
    Cidr
}

/// <summary>
/// Represents the raw input observed from any discovery source.
/// </summary>
public sealed record RawEndpointInput(
    string RawValue,
    string Source,
    string? TargetSlug = null,
    string? ProcessName = null,
    int Pid = 0,
    int Port = 0,
    string? Protocol = null,
    DateTime? Timestamp = null
);

/// <summary>
/// Full execution trace of an endpoint traversing the 9-stage pipeline.
/// </summary>
public sealed class PipelineResult
{
    public string RawInput { get; init; } = "";
    public string CanonicalValue { get; set; } = "";
    public EndpointKind Kind { get; set; }
    public string TargetSlug { get; set; } = "general";
    public string Source { get; init; } = "unknown";
    public DiscoveryStage CurrentStage { get; set; } = DiscoveryStage.Observed;
    public bool IsAccepted { get; set; }
    public string? FilterReason { get; set; }
    public CandidateConfidence Confidence { get; set; } = CandidateConfidence.High;
    public DateTime ObservedAt { get; init; } = DateTime.UtcNow;
    public List<string> StageLog { get; } = new();

    public void RecordStage(DiscoveryStage stage, string detail)
    {
        CurrentStage = stage;
        StageLog.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {stage}: {detail}");
    }
}

/// <summary>
/// Persistent record stored in the discovery store.
/// </summary>
public sealed class DiscoveredRecord
{
    public string Key { get; set; } = "";
    public EndpointKind Kind { get; set; }
    public string TargetSlug { get; set; } = "general";
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public int SeenCount { get; set; } = 1;
    public HashSet<string> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CandidateConfidence Confidence { get; set; } = CandidateConfidence.High;
    public string? ProcessName { get; set; }
    public int RemotePort { get; set; }

    [JsonIgnore]
    public bool IsIp => Kind is EndpointKind.IPv4 or EndpointKind.IPv6 or EndpointKind.Cidr;
}
