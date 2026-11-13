using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace GxbrielZapret.Services.Network;

/// <summary>
/// Result of network hop and TSPU boundary distance measurement.
/// </summary>
public sealed record HopMeasurement(
    string Host,
    int ServerHops,
    int EstimatedTspuHops,
    int RecommendedFakeTtl,
    int RecommendedAutoTtlDelta,
    bool DestinationReached,
    long RoundtripMs);

/// <summary>
/// Measures network hop distance to target servers via TTL-incrementing ICMP pings.
/// Helps determine optimal --dpi-desync-ttl and --dpi-desync-autottl without brute-forcing 10+ combinations.
/// </summary>
public static class HopDistanceService
{
    private const int MaxHops = 30;
    private const int ProbeTimeoutMs = 1500;

    /// <summary>
    /// Measures network hops to a destination host.
    /// Estimates the distance to local TSPU/middlebox and recommends optimal fake packet TTL.
    /// </summary>
    public static async Task<HopMeasurement> MeasureAsync(string host, CancellationToken ct)
    {
        using var ping = new Ping();
        var buffer = new byte[32];
        Random.Shared.NextBytes(buffer);

        int serverHops = 0;
        int highestExpiredHop = 0;
        bool reached = false;
        long totalMs = 0;

        for (int ttl = 1; ttl <= MaxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            var options = new PingOptions(ttl, dontFragment: true);
            try
            {
                var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(ProbeTimeoutMs), buffer, options, ct)
                    .ConfigureAwait(false);

                if (reply.Status == IPStatus.TtlExpired)
                {
                    highestExpiredHop = ttl;
                }
                else if (reply.Status == IPStatus.Success)
                {
                    serverHops = ttl;
                    reached = true;
                    totalMs = reply.RoundtripTime;
                    break;
                }
            }
            catch (PingException) { /* Intermediate hop dropped or silent */ }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* Ignore temporary socket glitches */ }
        }

        if (!reached)
        {
            // If destination filtered ICMP Echo, server is typically 1-2 hops past the last responding router
            serverHops = highestExpiredHop > 0 ? highestExpiredHop + 2 : 12;
        }

        // On Russian networks, TSPU typically sits within the ISP's boundary (hops 2-5).
        // If ServerHops is small (e.g. 5-7), TSPU is around hop 2-3.
        // If ServerHops is large (e.g. 15-20), TSPU is around hop 3-4.
        int tspuHops = serverHops <= 6 ? 2 : (serverHops <= 12 ? 3 : 4);

        // Recommended Fake TTL must reach TSPU but expire before reaching the destination server
        int recommendedTtl = Math.Clamp(tspuHops, 2, Math.Max(2, serverHops - 2));

        // AutoTTL delta: how many hops before destination to start triggering
        int autoTtlDelta = Math.Clamp(serverHops - tspuHops, 1, 4);

        return new HopMeasurement(
            host,
            serverHops,
            tspuHops,
            recommendedTtl,
            autoTtlDelta,
            reached,
            totalMs);
    }
}
