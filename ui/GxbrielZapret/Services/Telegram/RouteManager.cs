using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Network;

namespace GxbrielZapret.Services.Telegram;

/// <summary>
/// Status of an upstream Telegram routing endpoint.
/// </summary>
public enum RouteState
{
    Healthy,
    Degraded,
    Cooldown,
    Dead
}

/// <summary>
/// Tracks runtime performance, health scoring, and failover state for a single Telegram route.
/// </summary>
public sealed class RouteItem
{
    public string Key { get; }
    public string DisplayName { get; }
    public bool IsDirect { get; }
    public string BaseDomain { get; }

    public RouteState State { get; internal set; } = RouteState.Healthy;
    public double HealthScore { get; internal set; } = 80.0;
    public long LatencyMs { get; internal set; } = 120;
    public long TimeToFirstDataMs { get; internal set; } = 250;
    public int ConsecutiveFailures { get; internal set; }
    public int ConsecutiveSuccesses { get; internal set; }
    public int Http429Count { get; internal set; }
    public long CooldownUntilTick { get; internal set; }

    public long TotalBytesIn { get; internal set; }
    public long TotalBytesOut { get; internal set; }
    public int RelayedSessions { get; internal set; }
    public int FailedSessions { get; internal set; }

    public DateTime LastUsedUtc { get; internal set; } = DateTime.MinValue;
    public DateTime LastSuccessUtc { get; internal set; } = DateTime.MinValue;
    public DateTime LastFailureUtc { get; internal set; } = DateTime.MinValue;

    public RouteItem(string key, string displayName, bool isDirect, string baseDomain)
    {
        Key = key;
        DisplayName = displayName;
        IsDirect = isDirect;
        BaseDomain = baseDomain;
    }

    public bool IsCooling => Environment.TickCount64 < CooldownUntilTick;

    public void RefreshState()
    {
        if (IsCooling)
        {
            State = RouteState.Cooldown;
        }
        else if (ConsecutiveFailures >= 3)
        {
            State = RouteState.Dead;
        }
        else if (ConsecutiveFailures > 0 || HealthScore < 50.0 || LatencyMs > 800)
        {
            State = RouteState.Degraded;
        }
        else
        {
            State = RouteState.Healthy;
        }
    }
}

/// <summary>
/// Intelligent route balancer and failover manager for Telegram MTProto-over-WSS proxies.
/// Manages parallel candidate probing, adaptive health scoring, 429 exponential backoff,
/// hysteresis, and separate optimization for CHAT vs MEDIA traffic lanes.
/// </summary>
public sealed class RouteManager
{
    private const string Suffix = ".co.uk";
    public const string DirectKey = "direct";

    private static readonly string[] EncodedFronts =
    {
        "virkgj.com", "vmmzovy.com", "mkuosckvso.com", "zaewayzmplad.com", "twdmbzcm.com",
        "awzwsldi.com", "clngqrflngqin.com", "tjacxbqtj.com", "bxaxtxmrw.com", "dmohrsgmohcrwb.com",
        "vwbmtmoi.com", "khgrre.com", "ulihssf.com", "tmhqsdqmfpmk.com", "xwuwoqbm.com",
        "orgcnunpj.com", "zhkuldz.com", "zypoljnslxa.com", "efabnxaowuzs.com", "zaftuzsftqdq.com",
    };

    private readonly List<string> _baseDomains;
    private readonly ConcurrentDictionary<string, RouteItem> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _stickyRoute = new(StringComparer.OrdinalIgnoreCase); // "dc|lane" -> routeKey
    private int _mediaRoundRobin;
    private int _routeSwitchCount;

    public int RouteSwitchCount => Volatile.Read(ref _routeSwitchCount);
    public IReadOnlyList<string> AllBaseDomains => _baseDomains;

    public RouteManager()
    {
        _baseDomains = EncodedFronts.Select(Decode).ToList();

        // Seed Direct route
        _routes[DirectKey] = new RouteItem(DirectKey, "Direct DC (149.154.167.220)", isDirect: true, baseDomain: "web.telegram.org")
        {
            HealthScore = 95.0,
            LatencyMs = 60
        };

        // Seed Cloudflare front routes
        foreach (string domain in _baseDomains)
        {
            _routes[domain] = new RouteItem(domain, $"CF Worker ({domain})", isDirect: false, baseDomain: domain)
            {
                HealthScore = 85.0,
                LatencyMs = 120
            };
        }
    }

    public IReadOnlyCollection<RouteItem> GetAllRoutes() => _routes.Values.ToList();

    public RouteItem GetOrCreateRoute(string key)
    {
        return _routes.GetOrAdd(key, k => new RouteItem(k, k, isDirect: k == DirectKey, baseDomain: k));
    }

    /// <summary>
    /// Gets ranked candidate routes for a given DC and lane.
    /// CHAT lane prefers Direct route when healthy, falling back to top Cloudflare fronts.
    /// MEDIA lane prefers healthy Cloudflare fronts spread across workers, falling back to Direct.
    /// </summary>
    public IEnumerable<RouteItem> GetCandidates(int dc, bool isMedia)
    {
        string laneKey = isMedia ? "m" : "c";
        var direct = _routes[DirectKey];
        direct.RefreshState();

        var fronts = _baseDomains
            .Select(d => _routes[d])
            .ToList();

        foreach (var f in fronts) f.RefreshState();

        // 1. CHAT Lane
        if (!isMedia)
        {
            // If direct is healthy or degraded, prioritize it
            if (direct.State is RouteState.Healthy or RouteState.Degraded)
            {
                yield return direct;
            }

            // Next, ranked healthy fronts
            string sticky = _stickyRoute.GetOrAdd($"{dc}|{laneKey}", _ => fronts[Random.Shared.Next(fronts.Count)].Key);
            var rankedFronts = fronts
                .Where(f => f.State != RouteState.Cooldown && f.State != RouteState.Dead)
                .OrderByDescending(f => f.Key == sticky ? f.HealthScore + 15.0 : f.HealthScore) // Hysteresis bonus for sticky
                .ToList();

            foreach (var f in rankedFronts) yield return f;

            // Direct as fallback if it was in cooldown
            if (direct.State is RouteState.Cooldown or RouteState.Dead)
            {
                yield return direct;
            }

            // Cooldown fronts last
            foreach (var f in fronts.Where(f => f.State == RouteState.Cooldown || f.State == RouteState.Dead).OrderByDescending(f => f.HealthScore))
            {
                yield return f;
            }
        }
        // 2. MEDIA Lane
        else
        {
            // Filter available fronts
            var availableFronts = fronts
                .Where(f => f.State != RouteState.Cooldown && f.State != RouteState.Dead)
                .OrderByDescending(f => f.HealthScore)
                .ToList();

            if (availableFronts.Count > 0)
            {
                int start = (int)((uint)Interlocked.Increment(ref _mediaRoundRobin) % (uint)availableFronts.Count);
                for (int i = 0; i < availableFronts.Count; i++)
                {
                    yield return availableFronts[(start + i) % availableFronts.Count];
                }
            }

            // Direct route as fallback for media
            yield return direct;

            // Cooldown fronts if everything else failed
            foreach (var f in fronts.Where(f => f.State == RouteState.Cooldown || f.State == RouteState.Dead).OrderByDescending(f => f.HealthScore))
            {
                yield return f;
            }
        }
    }

    /// <summary>
    /// Executes fast parallel probing of top candidates to find the fastest reachable upstream.
    /// </summary>
    public async Task<RouteItem?> ProbeBestRouteAsync(int dc, bool isMedia, DohResolver doh, TimeSpan timeout, CancellationToken ct)
    {
        var candidates = GetCandidates(dc, isMedia).Take(4).ToList();
        var tasks = candidates.Select(async route =>
        {
            try
            {
                long start = Environment.TickCount64;
                if (route.IsDirect)
                {
                    if (!TgProxyProto.DcRedirects.TryGetValue(dc, out string? ip)) return (route, ok: false, latency: 9999);
                    string domain = $"kws{dc}.web.telegram.org";
                    var r = await TgWebSocket.ConnectAsync(ip, domain, timeout, sni: domain, ct: ct);
                    if (r.Ok)
                    {
                        await r.Ws!.CloseAsync();
                        long lat = Environment.TickCount64 - start;
                        return (route, ok: true, latency: lat);
                    }
                }
                else
                {
                    string domain = $"kws{dc}.{route.BaseDomain}";
                    string[] ips = await doh.ResolveAsync(domain, ct);
                    string host = ips.Length > 0 ? ips[0] : (await TryOsResolveAsync(domain, ct) ?? "");
                    if (string.IsNullOrEmpty(host)) return (route, ok: false, latency: 9999);

                    var r = await TgWebSocket.ConnectAsync(host, domain, timeout, sni: domain, ct: ct);
                    if (r.Ok)
                    {
                        await r.Ws!.CloseAsync();
                        long lat = Environment.TickCount64 - start;
                        return (route, ok: true, latency: lat);
                    }
                }
            }
            catch { /* probe timeout or connection error */ }
            return (route, ok: false, latency: 9999);
        });

        var results = await Task.WhenAll(tasks);
        var winner = results.Where(r => r.ok).OrderBy(r => r.latency).FirstOrDefault();

        if (winner.ok && winner.route != null)
        {
            RecordSuccess(winner.route.Key, winner.latency, winner.latency + 50, 0, 0);
            return winner.route;
        }

        return null;
    }

    public void RecordSuccess(string routeKey, long latencyMs, long ttfbMs, long bytesIn, long bytesOut)
    {
        if (!_routes.TryGetValue(routeKey, out var route)) return;

        lock (route)
        {
            route.ConsecutiveSuccesses++;
            route.ConsecutiveFailures = 0;
            route.LastSuccessUtc = DateTime.UtcNow;
            route.LastUsedUtc = DateTime.UtcNow;
            route.RelayedSessions++;
            route.TotalBytesIn += bytesIn;
            route.TotalBytesOut += bytesOut;

            if (latencyMs > 0)
            {
                // EWMA smoothing for latency (70% old + 30% new)
                route.LatencyMs = (long)(route.LatencyMs * 0.7 + latencyMs * 0.3);
            }
            if (ttfbMs > 0)
            {
                route.TimeToFirstDataMs = (long)(route.TimeToFirstDataMs * 0.7 + ttfbMs * 0.3);
            }

            // Health score recovery (+5 per success, up to 100)
            route.HealthScore = Math.Min(100.0, route.HealthScore + 5.0);
            route.CooldownUntilTick = 0;
            route.RefreshState();
        }
    }

    public void RecordFailure(string routeKey, string reason, int httpStatus = 0)
    {
        if (!_routes.TryGetValue(routeKey, out var route)) return;

        lock (route)
        {
            route.ConsecutiveFailures++;
            route.ConsecutiveSuccesses = 0;
            route.LastFailureUtc = DateTime.UtcNow;
            route.FailedSessions++;

            // Handle HTTP 429 rate limit with exponential backoff
            if (httpStatus == 429)
            {
                route.Http429Count++;
                int backoffSec = Math.Min(300, 30 * (1 << Math.Min(4, route.Http429Count - 1)));
                route.CooldownUntilTick = Environment.TickCount64 + (backoffSec * 1000L);
                route.HealthScore = Math.Max(10.0, route.HealthScore - 20.0);
            }
            // Dead front (no data response within 10s)
            else if (reason == "DeadFront")
            {
                route.CooldownUntilTick = Environment.TickCount64 + 45_000L;
                route.HealthScore = Math.Max(10.0, route.HealthScore - 30.0);
            }
            // Flaky death (connection dropped immediately < 6s)
            else if (reason == "FlakyDeath")
            {
                route.CooldownUntilTick = Environment.TickCount64 + 20_000L;
                route.HealthScore = Math.Max(15.0, route.HealthScore - 15.0);
            }
            // Connect timeout / TLS failure
            else
            {
                route.CooldownUntilTick = Environment.TickCount64 + (route.IsDirect ? 60_000L : 30_000L);
                route.HealthScore = Math.Max(10.0, route.HealthScore - 20.0);
            }

            route.RefreshState();
            Interlocked.Increment(ref _routeSwitchCount);
        }
    }

    private static async Task<string?> TryOsResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct);
            foreach (var a in addrs)
                if (a.AddressFamily == AddressFamily.InterNetwork) return a.ToString();
            return addrs.Length > 0 ? addrs[0].ToString() : null;
        }
        catch { return null; }
    }

    private static string Decode(string s)
    {
        if (!s.EndsWith(".com", StringComparison.Ordinal)) return s;
        string p = s[..^4];
        int n = p.Count(char.IsLetter);
        var sb = new StringBuilder(p.Length);
        foreach (char c in p)
        {
            if (char.IsLetter(c))
            {
                int b = c > '`' ? 97 : 65;
                sb.Append((char)(((c - b - n) % 26 + 26) % 26 + b));
            }
            else sb.Append(c);
        }
        return sb.ToString() + Suffix;
    }
}
