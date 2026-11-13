using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Engine;

/// <summary>
/// Supply-chain security validator for zapret2 engine binaries.
/// Enforces mandatory SHA-256 cryptographic verification against the embedded engine-manifest.json
/// before any binary is executed or staged.
/// </summary>
public static class EngineValidator
{
    private const string ManifestResource = "GXBRIEL_ZAPRET.engine.engine-manifest.json";

    public sealed class ManifestModel
    {
        public string Engine { get; set; } = "";
        public string Version { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Source { get; set; } = "";
        public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class FileEntry
    {
        public string Sha256 { get; set; } = "";
    }

    private static ManifestModel? _cachedManifest;
    private static readonly object _manifestLock = new();

    /// <summary>Loads and parses the official pinned engine manifest.</summary>
    public static ManifestModel LoadManifest()
    {
        lock (_manifestLock)
        {
            if (_cachedManifest is not null) return _cachedManifest;

            // Try embedded resource first
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(ManifestResource);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                var parsed = JsonSerializer.Deserialize<ManifestModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed is not null) return _cachedManifest = parsed;
            }

            throw new InvalidOperationException("Embedded engine-manifest.json not found or corrupted.");
        }
    }

    /// <summary>Computes SHA-256 hash formatted as lowercase hex string.</summary>
    public static string ComputeSha256(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("File not found for hash calculation", filePath);
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Verifies that an engine binary matches the pinned hash in engine-manifest.json.</summary>
    public static bool VerifyBinary(string filePath, string fileName)
    {
        var manifest = LoadManifest();
        if (!manifest.Files.TryGetValue(fileName, out var entry) || string.IsNullOrWhiteSpace(entry.Sha256))
            return false;

        string actual = ComputeSha256(filePath);
        return string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mandatory gate before Process.Start: verifies winws2.exe against the pinned manifest.
    /// Throws InvalidOperationException if hash mismatch or tampering is detected.
    /// </summary>
    public static void EnsureEngineVerified(string? winwsPath = null)
    {
        string path = winwsPath ?? AppPaths.WinwsExe;
        if (!File.Exists(path))
            throw new FileNotFoundException(Loc.T("winws2.exe не найден. Дождитесь загрузки движка."), path);

        var manifest = LoadManifest();
        if (!manifest.Files.TryGetValue("winws2.exe", out var entry) || string.IsNullOrWhiteSpace(entry.Sha256))
            throw new InvalidOperationException("Manifest does not define expected SHA-256 for winws2.exe");

        string actualHash = ComputeSha256(path);
        if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SECURITY ERROR: winws2.exe SHA-256 mismatch!\nExpected: {entry.Sha256}\nActual:   {actualHash}\nExecution blocked to prevent untrusted code execution.");
        }
    }
}
