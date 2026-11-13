using System;
using System.Collections.Generic;
using System.Net;

namespace GxbrielZapret.Services.Engine;

/// <summary>
/// Filters out DNS resolvers, OS telemetry, crash reporters and trackers
/// from live captured endpoints so only relevant application domains/IPs are recorded.
/// </summary>
public static class NoiseFilter
{
    private static readonly HashSet<string> PublicDnsIps = new(StringComparer.OrdinalIgnoreCase)
    {
        "1.1.1.1", "1.0.0.1", "162.159.36.1", "162.159.46.1", // Cloudflare DNS
        "8.8.8.8", "8.8.4.4",                                  // Google DNS
        "9.9.9.9", "149.112.112.112",                          // Quad9
        "77.88.8.8", "77.88.8.1",                              // Yandex DNS
        "208.67.222.222", "208.67.220.220",                    // OpenDNS
        "94.140.14.14", "94.140.15.15"                         // AdGuard DNS
    };

    private static readonly string[] TelemetryAndNoiseSuffixes =
    {
        // Microsoft / Windows telemetry
        "telemetry.microsoft.com",
        "events.data.microsoft.com",
        "settings-win.data.microsoft.com",
        "activity.windows.com",
        "msftconnecttest.com",
        "msftncsi.com",
        "windowsupdate.com",
        "smartscreen.microsoft.com",
        "wns.windows.com",

        // Analytics & crash tracking
        "crashlytics.com",
        "app-measurement.com",
        "google-analytics.com",
        "googletagmanager.com",
        "browser-intake-datadoghq.com",
        "sentry.io",
        "bugsnag.com",
        "doubleclick.net"
    };

    /// <summary>
    /// Checks whether an IPv4 address is noise (e.g. public DNS resolver or non-public address).
    /// </summary>
    public static bool IsIpNoise(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return true;
        string clean = ip.Trim();

        if (!HostlistService.TestPublicIPv4(clean)) return true;
        if (PublicDnsIps.Contains(clean)) return true;

        return false;
    }

    /// <summary>
    /// Checks whether a domain is noise (e.g. telemetry, crash reporter, analytics).
    /// </summary>
    public static bool IsDomainNoise(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return true;
        string clean = HostlistService.CleanDomain(domain);

        if (!HostlistService.IsValidDomain(clean)) return true;

        foreach (var suffix in TelemetryAndNoiseSuffixes)
        {
            if (clean.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                clean.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
