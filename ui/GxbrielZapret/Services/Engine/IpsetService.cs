using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Engine;

/// <summary>
/// Builds an IP-set for IP-based bypass (the lever for hard IP-blocks where
/// domain/SNI matching is useless). It resolves domains with the bundled <c>mdig.exe</c>
/// and aggregates addresses into CIDR subnets with <c>ip2net.exe</c>, or falls back
/// to resilient native .NET DNS resolution and subnet aggregation.
/// </summary>
public sealed class IpsetService
{
    public sealed record IpsetResult(string Path, int Subnets);

    /// <summary>Resolve Discord domains and write an aggregated ipset file. Returns the path + subnet count.</summary>
    public Task<IpsetResult> BuildDiscordIpsetAsync(IEnumerable<string> domains, CancellationToken ct) =>
        BuildIpsetForDomainsAsync("discord", domains, ct);

    /// <summary>Resolve arbitrary domain list and write aggregated ipset file (ipset-{name}.txt).</summary>
    public async Task<IpsetResult> BuildIpsetForDomainsAsync(string targetName, IEnumerable<string> domains, CancellationToken ct)
    {
        var list = domains.Select(d => d.Trim())
                          .Where(d => d.Length > 0 && !d.StartsWith('#'))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        if (list.Count == 0)
            throw new InvalidOperationException(Loc.T("Список доменов пуст."));

        string baseName = targetName.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase)
            ? targetName["ipset-".Length..]
            : targetName;

        string targetFile = AppPaths.IpsetFile(baseName);
        string? parent = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        List<string> subnets;

        // 1. Try external mdig + ip2net if engine tools exist
        if (File.Exists(AppPaths.MdigExe) && File.Exists(AppPaths.Ip2NetExe))
        {
            try
            {
                string ips = await ResolveWithMdigAsync(list, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ips))
                {
                    string agg = await AggregateWithIp2NetAsync(ips, ct).ConfigureAwait(false);
                    subnets = agg.Replace("\r\n", "\n").Split('\n')
                                 .Select(s => s.Trim())
                                 .Where(s => s.Length > 0)
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList();

                    if (subnets.Count > 0)
                    {
                        await File.WriteAllTextAsync(targetFile, string.Join("\n", subnets) + "\n", ct).ConfigureAwait(false);
                        return new IpsetResult(targetFile, subnets.Count);
                    }
                }
            }
            catch
            {
                // Fall back to native resolution below
            }
        }

        // 2. Native C# DNS resolution and subnet aggregation fallback
        var resolvedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in list)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(domain, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                foreach (var addr in addrs)
                {
                    string ipStr = addr.ToString();
                    if (HostlistService.TestPublicIPv4(ipStr))
                    {
                        resolvedIps.Add(ipStr);
                    }
                }
            }
            catch
            {
                // Domain might not resolve, continue with next
            }
        }

        subnets = AggregateIpsToSubnets(resolvedIps);
        if (subnets.Count == 0 && File.Exists(targetFile))
        {
            // Keep existing if resolution failed completely
            var existing = File.ReadAllLines(targetFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return new IpsetResult(targetFile, existing.Count);
        }

        await File.WriteAllTextAsync(targetFile, string.Join("\n", subnets) + "\n", ct).ConfigureAwait(false);
        return new IpsetResult(targetFile, subnets.Count);
    }

    /// <summary>
    /// Aggregates individual IPv4 addresses into CIDR /24 subnets or /32 addresses.
    /// </summary>
    public static List<string> AggregateIpsToSubnets(IEnumerable<string> ips)
    {
        var list = new List<IPAddress>();
        foreach (var ip in ips)
        {
            if (IPAddress.TryParse(ip.Trim(), out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                list.Add(parsed);
            }
        }

        if (list.Count == 0) return new List<string>();

        // Group by /24 prefix: if 2 or more IPs share the same /24, aggregate to /24
        var groups = list.GroupBy(ip =>
        {
            var b = ip.GetAddressBytes();
            return $"{b[0]}.{b[1]}.{b[2]}";
        });

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            if (g.Count() >= 2)
            {
                result.Add($"{g.Key}.0/24");
            }
            else
            {
                foreach (var ip in g)
                {
                    result.Add($"{ip}/32");
                }
            }
        }

        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Task<string> ResolveWithMdigAsync(IReadOnlyList<string> domains, CancellationToken ct) =>
        RunPipeAsync(AppPaths.MdigExe, "--family=4", string.Join("\n", domains) + "\n", ct);

    private static Task<string> AggregateWithIp2NetAsync(string ips, CancellationToken ct) =>
        RunPipeAsync(AppPaths.Ip2NetExe, "-4", ips, ct);

    /// <summary>Run a tool feeding <paramref name="stdin"/> and returning its stdout.</summary>
    private static async Task<string> RunPipeAsync(string exe, string args, string stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = AppPaths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.ASCII,
            StandardInputEncoding = Encoding.ASCII,
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        try
        {
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            p.StandardInput.Close();
            string outp = await outTask;
            string err = await errTask;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(outp))
                throw new InvalidOperationException(
                    Loc.T("{0} завершился с кодом {1}", Path.GetFileName(exe), p.ExitCode) +
                    (err.Trim().Length > 0 ? $": {err.Trim()}" : "."));
            return outp;
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
