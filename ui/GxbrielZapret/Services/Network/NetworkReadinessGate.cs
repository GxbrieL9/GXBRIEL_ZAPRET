using System.Net.NetworkInformation;
using System.Net.Sockets;
using GxbrielZapret.Localization;

namespace GxbrielZapret.Services.Network;

/// <summary>
/// Network readiness gate for system startup and post-reboot recovery.
/// Ensures the network stack, active interfaces, default route and DNS reachability
/// are verified before launching bypass engines or proxies.
/// </summary>
public static class NetworkReadinessGate
{
    private static Func<CancellationToken, Task<bool>>? _customProbe;

    /// <summary>
    /// Injects a custom probe for testing network readiness transitions without OS network changes.
    /// </summary>
    internal static void SetTestProbe(Func<CancellationToken, Task<bool>>? probe) => _customProbe = probe;

    /// <summary>
    /// Checks if any operational network interface with default gateway is currently available.
    /// </summary>
    public static bool IsInterfaceReady()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
                return false;

            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var ipProps = ni.GetIPProperties();
                if (ipProps == null) continue;

                // Interface has at least one unicast IP address and at least one gateway
                bool hasUnicast = ipProps.UnicastAddresses.Any(u =>
                    u.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
                bool hasGateway = ipProps.GatewayAddresses.Any(g => g.Address != null && !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6None));

                if (hasUnicast && (hasGateway || ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lightweight network connectivity probe: tests DNS/socket connectivity.
    /// </summary>
    public static async Task<bool> ProbeConnectivityAsync(CancellationToken ct = default)
    {
        if (_customProbe != null)
            return await _customProbe(ct);

        if (!IsInterfaceReady())
            return false;

        try
        {
            // Try resolving a well-known reliable host with a short timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            var entry = await System.Net.Dns.GetHostEntryAsync("one.one.one.one", cts.Token);
            return entry.AddressList.Length > 0;
        }
        catch
        {
            // Fallback: interface is operational and has gateway
            return IsInterfaceReady();
        }
    }

    /// <summary>
    /// Polls network status with backoff until network is ready or timeout expires.
    /// </summary>
    public static async Task<bool> WaitForNetworkReadyAsync(
        TimeSpan timeout,
        TimeSpan? initialPollInterval = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var poll = initialPollInterval ?? TimeSpan.FromMilliseconds(400);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        progress?.Report(Loc.T("Ожидание сети…"));

        while (stopwatch.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            if (await ProbeConnectivityAsync(ct))
            {
                progress?.Report(Loc.T("Сеть готова"));
                return true;
            }

            progress?.Report(Loc.T("Ожидание сети ({0}с)...", (int)(timeout - stopwatch.Elapsed).TotalSeconds));

            try
            {
                await Task.Delay(poll, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            // Linear backoff up to 1.5s
            if (poll < TimeSpan.FromMilliseconds(1500))
                poll = TimeSpan.FromMilliseconds(Math.Min(1500, poll.TotalMilliseconds + 200));
        }

        // Final check on timeout
        bool finalOk = await ProbeConnectivityAsync(ct);
        if (!finalOk)
        {
            progress?.Report(Loc.T("Таймаут ожидания сети"));
        }
        return finalOk;
    }
}
