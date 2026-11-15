using System.IO;
using System.Text.Json;
using System.Windows;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.Harness;

internal static class AntigravityRoutingHarness
{
    // Transport-only diagnostic: no login, model request, settings change or installed-file patch.
    internal static async Task RunAsync(string output, bool nextRoute)
    {
        int exit = 1;
        try
        {
            using var relay = new AntigravityRelayService();
            if (nextRoute) relay.NextRoute();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            string report = await relay.CheckAsync(timeout.Token);
            bool httpsReached = report.Contains(": HTTPS, HTTP ", StringComparison.Ordinal);
            string path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                TransportReached = httpsReached, ModelResponseVerified = false,
                Route = relay.Status, Report = report,
                Providers = AntigravitySmartDns.Providers.Select(p => new { p.Name, p.Url })
            }, new JsonSerializerOptions { WriteIndented = true }));
            await relay.StopAsync();
            exit = httpsReached ? 0 : 2;
        }
        finally { Application.Current.Shutdown(exit); }
    }
}
