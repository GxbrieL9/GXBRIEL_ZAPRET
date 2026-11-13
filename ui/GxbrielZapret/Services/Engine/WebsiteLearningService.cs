using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Engine;

/// <summary>
/// Handles live website network learning sessions.
/// Instead of static HTML scraping, observes live network footprint (DNS queries,
/// active TCP/UDP endpoints, SNI/hostnames) while the user interacts with the web service in any browser,
/// scores discovered candidate domains and IPs, and structures them for user review.
/// </summary>
public sealed class WebsiteLearningService
{
    public sealed record LearningProgress(
        int ElapsedSeconds,
        int DiscoveredDomainsCount,
        int DiscoveredIpsCount,
        int ActiveConnectionsCount,
        string LastDiscoveredHost
    );

    public sealed record LearningSessionResult(
        DiscoveryTarget Target,
        int DurationSeconds,
        List<DiscoveredItem> RecommendedItems,
        List<DiscoveredItem> PossibleItems,
        List<DiscoveredItem> ExcludedItems,
        int TotalEndpointsObserved,
        string SummaryMessage
    );

    private readonly object _sessionLock = new();
    private CancellationTokenSource? _activeSessionCts;
    private DiscoveryTarget? _currentTarget;
    private bool _isRunning;

    public bool IsSessionActive
    {
        get
        {
            lock (_sessionLock) { return _isRunning; }
        }
    }

    public DiscoveryTarget? CurrentTarget
    {
        get
        {
            lock (_sessionLock) { return _currentTarget; }
        }
    }

    // =========================================================================
    // Candidate Scoring Engine
    // =========================================================================

    private static readonly HashSet<string> KnownMediaCdnSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sndcdn.com", "soundcloud.com", "audio-cf.soundcloud.com", "cf-media.sndcdn.com",
        "cloudfront.net", "akamaized.net", "fastly.net", "cdn.discordapp.com",
        "rbxcdn.com", "epicgames.com", "akamaitechnologies.com", "edgekey.net"
    };

    /// <summary>
    /// Scores a discovered domain against the target's seed domains, known CDN patterns, and noise rules.
    /// </summary>
    public static (CandidateConfidence confidence, string reason) ScoreDomainCandidate(
        string domain,
        IReadOnlyList<string> seedDomains,
        int hitCount,
        DateTime firstSeen,
        DateTime sessionStart)
    {
        string clean = HostlistService.CleanDomain(domain);
        if (string.IsNullOrWhiteSpace(clean))
            return (CandidateConfidence.Low, "Пустой домен");

        // 1. Noise / Telemetry / Analytics check
        if (NoiseFilter.IsDomainNoise(clean))
            return (CandidateConfidence.Low, "Аналитика / Телеметрия / Шум");

        // 2. Exact match or subdomain of any seed domain
        foreach (var seed in seedDomains)
        {
            string cleanSeed = HostlistService.CleanDomain(seed);
            if (string.IsNullOrEmpty(cleanSeed)) continue;

            if (clean.Equals(cleanSeed, StringComparison.OrdinalIgnoreCase))
                return (CandidateConfidence.High, $"Основной seed-домен ({cleanSeed})");

            if (clean.EndsWith("." + cleanSeed, StringComparison.OrdinalIgnoreCase))
                return (CandidateConfidence.High, $"Поддомен {cleanSeed}");
        }

        // 3. Known related media CDNs (e.g. sndcdn for SoundCloud)
        foreach (var cdn in KnownMediaCdnSuffixes)
        {
            if (clean.Equals(cdn, StringComparison.OrdinalIgnoreCase) || clean.EndsWith("." + cdn, StringComparison.OrdinalIgnoreCase))
            {
                if (hitCount >= 2)
                    return (CandidateConfidence.High, $"Медиа / CDN сервис ({cdn})");
                return (CandidateConfidence.Medium, $"Сопутствующий CDN ({cdn})");
            }
        }

        // 4. Edge nodes and generic CDNs
        if (clean.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".akamaized.net", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".fastly.net", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".edgekey.net", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".edgesuite.net", StringComparison.OrdinalIgnoreCase))
        {
            return (CandidateConfidence.Medium, "Распределённый CDN узел");
        }

        // 5. If seen frequently right after user interaction
        if (hitCount >= 3)
            return (CandidateConfidence.Medium, $"Частые обращения во время сессии ({hitCount})");

        return (CandidateConfidence.Low, "Сторонний узел (низкая корреляция)");
    }

    /// <summary>
    /// Scores a discovered public IP address.
    /// </summary>
    public static (CandidateConfidence confidence, string reason) ScoreIpCandidate(
        string ip,
        int hitCount,
        bool associatedWithHighConfidenceDomain)
    {
        if (NoiseFilter.IsIpNoise(ip) || !HostlistService.TestPublicIPv4(ip))
            return (CandidateConfidence.Low, "Служебный или локальный IP");

        if (associatedWithHighConfidenceDomain)
            return (CandidateConfidence.High, "Разрешён из доверенного домена сервиса");

        if (hitCount >= 4)
            return (CandidateConfidence.Medium, $"Активный поток данных ({hitCount} пакетов/сокетов)");

        return (CandidateConfidence.Low, "Случайный сетевой эндпоинт");
    }

    // =========================================================================
    // Live Learning Session
    // =========================================================================

    public async Task<LearningSessionResult> StartLearningSessionAsync(
        DiscoveryTarget target,
        Action<LearningProgress>? progressCallback = null,
        CancellationToken externalCt = default)
    {
        lock (_sessionLock)
        {
            if (_isRunning)
                throw new InvalidOperationException("Сессия обучения уже запущена.");

            _isRunning = true;
            _currentTarget = target;
            _activeSessionCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        }

        var ct = _activeSessionCts.Token;
        var sessionStart = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var observedDomains = new Dictionary<string, DiscoveredItem>(StringComparer.OrdinalIgnoreCase);
        var observedIps = new Dictionary<string, DiscoveredItem>(StringComparer.OrdinalIgnoreCase);

        // Pre-populate with seed domains
        foreach (var seed in target.SeedDomains.Concat(target.KnownDomains))
        {
            string clean = HostlistService.CleanDomain(seed);
            if (!string.IsNullOrEmpty(clean) && !observedDomains.ContainsKey(clean))
            {
                observedDomains[clean] = new DiscoveredItem
                {
                    Value = clean,
                    IsIp = false,
                    ProcessName = "seed",
                    Confidence = CandidateConfidence.High,
                    Reason = "Базовый seed-домен",
                    SourceType = DomainSourceType.Known,
                    HitCount = 1,
                    FirstSeen = sessionStart,
                    LastSeen = sessionStart,
                    IsSelected = true
                };
            }
        }

        string lastDiscovered = target.SeedDomains.FirstOrDefault() ?? target.DisplayName;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int activeSockets = 0;

                // 1. Query active network sockets (TCP & UDP)
                try
                {
                    var props = IPGlobalProperties.GetIPGlobalProperties();
                    var tcpConns = props.GetActiveTcpConnections();

                    foreach (var conn in tcpConns)
                    {
                        if (conn.RemoteEndPoint.AddressFamily != AddressFamily.InterNetwork) continue;
                        string remoteIp = conn.RemoteEndPoint.Address.ToString();

                        if (!HostlistService.TestPublicIPv4(remoteIp) || NoiseFilter.IsIpNoise(remoteIp))
                            continue;

                        activeSockets++;
                        int rPort = conn.RemoteEndPoint.Port;

                        if (observedIps.TryGetValue(remoteIp, out var existingIp))
                        {
                            existingIp.HitCount++;
                            existingIp.LastSeen = DateTime.UtcNow;
                        }
                        else
                        {
                            var (conf, reason) = ScoreIpCandidate(remoteIp, 1, false);
                            observedIps[remoteIp] = new DiscoveredItem
                            {
                                Value = remoteIp,
                                IsIp = true,
                                RemotePort = rPort,
                                ProcessName = "web_stream",
                                Confidence = conf,
                                Reason = reason,
                                SourceType = DomainSourceType.Discovered,
                                HitCount = 1,
                                FirstSeen = DateTime.UtcNow,
                                LastSeen = DateTime.UtcNow,
                                IsSelected = conf != CandidateConfidence.Low
                            };
                        }
                    }
                }
                catch { }

                // 2. Query Windows DNS Resolver Cache
                try
                {
                    var dnsEntries = GetDnsCacheEntries();
                    foreach (var entry in dnsEntries)
                    {
                        string cleanDomain = HostlistService.CleanDomain(entry.EntryName);
                        if (string.IsNullOrWhiteSpace(cleanDomain) || NoiseFilter.IsDomainNoise(cleanDomain))
                            continue;

                        if (observedDomains.TryGetValue(cleanDomain, out var existing))
                        {
                            existing.HitCount++;
                            existing.LastSeen = DateTime.UtcNow;
                        }
                        else
                        {
                            var (conf, reason) = ScoreDomainCandidate(
                                cleanDomain,
                                target.SeedDomains.Concat(target.KnownDomains).ToList(),
                                1,
                                DateTime.UtcNow,
                                sessionStart);

                            observedDomains[cleanDomain] = new DiscoveredItem
                            {
                                Value = cleanDomain,
                                IsIp = false,
                                RemotePort = 443,
                                ProcessName = "dns_cache",
                                Confidence = conf,
                                Reason = reason,
                                SourceType = DomainSourceType.Discovered,
                                HitCount = 1,
                                FirstSeen = DateTime.UtcNow,
                                LastSeen = DateTime.UtcNow,
                                IsSelected = conf != CandidateConfidence.Low
                            };
                            lastDiscovered = cleanDomain;
                        }

                        // Link IP if entry has resolved data
                        if (!string.IsNullOrWhiteSpace(entry.Data) && HostlistService.TestPublicIPv4(entry.Data))
                        {
                            string ip = entry.Data.Trim();
                            if (!NoiseFilter.IsIpNoise(ip))
                            {
                                bool isHighDomain = observedDomains.TryGetValue(cleanDomain, out var d) && d.Confidence == CandidateConfidence.High;
                                if (observedIps.TryGetValue(ip, out var exIp))
                                {
                                    exIp.HitCount++;
                                    if (isHighDomain)
                                    {
                                        exIp.Confidence = CandidateConfidence.High;
                                        exIp.Reason = $"Разрешён из {cleanDomain}";
                                        exIp.IsSelected = true;
                                    }
                                }
                                else
                                {
                                    var (conf, reason) = ScoreIpCandidate(ip, 1, isHighDomain);
                                    observedIps[ip] = new DiscoveredItem
                                    {
                                        Value = ip,
                                        IsIp = true,
                                        RemotePort = 443,
                                        ProcessName = "dns_resolved",
                                        Confidence = conf,
                                        Reason = isHighDomain ? $"Разрешён из {cleanDomain}" : reason,
                                        SourceType = DomainSourceType.Discovered,
                                        HitCount = 1,
                                        FirstSeen = DateTime.UtcNow,
                                        LastSeen = DateTime.UtcNow,
                                        IsSelected = conf != CandidateConfidence.Low
                                    };
                                }
                            }
                        }
                    }
                }
                catch { }

                int elapsed = (int)stopwatch.Elapsed.TotalSeconds;
                progressCallback?.Invoke(new LearningProgress(
                    ElapsedSeconds: elapsed,
                    DiscoveredDomainsCount: observedDomains.Count,
                    DiscoveredIpsCount: observedIps.Count,
                    ActiveConnectionsCount: activeSockets,
                    LastDiscoveredHost: lastDiscovered
                ));

                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_sessionLock)
            {
                _isRunning = false;
                _currentTarget = null;
                _activeSessionCts?.Dispose();
                _activeSessionCts = null;
            }
        }

        // Re-score all items with final hit counts and durations
        var allItems = observedDomains.Values.Concat(observedIps.Values).ToList();
        foreach (var item in allItems)
        {
            if (!item.IsIp)
            {
                var (conf, reason) = ScoreDomainCandidate(
                    item.Value,
                    target.SeedDomains.Concat(target.KnownDomains).ToList(),
                    item.HitCount,
                    item.FirstSeen,
                    sessionStart);
                item.Confidence = conf;
                item.Reason = reason;
                item.IsSelected = (conf == CandidateConfidence.High || conf == CandidateConfidence.Medium);
            }
        }

        var recommended = allItems.Where(i => i.Confidence == CandidateConfidence.High).OrderByDescending(i => i.HitCount).ToList();
        var possible = allItems.Where(i => i.Confidence == CandidateConfidence.Medium).OrderByDescending(i => i.HitCount).ToList();
        var excluded = allItems.Where(i => i.Confidence == CandidateConfidence.Low).OrderByDescending(i => i.HitCount).ToList();

        string summary = $"{target.DisplayName} — обучение завершено ({Math.Max(1, (int)stopwatch.Elapsed.TotalSeconds)} сек).\n" +
                         $"Обнаружено: {observedDomains.Count} доменов, {observedIps.Count} IP.\n" +
                         $"Рекомендуется: {recommended.Count}, Возможно: {possible.Count}, Исключено: {excluded.Count}.";

        return new LearningSessionResult(
            Target: target,
            DurationSeconds: (int)stopwatch.Elapsed.TotalSeconds,
            RecommendedItems: recommended,
            PossibleItems: possible,
            ExcludedItems: excluded,
            TotalEndpointsObserved: allItems.Count,
            SummaryMessage: summary
        );
    }

    public void StopSession()
    {
        lock (_sessionLock)
        {
            _activeSessionCts?.Cancel();
        }
    }

    // =========================================================================
    // DNS Cache Extraction Helper (DnsGetCacheDataTable or ipconfig /displaydns)
    // =========================================================================

    public sealed record DnsCacheEntry(string EntryName, ushort Type, string Data);

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_CACHE_ENTRY
    {
        public IntPtr pNext;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string recName;
        public ushort wType;
        public ushort wDataLength;
        public uint dwFlags;
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable", SetLastError = true)]
    private static extern int DnsGetCacheDataTable(out IntPtr pEntry);

    public static List<DnsCacheEntry> GetDnsCacheEntries()
    {
        var list = new List<DnsCacheEntry>();

        // 1. Try native dnsapi.dll DnsGetCacheDataTable
        try
        {
            int status = DnsGetCacheDataTable(out IntPtr pEntry);
            if (status != 0 && pEntry != IntPtr.Zero)
            {
                IntPtr current = pEntry;
                while (current != IntPtr.Zero)
                {
                    var entry = Marshal.PtrToStructure<DNS_CACHE_ENTRY>(current);
                    if (!string.IsNullOrWhiteSpace(entry.recName))
                    {
                        list.Add(new DnsCacheEntry(entry.recName, entry.wType, ""));
                    }
                    current = entry.pNext;
                }
                return list;
            }
        }
        catch { }

        // 2. Fallback to ipconfig /displaydns parse if native call is unavailable in sandbox
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/displaydns",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(500);

                var lines = output.Split('\n');
                string currentName = "";
                foreach (var l in lines)
                {
                    string t = l.Trim();
                    if (t.StartsWith("Record Name", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("Имя записи", StringComparison.OrdinalIgnoreCase))
                    {
                        int colon = t.IndexOf(':');
                        if (colon > 0) currentName = t[(colon + 1)..].Trim();
                    }
                    else if ((t.StartsWith("A (IPv4)", StringComparison.OrdinalIgnoreCase) ||
                              t.StartsWith("A (Узел)", StringComparison.OrdinalIgnoreCase) ||
                              t.StartsWith("CNAME", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrEmpty(currentName))
                    {
                        int colon = t.IndexOf(':');
                        string data = colon > 0 ? t[(colon + 1)..].Trim() : "";
                        list.Add(new DnsCacheEntry(currentName, 1, data));
                    }
                }
            }
        }
        catch { }

        return list;
    }
}
