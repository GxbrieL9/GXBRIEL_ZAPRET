using System.Diagnostics;
using System.IO;
using System.Management;
using GxbrielZapret.Localization;

namespace GxbrielZapret.Services.Platform;

public sealed class BypassCleanupService
{
    private readonly Action _stopOwned;
    private readonly Func<IReadOnlyList<string>> _inspect;
    public BypassCleanupService()
    {
        _stopOwned = () => EngineProcessOwnership.StopOwned();
        _inspect = GetActiveBypassProcesses;
    }
    internal BypassCleanupService(Action stopOwned, Func<IReadOnlyList<string>> inspect)
    {
        _stopOwned = stopOwned; _inspect = inspect;
    }
    private static readonly string[] ProcessNames =
    {
        "winws",
        "winws2",
        "nfqws",
        "nfqws2",
        "goodbyedpi",
        "ciadpi",
        "byedpi",
        "spoofdpi",
        "dpitunnel",
        "green_tunnel",
        "zapret"
    };

    public sealed record Result(bool Clean, string Message, IReadOnlyList<string> Remaining);

    public async Task<Result> StopAndVerifyCleanAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            _stopOwned();
            ct.ThrowIfCancellationRequested();
            var remaining = _inspect();
            ct.ThrowIfCancellationRequested();
            if (remaining.Count == 0)
                return new Result(true, Loc.T("Окружение очищено: активных сторонних DPI-обходов не найдено."), remaining);
            return new Result(false,
                Loc.T("Обнаружены конфликтующие процессы обхода. Закройте их вручную и повторите запуск. Сторонние программы автоматически не завершались."), remaining);
        }, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<string> GetActiveBypassProcesses()
    {
        var result = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                string bare = Path.GetFileNameWithoutExtension(name);
                if (!LooksLikeBypassProcess(bare, obj["ExecutablePath"]?.ToString()))
                    continue;

                string pid = obj["ProcessId"]?.ToString() ?? "?";
                string cmd = obj["CommandLine"]?.ToString() ?? obj["ExecutablePath"]?.ToString() ?? name;
                result.Add($"{name} PID {pid}: {cmd}");
            }
        }
        catch
        {
            foreach (var procName in ProcessNames)
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try { result.Add($"{p.ProcessName} PID {p.Id}: {p.MainModule?.FileName ?? ""}"); }
                    catch { result.Add($"{p.ProcessName} PID {p.Id}"); }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
        }
        return result;
    }

    private static bool LooksLikeBypassProcess(string processName, string? path)
    {
        string name = (processName ?? "").ToLowerInvariant();
        string file = Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant();

        // Command lines of harmless developer tools often contain words like winws/zapret because they
        // run diagnostics from this repository. Only actual bypass executables should poison a strict
        // verification environment.
        return ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || ProcessNames.Contains(file, StringComparer.OrdinalIgnoreCase);
    }

    // Conflict detection does not authorize ending another application's logon task.
    internal static bool CanStopScheduledTask(string taskName) => false;
}
