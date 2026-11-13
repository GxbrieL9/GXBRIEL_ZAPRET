using System.Text.Json.Serialization;

namespace GxbrielZapret.Models;

/// <summary>
/// Detailed outcome record of a strategy test on a specific network.
/// Part of Persistent Network Knowledge (Master ТЗ v2, Section 5).
/// </summary>
public sealed class StrategyTestRecord
{
    [JsonPropertyName("strategyName")]
    public string StrategyName { get; set; } = string.Empty;

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "UNKNOWN"; // WORK, BROKEN, UNKNOWN

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "untested";

    [JsonPropertyName("lastTested")]
    public DateTime LastTested { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("successCount")]
    public int SuccessCount { get; set; }

    [JsonPropertyName("failureCount")]
    public int FailureCount { get; set; }

    [JsonPropertyName("averageLatencyMs")]
    public double AverageLatencyMs { get; set; }

    // Service-specific reachability verdicts
    [JsonPropertyName("discordOk")]
    public bool DiscordOk { get; set; }

    [JsonPropertyName("youtubeOk")]
    public bool YoutubeOk { get; set; }

    [JsonPropertyName("telegramOk")]
    public bool TelegramOk { get; set; }

    [JsonPropertyName("serviceDetails")]
    public Dictionary<string, string> ServiceDetails { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persistent knowledge entity representing a known network and its proven strategies.
/// Persisted in %LOCALAPPDATA%\GXBRIEL_ZAPRET\networks\{fingerprint}.json.
/// Survives application upgrades, rebuilds, and executable replacements.
/// </summary>
public sealed class NetworkProfile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("networkFingerprint")]
    public string NetworkFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("networkName")]
    public string NetworkName { get; set; } = string.Empty;

    [JsonPropertyName("adapterName")]
    public string AdapterName { get; set; } = string.Empty;

    [JsonPropertyName("gatewayMac")]
    public string GatewayMac { get; set; } = string.Empty;

    [JsonPropertyName("gatewayIp")]
    public string GatewayIp { get; set; } = string.Empty;

    [JsonPropertyName("localSubnet")]
    public string LocalSubnet { get; set; } = string.Empty;

    [JsonPropertyName("dnsServers")]
    public List<string> DnsServers { get; set; } = new();

    [JsonPropertyName("firstSeen")]
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("bestStrategyName")]
    public string? BestStrategyName { get; set; }

    [JsonPropertyName("bestStrategyFingerprint")]
    public string? BestStrategyFingerprint { get; set; }

    /// <summary>Map of strategy fingerprint or name to test records.</summary>
    [JsonPropertyName("testedStrategies")]
    public Dictionary<string, StrategyTestRecord> TestedStrategies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Strategy names generated or optimized specifically for this network.</summary>
    [JsonPropertyName("generatedStrategies")]
    public List<string> GeneratedStrategies { get; set; } = new();

    /// <summary>Discovered endpoints or targets seen on this network.</summary>
    [JsonPropertyName("discoveredEndpoints")]
    public List<string> DiscoveredEndpoints { get; set; } = new();

    public void RecordTest(string strategyName, string fingerprint, string status, string reason,
        bool discordOk, bool youtubeOk, bool telegramOk, double latencyMs = 0)
    {
        LastSeen = DateTime.UtcNow;
        string key = !string.IsNullOrWhiteSpace(fingerprint) ? fingerprint : strategyName;

        if (!TestedStrategies.TryGetValue(key, out var record))
        {
            record = new StrategyTestRecord
            {
                StrategyName = strategyName,
                Fingerprint = fingerprint
            };
            TestedStrategies[key] = record;
        }

        record.Status = status.ToUpperInvariant();
        record.Reason = reason;
        record.LastTested = DateTime.UtcNow;
        record.DiscordOk = discordOk;
        record.YoutubeOk = youtubeOk;
        record.TelegramOk = telegramOk;
        if (latencyMs > 0) record.AverageLatencyMs = latencyMs;

        if (status.Equals("WORK", StringComparison.OrdinalIgnoreCase))
        {
            record.SuccessCount++;
            // If this is the best so far, or no best was set, update best
            if (string.IsNullOrEmpty(BestStrategyName) || (discordOk && youtubeOk))
            {
                BestStrategyName = strategyName;
                BestStrategyFingerprint = fingerprint;
            }
        }
        else
        {
            record.FailureCount++;
            if (BestStrategyName == strategyName)
            {
                // Find another WORK strategy
                var alt = TestedStrategies.Values.FirstOrDefault(r => r.Status == "WORK");
                BestStrategyName = alt?.StrategyName;
                BestStrategyFingerprint = alt?.Fingerprint;
            }
        }
    }
}
