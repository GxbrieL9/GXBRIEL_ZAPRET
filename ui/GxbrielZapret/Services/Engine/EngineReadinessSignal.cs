namespace GxbrielZapret.Services.Engine;

// Process callbacks can outlive a startup wait. No disposable wait handle is captured here.
internal sealed class EngineReadinessSignal
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Confirmed => _completion.Task.IsCompletedSuccessfully && _completion.Task.Result;
    internal static bool IsCaptureReady(string? line) => !string.IsNullOrWhiteSpace(line) &&
        line.Contains("capture is started", StringComparison.OrdinalIgnoreCase);
    internal void ObserveOutput(string? line) { if (IsCaptureReady(line)) _completion.TrySetResult(true); }
    internal void ObserveExit() => _completion.TrySetResult(false);
    internal bool Wait(int milliseconds) => _completion.Task.Wait(milliseconds) && _completion.Task.Result;
    internal async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { return await _completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
    }
}
