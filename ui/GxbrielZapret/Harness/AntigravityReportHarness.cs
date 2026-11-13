using System.IO;
using System.Text.Json;
using System.Windows;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.Harness;

internal static class AntigravityReportHarness
{
    internal static async Task RunAsync(string output, string? root)
    {
        int exit = 0;
        var report = new Dictionary<string, object?> { ["Mode"] = "ReadOnly", ["StartedAtUtc"] = DateTime.UtcNow };
        try
        {
            var paths = AntigravityDiscovery.Find(root);
            var before = paths.ToDictionary(p => p.Key, p => AntigravityPatchEngine.Hash(p.Value));
            report["Status"] = await AntigravityPatcherService.GetStatusAsync(root: root);
            report["HashesUnchanged"] = paths.All(p => before[p.Key] == AntigravityPatchEngine.Hash(p.Value));
            report["Hashes"] = before;
        }
        catch (Exception ex) { report["Error"] = ex.Message; exit = 1; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            report["FinishedAtUtc"] = DateTime.UtcNow;
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Application.Current.Shutdown(exit); }
    }
}
