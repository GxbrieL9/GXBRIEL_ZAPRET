using System.IO;
using System.IO.Compression;
using GxbrielZapret.Localization;

namespace GxbrielZapret.Services.Infrastructure;

/// <summary>
/// Exports/imports the user's configuration as a single <c>.z2bak</c> file (a plain zip). It carries only
/// what the user set up — <c>settings.json</c>, <c>presets.json</c> and the <c>lists\</c> folder — and
/// deliberately NOT the engine binaries or logs: a backup is meant to be tiny and portable, and the
/// ~160 MB engine re-downloads itself on the next launch anyway.
/// </summary>
public sealed class BackupService
{
    /// <summary>Presence-only entry every backup carries, so <see cref="IsBackup"/>/<see cref="Restore"/>
    /// can reject an unrelated zip a user might have picked by mistake.</summary>
    private const string Marker = "gxbriel-backup.txt";
    private const string LegacyMarker = "backup-v1.txt";

    /// <summary>Suggested file name for the save dialog, e.g. <c>GxbrielZapret-backup-2026-08-20.z2bak</c>.</summary>
    public string DefaultFileName => $"GxbrielZapret-backup-{DateTime.Now:yyyy-MM-dd}.z2bak";

    /// <summary>Write a backup of the current configuration to <paramref name="path"/> (overwriting it).
    /// Built via a temp file + atomic move so a crash mid-write can't leave a half-written .z2bak behind.</summary>
    public void Export(string path)
    {
        string tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            zip.CreateEntry(Marker);
            AddFile(zip, AppPaths.SettingsFile, "settings.json");
            AddFile(zip, AppPaths.PresetsFile, "presets.json");
            AddFile(zip, AppPaths.DiscoveryTargetsFile, "discovery-targets.json");
            if (Directory.Exists(AppPaths.ProfilesDir))
                foreach (var f in Directory.EnumerateFiles(AppPaths.ProfilesDir, "*.gxprofile.json", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(AppPaths.ProfilesDir, f).Replace('\\', '/');
                    AddFile(zip, f, "profiles/" + rel);
                }
            if (Directory.Exists(AppPaths.ListsDir))
                foreach (var f in Directory.EnumerateFiles(AppPaths.ListsDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(AppPaths.ListsDir, f).Replace('\\', '/');
                    AddFile(zip, f, "lists/" + rel);
                }
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void AddFile(ZipArchive zip, string src, string entryName)
    {
        if (File.Exists(src)) zip.CreateEntryFromFile(src, entryName);
    }

    /// <summary>True if <paramref name="path"/> looks like a GxbrielZapret backup (a zip carrying the marker).</summary>
    public bool IsBackup(string path)
    {
        try { using var zip = ZipFile.OpenRead(path); return zip.GetEntry(Marker) is not null || zip.GetEntry(LegacyMarker) is not null; }
        catch { return false; }
    }

    /// <summary>Restore a backup into <c>%LOCALAPPDATA%\GxbrielZapret</c>, overwriting <c>settings.json</c>,
    /// <c>presets.json</c> and the files under <c>lists\</c>. The caller MUST relaunch the app immediately
    /// afterwards so the restored files are what gets loaded (and the outgoing instance — which holds the
    /// old settings in memory — has no chance to write them back over the restore). Throws if the file is
    /// not a valid backup.</summary>
    public void Restore(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.GetEntry(Marker) is null && zip.GetEntry(LegacyMarker) is null)
            throw new InvalidDataException(Loc.T("Это не резервная копия GxbrielZapret."));

        AppPaths.EnsureCreated();
        foreach (var entry in zip.Entries)
        {
            // Map each entry to a fixed, safe destination under Root; unknown entries (and the marker) are
            // skipped. Because list files land via Path.GetFileName, a crafted "..\" name can't escape.
            string? dest = ResolveDest(entry.FullName);
            if (dest is null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    /// <summary>Map a backup entry name to a safe absolute path under Root, or null to skip it.</summary>
    private static string? ResolveDest(string entryName)
    {
        string norm = entryName.Replace('\\', '/');
        if (norm == "settings.json" || norm == "config/settings.json") return AppPaths.SettingsFile;
        if (norm == "presets.json" || norm == "state/presets.json") return AppPaths.PresetsFile;
        if (norm == "discovery-targets.json" || norm == "config/discovery-targets.json") return AppPaths.DiscoveryTargetsFile;
        if (norm.StartsWith("profiles/", StringComparison.Ordinal) && !norm.EndsWith("/", StringComparison.Ordinal))
        {
            string sub = norm["profiles/".Length..];
            return Path.Combine(AppPaths.ProfilesDir, Path.GetFileName(sub));
        }
        if (norm.StartsWith("lists/", StringComparison.Ordinal) && !norm.EndsWith("/", StringComparison.Ordinal))
        {
            string sub = norm["lists/".Length..];
            return Path.Combine(AppPaths.ListsDir, Path.GetFileName(sub));
        }
        return null;
    }
}
