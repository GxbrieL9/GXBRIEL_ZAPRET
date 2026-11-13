using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Strategies;

/// <summary>Bounded diagnostic history, not a permanent traffic record. No signed media URLs or payloads.</summary>
internal static class ScanProbeLog
{
    private static readonly object Gate = new();
    internal static string PathName => Path.Combine(AppPaths.LogsDir, "strategy-probes.jsonl");
    internal static string Redact(string detail) => Regex.Replace(detail, @"(?:https?|wss?)://\S+", "[URL]", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    public static void Append(Canonical5ProbeResult result)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                string path = PathName;
                foreach (string existing in new[] { path, path + ".previous" })
                    if (File.Exists(existing) && File.GetLastWriteTimeUtc(existing) < DateTime.UtcNow.AddDays(-3)) File.Delete(existing);
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                string summary = Redact(result.Summary);
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    AtUtc = DateTime.UtcNow,
                    result.StrategyId,
                    result.Status,
                    result.PassCount,
                    result.TotalCount,
                    result.CheckUnavailable,
                    Summary = summary.Length > 8192 ? summary[..8192] : summary,
                    result.Probes,
                    Video = result.Flows is { } flows ? new
                    {
                        flows.YouTubeVideo.Passed,
                        flows.YouTubeVideo.Bytes,
                        flows.YouTubeVideo.MegabitsPerSecond,
                        flows.YouTubeVideo.FirstByteMs,
                        flows.YouTubeVideo.TransferMs
                    } : null
                }) + Environment.NewLine);
            }
        }
        catch { /* Diagnostic I/O must never interrupt recovery or alter verification results. */ }
    }
}
