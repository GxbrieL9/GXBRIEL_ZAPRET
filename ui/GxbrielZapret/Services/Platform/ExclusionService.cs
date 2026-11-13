using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Platform;

/// <summary>
/// Configures Windows Firewall allow-rules for GXBRIEL_ZAPRET and winws2.exe via netsh.exe.
/// In accordance with GXBRIEL_ZAPRET Zero-Trust security rules, automated modification
/// of Windows Defender exclusions via PowerShell is completely disabled.
/// </summary>
public sealed class ExclusionService
{
    /// <summary>Outcome of an apply run: whether every step succeeded + a human, per-step summary.</summary>
    public sealed record Result(bool AllOk, string Summary);

    /// <summary>Apply firewall rules. Safe to call repeatedly.</summary>
    public async Task<Result> ApplyAsync()
    {
        var log = new List<string>();
        bool ok = true;

        string? appExe = AppExePath();
        string winws = AppPaths.WinwsExe;

        // 1) Windows Defender exclusions
        await DefenderAllowAsync(log);

        // 2) Windows Firewall — allow the app, the engine, and known monitored game clients through.
        ok &= await FirewallAllowAsync("GXBRIEL_ZAPRET (движок)", winws, log);
        if (appExe is not null) ok &= await FirewallAllowAsync("GXBRIEL_ZAPRET", appExe, log);
        foreach (var gameExe in FindGameClientExes())
        {
            ok &= await FirewallAllowAsync($"GXBRIEL_ZAPRET · {Path.GetFileNameWithoutExtension(gameExe)}", gameExe, log);
        }

        return new Result(ok, string.Join('\n', log));
    }

    public async Task<Result> VerifyAsync()
    {
        var log = new List<string>();
        bool ok = true;

        string? appExe = AppExePath();
        string winws = AppPaths.WinwsExe;

        await DefenderVerifyAsync(log);
        ok &= await FirewallRuleExistsAsync("GXBRIEL_ZAPRET (движок)", winws, log);
        if (appExe is not null) ok &= await FirewallRuleExistsAsync("GXBRIEL_ZAPRET", appExe, log);
        foreach (var gameExe in FindGameClientExes())
        {
            ok &= await FirewallRuleExistsAsync($"GXBRIEL_ZAPRET · {Path.GetFileNameWithoutExtension(gameExe)}", gameExe, log);
        }

        return new Result(ok, string.Join('\n', log));
    }

    private static async Task<bool> DefenderAllowAsync(List<string> log)
    {
        try
        {
            string programData = AppPaths.SystemRuntimeDir;
            string appData = AppPaths.AppDataRoot;
            string? appExe = AppExePath();

            var paths = new List<string> { programData, appData };
            if (!string.IsNullOrWhiteSpace(appExe))
            {
                string? exeDir = Path.GetDirectoryName(appExe);
                if (!string.IsNullOrWhiteSpace(exeDir)) paths.Add(exeDir);
            }

            string pathsArg = string.Join(", ", paths.Select(p => $"'{p.Replace("'", "''")}'"));
            string psScript = $"Add-MpPreference -ExclusionPath {pathsArg} -ExclusionProcess 'winws2.exe' -ErrorAction SilentlyContinue";

            var (code, _) = await RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", psScript });
            if (code == 0)
            {
                log.Add("✓ " + Loc.T("Windows Defender: папки программы и процесс winws2.exe добавлены в исключения."));
                return true;
            }
            else
            {
                log.Add("ℹ " + Loc.T("Windows Defender: не удалось применить автоматически (проверьте права администратора)."));
                return true;
            }
        }
        catch (Exception ex)
        {
            log.Add("ℹ " + Loc.T("Windows Defender: {0}", ex.Message));
            return true;
        }
    }

    private static async Task<bool> DefenderVerifyAsync(List<string> log)
    {
        try
        {
            string psScript = "(Get-MpPreference).ExclusionPath";
            var (code, output) = await RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", psScript });
            if (code == 0 && output.Contains("GXBRIEL_ZAPRET", StringComparison.OrdinalIgnoreCase))
            {
                log.Add("✓ " + Loc.T("Windows Defender: папки программы находятся в списке исключений."));
                return true;
            }
        }
        catch { }
        log.Add("ℹ " + Loc.T("Windows Defender: если антивирус удаляет движок, нажмите «Добавить в исключения»."));
        return false;
    }

    /// <summary>The app's own .exe, or null when running as `dotnet …` in development.</summary>
    private static string? AppExePath()
    {
        string? p = Environment.ProcessPath;
        if (p is null) return null;
        if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (p.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return p;
    }

    private static IEnumerable<string> FindGameClientExes()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string exe in FindRunningGameClientExes())
            found.Add(exe);

        foreach (string exe in FindRiotInstallExes())
            found.Add(exe);

        foreach (string exe in found)
        {
            if (File.Exists(exe))
                yield return exe;
        }
    }

    private static IEnumerable<string> FindRunningGameClientExes()
    {
        string[] names =
        {
            "RiotClientServices",
            "Riot Client",
            "LeagueClient",
            "LeagueClientUx",
            "LeagueClientUxRender",
            "League of Legends",
        };

        foreach (string name in names)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var process in processes)
            {
                using (process)
                {
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch { path = null; }

                    if (!string.IsNullOrWhiteSpace(path))
                        yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> FindRiotInstallExes()
    {
        string installs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games",
            "RiotClientInstalls.json");

        if (File.Exists(installs))
        {
            IEnumerable<string> paths;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(installs));
                paths = RiotInstallPaths(doc.RootElement).ToList();
            }
            catch
            {
                paths = Array.Empty<string>();
            }

            foreach (string path in paths)
            {
                string normalized = path.Replace('/', Path.DirectorySeparatorChar);
                if (File.Exists(normalized))
                {
                    yield return normalized;
                    continue;
                }

                if (!Directory.Exists(normalized)) continue;
                foreach (string exe in RiotExesUnder(normalized))
                    yield return exe;
            }
        }

        foreach (string root in DriveInfo.GetDrives()
                     .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                     .Select(d => Path.Combine(d.RootDirectory.FullName, "Riot Games")))
        {
            if (!Directory.Exists(root)) continue;
            foreach (string exe in RiotExesUnder(root))
                yield return exe;
        }
    }

    private static IEnumerable<string> RiotInstallPaths(JsonElement root)
    {
        if (root.TryGetProperty("rc_default", out var rc) && rc.ValueKind == JsonValueKind.String)
            yield return rc.GetString()!;

        if (root.TryGetProperty("rc_live", out var live) && live.ValueKind == JsonValueKind.String)
            yield return live.GetString()!;

        if (root.TryGetProperty("associated_client", out var associated) && associated.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in associated.EnumerateObject())
            {
                yield return item.Name;
                if (item.Value.ValueKind == JsonValueKind.String)
                    yield return item.Value.GetString()!;
            }
        }
    }

    private static IEnumerable<string> RiotExesUnder(string root)
    {
        string[] relative =
        {
            Path.Combine("Riot Client", "RiotClientServices.exe"),
            Path.Combine("Riot Client", "RiotClientElectron", "Riot Client.exe"),
            Path.Combine("League of Legends", "LeagueClient.exe"),
            Path.Combine("League of Legends", "LeagueClientUx.exe"),
            Path.Combine("League of Legends", "LeagueClientUxRender.exe"),
            Path.Combine("League of Legends", "Game", "League of Legends.exe"),
        };

        foreach (string rel in relative)
        {
            string exe = Path.Combine(root, rel);
            if (File.Exists(exe))
                yield return exe;
        }
    }

    private static async Task<bool> FirewallAllowAsync(string name, string exe, List<string> log)
    {
        if (!File.Exists(exe))
        {
            log.Add($"ℹ {name}: " + Loc.T("файл ещё не установлен, правило брандмауэра пропущено"));
            return true;
        }

        // Drop any prior rules with this name first, so clicking again doesn't pile up duplicates.
        await RunAsync("netsh.exe", new[] { "advfirewall", "firewall", "delete", "rule", $"name={name}" });
        var (cIn, oIn) = await RunAsync("netsh.exe", new[]
        {
            "advfirewall", "firewall", "add", "rule",
            $"name={name}", "dir=in", "action=allow", $"program={exe}", "enable=yes", "profile=any",
        });
        var (cOut, oOut) = await RunAsync("netsh.exe", new[]
        {
            "advfirewall", "firewall", "add", "rule",
            $"name={name}", "dir=out", "action=allow", $"program={exe}", "enable=yes", "profile=any",
        });
        return Note(log, cIn == 0 && cOut == 0, Loc.T("Брандмауэр · «{0}»", name), oIn + " " + oOut);
    }

    private static async Task<bool> FirewallRuleExistsAsync(string name, string exe, List<string> log)
    {
        if (!File.Exists(exe))
        {
            log.Add($"ℹ {name}: " + Loc.T("файл ещё не установлен"));
            return false;
        }

        var (code, output) = await RunAsync("netsh.exe", new[] { "advfirewall", "firewall", "show", "rule", $"name={name}", "verbose" });
        bool ok = code == 0
                  && output.Contains(exe, StringComparison.OrdinalIgnoreCase)
                  && output.Contains("Action", StringComparison.OrdinalIgnoreCase)
                  && output.Contains("Allow", StringComparison.OrdinalIgnoreCase);

        log.Add(ok
            ? $"✓ {Loc.T("Брандмауэр · «{0}»", name)}"
            : $"✗ {Loc.T("Брандмауэр · «{0}»", name)} — {Loc.T("правило отсутствует или указывает на другой exe")}");
        return ok;
    }

    private static bool Note(List<string> log, bool ok, string what, string output)
    {
        log.Add(ok ? $"✓ {what}" : $"✗ {what} — {Short(output)}");
        return ok;
    }

    private static string Short(string s)
    {
        string t = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (t.Length == 0) return Loc.T("нет вывода");
        return t.Length > 140 ? t[..140] + "…" : t;
    }

    private static async Task<(int code, string output)> RunAsync(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            // Drain both pipes concurrently (background) so neither can stall the other, then wait.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, Loc.T("истекло время ожидания"));
            }
            string output = (await outTask) + (await errTask);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
