using System.IO;
using System.Text.Json;
using System.Windows;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Harness;

/// <summary>
/// Headless settings migration probe used by release validation.
/// </summary>
internal static class SettingsMigrationHarness
{
    internal static void Run(string outFile)
    {
        try
        {
            AppPaths.EnsureCreated();
            var settings = new SettingsService();
            settings.Save();

            var summary = new
            {
                settingsPath = AppPaths.SettingsFile,
                schemaVersion = settings.Settings.SchemaVersion,
                bypassDesired = settings.Settings.BypassDesired,
                tgProxyAutostart = settings.Settings.TgProxyAutostart,
                tgProxyPort = settings.Settings.TgProxyPort,
                activeHostlist = settings.Settings.ActiveHostlist,
                backupsDir = AppPaths.BackupsDir
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outFile) ?? AppPaths.TempDir);
            File.WriteAllText(outFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outFile, "EXC: " + ex); } catch { }
        }
        finally
        {
            Application.Current.Shutdown(0);
        }
    }
}
