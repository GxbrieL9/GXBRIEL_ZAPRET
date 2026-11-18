using System.IO;
using System.Net;
using System.Text.Json;

namespace GxbrielZapret.Services.Platform;

internal static class AntigravityGoogleNetworks
{
    // Snapshot from https://www.gstatic.com/ipranges/goog.json, 2026-11-15.
    // A genuine Google answer is ordinary DNS, not an external Smart DNS route.
    private static readonly Lazy<IPNetwork[]> Networks = new(() =>
    {
        using var stream = typeof(AntigravityGoogleNetworks).Assembly.GetManifestResourceStream("GXBRIEL_ZAPRET.google-ip-ranges.json")
            ?? throw new IOException("Отсутствует список сетей Google.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("prefixes").EnumerateArray().Select(p =>
            IPNetwork.Parse(p.TryGetProperty("ipv4Prefix", out var v4) ? v4.GetString()! : p.GetProperty("ipv6Prefix").GetString()!)).ToArray();
    });

    internal static bool Contains(IPAddress address) => Networks.Value.Any(n => n.Contains(
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));
}
