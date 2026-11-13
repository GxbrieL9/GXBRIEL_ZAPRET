using System.IO;
using System.Text.Json;

namespace GxbrielZapret.Services.Infrastructure;

/// <summary>
/// Handles automatic, non-destructive migration of user data:
/// 1. From flat legacy files in %LOCALAPPDATA%\GXBRIEL_ZAPRET into the new partitioned directories.
/// 2. From portable folders next to the EXE (e.g. config/, profiles/, lists/) into %LOCALAPPDATA%\GXBRIEL_ZAPRET.
/// Never deletes source files next to the EXE.
/// </summary>
public static class MigrationManager
{
    private static readonly object SyncLock = new();
    private static bool _migrated;

    public static void EnsureMigrated()
    {
        if (_migrated) return;
        lock (SyncLock)
        {
            if (_migrated) return;
            try
            {
                AppPaths.EnsureCreated();
                MigrateFlatAppData();
                MigrateAdjacentPortableData();
                _migrated = true;
            }
            catch
            {
                // Non-fatal: the application should always continue running
            }
        }
    }

    /// <summary>
    /// Migrates flat files from %LOCALAPPDATA%\GXBRIEL_ZAPRET into their designated subdirectories.
    /// </summary>
    private static void MigrateFlatAppData()
    {
        string root = AppPaths.AppDataRoot;
        if (!Directory.Exists(root)) return;

        // 1. settings.json -> config/settings.json
        string flatSettings = Path.Combine(root, "settings.json");
        if (File.Exists(flatSettings))
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                try { File.Move(flatSettings, AppPaths.SettingsFile); } catch { }
            }
            else
            {
                // Both exist: archive the old flat one
                try { File.Move(flatSettings, Path.Combine(AppPaths.BackupsDir, $"legacy-flat-settings-{DateTime.Now:yyyyMMddHHmmss}.json"), overwrite: true); } catch { }
            }
        }

        // 2. discovery-targets.json -> config/discovery-targets.json
        string flatDiscovery = Path.Combine(root, "discovery-targets.json");
        if (File.Exists(flatDiscovery))
        {
            if (!File.Exists(AppPaths.DiscoveryTargetsFile))
            {
                try { File.Move(flatDiscovery, AppPaths.DiscoveryTargetsFile); } catch { }
            }
            else
            {
                try { File.Move(flatDiscovery, Path.Combine(AppPaths.BackupsDir, $"legacy-flat-discovery-targets-{DateTime.Now:yyyyMMddHHmmss}.json"), overwrite: true); } catch { }
            }
        }

        // 3. presets.json -> state/presets.json
        string flatPresets = Path.Combine(root, "presets.json");
        if (File.Exists(flatPresets))
        {
            if (!File.Exists(AppPaths.PresetsFile))
            {
                try { File.Move(flatPresets, AppPaths.PresetsFile); } catch { }
            }
            else
            {
                try { File.Move(flatPresets, Path.Combine(AppPaths.BackupsDir, $"legacy-flat-presets-{DateTime.Now:yyyyMMddHHmmss}.json"), overwrite: true); } catch { }
            }
        }

        // 4. tmp/ -> temp/
        string oldTmp = Path.Combine(root, "tmp");
        if (Directory.Exists(oldTmp) && oldTmp != AppPaths.TempDir)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(oldTmp))
                {
                    string dest = Path.Combine(AppPaths.TempDir, Path.GetFileName(file));
                    if (!File.Exists(dest)) File.Move(file, dest);
                }
                Directory.Delete(oldTmp, recursive: true);
            }
            catch { }
        }
    }

    /// <summary>
    /// Checks the directory next to the running executable for portable data (config/, profiles/, lists/, etc.)
    /// and safely copies missing files to %LOCALAPPDATA%\GXBRIEL_ZAPRET without removing originals.
    /// </summary>
    private static void MigrateAdjacentPortableData()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) return;

        // If the EXE happens to be running inside AppData itself, skip adjacent migration
        if (string.Equals(Path.GetFullPath(baseDir).TrimEnd('\\'), Path.GetFullPath(AppPaths.AppDataRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return;

        // 1. Settings adjacent to EXE
        string adjSettings = Path.Combine(baseDir, "settings.json");
        string adjConfigSettings = Path.Combine(baseDir, "config", "settings.json");
        if (!File.Exists(AppPaths.SettingsFile))
        {
            if (File.Exists(adjConfigSettings))
            {
                try { File.Copy(adjConfigSettings, AppPaths.SettingsFile); } catch { }
            }
            else if (File.Exists(adjSettings))
            {
                try { File.Copy(adjSettings, AppPaths.SettingsFile); } catch { }
            }
        }

        // 2. Presets adjacent to EXE
        string adjPresets = Path.Combine(baseDir, "presets.json");
        string adjStatePresets = Path.Combine(baseDir, "state", "presets.json");
        if (!File.Exists(AppPaths.PresetsFile))
        {
            if (File.Exists(adjStatePresets))
            {
                try { File.Copy(adjStatePresets, AppPaths.PresetsFile); } catch { }
            }
            else if (File.Exists(adjPresets))
            {
                try { File.Copy(adjPresets, AppPaths.PresetsFile); } catch { }
            }
        }

        // 3. Discovery targets adjacent to EXE
        string adjDiscovery = Path.Combine(baseDir, "discovery-targets.json");
        string adjConfigDiscovery = Path.Combine(baseDir, "config", "discovery-targets.json");
        if (!File.Exists(AppPaths.DiscoveryTargetsFile))
        {
            if (File.Exists(adjConfigDiscovery))
            {
                try { File.Copy(adjConfigDiscovery, AppPaths.DiscoveryTargetsFile); } catch { }
            }
            else if (File.Exists(adjDiscovery))
            {
                try { File.Copy(adjDiscovery, AppPaths.DiscoveryTargetsFile); } catch { }
            }
        }

        // 4. Profiles adjacent to EXE (profiles/work, profiles/broken, profiles/lists)
        string adjProfiles = Path.Combine(baseDir, "profiles");
        if (Directory.Exists(adjProfiles))
        {
            CopyDirectoryMissingOnly(Path.Combine(adjProfiles, "work"), AppPaths.ProfilesWorkDir);
            CopyDirectoryMissingOnly(Path.Combine(adjProfiles, "broken"), AppPaths.ProfilesBrokenDir);
            CopyDirectoryMissingOnly(Path.Combine(adjProfiles, "lists"), AppPaths.ProfilesListsDir);
        }

        // 5. Lists adjacent to EXE
        string adjLists = Path.Combine(baseDir, "lists");
        if (Directory.Exists(adjLists))
        {
            CopyDirectoryMissingOnly(adjLists, AppPaths.ListsDir);
        }

        // Record migration log in state/
        try
        {
            string stateFile = Path.Combine(AppPaths.StateDir, "migration.json");
            var record = new
            {
                Timestamp = DateTime.UtcNow,
                MigratedFromBaseDir = baseDir,
                AppDataRoot = AppPaths.AppDataRoot,
                SystemRuntimeDir = AppPaths.SystemRuntimeDir
            };
            File.WriteAllText(stateFile, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void CopyDirectoryMissingOnly(string sourceDir, string targetDir)
    {
        try
        {
            if (!Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(file));
                if (!File.Exists(dest))
                {
                    try { File.Copy(file, dest); } catch { }
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
            {
                string subName = Path.GetFileName(sub);
                CopyDirectoryMissingOnly(sub, Path.Combine(targetDir, subName));
            }
        }
        catch { }
    }
}
