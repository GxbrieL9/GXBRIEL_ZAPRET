using System.IO;
using System.Text.Json;

namespace GxbrielZapret.Services.Infrastructure;

public sealed class EngineStateRecord
{
    public int SchemaVersion { get; set; } = 1;
    public bool BypassActive { get; set; }
    public string? ActivePresetName { get; set; }
    public string? ActiveHostlist { get; set; }
    public DateTime? LastStartedUtc { get; set; }
    public DateTime? LastStoppedUtc { get; set; }
    public int? ProcessId { get; set; }
}

public static class EngineStateStore
{
    private static readonly object _lock = new();

    public static EngineStateRecord? Load()
    {
        lock (_lock)
        {
            string file = AppPaths.EngineStateFile;
            if (!File.Exists(file)) return null;

            try
            {
                string json = File.ReadAllText(file);
                return JsonSerializer.Deserialize<EngineStateRecord>(json);
            }
            catch
            {
                return null;
            }
        }
    }

    public static void Save(EngineStateRecord record)
    {
        lock (_lock)
        {
            string dir = AppPaths.StateDir;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string file = AppPaths.EngineStateFile;
            string tempFile = Path.Combine(dir, $".engine_state_{Guid.NewGuid():N}.tmp");

            try
            {
                string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(flushToDisk: true);
                }

                File.Move(tempFile, file, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }
    }
}
