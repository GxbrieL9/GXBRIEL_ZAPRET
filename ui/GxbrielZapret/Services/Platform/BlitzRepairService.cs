using System.Diagnostics;
using System.IO;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Platform;

public sealed record BlitzRepairResult(bool RuleInstalled, string? BlitzPath, IReadOnlyList<string> Notes);

/// <summary>
/// Keeps Blitz from hanging on Bunny CDN edge ranges that are blackholed by the local ISP.
/// Blocking those dead ranges makes the Electron updater fail fast and fall back to reachable edges.
/// </summary>
public static class BlitzRepairService
{
    public const string HeadlessRepairArgument = "--repair-blitz-firewall";

    private const string RuleName = "GXBRIEL Blitz bad Bunny edge fallback";
    private const string TaskName = "GXBRIEL_ZAPRET Blitz Repair";
    private const string BadBunnyRanges = "138.199.36.0/24,138.199.37.0/24,169.150.247.0/24";

    public static BlitzRepairResult RunStartupMaintenance()
    {
        return RunStartupMaintenance(allowElevatedTask: true);
    }

    public static BlitzRepairResult RunElevatedRepair()
    {
        try
        {
            var result = RunStartupMaintenance(allowElevatedTask: false);
            WriteRepairLog(result);
            return result;
        }
        catch (Exception ex)
        {
            WriteRepairLog(new BlitzRepairResult(false, null, new[] { ex.ToString() }));
            throw;
        }
        finally
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }
    }

    private static BlitzRepairResult RunStartupMaintenance(bool allowElevatedTask)
    {
        var notes = new List<string>();
        string? blitzPath = ResolveBlitzPath();
        if (string.IsNullOrWhiteSpace(blitzPath) || !File.Exists(blitzPath))
        {
            notes.Add(Loc.T("Blitz repair: Blitz.exe не найден, firewall fallback пропущен."));
            return new BlitzRepairResult(false, blitzPath, notes);
        }

        bool installed = EnsureBadEdgeFirewallRule(blitzPath) && FirewallRuleExists();
        if (!installed && allowElevatedTask)
        {
            bool scheduled = StartElevatedRepairTask();
            notes.Add(scheduled
                ? Loc.T("Blitz repair: запущена elevated-задача для установки firewall fallback.")
                : Loc.T("Blitz repair: не удалось запустить elevated-задачу для firewall fallback."));
        }

        notes.Add(installed
            ? Loc.T("Blitz repair: firewall fallback для недоступных Bunny edge установлен.")
            : Loc.T("Blitz repair: не удалось установить firewall fallback для Bunny edge."));

        return new BlitzRepairResult(installed, blitzPath, notes);
    }

    public static bool FirewallRuleExists()
    {
        return RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\"") == 0;
    }

    private static string? ResolveBlitzPath()
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string candidate = Path.Combine(local, "Programs", "Blitz", "Blitz.exe");
            if (File.Exists(candidate)) return candidate;
        }
        catch { }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "Blitz.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (proc is null) return null;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    private static bool EnsureBadEdgeFirewallRule(string blitzPath)
    {
        DeleteRule();

        string args =
            "advfirewall firewall add rule " +
            $"name=\"{RuleName}\" " +
            "dir=out action=block enable=yes profile=any protocol=TCP remoteport=443 " +
            $"remoteip={BadBunnyRanges} " +
            $"program=\"{blitzPath}\"";

        return RunNetsh(args) == 0;
    }

    private static void WriteRepairLog(BlitzRepairResult result)
    {
        try
        {
            AppPaths.EnsureCreated();
            Directory.CreateDirectory(AppPaths.LogsDir);
            string path = Path.Combine(AppPaths.LogsDir, "blitz-repair.log");
            var lines = new List<string>
            {
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ruleInstalled={result.RuleInstalled} blitzPath={result.BlitzPath ?? "(null)"}"
            };
            lines.AddRange(result.Notes.Select(n => "  " + n));
            File.AppendAllLines(path, lines);
        }
        catch { }
    }

    private static bool StartElevatedRepairTask()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) ||
            !File.Exists(exe) ||
            exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string xml = BuildRepairTaskXml(exe);
        string tempXml = Path.Combine(Path.GetTempPath(), $"gx_blitz_repair_{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempXml, xml, System.Text.Encoding.Unicode);
            var create = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempXml}\" /F");
            if (create != 0) return false;

            var run = RunSchtasks($"/Run /TN \"{TaskName}\"");
            return run == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(tempXml)) File.Delete(tempXml); } catch { }
        }
    }

    private static string BuildRepairTaskXml(string exePath)
    {
        string workDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>GXBRIEL_ZAPRET Blitz Firewall Repair</Description>
                <Author>GXBRIEL_ZAPRET</Author>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exePath}</Command>
                  <Arguments>{HeadlessRepairArgument}</Arguments>
                  <WorkingDirectory>{workDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static void DeleteRule()
    {
        RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
    }

    private static int RunNetsh(string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (proc is null) return -1;
            proc.WaitForExit(8000);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static int RunSchtasks(string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (proc is null) return -1;
            proc.WaitForExit(10000);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
