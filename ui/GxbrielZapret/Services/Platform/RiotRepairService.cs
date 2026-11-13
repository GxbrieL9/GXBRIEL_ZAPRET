using System.Diagnostics;
using System.IO;
using System.Management;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Platform;

/// <summary>
/// Targeted repair path for the Riot/League failure mode where the client loads but entitlement-token
/// TLS handshakes reset. The fix must stay narrow: only the entitlement endpoint is desynced, while the
/// rest of Riot auth/chat/config traffic remains direct.
/// </summary>
public static class RiotRepairService
{
    public const string MinimalEntitlementsListName = "list-riot-entitlements-minimal.txt";

    private static readonly string[] MinimalEntitlementHosts =
    {
        "entitlements.auth.riotgames.com",
        "entitlements.auth.riotgames.com.cdn.cloudflare.net"
    };

    private static readonly string[] LegacyOptionMarkers =
    {
        "list-ultimate",
        "ipset-all",
        "list-riot-entitlements.txt",
        "--wf-tcp-out=80,443-65535",
        "--wf-udp-out=443,19294-19344,50000-65535"
    };

    public sealed record StartupMaintenanceResult(
        int LegacyOptionsNeutralized,
        IReadOnlyList<string> Notes);

    public sealed record HelperResult(bool StartedOrAlreadyRunning, int? ProcessId, string Message);

    public static StartupMaintenanceResult RunStartupMaintenance()
    {
        var notes = new List<string>();
        EnsureMinimalEntitlementsHostlist();

        int neutralized = NeutralizeDangerousLegacyOptions(notes);
        notes.AddRange(DetectLegacyAutostartTasks());

        return new StartupMaintenanceResult(neutralized, notes);
    }

    public static string EnsureMinimalEntitlementsHostlist()
    {
        Directory.CreateDirectory(AppPaths.ListsDir);
        Directory.CreateDirectory(AppPaths.RuntimeListsDir);

        string source = Path.Combine(AppPaths.ListsDir, MinimalEntitlementsListName);
        WriteMinimalHostlist(source);

        string runtime = Path.Combine(AppPaths.RuntimeListsDir, MinimalEntitlementsListName);
        WriteMinimalHostlist(runtime);
        return runtime;
    }

    public static HelperResult EnsureMinimalEntitlementsHelperRunning()
    {
        try
        {
            if (!File.Exists(AppPaths.WinwsExe))
                return new HelperResult(false, null, Loc.T("Riot repair: winws2.exe не найден."));

            string hostlist = EnsureMinimalEntitlementsHostlist();
            var existing = FindMinimalHelperProcess(hostlist);
            if (existing is not null)
            {
                int pid = existing.Id;
                existing.Dispose();
                return new HelperResult(true, pid, Loc.T("Riot repair: минимальный entitlements-helper уже запущен (PID {0}).", pid));
            }

            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.WinwsExe,
                WorkingDirectory = AppPaths.EngineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in BuildMinimalEntitlementsArguments(hostlist))
                psi.ArgumentList.Add(arg);

            var proc = Process.Start(psi);
            return proc is null
                ? new HelperResult(false, null, Loc.T("Riot repair: не удалось запустить минимальный entitlements-helper."))
                : new HelperResult(true, proc.Id, Loc.T("Riot repair: запущен минимальный entitlements-helper (PID {0}).", proc.Id));
        }
        catch (Exception ex)
        {
            return new HelperResult(false, null, Loc.T("Riot repair: ошибка запуска entitlements-helper: {0}", ex.Message));
        }
    }

    public static List<string> BuildMinimalEntitlementsArguments(string? hostlistPath = null)
    {
        string hostlist = string.IsNullOrWhiteSpace(hostlistPath)
            ? EnsureMinimalEntitlementsHostlist()
            : hostlistPath;

        return new List<string>
        {
            "--debug=1",
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-lib.lua"),
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-antidpi.lua"),
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-auto.lua"),
            "--wf-tcp-out=443",
            "--filter-tcp=443",
            "--filter-l7=tls",
            "--hostlist=" + hostlist,
            "--payload=tls_client_hello",
            "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:tls_mod=rnd,dupsid:repeats=8",
            "--lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google:optional"
        };
    }

    public static bool IsRiotOrLeagueRunning()
    {
        string[] names =
        {
            "RiotClientServices",
            "Riot Client",
            "LeagueClient",
            "LeagueClientUx",
            "LeagueClientUxRender",
            "LeagueOfLegends"
        };

        foreach (string name in names)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            catch { }
        }
        return false;
    }

    private static void WriteMinimalHostlist(string path)
    {
        string text = string.Join(Environment.NewLine, MinimalEntitlementHosts) + Environment.NewLine;
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal))
            return;
        File.WriteAllText(path, text);
    }

    private static int NeutralizeDangerousLegacyOptions(List<string> notes)
    {
        int count = 0;
        foreach (string file in EnumerateLegacyOptionsFiles())
        {
            try
            {
                string text = File.ReadAllText(file);
                if (text.Contains("disabled by GXBRIEL_ZAPRET Riot repair", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool dangerous = LegacyOptionMarkers.Any(marker =>
                    text.Contains(marker, StringComparison.OrdinalIgnoreCase));
                if (!dangerous) continue;

                string backup = file + ".disabled-riot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(file, backup, overwrite: true);
                File.WriteAllText(file,
                    "# disabled by GXBRIEL_ZAPRET Riot repair: legacy direct winws options broke Riot/League TLS" +
                    Environment.NewLine);
                count++;
                notes.Add(Loc.T("Riot repair: legacy managed-winws-options.txt нейтрализован: {0}", file));
            }
            catch (Exception ex)
            {
                notes.Add(Loc.T("Riot repair: не удалось проверить legacy options {0}: {1}", file, ex.Message));
            }
        }
        return count;
    }

    private static IEnumerable<string> EnumerateLegacyOptionsFiles()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            try { roots.Add(Path.GetFullPath(root)); } catch { }
        }

        AddRoot(AppPaths.ConfigDir);
        AddRoot(Path.Combine(AppContext.BaseDirectory, "config"));
        AddRoot(Path.Combine(Directory.GetCurrentDirectory(), "config"));
        AddRoot(Path.Combine(AppPaths.ProjectRoot, "config"));

        foreach (string root in roots)
        {
            string file = Path.Combine(root, "managed-winws-options.txt");
            if (File.Exists(file)) yield return file;
        }
    }

    private static IReadOnlyList<string> DetectLegacyAutostartTasks()
    {
        var notes = new List<string>();
        string[] taskNames =
        {
            "GXBRIEL_ZAPRET_Autostart",
            "GXBRIEL_ZAPRET Autostart",
            "GxbrielZapret Autostart"
        };

        foreach (string task in taskNames)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{task}\" /XML",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p is null) continue;
                string xml = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(xml)) continue;

                bool legacyDirectWinws =
                    xml.Contains("managed-winws-options.txt", StringComparison.OrdinalIgnoreCase) ||
                    xml.Contains("profiles\\bin\\winws.exe", StringComparison.OrdinalIgnoreCase) ||
                    xml.Contains("profiles/bin/winws.exe", StringComparison.OrdinalIgnoreCase);

                if (legacyDirectWinws)
                    notes.Add(Loc.T("Riot repair: найдена старая задача автозапуска {0}, запускающая winws напрямую. Старый options-файл будет нейтрализован.", task));
            }
            catch { }
        }

        return notes;
    }

    private static Process? FindMinimalHelperProcess(string hostlistPath)
    {
        string normalized = Path.GetFullPath(hostlistPath);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name='winws2.exe' OR Name='winws.exe'");
            foreach (ManagementObject obj in searcher.Get())
            {
                string? cmd = obj["CommandLine"]?.ToString();
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                if (!cmd.Contains(MinimalEntitlementsListName, StringComparison.OrdinalIgnoreCase) &&
                    !cmd.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (int.TryParse(obj["ProcessId"]?.ToString(), out int pid))
                {
                    try { return Process.GetProcessById(pid); } catch { }
                }
            }
        }
        catch { }
        return null;
    }
}
