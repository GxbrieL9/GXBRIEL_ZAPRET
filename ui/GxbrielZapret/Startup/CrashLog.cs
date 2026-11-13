using System.IO;
using System.Windows;
using System.Windows.Threading;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Startup;

/// <summary>
/// Last-resort crash journal (<c>logs\fatal.log</c>). Every write is best-effort: a logger that can
/// throw would turn a recoverable UI exception into a silent process death.
/// </summary>
internal static class CrashLog
{
    internal static void RecordLifecycle(string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "lifecycle.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PID {Environment.ProcessId}: {message}\n");
        }
        catch { }
    }
    /// <summary>Catch what escapes the dispatcher (shown to the user, then swallowed so the app keeps
    /// running) and what escapes any other thread (nothing can be done, but it gets recorded).</summary>
    internal static void Install(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Write(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            if (!IsBenignUnobservedException(args.Exception))
            {
                Write(args.Exception);
            }
        };
    }

    private static bool IsBenignUnobservedException(Exception? ex)
    {
        if (ex is null) return true;
        if (ex is AggregateException agg)
            return agg.Flatten().InnerExceptions.All(IsBenignUnobservedException);

        if (ex is OperationCanceledException or TaskCanceledException)
            return true;

        if (ex is System.Net.Sockets.SocketException se)
        {
            return se.SocketErrorCode is System.Net.Sockets.SocketError.OperationAborted // 995
                                      or System.Net.Sockets.SocketError.TimedOut         // 10060
                                      or System.Net.Sockets.SocketError.ConnectionReset  // 10054
                                      or System.Net.Sockets.SocketError.ConnectionRefused// 10061
                                      or System.Net.Sockets.SocketError.ConnectionAborted// 10053
                                      or System.Net.Sockets.SocketError.HostUnreachable
                                      or System.Net.Sockets.SocketError.NetworkUnreachable;
        }

        if (ex is System.IO.IOException ioe && ioe.InnerException is System.Net.Sockets.SocketException innerSe)
            return IsBenignUnobservedException(innerSe);

        return false;
    }

    internal static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "fatal.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write(e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show(e.Exception.Message, Loc.T("GXBRIEL_ZAPRET — ошибка"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
