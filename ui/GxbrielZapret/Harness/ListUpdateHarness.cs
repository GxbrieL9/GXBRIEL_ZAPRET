using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Harness;

/// <summary>
/// <c>GxbrielZapret.exe --update-lists [outFile]</c> or <c>--parse-blitzfix [outFile]</c>
/// Headless runner to parse BlitzFix network traces, download upstream lists from mirrors,
/// resolve DNS, aggregate IP subnets, and synchronize all hostlist files.
/// </summary>
internal static class ListUpdateHarness
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    internal static void Run(string outFile)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);

        var sb = new StringBuilder();
        void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Console.WriteLine(line);
            sb.AppendLine(line);
        }

        Log("=== Запуск нативного парсинга BlitzFix и обновления баз хостлистов ===");

        try
        {
            AppPaths.EnsureCreated();
            var hostlists = new HostlistService();
            var ipset = new IpsetService();

            Log($"Каталог проекта: {AppPaths.ProjectRoot}");
            Log($"Каталог BlitzFix: {AppPaths.BlitzFixDir}");
            Log($"Каталог профилей списков: {AppPaths.ProfilesListsDir}");

            var summary = Task.Run(() => hostlists.UpdateAndParseOnlineDatabasesAsync(
                ipset,
                status => Log(status),
                CancellationToken.None)).GetAwaiter().GetResult();

            Log("");
            Log("================= РЕЗУЛЬТАТЫ ПАРСИНГА И ОБНОВЛЕНИЯ =================");
            Log(summary.StatusMessage);
            Log($"BlitzFix файлов обработано: {summary.BlitzFilesCount}");
            Log($"BlitzFix эндпоинтов спарсено: {summary.BlitzEndpointsCount}");
            Log($"Доменов Discord: {summary.DiscordDomains}");
            Log($"Подсетей Discord (ipset): {summary.DiscordSubnets}");
            Log($"Доменов YouTube: {summary.YoutubeDomains}");
            Log($"Доменов LoL / Riot: {summary.LolDomains}");
            Log($"Общих доменов (General): {summary.GeneralDomains}");
            Log($"Новых записей добавлено: {summary.TotalNewDomainsAdded}");
            Log($"Зеркал успешно опрошено: {summary.MirrorsDownloaded}");
            Log("====================================================================");
        }
        catch (Exception ex)
        {
            Log($"ОШИБКА: {ex}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(outFile))
            {
                try
                {
                    string fullPath = Path.GetFullPath(outFile);
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
                }
                catch { }
            }
            Application.Current.Shutdown(0);
        }
    }
}
