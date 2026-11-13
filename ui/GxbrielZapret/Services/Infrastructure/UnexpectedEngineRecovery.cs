using GxbrielZapret.Localization;

namespace GxbrielZapret.Services.Infrastructure;

/// <summary>Bounded recovery of unexpected process exits, independent of network health probes.</summary>
internal sealed class UnexpectedEngineRecovery : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<bool> _mayRecover;
    private readonly Func<CancellationToken, Task<bool>> _restart;
    private readonly Action<string> _report;
    private readonly TimeProvider _clock;
    private readonly TimeSpan[] _delays;
    private readonly Queue<DateTimeOffset> _attempts = new();
    private CancellationTokenSource? _cancellation;
    private Task _pending = Task.CompletedTask;
    private bool _running;
    private bool _disposed;
    private long _exitVersion;
    private static readonly TimeSpan BudgetWindow = TimeSpan.FromMinutes(2);
    internal event Action? StateChanged;
    internal bool IsRecovering
    {
        get { lock (_gate) return _running && _cancellation is { IsCancellationRequested: false }; }
    }

    internal UnexpectedEngineRecovery(Func<bool> mayRecover, Func<CancellationToken, Task<bool>> restart,
        Action<string>? report = null, TimeSpan[]? delays = null, TimeProvider? clock = null)
    {
        _mayRecover = mayRecover;
        _restart = restart;
        _report = report ?? (_ => { });
        _delays = delays ?? [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
        _clock = clock ?? TimeProvider.System;
    }

    internal Task NotifyUnexpectedExitAsync()
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed || !_mayRecover()) return Task.CompletedTask;
            _exitVersion++;
            if (_running && _cancellation is { IsCancellationRequested: false }) return _pending;
            var source = new CancellationTokenSource();
            _cancellation = source;
            _running = true;
            pending = _pending = Task.Run(() => RecoverAsync(source));
        }
        StateChanged?.Invoke();
        return pending;
    }

    private async Task RecoverAsync(CancellationTokenSource source)
    {
        var ct = source.Token;
        try
        {
            foreach (var delay in _delays)
            {
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
                if (!_mayRecover()) return;
                long version;
                lock (_gate)
                {
                    var now = _clock.GetUtcNow();
                    while (_attempts.TryPeek(out var attempted) && now - attempted >= BudgetWindow)
                        _attempts.Dequeue();
                    if (_attempts.Count >= 3) break;
                    _attempts.Enqueue(now);
                    version = _exitVersion;
                }
                ct.ThrowIfCancellationRequested();
                try
                {
                    bool restored = await _restart(ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        // An exit during the attempt must not disappear behind a late success.
                        if (restored && version == _exitVersion) return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _report(Loc.T("Ошибка автоматического восстановления: {0}", ex.Message)); }
            }
            if (!ct.IsCancellationRequested && _mayRecover())
                _report(Loc.T("Автоматическое восстановление приостановлено: лимит попыток исчерпан. Проверьте журнал и включите обход вручную."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            bool changed = false;
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, source))
                {
                    _cancellation = null;
                    _running = false;
                    changed = true;
                }
            }
            source.Dispose();
            if (changed) StateChanged?.Invoke();
        }
    }

    internal void Cancel()
    {
        lock (_gate) { _cancellation?.Cancel(); }
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _cancellation?.Cancel();
        }
    }
}
