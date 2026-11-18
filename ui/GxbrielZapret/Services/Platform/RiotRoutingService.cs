using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GxbrielZapret.Services.Platform;

/// <summary>
/// Manages hosts-based routing optimization for Blitz desktop and Let's Encrypt CRL endpoints.
/// Riot Games and League of Legends domains are intentionally excluded from hosts overrides
/// to allow Atlas OS / native DNS resolution without pinned or unconfirmed edge IP overrides.
/// </summary>
public static class RiotRoutingService
{
    public const string HeaderTag = "# GXBRIEL_ZAPRET - Game Routing Fix (Riot & Blitz)";
    public const string FooterTag = "# END GXBRIEL_ZAPRET - Game Routing Fix";
    public static readonly string[] LegacyHeaders =
    {
        "# GXBRIEL_ZAPRET - Game Routing Fix (Riot, Blitz & Splice)",
        "# GXBRIEL_ZAPRET - Game Routing Fix (Riot & Blitz)",
        "# GXBRIEL_ZAPRET - Riot Games Cloudflare Fix",
        "# GXBRIEL_ZAPRET - Riot clientconfig route (verified HTTPS)",
        "# GXBRIEL_ZAPRET - Riot clientconfig route"
    };
    public static readonly string[] LegacyFooters =
    {
        "# END GXBRIEL_ZAPRET - Riot Games Fix",
        "# END GXBRIEL_ZAPRET - Riot clientconfig route"
    };

    public static readonly (string Ip, string Domain)[] Mappings =
    {
        // Blitz Desktop App
        ("185.111.111.155", "blitz.gg"),
        ("185.111.111.155", "blitzapp.gg"),
        ("185.111.111.155", "blitz-desktop.blitz.gg"),
        ("185.111.111.155", "blitz-desktop.b-cdn.net"),
        ("185.111.111.155", "blitz-cdn.blitz.gg"),
        ("185.111.111.155", "blitz-main.blitz.gg"),
        ("185.111.111.155", "blitz-deltas.blitz.gg"),
        ("185.111.111.155", "blitz-datalake-server.b-cdn.net"),
        ("185.111.111.155", "blitz-cdn-images.b-cdn.net"),
        ("185.111.111.155", "blitz-cdn-plain.b-cdn.net"),
        ("185.111.111.155", "blitz-data-api.b-cdn.net"),

        // Let's Encrypt Certificate Revocation Lists (fixes Steam, Discord & Web PKI when local DNS/VPN sinkholes lencr.org)
        ("8.47.69.5", "yr2.c.lencr.org"),
        ("8.47.69.5", "r3.c.lencr.org"),
        ("8.47.69.5", "r10.c.lencr.org"),
        ("8.47.69.5", "r11.c.lencr.org"),
        ("8.47.69.5", "e1.c.lencr.org"),
        ("8.47.69.5", "e5.c.lencr.org"),
        ("8.47.69.5", "e6.c.lencr.org"),
        ("23.32.25.249", "x1.c.lencr.org")
    };

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern int DnsFlushResolverCache();

    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static bool ApplyHostsFix(string? hostsPath = null, bool flushDns = true, bool ensureVanguard = true)
    {
        try
        {
            if (ensureVanguard && hostsPath == null)
            {
                EnsureVanguardService();
            }

            string path = hostsPath ?? HostsPath;
            if (!File.Exists(path)) return false;

            string content = File.ReadAllText(path, Encoding.UTF8);
            if (content.Contains(HeaderTag, StringComparison.OrdinalIgnoreCase) &&
                content.Contains("blitz-desktop.blitz.gg", StringComparison.OrdinalIgnoreCase) &&
                content.Contains("yr2.c.lencr.org", StringComparison.OrdinalIgnoreCase) &&
                !content.Contains("clientconfig.rpg.riotgames.com", StringComparison.OrdinalIgnoreCase) &&
                !content.Contains("riotgames.com", StringComparison.OrdinalIgnoreCase) &&
                !content.Contains("pvp.net", StringComparison.OrdinalIgnoreCase) &&
                !content.Contains("api.splice.com", StringComparison.OrdinalIgnoreCase))
            {
                if (flushDns) FlushDns();
                return true;
            }

            // Clean up any legacy or partial block first
            RemoveHostsFix(path, flushDns: false);

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(HeaderTag);
            foreach (var (ip, domain) in Mappings)
            {
                sb.AppendLine($"{ip,-16} {domain}");
            }
            sb.AppendLine(FooterTag);

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            if (flushDns) FlushDns();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureVanguardService()
    {
        try
        {
            string vgcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Vanguard", "vgc.exe");
            if (!File.Exists(vgcPath)) return;

            var psiConfig = new ProcessStartInfo("sc.exe", "config vgc start= auto")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psiConfig)?.WaitForExit(3000);

            var psiStart = new ProcessStartInfo("sc.exe", "start vgc")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psiStart)?.WaitForExit(3000);
        }
        catch { }
    }

    public static bool RemoveHostsFix(string? hostsPath = null, bool flushDns = true)
    {
        try
        {
            string path = hostsPath ?? HostsPath;
            if (!File.Exists(path)) return false;

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            var filtered = new List<string>();
            bool inBlock = false;
            bool changed = false;
            int blockLineCount = 0;

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                bool isHeader = trimmed.Equals(HeaderTag, StringComparison.OrdinalIgnoreCase) ||
                    Array.Exists(LegacyHeaders, h => trimmed.Equals(h, StringComparison.OrdinalIgnoreCase));
                if (isHeader)
                {
                    inBlock = true;
                    blockLineCount = 0;
                    changed = true;
                    continue;
                }
                bool isFooter = trimmed.Equals(FooterTag, StringComparison.OrdinalIgnoreCase) ||
                    Array.Exists(LegacyFooters, f => trimmed.Equals(f, StringComparison.OrdinalIgnoreCase));
                if (isFooter)
                {
                    inBlock = false;
                    changed = true;
                    continue;
                }
                if (inBlock)
                {
                    blockLineCount++;
                    if (blockLineCount > 150)
                    {
                        inBlock = false;
                        filtered.Add(line);
                    }
                    else
                    {
                        changed = true;
                    }
                    continue;
                }
                filtered.Add(line);
            }

            if (!changed) return true;

            File.WriteAllLines(path, filtered, Encoding.UTF8);
            if (flushDns) FlushDns();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void FlushDns()
    {
        try
        {
            DnsFlushResolverCache();
        }
        catch { }
    }
}
