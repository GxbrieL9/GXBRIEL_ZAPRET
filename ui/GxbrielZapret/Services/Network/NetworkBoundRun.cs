namespace GxbrielZapret.Services.Network;

internal sealed class NetworkBoundRun : IAsyncDisposable
{
    private readonly Func<string?> _current;
    private readonly CancellationTokenSource _stop;
    private readonly Task _watch;
    public string Fingerprint { get; }
    private int _networkChanged;
    public bool NetworkChanged => Volatile.Read(ref _networkChanged) != 0;
    public CancellationToken Token => _stop.Token;

    public NetworkBoundRun(Func<string?> current, CancellationToken userToken, TimeSpan? interval = null)
    {
        _current = current;
        Fingerprint = current() ?? throw new InvalidOperationException("Не удалось определить сеть. Подключитесь к сети и повторите подбор.");
        if (string.IsNullOrWhiteSpace(Fingerprint) || Fingerprint == "default") throw new InvalidOperationException("Не удалось определить сеть.");
        _stop = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        _watch = WatchAsync(interval ?? TimeSpan.FromSeconds(2));
    }

    public void Checkpoint()
    {
        string? current;
        try { current = _current(); }
        catch { current = null; }
        if (!string.Equals(current, Fingerprint, StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _networkChanged, 1);
            _stop.Cancel();
        }
        Token.ThrowIfCancellationRequested();
    }

    private async Task WatchAsync(TimeSpan interval)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, Token).ConfigureAwait(false);
                await Task.Run(Checkpoint, Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _watch.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
