using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Network;

public sealed record RiotEndpointProbeResult(string Name, string Host, DiagStatus Tls12, DiagStatus Tls13, DiagStatus Http)
{
    public bool IsReachable => (Tls12 == DiagStatus.Ok || Tls13 == DiagStatus.Ok) && Http == DiagStatus.Ok;
}

public sealed record RiotDiagnosticsReport(int PassedCount, int TotalCount, IReadOnlyList<RiotEndpointProbeResult> Endpoints)
{
    public bool AllReachable => TotalCount > 0 && PassedCount == TotalCount;
}

/// <summary>Opt-in launcher/auth reachability checks; does not test game sessions or strategy effectiveness.</summary>
public sealed class RiotDiagnosticsService
{
    public const string GroupName = "Riot Games";
    public static readonly (string Name, string Host)[] Endpoints =
    {
        ("Client Config", "clientconfig.rpg.riotgames.com"),
        ("Auth", "auth.riotgames.com"),
        ("Entitlements", "entitlements.auth.riotgames.com")
    };

    private readonly Func<string, SslProtocols, CancellationToken, Task<DiagStatus>> _tlsProbe;
    private readonly Func<string, CancellationToken, Task<DiagStatus>> _reachProbe;

    public RiotDiagnosticsService() : this(NetProbe.TlsAsync, NetProbe.ReachAsync) { }

    public RiotDiagnosticsService(Func<string, SslProtocols, CancellationToken, Task<DiagStatus>> tlsProbe,
        Func<string, CancellationToken, Task<DiagStatus>> reachProbe)
    {
        _tlsProbe = tlsProbe ?? throw new ArgumentNullException(nameof(tlsProbe));
        _reachProbe = reachProbe ?? throw new ArgumentNullException(nameof(reachProbe));
    }

    public static List<DiagRow> BuildRiotRows() => Endpoints.Select(e => new DiagRow { Group = GroupName, Name = e.Name, Host = e.Host }).ToList();

    public async Task<RiotDiagnosticsReport> CheckAsync(CancellationToken ct = default)
    {
        var rows = new List<RiotEndpointProbeResult>(Endpoints.Length);
        foreach (var (name, host) in Endpoints)
        {
            ct.ThrowIfCancellationRequested();
            var tls12 = await _tlsProbe(host, SslProtocols.Tls12, ct).ConfigureAwait(false);
            var tls13 = await _tlsProbe(host, SslProtocols.Tls13, ct).ConfigureAwait(false);
            var http = await _reachProbe(host, ct).ConfigureAwait(false);
            rows.Add(new(name, host, tls12, tls13, http));
        }
        return new(rows.Count(r => r.IsReachable), rows.Count, rows);
    }
}
