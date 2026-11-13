using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Security.Principal;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.Services.Infrastructure;

public sealed record ProductHealthResult(string Summary, IReadOnlyList<string> Notes);

/// <summary>
/// Small product-quality maintenance pass that runs on startup. It keeps the portable install tidy and
/// explains the real runtime state in human terms instead of leaving users to infer it from folders.
/// </summary>
public static class ProductHealthService
{
    private static readonly string[] CoreSelectedLists =
    {
        "list-discord.txt",
        "list-youtube.txt",
        "list-lol.txt",
        "list-riot-auth.txt",
        "list-riot-entitlements.txt",
        "list-blitz.txt"
    };

    public static ProductHealthResult RunStartupMaintenance(AppSettings settings)
    {
        var notes = new List<string>();
        AppPaths.EnsureCreated();

        bool admin = IsAdministrator();
        notes.Add(admin
            ? Loc.T("Права: приложение запущено от имени администратора.")
            : Loc.T("Права: приложение не повышено; запуск движка WinDivert может запросить UAC или не стартовать."));

        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            notes.Add(Loc.T("EXE: {0}", exePath));
            notes.Add(CheckExeBuild(exePath));
            notes.Add(CheckLaunchPath(exePath));
        }

        notes.Add(Loc.T("Данные: {0}", AppPaths.AppDataRoot));
        notes.Add(Loc.T("Движок: {0}", AppPaths.EngineDir));
        notes.Add(CheckOsAndArchitecture());
        notes.Add(CheckSystemClock());
        notes.Add(CheckPortableRootShape(exePath));
        notes.Add(CheckCloudSyncedPath(AppPaths.AppDataRoot, Loc.T("папка данных")));
        notes.Add(CheckCloudSyncedPath(AppPaths.EngineDir, Loc.T("папка движка")));
        notes.Add(File.Exists(AppPaths.WinwsExe)
            ? Loc.T("winws2.exe найден.")
            : Loc.T("winws2.exe не найден — будет восстановлен из встроенного runtime или скачан при обновлении."));
        notes.Add(CheckWritable(AppPaths.AppDataRoot, Loc.T("папка данных")));
        notes.Add(CheckWritable(AppPaths.EngineDir, Loc.T("папка движка")));
        notes.Add(CheckFreeSpace(AppPaths.AppDataRoot, Loc.T("диск данных")));
        notes.Add(CheckPathLength(exePath));
        notes.Add(CheckDuplicateUiProcesses());

        new HostlistService().SeedDefaults();

        if (!AppPaths.TryRestoreBundledEngine(out string? restoreError))
            notes.Add(Loc.T("Встроенный runtime не восстановился: {0}", restoreError ?? "unknown"));
        else
            notes.Add(Loc.T("Встроенный runtime проверен/восстановлен."));

        int tempRemoved = CleanOldEntries(AppPaths.TempDir, TimeSpan.FromDays(3));
        if (tempRemoved > 0) notes.Add(Loc.T("Очищено временных файлов/папок: {0}", tempRemoved));

        int staleDownloads = CleanStaleDownloadFiles(AppPaths.AppDataRoot);
        if (staleDownloads > 0) notes.Add(Loc.T("Удалено зависших файлов обновления: {0}", staleDownloads));

        int staleStages = CleanStaleStageDirectories(AppPaths.TempDir, TimeSpan.FromHours(12));
        if (staleStages > 0) notes.Add(Loc.T("Удалено зависших stage-папок runtime/blitzfix: {0}", staleStages));

        int emptyLogsRemoved = CleanEmptyOldFiles(AppPaths.LogsDir, TimeSpan.FromDays(1));
        if (emptyLogsRemoved > 0) notes.Add(Loc.T("Удалено пустых старых логов: {0}", emptyLogsRemoved));

        int oldBackups = TrimOldBackups(AppPaths.BackupsDir, 12);
        if (oldBackups > 0) notes.Add(Loc.T("Удалено старых резервных копий настроек: {0}", oldBackups));

        int rotatedBrokenJson = QuarantineBrokenJsonFiles();
        if (rotatedBrokenJson > 0) notes.Add(Loc.T("Битые JSON-файлы перенесены в backups: {0}", rotatedBrokenJson));

        int staleBrokenProfiles = CleanStaleBrokenProfiles();
        if (staleBrokenProfiles > 0) notes.Add(Loc.T("Удалено отброшенных временных профилей: {0}", staleBrokenProfiles));

        int deduped = NormalizeHostlists(AppPaths.ListsDir);
        if (deduped > 0) notes.Add(Loc.T("Убраны дубли строк в списках: {0} файл(ов)", deduped));

        int prunedSelected = PruneMissingSelectedLists();
        if (prunedSelected > 0) notes.Add(Loc.T("Из selected.txt убраны отсутствующие списки: {0}", prunedSelected));

        int selectedRepair = EnsureCoreSelectedLists();
        if (selectedRepair > 0) notes.Add(Loc.T("В selected.txt восстановлены обязательные списки: {0}", selectedRepair));

        int runtimeLists = MaterializeRuntimeLists();
        if (runtimeLists > 0) notes.Add(Loc.T("Runtime-копии списков обновлены: {0}", runtimeLists));

        notes.Add(CheckSelectedListCoverage());
        notes.Add(CheckRuntimeListCoverage());
        notes.Add(CheckLargeHostlists());
        notes.Add(CheckSettingsSchema(settings));
        notes.Add(CheckEngineHash());
        notes.Add(CheckCriticalHostlists());
        notes.Add(CheckLegacyFolders());
        notes.Add(CheckProxyBackup(settings));
        notes.Add(CheckConflictsQuick());

        var riotRepair = RiotRepairService.RunStartupMaintenance();
        foreach (string note in riotRepair.Notes)
            notes.Add(note);
        if (riotRepair.LegacyOptionsNeutralized > 0)
            notes.Add(Loc.T("Riot repair: нейтрализовано legacy options-файлов: {0}", riotRepair.LegacyOptionsNeutralized));

        var blitzRepair = BlitzRepairService.RunStartupMaintenance();
        foreach (string note in blitzRepair.Notes)
            notes.Add(note);

        var autostart = new AutostartService();
        if (autostart.IsSupported)
        {
            var integrity = autostart.CheckIntegrity(settings.Autostart);
            if (settings.Autostart && integrity is AutostartIntegrity.StalePath or AutostartIntegrity.MissingWhileDesired or AutostartIntegrity.Corrupted)
            {
                bool repaired = autostart.SyncAutostart(true);
                integrity = autostart.CheckIntegrity(true);
                notes.Add(repaired
                    ? Loc.T("Автозапуск: задача была автоматически восстановлена.")
                    : Loc.T("Автозапуск: автоматическое восстановление не удалось."));
            }

            notes.Add(integrity switch
            {
                AutostartIntegrity.EnabledAndValid => Loc.T("Автозапуск: задача установлена и указывает на текущий exe."),
                AutostartIntegrity.StalePath => Loc.T("Автозапуск: задача указывает на старый путь."),
                AutostartIntegrity.MissingWhileDesired => Loc.T("Автозапуск: в настройках включён, но задача Windows отсутствует."),
                AutostartIntegrity.Corrupted => Loc.T("Автозапуск: задача повреждена."),
                _ => Loc.T("Автозапуск: выключен."),
            });
        }

        WriteHealthReport(notes);

        string summary = BuildSummary(admin, exePath, tempRemoved, emptyLogsRemoved, deduped, selectedRepair, runtimeLists);
        return new ProductHealthResult(summary, notes);
    }

    private static string BuildSummary(bool admin, string? exePath, int tempRemoved, int emptyLogsRemoved, int deduped, int selectedRepair, int runtimeLists)
    {
        var bits = new List<string>
        {
            admin ? Loc.T("админ: да") : Loc.T("админ: нет"),
            File.Exists(AppPaths.WinwsExe) ? Loc.T("движок: на месте") : Loc.T("движок: нет"),
            LooksLikeTempLaunch(exePath) ? Loc.T("запуск из Temp") : Loc.T("путь exe: постоянный"),
        };
        if (tempRemoved > 0) bits.Add(Loc.T("temp очищен: {0}", tempRemoved));
        if (emptyLogsRemoved > 0) bits.Add(Loc.T("пустые логи: {0}", emptyLogsRemoved));
        if (deduped > 0) bits.Add(Loc.T("списки дедуп: {0}", deduped));
        if (selectedRepair > 0) bits.Add(Loc.T("selected восстановлен"));
        if (runtimeLists > 0) bits.Add(Loc.T("runtime-списки: {0}", runtimeLists));
        return string.Join(" · ", bits);
    }

    private static string CheckOsAndArchitecture()
    {
        try
        {
            var os = Environment.OSVersion.Version;
            if (!OperatingSystem.IsWindows())
                return Loc.T("Предупреждение: приложение рассчитано на Windows, текущая ОС не распознана как Windows.");
            if (!Environment.Is64BitOperatingSystem || RuntimeInformation.OSArchitecture != Architecture.X64)
                return Loc.T("Предупреждение: нужна 64-битная Windows; текущая архитектура: {0}.", RuntimeInformation.OSArchitecture);
            return Loc.T("Платформа: Windows {0}, x64.", os);
        }
        catch { return Loc.T("Платформа: не удалось проверить ОС/архитектуру."); }
    }

    private static string CheckExeBuild(string exePath)
    {
        try
        {
            if (!File.Exists(exePath))
                return Loc.T("EXE build: файл не найден для проверки.");

            var info = new FileInfo(exePath);
            string hash;
            using (var stream = File.OpenRead(exePath))
            {
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }

            string shortHash = hash.Length > 16 ? hash[..16] : hash;
            return Loc.T("EXE build: {0:yyyy-MM-dd HH:mm:ss} · {1} байт · SHA256 {2}",
                info.LastWriteTime, info.Length, shortHash);
        }
        catch (Exception ex)
        {
            return Loc.T("EXE build: не удалось проверить ({0}).", ex.Message);
        }
    }

    private static string CheckSystemClock()
    {
        try
        {
            int year = DateTimeOffset.Now.Year;
            return year is < 2024 or > 2035
                ? Loc.T("Предупреждение: системная дата выглядит неверной ({0}). TLS, обновления и DNS могут ломаться.", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"))
                : Loc.T("Системная дата: выглядит корректно ({0}).", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"));
        }
        catch { return Loc.T("Системная дата: не удалось проверить."); }
    }

    private static string CheckPortableRootShape(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return Loc.T("Папка запуска: неизвестна.");
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
            string name = Path.GetFileName(dir);
            if (dir.Contains("Desktop", StringComparison.OrdinalIgnoreCase))
                return Loc.T("Предупреждение: запуск с рабочего стола. Для стабильных обновлений лучше отдельная папка, например C:\\Tools\\GXBRIEL_ZAPRET.");
            if (dir.Contains("Downloads", StringComparison.OrdinalIgnoreCase))
                return Loc.T("Предупреждение: запуск из Downloads. Архиваторы/чистилки часто удаляют такие папки.");
            if (name.Contains(".zip", StringComparison.OrdinalIgnoreCase))
                return Loc.T("Предупреждение: путь похож на запуск из архива. Распакуйте приложение в обычную папку.");
            return Loc.T("Папка запуска: выглядит подходящей.");
        }
        catch { return Loc.T("Папка запуска: не удалось проверить."); }
    }

    private static string CheckCloudSyncedPath(string directory, string label)
    {
        try
        {
            string full = Path.GetFullPath(directory);
            string[] markers = { "\\OneDrive\\", "\\Dropbox\\", "\\Google Drive\\", "\\YandexDisk\\", "\\iCloudDrive\\" };
            return markers.Any(m => full.Contains(m, StringComparison.OrdinalIgnoreCase))
                ? Loc.T("Предупреждение: {0} находится в облачной синхронизации. Runtime и логи надёжнее держать вне sync-папок.", label)
                : Loc.T("Синхронизация: {0} не похожа на облачную папку.", label);
        }
        catch { return Loc.T("Синхронизация: не удалось проверить {0}.", label); }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string CheckLaunchPath(string exePath)
    {
        try
        {
            if (LooksLikeTempLaunch(exePath))
                return Loc.T("Предупреждение: exe запущен из временной папки. Лучше распаковать архив в постоянную папку, иначе автозапуск и исключения антивируса могут указывать в мусорный путь.");

            string root = Path.GetPathRoot(Path.GetFullPath(exePath)) ?? "";
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase));
            if (drive is not null)
            {
                if (drive.DriveType == DriveType.Network)
                    return Loc.T("Предупреждение: exe запущен с сетевого диска. Для WinDivert и автозапуска надёжнее локальная папка.");
                if (drive.IsReady && drive.AvailableFreeSpace < 250L * 1024 * 1024)
                    return Loc.T("Предупреждение: на диске с exe осталось меньше 250 МБ. Обновление и распаковка runtime могут сорваться.");
            }
        }
        catch { }
        return Loc.T("Путь запуска выглядит постоянным.");
    }


    private static string CheckWritable(string directory, string label)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, ".health_write_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(file, "ok");
            File.Delete(file);
            return Loc.T("Запись: {0} доступна.", label);
        }
        catch (Exception ex)
        {
            return Loc.T("Предупреждение: {0} недоступна для записи: {1}", label, ex.Message);
        }
    }

    private static string CheckFreeSpace(string directory, string label)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? "";
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase));
            if (drive is null || !drive.IsReady) return Loc.T("Свободное место: {0} не проверен.", label);
            long mb = drive.AvailableFreeSpace / 1024 / 1024;
            return mb < 500
                ? Loc.T("Предупреждение: на {0} осталось {1} МБ, обновления могут сорваться.", label, mb)
                : Loc.T("Свободное место: {0} — {1} МБ.", label, mb);
        }
        catch (Exception ex) { return Loc.T("Свободное место: не удалось проверить {0}: {1}", label, ex.Message); }
    }

    private static string CheckPathLength(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return Loc.T("Длина пути exe: неизвестна.");
        try
        {
            int len = Path.GetFullPath(exePath).Length;
            return len > 180
                ? Loc.T("Предупреждение: путь exe длинный ({0} символов). Некоторые Windows API/антивирусы хуже работают с длинными путями.", len)
                : Loc.T("Длина пути exe: {0} символов.", len);
        }
        catch { return Loc.T("Длина пути exe: не удалось проверить."); }
    }

    private static string CheckDuplicateUiProcesses()
    {
        try
        {
            int count = System.Diagnostics.Process.GetProcessesByName("GXBRIEL_ZAPRET").Length;
            return count > 1
                ? Loc.T("Предупреждение: найдено несколько окон GXBRIEL_ZAPRET ({0}). Лучше оставить одно.", count)
                : Loc.T("Дубликаты приложения: не найдены.");
        }
        catch { return Loc.T("Дубликаты приложения: не удалось проверить."); }
    }

    private static string CheckEngineHash()
    {
        try
        {
            if (!File.Exists(AppPaths.WinwsExe)) return Loc.T("Hash движка: winws2.exe отсутствует.");
            EngineValidator.EnsureEngineVerified(AppPaths.WinwsExe);
            return Loc.T("Hash движка: winws2.exe совпадает с встроенным manifest.");
        }
        catch (Exception ex)
        {
            return Loc.T("Предупреждение: hash движка не прошёл проверку: {0}", ex.Message);
        }
    }

    private static string CheckCriticalHostlists()
    {
        try
        {
            var empty = CoreSelectedLists
                .Where(name => !File.Exists(Path.Combine(AppPaths.ListsDir, name)) || new FileInfo(Path.Combine(AppPaths.ListsDir, name)).Length == 0)
                .ToList();
            return empty.Count == 0
                ? Loc.T("Критичные списки: Discord/YouTube/LoL/Riot/Blitz на месте.")
                : Loc.T("Предупреждение: пустые или отсутствующие списки: {0}", string.Join(", ", empty));
        }
        catch (Exception ex) { return Loc.T("Критичные списки: не удалось проверить: {0}", ex.Message); }
    }

    private static string CheckLegacyFolders()
    {
        try
        {
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GxbrielZapret");
            if (Directory.Exists(legacy))
                return Loc.T("Найдена старая папка данных GxbrielZapret: {0}. Она не используется новой portable-схемой.", legacy);
        }
        catch { }
        return Loc.T("Старые папки данных: не найдены.");
    }

    private static string CheckProxyBackup(AppSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.SystemProxyBackup)
            ? Loc.T("Системный proxy: хвостов от прошлого запуска нет.")
            : Loc.T("Системный proxy: найден backup прошлого состояния, будет восстановлен при инициализации.");
    }

    private static string CheckConflictsQuick()
    {
        try
        {
            var conflicts = ConflictScanService.ScanConflicts();
            int hard = conflicts.Count(f => f.Severity == EnvSeverity.Conflict);
            int warn = conflicts.Count(f => f.Severity == EnvSeverity.Warning);
            return hard == 0 && warn == 0
                ? Loc.T("Конфликты окружения: не найдены.")
                : Loc.T("Конфликты окружения: {0} критичных, {1} предупреждений.", hard, warn);
        }
        catch { return Loc.T("Конфликты окружения: не удалось проверить."); }
    }

    private static bool LooksLikeTempLaunch(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            string full = Path.GetFullPath(exePath);
            string temp = Path.GetFullPath(Path.GetTempPath());
            string localTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(localTemp, StringComparison.OrdinalIgnoreCase)
                || full.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase)
                || full.Contains("\\7z", StringComparison.OrdinalIgnoreCase)
                || full.Contains("\\Rar$", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static int CleanOldEntries(string directory, TimeSpan maxAge)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            DateTime cutoff = DateTime.UtcNow - maxAge;
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch { }
            }

            foreach (string dir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                        removed++;
                    }
                }
                catch { }
            }
        }
        catch { }
        return removed;
    }

    private static int CleanStaleDownloadFiles(string directory)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (!name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".download", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-6))
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch { }
            }
        }
        catch { }
        return removed;
    }

    private static int CleanStaleStageDirectories(string directory, TimeSpan maxAge)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            DateTime cutoff = DateTime.UtcNow - maxAge;
            foreach (string dir in Directory.EnumerateDirectories(directory, "*-stage-*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith("runtime-stage-", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("blitzfix-stage-", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                        removed++;
                    }
                }
                catch { }
            }
        }
        catch { }
        return removed;
    }

    private static int CleanEmptyOldFiles(string directory, TimeSpan maxAge)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            DateTime cutoff = DateTime.UtcNow - maxAge;
            foreach (string file in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0 && info.LastWriteTimeUtc < cutoff)
                    {
                        info.Delete();
                        removed++;
                    }
                }
                catch { }
            }
        }
        catch { }
        return removed;
    }

    private static int TrimOldBackups(string directory, int keep)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var files = Directory.EnumerateFiles(directory, "*.bak*", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(Math.Max(keep, 0));
            foreach (var info in files)
            {
                try { info.Delete(); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    private static int NormalizeHostlists(string directory)
    {
        int changed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            foreach (string file in Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (!name.StartsWith("list-", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("selected.txt", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] lines = File.ReadAllLines(file);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var normalized = new List<string>();
                bool modified = false;
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0)
                    {
                        modified |= raw.Length != 0;
                        continue;
                    }

                    if (line.StartsWith("#", StringComparison.Ordinal))
                    {
                        normalized.Add(line);
                        modified |= !string.Equals(raw, line, StringComparison.Ordinal);
                        continue;
                    }

                    if (!seen.Add(line))
                    {
                        modified = true;
                        continue;
                    }
                    normalized.Add(line);
                    modified |= !string.Equals(raw, line, StringComparison.Ordinal);
                }

                string newText = normalized.Count == 0 ? string.Empty : string.Join(Environment.NewLine, normalized) + Environment.NewLine;
                string oldText = string.Join(Environment.NewLine, lines) + (lines.Length == 0 ? string.Empty : Environment.NewLine);
                if (modified || !string.Equals(newText, oldText, StringComparison.Ordinal))
                {
                    File.WriteAllText(file, newText);
                    changed++;
                }
            }
        }
        catch { }
        return changed;
    }

    private static int QuarantineBrokenJsonFiles()
    {
        int moved = 0;
        string[] files =
        {
            AppPaths.SettingsFile,
            AppPaths.DiscoveryTargetsFile,
            AppPaths.PresetsFile,
            AppPaths.EngineStateFile,
        };

        foreach (string file in files)
        {
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length == 0) continue;
                using var stream = File.OpenRead(file);
                using var _ = JsonDocument.Parse(stream);
            }
            catch (JsonException)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.BackupsDir);
                    string dest = Path.Combine(AppPaths.BackupsDir,
                        Path.GetFileName(file) + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                    File.Move(file, dest, overwrite: true);
                    moved++;
                }
                catch { }
            }
            catch { }
        }

        return moved;
    }

    private static int PruneMissingSelectedLists()
    {
        try
        {
            string path = Path.Combine(AppPaths.ListsDir, "selected.txt");
            if (!File.Exists(path)) return 0;
            var kept = new List<string>();
            int removed = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    kept.Add(line);
                    continue;
                }

                if (File.Exists(Path.Combine(AppPaths.ListsDir, line)))
                    kept.Add(line);
                else
                    removed++;
            }

            if (removed > 0)
                File.WriteAllText(path, string.Join(Environment.NewLine, kept) + Environment.NewLine);
            return removed;
        }
        catch { return 0; }
    }

    private static int EnsureCoreSelectedLists()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ListsDir);
            string path = Path.Combine(AppPaths.ListsDir, "selected.txt");
            var lines = File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                : new List<string>();
            var set = new HashSet<string>(lines, StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (string item in CoreSelectedLists)
            {
                string listPath = Path.Combine(AppPaths.ListsDir, item);
                if (File.Exists(listPath) && set.Add(item))
                {
                    lines.Add(item);
                    added++;
                }
            }
            if (added > 0)
                File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            return added;
        }
        catch { return 0; }
    }

    private static int MaterializeRuntimeLists()
    {
        int updated = 0;
        try
        {
            if (!Directory.Exists(AppPaths.ListsDir)) return 0;
            foreach (string file in Directory.EnumerateFiles(AppPaths.ListsDir, "*.txt", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (!name.StartsWith("list-", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("selected.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    HostlistService.MaterializeRuntimeList(file);
                    updated++;
                }
                catch { }
            }
        }
        catch { }
        return updated;
    }

    private static string CheckSelectedListCoverage()
    {
        try
        {
            string path = Path.Combine(AppPaths.ListsDir, "selected.txt");
            if (!File.Exists(path)) return Loc.T("selected.txt: отсутствует, будет создан при запуске обхода.");
            var selected = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .ToList();
            return selected.Count == 0
                ? Loc.T("Предупреждение: selected.txt пуст — allow-list обхода не получит домены.")
                : Loc.T("selected.txt: активных списков {0}.", selected.Count);
        }
        catch { return Loc.T("selected.txt: не удалось проверить."); }
    }

    private static string CheckRuntimeListCoverage()
    {
        try
        {
            if (!Directory.Exists(AppPaths.RuntimeListsDir)) return Loc.T("Runtime-списки: папка ещё не создана.");
            int count = Directory.EnumerateFiles(AppPaths.RuntimeListsDir, "*.txt", SearchOption.TopDirectoryOnly).Count();
            return count == 0
                ? Loc.T("Предупреждение: runtime-списки пусты; перед запуском будет нужна материализация списков.")
                : Loc.T("Runtime-списки: файлов {0}.", count);
        }
        catch { return Loc.T("Runtime-списки: не удалось проверить."); }
    }

    private static string CheckLargeHostlists()
    {
        try
        {
            if (!Directory.Exists(AppPaths.ListsDir)) return Loc.T("Размер списков: папка отсутствует.");
            var largest = Directory.EnumerateFiles(AppPaths.ListsDir, "*.txt", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault();
            if (largest is null) return Loc.T("Размер списков: списков пока нет.");
            long mb = largest.Length / 1024 / 1024;
            return mb >= 15
                ? Loc.T("Предупреждение: крупный список {0} ({1} МБ). Запуск может быть медленнее.", largest.Name, mb)
                : Loc.T("Размер списков: самый крупный {0} — {1} КБ.", largest.Name, Math.Max(1, largest.Length / 1024));
        }
        catch { return Loc.T("Размер списков: не удалось проверить."); }
    }

    private static string CheckSettingsSchema(AppSettings settings)
    {
        try
        {
            return settings.SchemaVersion <= 0
                ? Loc.T("Настройки: новый или legacy settings.json, схема будет сохранена при следующем изменении.")
                : Loc.T("Настройки: schemaVersion {0}.", settings.SchemaVersion);
        }
        catch { return Loc.T("Настройки: не удалось проверить schemaVersion."); }
    }

    private static int CleanStaleBrokenProfiles()
    {
        try
        {
            if (!Directory.Exists(AppPaths.ProfilesBrokenDir)) return 0;
            int count = 0;
            foreach (var file in Directory.EnumerateFiles(AppPaths.ProfilesBrokenDir, "*.*"))
            {
                try
                {
                    File.Delete(file);
                    count++;
                }
                catch { }
            }
            return count;
        }
        catch { return 0; }
    }

    private static void WriteHealthReport(IReadOnlyList<string> notes)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.WriteAllLines(Path.Combine(AppPaths.LogsDir, "product-health.txt"),
                new[] { "GXBRIEL_ZAPRET product health", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "" }.Concat(notes));
        }
        catch { }
    }
}
