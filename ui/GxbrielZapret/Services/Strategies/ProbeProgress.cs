using System.Diagnostics;

namespace GxbrielZapret.Services.Strategies;

// A heartbeat is independent of socket reads: waiting on the network remains visible.
internal sealed class ProbeProgress : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<string>? _report;
    private readonly Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _stage = "Подготовка", _detail = "";
    private int _budget;
    private bool _disposed;
    internal ProbeProgress(Action<string>? report)
    {
        _report = report;
        _timer = new Timer(_ => Publish(), null, 1000, 1000);
    }
    internal void Stage(string stage, int budget = 0)
    {
        lock (_gate) { _stage = stage; _detail = ""; _budget = budget; _clock.Restart(); Publish(); }
    }
    internal void Update(string detail)
    {
        lock (_gate) { _detail = detail; Publish(); }
    }
    private void Publish()
    {
        lock (_gate)
        {
            if (_disposed || _report is null) return;
            int elapsed = (int)_clock.Elapsed.TotalSeconds;
            string remaining = _budget > 0 ? $" · до тайм-аута {Math.Max(0, _budget - elapsed)} с" : "";
            _report($"{_stage} · {elapsed} с{remaining}" + (_detail.Length == 0 ? "" : "\n" + _detail));
        }
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _timer.Dispose(); }
    }
}
