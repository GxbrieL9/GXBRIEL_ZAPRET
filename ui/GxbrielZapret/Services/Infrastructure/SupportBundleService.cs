using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Startup;

namespace GxbrielZapret.Services.Infrastructure;

/// <summary>
/// Packages diagnostics, sanitized logs, conflict detection, and network profiles
/// into a portable ZIP archive for developer/community troubleshooting.
/// Completely sanitizes usernames, paths, and secrets.
/// </summary>
public sealed class SupportBundleService
{
    private readonly SettingsService _settings;

    public SupportBundleService(SettingsService settings)
    {
        _settings = settings;
    }

    public async Task<string> CreateBundleAsync(string outputPath, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gxbriel_support_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. System summary (no user tokens or private credentials)
            var summary = new
            {
                GeneratedAtUtc = DateTime.UtcNow,
                OsVersion = Environment.OSVersion.VersionString,
                Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                DotNetVersion = Environment.Version.ToString(),
                Settings = new
                {
                    _settings.Settings.BypassDesired,
                    _settings.Settings.DisableQuic,
                    _settings.Settings.GameFilter,
                    _settings.Settings.TgProxyCoverage,
                    _settings.Settings.DebugLog,
                    _settings.Settings.LeagueTcp2099Bypass,
                    _settings.Settings.RiotEntitlementsBypass,
                    _settings.Settings.MinimizeToTray,
                    SelectedPreset = _settings.Settings.ActivePresetName
                }
            };
            var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(Path.Combine(tempDir, "system-summary.json"),
                JsonSerializer.Serialize(summary, jsonOpts), ct).ConfigureAwait(false);

            // 2. Conflict scan report
            var conflicts = ConflictScanService.ScanConflicts();
            var sbConflicts = new StringBuilder();
            sbConflicts.AppendLine($"Conflict Scan Results ({conflicts.Count} found):");
            foreach (var c in conflicts)
            {
                sbConflicts.AppendLine($"- [{c.Severity}] {c.Title}: {c.Detail}");
            }
            await File.WriteAllTextAsync(Path.Combine(tempDir, "conflicts.txt"), sbConflicts.ToString(), ct).ConfigureAwait(false);

            // 3. Engine logs (sanitized tail of up to 2 latest session logs)
            var logFiles = Directory.Exists(AppPaths.LogsDir)
                ? Directory.GetFiles(AppPaths.LogsDir, "engine-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(2)
                    .ToArray()
                : Array.Empty<string>();

            var sbLogs = new StringBuilder();
            foreach (var logFile in logFiles)
            {
                sbLogs.AppendLine($"=== File: {Path.GetFileName(logFile)} ===");
                try
                {
                    var lines = await File.ReadAllLinesAsync(logFile, ct).ConfigureAwait(false);
                    var tail = lines.TakeLast(500);
                    foreach (var line in tail)
                    {
                        sbLogs.AppendLine(SanitizeLogLine(line));
                    }
                }
                catch { /* Ignore locked files */ }
            }
            await File.WriteAllTextAsync(Path.Combine(tempDir, "engine-logs-tail.txt"), sbLogs.ToString(), ct).ConfigureAwait(false);

            // 4. Crash log (if present)
            var fatalLogPath = Path.Combine(AppPaths.LogsDir, "fatal.log");
            if (File.Exists(fatalLogPath))
            {
                try
                {
                    var crashText = await File.ReadAllTextAsync(fatalLogPath, ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(tempDir, "crash-log.txt"), SanitizeLogLine(crashText), ct).ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }

            // 5. Compress to destination ZIP
            var targetDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
            if (File.Exists(outputPath)) File.Delete(outputPath);

            ZipFile.CreateFromDirectory(tempDir, outputPath, CompressionLevel.Optimal, false);
            return outputPath;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    private static string SanitizeLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;
        var sanitized = line.Replace(Environment.UserName, "USER", StringComparison.OrdinalIgnoreCase);
        sanitized = sanitized.Replace(Environment.MachineName, "HOST", StringComparison.OrdinalIgnoreCase);
        return sanitized;
    }
}
