using System.Net.NetworkInformation;

namespace GxbrielZapret.Services.Network;

/// <summary>Chooses the real internet adapter when virtual VPN adapters are also present.</summary>
public static class PrimaryNetworkSelector
{
    public static NetworkInterface? Find()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsUsable)
                .OrderByDescending(Score)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool IsUsable(NetworkInterface n)
    {
        if (n.OperationalStatus != OperationalStatus.Up) return false;
        if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        return n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }

    private static int Score(NetworkInterface n)
    {
        int score = n.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet => 1000,
            NetworkInterfaceType.Wireless80211 => 950,
            _ => 100
        };

        string text = $"{n.Name} {n.Description}";
        if (text.Contains("radmin", StringComparison.OrdinalIgnoreCase)
            || text.Contains("virtual", StringComparison.OrdinalIgnoreCase)
            || text.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
            || text.Contains("default switch", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vpn", StringComparison.OrdinalIgnoreCase))
            score -= 900;

        try
        {
            var ipv4 = n.GetIPProperties().GetIPv4Properties();
            if (ipv4 is not null) score -= Math.Min(ipv4.Index, 999);
        }
        catch { }

        return score;
    }
}
