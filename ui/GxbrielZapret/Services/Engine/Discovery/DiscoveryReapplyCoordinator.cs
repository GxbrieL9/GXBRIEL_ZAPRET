using GxbrielZapret.Localization;

namespace GxbrielZapret.Services.Engine.Discovery;

/// <summary>
/// Coordinates dynamic endpoint discovery with live engine reapplication.
/// Implements debouncing, coalescing, race-condition safety, and manual-stop precedence.
/// </summary>
public sealed class DiscoveryReapplyCoordinator : IDisposable
{
    private readonly EngineService _engine;
    private readonly DiscoveryStore _store;
    private readonly object _lock = new();
    private readonly TimeSpan _debounceInterval;
    private CancellationTokenSource? _debounceCts;
    private int _pendingCoalescedCount;
    private bool _isDisposed;

    public int ReapplyCount { get; private set; }
    public int CoalescedDiscoveriesCount { get; private set; }

    public event Action<string>? ReapplyTriggered;

    public DiscoveryReapplyCoordinator(EngineService engine, DiscoveryStore store, TimeSpan? debounceInterval = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _debounceInterval = debounceInterval ?? TimeSpan.FromMilliseconds(400);

        // Cancel pending reapply if user manually stops the engine
        _engine.StateChanged += OnEngineStateChanged;
    }

    private void OnEngineStateChanged(EngineState state)
    {
        if (state is EngineState.Stopping or EngineState.Stopped)
        {
            CancelPendingReapply("Engine stopping or stopped by user");
        }
    }

    /// <summary>
    /// Submits a raw endpoint into the pipeline.
    /// If accepted as a new effective item, materializes runtime lists and schedules a debounced live reapply if the engine is running.
    /// </summary>
    public PipelineResult IngestAndReapply(RawEndpointInput input)
    {
        var result = _store.IngestEndpoint(input);

        if (result.IsAccepted)
        {
            // Check if record was created or updated
            // Stage 8 (RuntimeMaterialized) is only recorded if this is a newly effective item
            bool isNewEffective = result.StageLog.Any(s => s.Contains("Created new persistent record"));

            if (isNewEffective)
            {
                lock (_lock)
                {
                    _pendingCoalescedCount++;
                    CoalescedDiscoveriesCount++;
                }

                if (_engine.IsRunning)
                {
                    ScheduleDebouncedReapply($"Discovered new {result.Kind}: {result.CanonicalValue} ({result.TargetSlug})");
                    result.RecordStage(DiscoveryStage.Applied, "Scheduled debounced live reapply");
                }
                else
                {
                    result.RecordStage(DiscoveryStage.Applied, "Engine stopped: persisted for next manual launch");
                }
            }
            else
            {
                result.RecordStage(DiscoveryStage.Applied, "Duplicate item: updated metadata only, reapply skipped");
            }
        }

        return result;
    }

    /// <summary>
    /// Ingests multiple endpoints, materializes runtime lists for all affected slugs, and schedules a single debounced reapply.
    /// </summary>
    public List<PipelineResult> IngestBatch(IEnumerable<RawEndpointInput> inputs)
    {
        var results = new List<PipelineResult>();
        int newEffectiveCount = 0;
        var affectedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            var r = _store.IngestEndpoint(input);
            results.Add(r);
            if (r.IsAccepted && r.StageLog.Any(s => s.Contains("Created new persistent record")))
            {
                newEffectiveCount++;
                affectedSlugs.Add(r.TargetSlug);
            }
        }

        if (newEffectiveCount > 0)
        {
            foreach (var slug in affectedSlugs)
            {
                _store.MaterializeTargetList(slug);
            }

            lock (_lock)
            {
                _pendingCoalescedCount += newEffectiveCount;
                CoalescedDiscoveriesCount += newEffectiveCount;
            }

            if (_engine.IsRunning)
            {
                ScheduleDebouncedReapply($"Batch of {newEffectiveCount} new endpoints across {affectedSlugs.Count} targets");
                foreach (var r in results.Where(x => x.IsAccepted))
                {
                    r.RecordStage(DiscoveryStage.Applied, "Scheduled debounced batch live reapply");
                }
            }
        }

        return results;
    }

    private void ScheduleDebouncedReapply(string reason)
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            // Reset debounce timer
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();

            var token = _debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounceInterval, token).ConfigureAwait(false);

                    if (token.IsCancellationRequested) return;

                    int count;
                    lock (_lock)
                    {
                        count = _pendingCoalescedCount;
                        _pendingCoalescedCount = 0;
                    }

                    if (_engine.IsRunning && count > 0)
                    {
                        bool success = await _engine.ReapplyActiveStrategyAsync(
                            $"Dynamic discovery reapply ({reason})", token).ConfigureAwait(false);

                        if (success)
                        {
                            lock (_lock) { ReapplyCount++; }
                            ReapplyTriggered?.Invoke(reason);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // Persistent state is never lost even if reapply fails
                    _engine.ReapplyActiveStrategy($"Failure recovery: {ex.Message}");
                }
            }, token);
        }
    }

    public void CancelPendingReapply(string reason = "")
    {
        lock (_lock)
        {
            if (_debounceCts is not null && !_debounceCts.IsCancellationRequested)
            {
                _debounceCts.Cancel();
                _debounceCts.Dispose();
                _debounceCts = null;
            }
            _pendingCoalescedCount = 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _engine.StateChanged -= OnEngineStateChanged;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }
}
