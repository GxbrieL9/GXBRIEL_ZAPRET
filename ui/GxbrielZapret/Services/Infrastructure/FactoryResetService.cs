using System.Diagnostics;
using System.IO;

namespace GxbrielZapret.Services.Infrastructure;

public sealed record FactoryResetResult(int DeletedDirectories, int DeletedFiles, IReadOnlyList<string> Errors, bool CriticalDataWiped = true)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Removes all mutable GXBRIEL_ZAPRET state so the next launch starts like a fresh install.
/// </summary>
public sealed class FactoryResetService
{
    private static readonly string[] LingeringProcesses =
    {
        "winws2", "winws", "nfqws", "nfqws2", "goodbyedpi", "ciadpi", "byedpi", "spoofdpi", "dpitunnel", "green_tunnel", "zapret", "tcpvcon64", "tcpvcon", "mdig", "ip2net"
    };

    public FactoryResetResult Reset()
    {
        KillLingeringProcesses();
        StopDriverServices();
        try { Platform.RiotRoutingService.RemoveHostsFix(); } catch { }

        int directories = 0;
        int files = 0;
        var errors = new List<string>();

        foreach (string file in FilesToDelete())
        {
            try
            {
                if (!File.Exists(file)) continue;
                RemoveReadOnly(file);
                File.Delete(file);
                files++;
            }
            catch (Exception ex)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }

        foreach (string directory in DirectoriesToDelete())
        {
            SafeDeleteDirectory(directory, ref directories, ref files, errors);
        }

        // Also clean up any lingering root files in AppDataRoot
        try
        {
            if (Directory.Exists(AppPaths.AppDataRoot))
            {
                foreach (string strayFile in Directory.EnumerateFiles(AppPaths.AppDataRoot))
                {
                    try
                    {
                        RemoveReadOnly(strayFile);
                        File.Delete(strayFile);
                        files++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{strayFile}: {ex.Message}");
                    }
                }
            }
        }
        catch { }

        // Also clean up system runtime directory if under resolved ProgramDataRoot
        try
        {
            string? programDataRoot = Path.GetDirectoryName(AppPaths.SystemRuntimeDir);
            if (!string.IsNullOrWhiteSpace(programDataRoot) && Directory.Exists(programDataRoot))
            {
                SafeDeleteDirectory(programDataRoot, ref directories, ref files, errors);
            }
        }
        catch { }

        try
        {
            AppPaths.ResetSystemRuntimeDirCache();
            AppPaths.ResetEngineDirCache();
        }
        catch { }

        bool criticalWiped = !Directory.Exists(AppPaths.ConfigDir)
                          && !Directory.Exists(AppPaths.ListsDir)
                          && !Directory.Exists(AppPaths.StateDir);

        return new FactoryResetResult(directories, files, errors, criticalWiped);
    }

    private static void SafeDeleteDirectory(string directory, ref int dirs, ref int files, List<string> errors)
    {
        try
        {
            if (!Directory.Exists(directory)) return;

            // Clear read-only flags and delete all files inside
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    RemoveReadOnly(file);
                    File.Delete(file);
                    files++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{file}: {ex.Message}");
                }
            }

            // Remove subdirectories from deepest first
            foreach (string subDir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(subDir))
                    {
                        RemoveReadOnly(subDir);
                        Directory.Delete(subDir, recursive: false);
                        dirs++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{subDir}: {ex.Message}");
                }
            }

            if (Directory.Exists(directory))
            {
                RemoveReadOnly(directory);
                Directory.Delete(directory, recursive: true);
                dirs++;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{directory}: {ex.Message}");
        }
    }

    private static void RemoveReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    private static void KillLingeringProcesses()
    {
        foreach (string name in LingeringProcesses)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(3000);
                        }
                    }
                    catch { }
                    finally
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    private static void StopDriverServices()
    {
        string[] services = { "WinDivert", "WinDivert14" };
        foreach (string svc in services)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"stop {svc}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                p?.WaitForExit(2000);
            }
            catch { }
        }
    }

    internal static IReadOnlyList<string> DirectoriesToDelete() =>
    [
        AppPaths.ConfigDir,
        AppPaths.ProfilesDir,
        AppPaths.ListsDir,
        AppPaths.NetworksDir,
        AppPaths.StrategiesDir,
        AppPaths.TestsDir,
        AppPaths.DiscoveryDir,
        AppPaths.TelemetryDir,
        AppPaths.TelegramDir,
        AppPaths.LogsDir,
        AppPaths.CacheDir,
        AppPaths.TempDir,
        AppPaths.BackupsDir,
        AppPaths.StateDir,
        AppPaths.EngineDir,
    ];

    internal static IReadOnlyList<string> FilesToDelete() =>
    [
        AppPaths.SettingsFile,
        AppPaths.DiscoveryTargetsFile,
        AppPaths.PresetsFile,
        AppPaths.EngineStateFile,
    ];
}
