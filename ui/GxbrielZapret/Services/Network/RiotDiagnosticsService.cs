using System.Diagnostics;
using System.Net.Sockets;
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

public sealed record RiotChatProbeResult(
    string Host,
    int Port,
    bool IsReachable,
    long LatencyMs,
    string Detail);

public sealed record RiotDiagnosticsReport(
    bool AllReachable,
    int PassedCount,
    int TotalCount,
    IReadOnlyList<RiotEndpointProbeResult> Endpoints,
    string Summary,
    RiotChatProbeResult? Chat = null);

/// <summary>
/// Standalone opt-in diagnostics for Riot Games / League of Legends endpoints.
/// Probes clientconfig.rpg.riotgames.com, auth.riotgames.com, and entitlements.auth.riotgames.com.
/// Kept completely separate from the mandatory strategy score.
/// </summary>
public sealed class RiotDiagnosticsService
{
    public const string GroupName = "Riot Games";
    public const string ChatHost = "euw1.chat.si.riotgames.com";
    public const int ChatPort = 5223;

    public static readonly (string Name, string Host)[] Endpoints = new[]
    {
        ("Client Config", "clientconfig.rpg.riotgames.com"),
        ("Auth", "auth.riotgames.com"),
        ("Entitlements", "entitlements.auth.riotgames.com")
    };

    private readonly Func<string, SslProtocols, CancellationToken, Task<DiagStatus>> _tlsProbe;
    private readonly Func<string, CancellationToken, Task<DiagStatus>> _reachProbe;
    private readonly Func<string, CancellationToken, Task<(bool ok, long ms)>> _pingProbe;
    private readonly Func<string, int, CancellationToken, Task<RiotChatProbeResult>> _chatProbe;

    public RiotDiagnosticsService() : this(NetProbe.TlsAsync, NetProbe.ReachAsync, NetProbe.PingAsync, ProbeTcpChatAsync) { }

    public RiotDiagnosticsService(
        Func<string, SslProtocols, CancellationToken, Task<DiagStatus>> tlsProbe,
        Func<string, CancellationToken, Task<DiagStatus>> reachProbe,
        Func<string, CancellationToken, Task<(bool ok, long ms)>>? pingProbe = null,
        Func<string, int, CancellationToken, Task<RiotChatProbeResult>>? chatProbe = null)
    {
        _tlsProbe = tlsProbe ?? throw new ArgumentNullException(nameof(tlsProbe));
        _reachProbe = reachProbe ?? throw new ArgumentNullException(nameof(reachProbe));
        _pingProbe = pingProbe ?? NetProbe.PingAsync;
        _chatProbe = chatProbe ?? ProbeTcpChatAsync;
    }

    public static async Task<RiotChatProbeResult> ProbeTcpChatAsync(string host, int port, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            return new RiotChatProbeResult(host, port, true, sw.ElapsedMilliseconds, "TCP OK");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new RiotChatProbeResult(host, port, false, sw.ElapsedMilliseconds, "TCP timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new RiotChatProbeResult(host, port, false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public static bool IsRiotClientRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("RiotClientUx").Length > 0 || System.Diagnostics.Process.GetProcessesByName("RiotClientServices").Length > 0; }
        catch { return false; }
    }

    public static bool IsLeagueClientRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("LeagueClientUx").Length > 0 || System.Diagnostics.Process.GetProcessesByName("LeagueClient").Length > 0; }
        catch { return false; }
    }

    public static List<DiagRow> BuildRiotRows() =>
        Endpoints.Select(e => new DiagRow { Group = GroupName, Name = e.Name, Host = e.Host }).ToList();

    public async Task<RiotDiagnosticsReport> CheckAsync(CancellationToken ct = default)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(TimeSpan.FromSeconds(10));
        var token = budgetCts.Token;

        using var sem = new SemaphoreSlim(3);
        var tasks = Endpoints.Select(async ep =>
        {
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var (name, host) = ep;
                var t12Task = _tlsProbe(host, SslProtocols.Tls12, token);
                var t13Task = _tlsProbe(host, SslProtocols.Tls13, token);
                var httpTask = _reachProbe(host, token);
                await Task.WhenAll(t12Task, t13Task, httpTask).ConfigureAwait(false);

                var tls12 = await t12Task;
                var tls13 = await t13Task;
                var http = await httpTask;
                bool reachable = (tls12 == DiagStatus.Ok || tls13 == DiagStatus.Ok) && http == DiagStatus.Ok;
                return new RiotEndpointProbeResult(name, host, tls12, tls13, http, reachable);
            }
            finally
            {
                sem.Release();
            }
        });

        var chatTask = _chatProbe(ChatHost, ChatPort, token);

        RiotEndpointProbeResult[] results;
        RiotChatProbeResult chatResult;
        try
        {
            results = await Task.WhenAll(tasks).ConfigureAwait(false);
            chatResult = await chatTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            results = Endpoints.Select(ep => new RiotEndpointProbeResult(ep.Name, ep.Host, DiagStatus.Timeout, DiagStatus.Timeout, DiagStatus.Timeout, false)).ToArray();
            chatResult = new RiotChatProbeResult(ChatHost, ChatPort, false, 0, "timeout");
        }

        int passed = results.Count(r => r.IsReachable);
        bool allReachable = passed == Endpoints.Length;
        string summary = allReachable
            ? (chatResult.IsReachable
                ? Loc.T("HTTPS Riot (clientconfig, auth, entitlements) и чат XMPP (5223) доступны. Вход в матч проверяется вручную.")
                : Loc.T("HTTPS Riot доступны, но чат/друзья (TCP 5223) недоступны ({0}) — проверьте Smart-DNS/VPN (исключите chat.si.riotgames.com).", chatResult.Detail))
            : Loc.T("Доступно {0} из {1} HTTPS эндпоинтов Riot.", passed, Endpoints.Length);

        return new RiotDiagnosticsReport(allReachable, passed, Endpoints.Length, results, summary, chatResult);
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
