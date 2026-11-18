using System.Security.Authentication;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Network;

public sealed record RiotEndpointProbeResult(
    string Name,
    string Host,
    DiagStatus Tls12,
    DiagStatus Tls13,
    DiagStatus Http,
    bool IsReachable)
{
    public bool Passes => IsReachable;
}

public sealed record RiotDiagnosticsReport(
    bool AllReachable,
    int PassedCount,
    int TotalCount,
    IReadOnlyList<RiotEndpointProbeResult> Endpoints,
    string Summary);

/// <summary>
/// Standalone opt-in diagnostics for Riot Games / League of Legends endpoints.
/// Probes clientconfig.rpg.riotgames.com, auth.riotgames.com, and entitlements.auth.riotgames.com.
/// Kept completely separate from the mandatory strategy score.
/// </summary>
public sealed class RiotDiagnosticsService
{
    public const string GroupName = "Riot Games";

    public static readonly (string Name, string Host)[] Endpoints = new[]
    {
        ("Client Config", "clientconfig.rpg.riotgames.com"),
        ("Auth", "auth.riotgames.com"),
        ("Entitlements", "entitlements.auth.riotgames.com")
    };

    private readonly Func<string, SslProtocols, CancellationToken, Task<DiagStatus>> _tlsProbe;
    private readonly Func<string, CancellationToken, Task<DiagStatus>> _reachProbe;
    private readonly Func<string, CancellationToken, Task<(bool ok, long ms)>> _pingProbe;

    public RiotDiagnosticsService() : this(NetProbe.TlsAsync, NetProbe.ReachAsync, NetProbe.PingAsync) { }

    public RiotDiagnosticsService(
        Func<string, SslProtocols, CancellationToken, Task<DiagStatus>> tlsProbe,
        Func<string, CancellationToken, Task<DiagStatus>> reachProbe,
        Func<string, CancellationToken, Task<(bool ok, long ms)>>? pingProbe = null)
    {
        _tlsProbe = tlsProbe ?? throw new ArgumentNullException(nameof(tlsProbe));
        _reachProbe = reachProbe ?? throw new ArgumentNullException(nameof(reachProbe));
        _pingProbe = pingProbe ?? NetProbe.PingAsync;
    }

    public static List<DiagRow> BuildRiotRows() =>
        Endpoints.Select(e => new DiagRow { Group = GroupName, Name = e.Name, Host = e.Host }).ToList();

    public async Task<RiotDiagnosticsReport> CheckAsync(CancellationToken ct = default)
    {
        var results = new List<RiotEndpointProbeResult>();
        int passed = 0;

        foreach (var (name, host) in Endpoints)
        {
            ct.ThrowIfCancellationRequested();
            var tls12 = await _tlsProbe(host, SslProtocols.Tls12, ct).ConfigureAwait(false);
            var tls13 = await _tlsProbe(host, SslProtocols.Tls13, ct).ConfigureAwait(false);
            var http = await _reachProbe(host, ct).ConfigureAwait(false);

            bool reachable = (tls12 == DiagStatus.Ok || tls13 == DiagStatus.Ok) && http == DiagStatus.Ok;
            if (reachable) passed++;

            results.Add(new RiotEndpointProbeResult(name, host, tls12, tls13, http, reachable));
        }

        bool allReachable = passed == Endpoints.Length;
        string summary = allReachable
            ? Loc.T("Все эндпоинты Riot (clientconfig, auth, entitlements) доступны.")
            : Loc.T("Доступно {0} из {1} эндпоинтов Riot.", passed, Endpoints.Length);

        return new RiotDiagnosticsReport(allReachable, passed, Endpoints.Length, results, summary);
    }

    public async Task ProbeRowAsync(DiagRow row, CancellationToken ct = default)
    {
        row.Ping = DiagStatus.Running;
        var (pOk, pMs) = await _pingProbe(row.Host, ct).ConfigureAwait(false);
        row.PingText = pOk ? Loc.T("{0} мс", pMs) : Loc.T("таймаут");
        row.Ping = pOk ? DiagStatus.Ok : DiagStatus.Timeout;

        row.Http = DiagStatus.Running;
        row.Http = await _reachProbe(row.Host, ct).ConfigureAwait(false);

        row.Tls12 = DiagStatus.Running;
        row.Tls12 = await _tlsProbe(row.Host, SslProtocols.Tls12, ct).ConfigureAwait(false);

        row.Tls13 = DiagStatus.Running;
        row.Tls13 = await _tlsProbe(row.Host, SslProtocols.Tls13, ct).ConfigureAwait(false);
    }
}
