using System.IO;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Network;

/// <summary>
/// Persistent storage layer for Network Profiles (Master ТЗ v2, Section 5).
/// Test results (WORK/BROKEN/UNKNOWN) and autopick history are treated as
/// Persistent Network Knowledge, never as disposable cache.
/// Stored in %LOCALAPPDATA%\GXBRIEL_ZAPRET\networks\{fingerprint}.json.
/// </summary>
public sealed class NetworkKnowledgeStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object FileLock = new();

    private const int MaxBackups = 5;

    public static string GetProfilePath(string fingerprint)
    {
        AppPaths.EnsureCreated();
        string safeName = string.Join("_", fingerprint.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(AppPaths.NetworksDir, $"{safeName}.json");
    }

    public static NetworkProfile GetOrCreate(string? fingerprint)
    {
        string fp = !string.IsNullOrWhiteSpace(fingerprint) ? fingerprint : (NetworkFingerprint.Current() ?? "default_network");
        var existing = Load(fp);
        if (existing is not null) return existing;

        var fresh = new NetworkProfile
        {
            NetworkFingerprint = fp,
            NetworkName = "Network " + fp,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow
        };
        Save(fresh);
        return fresh;
    }

    public static NetworkProfile? Load(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        string path = GetProfilePath(fingerprint);
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<NetworkProfile>(json, JsonOpts);
                if (loaded != null) return loaded;
            }
            catch
            {
                // Corrupted file: try recovery from rolling backup
                var recovered = TryLoadBackup(path);
                if (recovered != null)
                {
                    try
                    {
                        string corrupt = Path.Combine(AppPaths.BackupsDir, $"network-corrupt-{Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:yyyyMMddHHmmss}.json");
                        if (File.Exists(path)) File.Move(path, corrupt, overwrite: true);
                    }
                    catch { }
                    return recovered;
                }
            }
            return null;
        }
    }

    public static void Save(NetworkProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.NetworkFingerprint)) return;
        string path = GetProfilePath(profile.NetworkFingerprint);
        string? dir = Path.GetDirectoryName(path);
        string tempPath = Path.Combine(dir ?? AppPaths.NetworksDir, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        lock (FileLock)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                RotateBackups(path);

                string json = JsonSerializer.Serialize(profile, JsonOpts);
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, System.Text.Encoding.UTF8))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(flushToDisk: true);
                }

                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    private static NetworkProfile? TryLoadBackup(string profilePath)
    {
        try
        {
            for (int i = 1; i <= MaxBackups; i++)
            {
                string path = BackupPath(profilePath, i);
                if (!File.Exists(path)) continue;
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<NetworkProfile>(json, JsonOpts);
                if (loaded != null) return loaded;
            }
        }
        catch { }
        return null;
    }

    private static void RotateBackups(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(AppPaths.BackupsDir);
            for (int i = MaxBackups; i >= 2; i--)
            {
                string newer = BackupPath(path, i - 1);
                string older = BackupPath(path, i);
                if (File.Exists(newer)) File.Copy(newer, older, overwrite: true);
            }
            File.Copy(path, BackupPath(path, 1), overwrite: true);
        }
        catch { }
    }

    private static string BackupPath(string path, int index) =>
        Path.Combine(AppPaths.BackupsDir, $"{Path.GetFileName(path)}.bak{index}");

    public static IReadOnlyList<NetworkProfile> GetAll()
    {
        var list = new List<NetworkProfile>();
        lock (FileLock)
        {
            try
            {
                if (!Directory.Exists(AppPaths.NetworksDir)) return list;
                foreach (var file in Directory.EnumerateFiles(AppPaths.NetworksDir, "*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var p = JsonSerializer.Deserialize<NetworkProfile>(json, JsonOpts);
                        if (p is not null) list.Add(p);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return list;
    }
}
