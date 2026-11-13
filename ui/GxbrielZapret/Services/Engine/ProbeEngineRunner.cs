using System.Diagnostics;
using System.IO;
using System.Text;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.Services.Engine;

internal interface IProbeEngineRunner : IDisposable
{
    bool IsAlive { get; }
    bool IsReady { get; }
    string DiagnosticOutput => "";
    void Start(List<string> args);
    Task WaitReadyAsync(CancellationToken ct);
    void Stop();
}

/// <summary>
/// Runs a THROWAWAY winws2 process for probing — shared by the auto-selector and the strategy
/// generator, which both cycle "launch a candidate → probe the goal hosts → kill it" dozens of times.
///
/// Deliberately NOT <see cref="EngineService"/>: that one owns the long-lived bypass process (state
/// machine, journal streaming, TCP-timestamps tuning, restart-on-strategy-change) and must stay
/// separate. This class owns candidate processes and their temporary TCP timestamp setting.
///
/// It exists because the two callers previously carried byte-identical private copies of all of this,
/// and the readiness wait below had to be written twice when it was introduced. Argument building
/// stays with the callers — that part legitimately differs (bypassAll vs gameFilter).
/// </summary>
internal sealed class ProbeEngineRunner : IProbeEngineRunner
{
    /// <summary>Cap on the explicit WinDivert-capture wait; timeout does not confirm readiness.</summary>
    private const int ReadyCapMs = 3000;

    /// <summary>Let WinDivert go live after the log line before traffic is probed (matching proven old GX startup delay).</summary>
    private const int SettleMs = 1200;

    /// <summary>Grace period for the killed process to actually detach from WinDivert.</summary>
    private const int ExitWaitMs = 4000;

    private Process? _proc;
    private readonly TcpTimestampsService? _timestamps;
    private readonly Queue<string> _diagnostics = new();
    public string DiagnosticOutput { get { lock (_diagnostics) return string.Join(" | ", _diagnostics); } }
    private void Observe(EngineReadinessSignal ready, string? line)
    {
        ready.ObserveOutput(line);
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_diagnostics)
        {
            _diagnostics.Enqueue(line.Length > 512 ? line[..512] : line);
            while (_diagnostics.Count > 16) _diagnostics.Dequeue();
        }
    }
    // Signalled when the running candidate's winws2 reports WinDivert is attached. Recreated per
    // Start; each process's handlers capture their own instance.
    private EngineReadinessSignal? _ready;

    private readonly string? _exeOverride;
    private readonly string? _workDirOverride;

    /// <summary>Production use: the installed winws2.</summary>
    public ProbeEngineRunner() { _timestamps = new TcpTimestampsService(); }

    /// <summary>Test seam. winws2 needs admin and monopolises WinDivert, so the process lifecycle here
    /// (readiness detection, early-exit unblock, kill) can only be exercised with a stand-in binary.</summary>
    internal ProbeEngineRunner(string exePath, string workDir)
    {
        _exeOverride = exePath;
        _workDirOverride = workDir;
    }

    /// <summary>False once the candidate's engine has died — e.g. bad arguments — so the caller can
    /// score it as a total failure instead of probing against no engine at all.</summary>
    public bool IsAlive { get { try { return _proc is { HasExited: false }; } catch { return false; } } }
    public bool IsReady => _ready?.Confirmed == true && IsAlive;

    /// <summary>Test seam: identifies the current process so a test can prove that a second
    /// <see cref="Start"/> really replaced the first one. 0 when nothing is running.</summary>
    internal int ProcessId
    {
        get { try { return _proc?.Id ?? 0; } catch { return 0; } }
    }

    public void Start(List<string> args)
    {
        Stop(); // defensive: never leave a previous candidate's winws2 alive (two engines would fight
                // over WinDivert and poison every subsequent probe).
        lock (_diagnostics) _diagnostics.Clear();

        // Supply-chain integrity gate: verify SHA-256 before starting candidate process
        string targetExe = _exeOverride ?? AppPaths.WinwsExe;
        EngineValidator.EnsureEngineVerified(targetExe);
        var psi = new ProcessStartInfo
        {
            // Resolved per Start (not cached in the ctor) so the paths keep the same late binding they
            // had when each service built the ProcessStartInfo itself.
            FileName = _exeOverride ?? AppPaths.WinwsExe,
            WorkingDirectory = _workDirOverride ?? AppPaths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        var ready = new EngineReadinessSignal();
        _ready = ready;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Signal readiness on the WinDivert line; also unblock on early exit (bad args) so a candidate
        // whose engine dies instantly fails fast instead of waiting out the cap.
        p.OutputDataReceived += (_, e) => Observe(ready, e.Data);
        p.ErrorDataReceived += (_, e) => Observe(ready, e.Data);
        p.Exited += (_, _) =>
        {
            ready.ObserveExit();
            try { Observe(ready, $"process exited: {p.ExitCode} (0x{unchecked((uint)p.ExitCode):X8})"); }
            catch (InvalidOperationException) { }
        };
        _proc = p;
        try
        {
            // Match production EngineService: tcp_ts fooling requires outgoing TCP timestamps.
            // The process test seam never changes global network settings.
            _timestamps?.EnableForSession();
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Bounded wait for explicit capture readiness. A living process alone is insufficient.</summary>
    public async Task WaitReadyAsync(CancellationToken ct)
    {
        var ready = _ready;
        if (ready is null)
        {
            return;
        }
        if (await ready.WaitAsync(TimeSpan.FromMilliseconds(ReadyCapMs), ct).ConfigureAwait(false))
            await Task.Delay(SettleMs, ct).ConfigureAwait(false);
    }

    public void Stop()
    {
        var process = _proc;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(ExitWaitMs))
                    throw new IOException("Процесс проверяемой стратегии не завершился. Следующий кандидат не будет запущен.");
            }
        }
        catch (InvalidOperationException) { /* no process was started */ }
        // Preserve ownership on access denied/timeout: silently dropping this handle permits two engines.
        process.Dispose();
        _proc = null;
        _ready = null;
        _timestamps?.RestoreAfterSession();
    }

    public void Dispose() => Stop();
}
