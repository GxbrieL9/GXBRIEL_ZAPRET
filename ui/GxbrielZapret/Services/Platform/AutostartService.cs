using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using GxbrielZapret.Localization;

namespace GxbrielZapret.Services.Platform;

public enum AutostartIntegrity
{
    Disabled,
    EnabledAndValid,
    StalePath,
    MissingWhileDesired,
    Corrupted
}

/// <summary>
/// Manages a Scheduled Task that launches the (elevated) UI at logon.
/// A plain Run registry key cannot start an app elevated without a UAC prompt,
/// so a task with "highest privileges" is used instead.
/// </summary>
public sealed class AutostartService
{
    public const string TaskName = "GXBRIEL_ZAPRET Autostart";
    private const string LegacyTaskName = "GxbrielZapret Autostart";

    private static string? ExePath => Environment.ProcessPath;

    public bool IsSupported =>
        ExePath is not null &&
        ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !ExePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    public bool IsEnabled()
    {
        var (code, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public bool Enable()
    {
        if (!IsSupported || ExePath is null) return false;
        // Clean up legacy task if present
        RunSchtasks($"/Delete /TN \"{LegacyTaskName}\" /F");

        // Try creating via hardened XML definition first (handles battery, logon delay, execution timeout, working dir)
        string xml = BuildTaskXml(ExePath, "--tray", 3);
        string tempXml = Path.Combine(Path.GetTempPath(), $"gx_autostart_{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempXml, xml, System.Text.Encoding.Unicode);
            var (xmlCode, _) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempXml}\" /F");
            if (xmlCode == 0) return true;
        }
        catch { }
        finally
        {
            try { if (File.Exists(tempXml)) File.Delete(tempXml); } catch { }
        }

        // Fallback to standard CLI parameters if XML creation failed
        var (code, _) = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\" --tray\" " +
            "/SC ONLOGON /RL HIGHEST /F");
        return code == 0;
    }

    public string? ExportTaskXml()
    {
        var (code, output) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
        return code == 0 ? output : null;
    }

    public bool IsTaskStale(out string? registeredPath, out string? currentPath)
    {
        registeredPath = null;
        currentPath = ExePath;

        string? xml = ExportTaskXml();
        if (string.IsNullOrWhiteSpace(xml)) return false;

        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var exec = doc.Descendants(ns + "Exec").FirstOrDefault();
            if (exec == null)
            {
                // Fallback namespace-agnostic search
                exec = doc.Descendants().FirstOrDefault(d => d.Name.LocalName == "Exec");
            }

            if (exec != null)
            {
                var cmdElem = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Command");
                registeredPath = cmdElem?.Value?.Trim('"');
            }

            if (!string.IsNullOrEmpty(registeredPath))
            {
                if (!File.Exists(registeredPath)) return true;
                if (!string.IsNullOrEmpty(currentPath) &&
                    !string.Equals(Path.GetFullPath(registeredPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public AutostartIntegrity CheckIntegrity(bool desiredAutostart)
    {
        if (!IsEnabled())
            return desiredAutostart ? AutostartIntegrity.MissingWhileDesired : AutostartIntegrity.Disabled;

        if (IsTaskStale(out _, out _))
            return AutostartIntegrity.StalePath;

        return AutostartIntegrity.EnabledAndValid;
    }

    public bool SyncAutostart(bool desiredAutostart)
    {
        if (desiredAutostart)
        {
            if (!IsEnabled() || IsTaskStale(out _, out _))
                return Enable();
            return true;
        }
        else
        {
            if (IsEnabled())
                return Disable();
            return true;
        }
    }

    public static string BuildTaskXml(string exePath, string args = "--tray", int delaySeconds = 3)
    {
        string workDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>GXBRIEL_ZAPRET Elevated Autostart</Description>
                <Author>GXBRIEL_ZAPRET</Author>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT{delaySeconds}S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exePath}</Command>
                  <Arguments>{args}</Arguments>
                  <WorkingDirectory>{workDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public bool Disable()
    {
        RunSchtasks($"/Delete /TN \"{LegacyTaskName}\" /F");
        var (code, _) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        return code == 0;
    }

    private static (int code, string output) RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            // Drain both pipes concurrently (in the background) so neither can stall the other, then
            // wait for exit — reading only one pipe sequentially is the classic two-pipe deadlock.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, Loc.T("schtasks не ответил вовремя."));
            }
            string output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
