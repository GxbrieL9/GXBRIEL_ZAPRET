using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Services.Strategies;

namespace GxbrielZapret.Harness;

internal static class ConnectivityReportHarness
{
    public static async Task RunAsync(string output, bool smoke, string domain = "example.org")
    {
        var report = new Dictionary<string, object?> { ["StartedAtUtc"] = DateTime.UtcNow, ["Smoke"] = smoke };
        int exit = 0;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(smoke ? 240 : 60));
            using var runner = new Blockcheck2Runner();
            report["Environment"] = await runner.CheckEnvironmentAsync(timeout.Token);
            if (smoke)
            {
                var engines = Process.GetProcessesByName("winws2");
                try { if (engines.Any(p => !p.HasExited)) throw new InvalidOperationException("Stop the bypass engine before the isolated native smoke test."); }
                finally { foreach (var engine in engines) engine.Dispose(); }
                var timestamps = new TcpTimestampsService();
                try
                {
                    await Task.Run(() => timestamps.EnableForSession());
                    report["NativeSmoke"] = await runner.RunAsync(new[] { domain }, timeout.Token);
                }
                finally { await Task.Run(() => timestamps.RestoreAfterSession()); }
            }
            else
            {
                report["Network"] = await Task.Run(NetworkFingerprint.Current);
                report["YouTube"] = await YouTubeApplicationProbe.CheckAsync(timeout.Token);
                report["ApplicationFlows"] = await ApplicationFlowProbe.CheckAsync(Models.AutoScope.Both, timeout.Token);
            }
        }
        catch (Exception ex) { report["Error"] = ex.ToString(); exit = 1; }
        finally
        {
            report["FinishedAtUtc"] = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { Application.Current.Shutdown(exit); }
        }
    }
}
