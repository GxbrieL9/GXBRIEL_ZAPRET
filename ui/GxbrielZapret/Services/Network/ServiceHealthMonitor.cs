using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Network;

public enum ServiceHealthState
{
    NotChecked,
    Checking,
    Working,
    Failed,
    Inconclusive
}

public sealed record ServiceHealthSnapshot(
    ServiceHealthState Discord,
    ServiceHealthState YouTube,
    ServiceHealthState Telegram,
    DateTime Timestamp,
    long Revision = 0,
    string DiscordDetails = "",
    string YouTubeDetails = "")
{
    public bool IsFullyHealthy => Discord == ServiceHealthState.Working && YouTube == ServiceHealthState.Working;
    public bool IsHealthyFor(AutoScope scope) => scope switch
    {
        AutoScope.Discord => Discord == ServiceHealthState.Working,
        AutoScope.YouTube => YouTube == ServiceHealthState.Working,
        _ => IsFullyHealthy
    };
    public bool IsConclusiveFor(AutoScope scope) => scope switch
    {
        AutoScope.Discord => Discord is ServiceHealthState.Working or ServiceHealthState.Failed,
        AutoScope.YouTube => YouTube is ServiceHealthState.Working or ServiceHealthState.Failed,
        _ => IsConclusiveFor(AutoScope.Discord) && IsConclusiveFor(AutoScope.YouTube)
    };
}

/// <summary>
/// Unified truthful health monitor and background watchdog.
/// Never fabricates working status from EngineState.Running.
/// Performs actual network probes against canonical target matrices.
/// </summary>
public sealed class ServiceHealthMonitor : IDisposable
{
    // Canonical targets per MASTER TZ Section 1.1 & 2
    public static readonly string[] DiscordWatch = { "discord.com", "gateway.discord.gg" };
    public static readonly string[] YouTubeWatch = { "www.youtube.com", "redirector.googlevideo.com" };

    private const int WatchdogIntervalSec = 300;
    private const int RetryDelaySec = 5;
    private const int MaxConsecutiveFailures = 2;
    private const int CooldownSec = 120;

    public ServiceHealthState DiscordState { get; private set; } = ServiceHealthState.NotChecked;
    public ServiceHealthState YouTubeState { get; private set; } = ServiceHealthState.NotChecked;
    public ServiceHealthState TelegramState { get; private set; } = ServiceHealthState.NotChecked;

    public string DiscordDetails { get; private set; } = "";
    public string YouTubeDetails { get; private set; } = "";

    public event Action<ServiceHealthSnapshot>? HealthChanged;
    public event Action? HealthDegraded;

    private CancellationTokenSource? _watchdogCts;
    private int _consecutiveFailures;
    private DateTime _lastDegradedSignal = DateTime.MinValue;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource _sessionCts = new();
    private readonly Func<CancellationToken, Task<(bool? ok, string details)>> _discordProbe;
    private readonly Func<CancellationToken, Task<(bool? ok, string details)>> _youtubeProbe;
    private long _generation;
    private long _revision;
    private DateTime _timestamp;
    private int _activeProbes;
    private bool _disposed;
    private AutoScope _watchdogScope;

    public ServiceHealthMonitor() : this(ProbeDiscordAsync, ProbeYouTubeAsync) { }

    internal ServiceHealthMonitor(
        Func<CancellationToken, Task<(bool? ok, string details)>> discordProbe,
        Func<CancellationToken, Task<(bool? ok, string details)>> youtubeProbe)
    {
        _discordProbe = discordProbe;
        _youtubeProbe = youtubeProbe;
    }

    internal ServiceHealthMonitor(
        Func<CancellationToken, Task<(bool ok, string details)>> discordProbe,
        Func<CancellationToken, Task<(bool ok, string details)>> youtubeProbe)
    {
        _discordProbe = async ct => { var (ok, details) = await discordProbe(ct).ConfigureAwait(false); return (ok, details); };
        _youtubeProbe = async ct => { var (ok, details) = await youtubeProbe(ct).ConfigureAwait(false); return (ok, details); };
    }

    public bool IsWatchdogRunning => _watchdogCts is not null && !_watchdogCts.IsCancellationRequested;

    public void Reset()
    {
        CancellationTokenSource previous;
        lock (_stateLock)
        {
            if (_disposed) return;
            previous = _sessionCts;
            _sessionCts = new CancellationTokenSource();
            _generation++;
            _revision++;
            _timestamp = default;
            DiscordState = ServiceHealthState.NotChecked;
            YouTubeState = ServiceHealthState.NotChecked;
            DiscordDetails = Loc.T("Не проверено");
            YouTubeDetails = Loc.T("Не проверено");
            _consecutiveFailures = 0;
            _lastDegradedSignal = DateTime.MinValue;
        }
        previous.Cancel();
        previous.Dispose();
        NotifyState();
    }

    public void SetTelegramState(ServiceHealthState state)
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            TelegramState = state;
            _revision++;
        }
        NotifyState();
    }

    public async Task<ServiceHealthSnapshot> ProbeAllAsync(CancellationToken ct)
    {
        long generation;
        CancellationTokenSource linked;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = _generation;
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            _activeProbes++;
        }
        using var probeCts = linked;
        bool acquired = false;
        try
        {
            await _probeLock.WaitAsync(probeCts.Token).ConfigureAwait(false);
            acquired = true;
            lock (_stateLock)
            {
                if (_disposed || generation != _generation) return GetSnapshot();
                DiscordState = ServiceHealthState.Checking;
                YouTubeState = ServiceHealthState.Checking;
                _revision++;
            }
            NotifyState();

            // Run Discord and YouTube probes
            var dcTask = RunProbeSafelyAsync(_discordProbe, probeCts.Token);
            var ytTask = RunProbeSafelyAsync(_youtubeProbe, probeCts.Token);

            await Task.WhenAll(dcTask, ytTask).ConfigureAwait(false);

            var (dcOk, dcText) = dcTask.Result;
            var (ytOk, ytText) = ytTask.Result;

            lock (_stateLock)
            {
                if (_disposed || generation != _generation) return GetSnapshot();
                probeCts.Token.ThrowIfCancellationRequested();
                DiscordState = dcOk switch { true => ServiceHealthState.Working, false => ServiceHealthState.Failed, _ => ServiceHealthState.Inconclusive };
                DiscordDetails = dcText;
                YouTubeState = ytOk switch { true => ServiceHealthState.Working, false => ServiceHealthState.Failed, _ => ServiceHealthState.Inconclusive };
                YouTubeDetails = ytText;
                _timestamp = DateTime.UtcNow;
                _revision++;
            }

            var snapshot = GetSnapshot();
            NotifyState();
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            lock (_stateLock)
            {
                if (!_disposed && generation == _generation && acquired)
                {
                    DiscordState = ServiceHealthState.NotChecked;
                    YouTubeState = ServiceHealthState.NotChecked;
                    DiscordDetails = YouTubeDetails = Loc.T("Проверка отменена");
                    _timestamp = default;
                    _revision++;
                }
            }
            NotifyState();
            ct.ThrowIfCancellationRequested();
            return GetSnapshot();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ServiceHealthMonitor] Probe error: {ex}");
            lock (_stateLock)
            {
                if (!_disposed && generation == _generation)
                {
                    DiscordState = ServiceHealthState.Inconclusive;
                    YouTubeState = ServiceHealthState.Inconclusive;
                    DiscordDetails = YouTubeDetails = ex.Message;
                    _timestamp = DateTime.UtcNow;
                    _revision++;
                }
            }
            NotifyState();
            return GetSnapshot();
        }
        finally
        {
            if (acquired) _probeLock.Release();
            lock (_stateLock)
            {
                _activeProbes--;
                if (_disposed && _activeProbes == 0) _probeLock.Dispose();
            }
        }
    }

    public void StartWatchdog(AutoScope scope = AutoScope.Both)
    {
        if (_disposed || (IsWatchdogRunning && _watchdogScope == scope)) return;
        StopWatchdog();
        _watchdogScope = scope;
        _watchdogCts = new CancellationTokenSource();
        _ = WatchdogLoopAsync(scope, _watchdogCts.Token);
    }

    private static async Task<(bool? ok, string details)> RunProbeSafelyAsync(
        Func<CancellationToken, Task<(bool? ok, string details)>> probe, CancellationToken ct)
    {
        try { return await probe(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Trace.WriteLine("[service probe] " + ex);
            return (null, Loc.T("Ошибка проверки: {0}", ex.Message));
        }
    }

    public void StopWatchdog()
    {
        try
        {
            _watchdogCts?.Cancel();
            _watchdogCts?.Dispose();
        }
        catch { }
        _watchdogCts = null;
    }

    private async Task WatchdogLoopAsync(AutoScope scope, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(WatchdogIntervalSec), ct).ConfigureAwait(false);

                // Run lightweight health probe
                var snapshot = await ProbeAllAsync(ct).ConfigureAwait(false);

                if (!snapshot.IsConclusiveFor(scope))
                {
                    _consecutiveFailures = 0;
                    continue;
                }

                if (snapshot.IsHealthyFor(scope))
                {
                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures++;

                    // Short retry
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySec), ct).ConfigureAwait(false);
                    var retrySnapshot = await ProbeAllAsync(ct).ConfigureAwait(false);

                    if (!retrySnapshot.IsConclusiveFor(scope))
                    {
                        _consecutiveFailures = 0;
                        continue;
                    }

                    if (!retrySnapshot.IsHealthyFor(scope))
                    {
                        if (_consecutiveFailures >= MaxConsecutiveFailures)
                        {
                            if ((DateTime.UtcNow - _lastDegradedSignal).TotalSeconds >= CooldownSec)
                            {
                                _lastDegradedSignal = DateTime.UtcNow;
                                try
                                {
                                    HealthDegraded?.Invoke();
                                }
                                catch { }
                            }
                        }
                    }
                    else
                    {
                        _consecutiveFailures = 0;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ServiceHealthMonitor] Watchdog loop error: {ex.Message}");
            }
        }
    }

    private static async Task<(bool? ok, string details)> ProbeDiscordAsync(CancellationToken ct)
    {
        var result = await ApplicationFlowProbe.ProbeDiscordAsync(ct).ConfigureAwait(false);
        return (result.Passed, result.Detail);
    }

    private static async Task<(bool? ok, string details)> ProbeYouTubeAsync(CancellationToken ct)
    {
        var appTask = YouTubeApplicationProbe.CheckAsync(ct);
        var videoTask = ApplicationFlowProbe.ProbeYouTubeVideoAsync(ct);

        await Task.WhenAll(appTask, videoTask).ConfigureAwait(false);

        var appResult = appTask.Result;
        var video = videoTask.Result;

        if (!appResult.Ok)
        {
            return (false, appResult.Details);
        }

        return (video.Passed is null ? null : video.Passed == true && video.Bytes >= ApplicationFlowProbe.VideoSampleBytes,
            "HTML целиком + фрагмент JavaScript · " + video.Detail + ". HTTP/3 и настройки браузера не проверены.");
    }

    private void NotifyState()
    {
        var snap = GetSnapshot();
        try
        {
            HealthChanged?.Invoke(snap);
        }
        catch { }
    }

    public ServiceHealthSnapshot GetSnapshot()
    {
        lock (_stateLock)
            return new(DiscordState, YouTubeState, TelegramState, _timestamp, _revision, DiscordDetails, YouTubeDetails);
    }

    public void Dispose()
    {
        StopWatchdog();
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            if (_activeProbes == 0) _probeLock.Dispose();
        }
        _sessionCts.Cancel();
        _sessionCts.Dispose();
    }
}
