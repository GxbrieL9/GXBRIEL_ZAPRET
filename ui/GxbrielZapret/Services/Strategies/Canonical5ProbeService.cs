using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;

namespace GxbrielZapret.Services.Strategies;

public sealed record ProbeLaunchOptions(string? HostlistPath = null, bool GameFilter = false,
    bool LeagueTcp2099Bypass = false, bool RiotEntitlementsBypass = false)
{
    public List<string> BuildArguments(Preset preset, bool bypassAll, bool disableQuic) =>
        EngineService.BuildArguments(preset, HostlistPath, gameFilter: GameFilter, bypassAll: bypassAll,
            disableQuic: disableQuic, leagueTcp2099Bypass: LeagueTcp2099Bypass,
            riotEntitlementsBypass: RiotEntitlementsBypass, forLaunch: true);

    public string VerificationKey(bool bypassAll, bool disableQuic)
    {
        string list = HostlistPath is null ? "" : Path.GetFullPath(HostlistPath);
        string contents = File.Exists(list) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(list))) : "missing";
        return $"{BlockcheckProfileStore.OptionsKey(bypassAll, disableQuic)};game={GameFilter};league={LeagueTcp2099Bypass};riot={RiotEntitlementsBypass};list={list};sha={contents}";
    }
}

public sealed record Canonical5ProbeResult(
    string StrategyName,
    string StrategyId,
    bool IsWork,
    int PassCount,
    int TotalCount,
    long LatencyMs,
    Dictionary<string, bool> Probes,
    string Summary,
    ApplicationFlowResult? Flows = null,
    bool CheckUnavailable = false,
    Dictionary<string, string>? TargetDetails = null
)
{
    public bool IsInconclusive => !IsWork && (CheckUnavailable || TotalCount > 0 && PassCount == TotalCount &&
        Canonical5ProbeService.ScopeForTargets(Probes.Keys) is { } scope && Flows?.IsConclusive(scope) != true);
    public string Status => IsWork ? "WORK" : IsInconclusive ? "INCONCLUSIVE" : "BROKEN";
}

/// <summary>
/// Probes a winws2 strategy against the 5 canonical mandatory endpoints:
/// 1. www.youtube.com
/// 2. redirector.googlevideo.com
/// 3. i.ytimg.com
/// 4. discord.com/api/v10/gateway
/// 5. gateway.discord.gg
///
/// A strategy is WORK when the engine is alive and all selected targets pass.
/// Partial results (e.g. YouTube works while Discord fails) are not WORK for a combined scope.
/// </summary>
public sealed class Canonical5ProbeService : IDisposable
{
    public static readonly IReadOnlyList<string> CanonicalTargets = new[]
    {
        "www.youtube.com",
        "redirector.googlevideo.com",
        "i.ytimg.com",
        "discord.com",
        "gateway.discord.gg"
    };

    private readonly IProbeEngineRunner _runner;
    private readonly bool _defaultFlowProbe;
    private readonly Func<string, CancellationToken, Task<FlowEvidence>> _probeTarget;
    private readonly Func<AutoScope, CancellationToken, Task<ApplicationFlowResult>> _probeFlows;

    public Canonical5ProbeService()
    {
        _runner = new ProbeEngineRunner();
        _defaultFlowProbe = true;
        _probeTarget = ProbeTargetWithDetailAsync;
        _probeFlows = CreateFlowProbe();
    }

    private static Func<AutoScope, CancellationToken, Task<ApplicationFlowResult>> CreateFlowProbe()
    {
        // Each sample must include discovery/startup of the video, with no warm session from another candidate.
        return ApplicationFlowProbe.CheckAsync;
    }

    internal Canonical5ProbeService(IProbeEngineRunner runner,
        Func<string, CancellationToken, Task<bool>> probeTarget,
        Func<AutoScope, CancellationToken, Task<ApplicationFlowResult>> probeFlows)
    {
        _runner = runner;
        _probeTarget = async (host, ct) => new FlowEvidence(await probeTarget(host, ct).ConfigureAwait(false), "injected transport probe");
        _probeFlows = probeFlows;
    }

    public void Dispose() => _runner.Dispose();

    public async Task<Canonical5ProbeResult> EvaluateCandidateAsync(
        Preset preset,
        Action<string, bool>? onHostProbed,
        CancellationToken ct,
        bool bypassAll = false,
        IReadOnlyList<string>? targets = null,
        bool disableQuic = false,
        AutoScope? applicationScope = null,
        ProbeLaunchOptions? launchOptions = null,
        Action<string>? onProgress = null)
    {
        targets ??= CanonicalTargets;
        targets = targets.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Count == 0) throw new ArgumentException("At least one probe target is required.", nameof(targets));
        // Omitting the scope must never silently turn a service check into HTTPS-only WORK.
        var inferredScope = ScopeForTargets(targets);
        if (applicationScope.HasValue && inferredScope.HasValue && applicationScope != inferredScope)
            throw new ArgumentException("Режим проверки должен соответствовать выбранным сервисам.", nameof(applicationScope));
        applicationScope ??= inferredScope;
        ct.ThrowIfCancellationRequested();
        var probeResults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var targetDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets) probeResults[t] = false;

        using var progress = new ProbeProgress(onProgress);
        var sw = Stopwatch.StartNew();
        try
        {
            var args = (launchOptions ?? new()).BuildArguments(preset, bypassAll, disableQuic);
            progress.Stage("Запуск стратегии и ожидание готовности движка");
            await Task.Run(() => _runner.Start(args), ct).ConfigureAwait(false);
            await _runner.WaitReadyAsync(ct).ConfigureAwait(false);

            if (!_runner.IsReady)
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
                await Task.Run(() => _runner.Start(args), ct).ConfigureAwait(false);
                await _runner.WaitReadyAsync(ct).ConfigureAwait(false);
            }

            if (!_runner.IsReady)
            {
                sw.Stop();
                return Finish(new Canonical5ProbeResult(
                    preset.Name,
                    preset.ReferenceId ?? preset.Fingerprint,
                    IsWork: false,
                    PassCount: 0,
                    TotalCount: targets.Count,
                    LatencyMs: 0,
                    Probes: probeResults,
                    Summary: Loc.T("Движок не запустился") + ": " + _runner.DiagnosticOutput,
                    CheckUnavailable: true
                ));
            }

            progress.Stage("Проверка сайтов · по 3 запроса на цель");
            using var sem = new SemaphoreSlim(6);
            var durations = new List<long>();
            var hostAnyOk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var tasks = targets.Select(async target =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    bool ok = true;
                    bool anyOk = false;
                    var details = new List<string>();
                    // Independent requests expose cold-connect failures hidden by a single successful retry.
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        var timer = Stopwatch.StartNew();
                        progress.Update($"{target} · запрос {attempt + 1}/3");
                        var evidence = await _probeTarget(target, ct).ConfigureAwait(false);
                        timer.Stop();
                        lock (durations) durations.Add(Math.Max(1, timer.ElapsedMilliseconds));
                        bool stepOk = evidence.Passed == true;
                        ok &= stepOk;
                        anyOk |= stepOk;
                        details.Add($"{attempt + 1}/3: {evidence.Detail} ({timer.ElapsedMilliseconds} ms)");
                    }
                    lock (probeResults)
                    {
                        probeResults[target] = ok;
                        hostAnyOk[target] = anyOk;
                        targetDetails[target] = string.Join("; ", details);
                    }
                    onHostProbed?.Invoke(target, ok);
                    return ok;
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            bool engineAlive = _runner.IsAlive;
            if (!engineAlive)
                foreach (var target in targets) probeResults[target] = false;

            int passCount = probeResults.Values.Count(v => v);
            int totalCount = targets.Count;
            // A strategy is usable only when every selected target passes. A majority result can
            // hide a broken service (for example all YouTube targets pass while Discord is dead),
            // and Fast Pick must never stop on that partial result.
            bool isWork = engineAlive && totalCount > 0 && passCount == totalCount;
            ApplicationFlowResult? flows = null;
            if (engineAlive && applicationScope.HasValue)
            {
                bool ytHasHtml = !targets.Any(t => t.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase))
                    || (hostAnyOk.TryGetValue("www.youtube.com", out bool ytAny) && ytAny);
                bool dcHasApiOrGateway = !targets.Any(t => t.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
                                                        || t.Equals("gateway.discord.gg", StringComparison.OrdinalIgnoreCase))
                    || (hostAnyOk.TryGetValue("discord.com", out bool dc1) && dc1)
                    || (hostAnyOk.TryGetValue("gateway.discord.gg", out bool dc2) && dc2);
                var session = new YouTubeMediaSession();
                bool ytStopped = false;
                bool dcStopped = false;

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var timer = Stopwatch.StartNew();
                    string serviceName = applicationScope == AutoScope.Discord ? "Gateway Discord" : applicationScope == AutoScope.YouTube ? "Видеоданные YouTube" : "Видеоданные YouTube + Gateway Discord";
                    progress.Stage($"{serviceName} · замер {attempt + 1}/3", applicationScope == AutoScope.Discord ? 12 : 25);

                    ApplicationFlowResult sample;
                    if (_defaultFlowProbe)
                    {
                        var needYt = applicationScope.Value is AutoScope.YouTube or AutoScope.Both;
                        var needDc = applicationScope.Value is AutoScope.Discord or AutoScope.Both;
                        FlowEvidence ytEv;
                        FlowEvidence dcEv;

                        if (needYt && (!ytHasHtml || ytStopped))
                            ytEv = !ytHasHtml
                                ? new FlowEvidence(null, "получение тестовой ссылки: сайт недоступен (0/3); передача видео не проверена")
                                : flows!.YouTubeVideo;
                        else if (!needYt)
                            ytEv = new FlowEvidence(null, "не выбран");
                        else
                            ytEv = default!;

                        if (needDc && (!dcHasApiOrGateway || dcStopped))
                            dcEv = !dcHasApiOrGateway
                                ? new FlowEvidence(false, "API/Gateway: сайт недоступен (0/3)")
                                : flows!.DiscordGateway;
                        else if (!needDc)
                            dcEv = new FlowEvidence(null, "не выбран");
                        else
                            dcEv = default!;

                        bool runYt = needYt && ytHasHtml && !ytStopped;
                        bool runDc = needDc && dcHasApiOrGateway && !dcStopped;
                        if (runYt && runDc)
                        {
                            var ytTask = ApplicationFlowProbe.CheckAsync(AutoScope.YouTube, session, ct, progress.Update);
                            var dcTask = ApplicationFlowProbe.CheckAsync(AutoScope.Discord, session, ct, progress.Update);
                            await Task.WhenAll(ytTask, dcTask).ConfigureAwait(false);
                            ytEv = (await ytTask.ConfigureAwait(false)).YouTubeVideo;
                            dcEv = (await dcTask.ConfigureAwait(false)).DiscordGateway;
                        }
                        else if (runYt)
                        {
                            ytEv = (await ApplicationFlowProbe.CheckAsync(AutoScope.YouTube, session, ct, progress.Update).ConfigureAwait(false)).YouTubeVideo;
                        }
                        else if (runDc)
                        {
                            dcEv = (await ApplicationFlowProbe.CheckAsync(AutoScope.Discord, session, ct, progress.Update).ConfigureAwait(false)).DiscordGateway;
                        }

                        sample = new ApplicationFlowResult(ytEv, dcEv);
                        if (ytEv.Passed == false) ytStopped = true;
                        if (dcEv.Passed == false) dcStopped = true;
                    }
                    else
                    {
                        sample = await _probeFlows(applicationScope.Value, ct).ConfigureAwait(false);
                    }

                    progress.Update(sample.Summary);
                    timer.Stop();
                    durations.Add(Math.Max(1, timer.ElapsedMilliseconds));
                    ct.ThrowIfCancellationRequested();
                    // Keep each service's weakest evidence independently; Discord must not hide YouTube.
                    flows = flows is null ? sample : new(
                        Weakest(flows.YouTubeVideo, sample.YouTubeVideo),
                        Weakest(flows.DiscordGateway, sample.DiscordGateway));
                    isWork &= _runner.IsAlive && sample.Passes(applicationScope.Value);
                    if (!_runner.IsAlive) break;
                    if (_defaultFlowProbe &&
                        (applicationScope == AutoScope.YouTube && ytStopped ||
                         applicationScope == AutoScope.Discord && dcStopped ||
                         applicationScope == AutoScope.Both && (!ytHasHtml || ytStopped) && (!dcHasApiOrGateway || dcStopped)))
                    {
                        break;
                    }
                }
            }
            sw.Stop();
            // Worst measured request/flow, excluding engine startup and semaphore queue time.
            long latencyMs = durations.Count > 0 ? durations.Max() : 0;

            string summary = isWork
                ? Loc.T("{0}/{1} целей доступны ({2} ms)", passCount, totalCount, latencyMs)
                : passCount == totalCount && flows?.IsConclusive(applicationScope ?? AutoScope.Both) != true
                ? $"HTTPS {passCount}/{totalCount}; прикладная проверка не подтверждена"
                : Loc.T("{0}/{1} целей (не прошло)", passCount, totalCount);
            if (flows is not null) summary += " · " + flows.Summary;
            summary += $" · до 3 повторов; худшая задержка {latencyMs} ms";
            if (passCount < totalCount)
                summary += " · " + string.Join("; ", targetDetails.Where(p => !probeResults[p.Key]).Select(p => p.Key + ": " + p.Value));

            return Finish(new Canonical5ProbeResult(
                preset.Name,
                preset.ReferenceId ?? preset.Fingerprint,
                IsWork: isWork,
                PassCount: passCount,
                TotalCount: totalCount,
                LatencyMs: latencyMs,
                Probes: probeResults,
                Summary: summary,
                Flows: flows,
                TargetDetails: targetDetails
            ));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            foreach (var target in targets) probeResults[target] = false;
            return Finish(new Canonical5ProbeResult(preset.Name, preset.ReferenceId ?? preset.Fingerprint,
                false, 0, targets.Count, 0, probeResults, Loc.T("Ошибка проверки: {0}", ex.Message), CheckUnavailable: true));
        }
        finally
        {
            _runner.Stop();
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static FlowEvidence Weakest(FlowEvidence previous, FlowEvidence next)
    {
        static int Rank(FlowEvidence value) => value.Passed == false ? 0 : value.Passed is null ? 1 : 2;
        if (Rank(next) < Rank(previous)) return next;
        if (Rank(next) > Rank(previous)) return previous;
        return next.MegabitsPerSecond is { } speed &&
            (previous.MegabitsPerSecond is null || speed < previous.MegabitsPerSecond) ? next : previous;
    }

    private static Canonical5ProbeResult Finish(Canonical5ProbeResult result)
    {
        ScanProbeLog.Append(result);
        return result;
    }

    public static async Task<bool> ProbeTargetAsync(string target, CancellationToken ct)
        => (await ProbeTargetWithDetailAsync(target, ct).ConfigureAwait(false)).Passed == true;

    private static async Task<FlowEvidence> ProbeTargetWithDetailAsync(string target, CancellationToken ct)
    {
        if (target.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            var application = await YouTubeApplicationProbe.CheckAsync(ct).ConfigureAwait(false);
            return new(application.Ok, application.Details);
        }
        var (ok, code, exitCode) = await NetProbe.RunCurlAsync(NetProbe.HostToUrl(target), null, ct).ConfigureAwait(false);
        string detail = exitCode switch { 0 => "ответ получен", 28 => "тайм-аут", 35 => "ошибка соединения/TLS", _ => "ошибка транспорта" };
        return new(ok && IsExpectedTargetResponse(target, code), $"HTTP {code}; {detail} ({exitCode})");
    }

    internal static AutoScope? ScopeForTargets(IEnumerable<string> targets)
    {
        var hosts = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool youtube = AutoScope.YouTube.GoalHosts().Any(hosts.Contains);
        bool discord = AutoScope.Discord.GoalHosts().Any(hosts.Contains);
        return youtube && discord ? AutoScope.Both : youtube ? AutoScope.YouTube : discord ? AutoScope.Discord : null;
    }

    internal static bool IsExpectedTargetResponse(string target, string code)
    {
        if (int.TryParse(code, out int status) && status is >= 200 and <= 299) return true;
        // These bare service roots intentionally return an error. This is reachability evidence
        // only; WORK additionally requires real video bytes and/or the Discord WebSocket Hello.
        return target.ToLowerInvariant() switch
        {
            "cdn.discordapp.com" or "challenges.cloudflare.com" => code == "403",
            "i.ytimg.com" or "updates.discord.com" => code == "404",
            "gateway.discord.gg" => code is "400" or "404",
            _ => false
        };
    }
}
