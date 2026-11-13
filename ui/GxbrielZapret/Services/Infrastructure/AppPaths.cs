using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace GxbrielZapret.Services.Infrastructure;

/// <summary>
/// Central place for all on-disk locations across GXBRIEL_ZAPRET.
/// All mutable user data lives strictly under %LOCALAPPDATA%\GXBRIEL_ZAPRET.
/// System/elevated runtime binaries live under %ProgramData%\GXBRIEL_ZAPRET\runtime.
/// Nothing mutable is ever written adjacent to the executable.
/// </summary>
public static class AppPaths
{
    private const string RuntimeZipResource = "GXBRIEL_ZAPRET.engine.zapret2-runtime.zip";
    private const string RuntimeManifestResource = "GXBRIEL_ZAPRET.engine.engine-manifest.json";
    private const string BlitzFixZipResource = "GXBRIEL_ZAPRET.engine.blitzfix-tools.zip";
    // ==========================================
    // 1. Root Locations
    // ==========================================
    public static string AppDataRoot => ResolveAppDataRoot();

    private static string ResolveAppDataRoot()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--appdataroot=", StringComparison.OrdinalIgnoreCase))
                {
                    string path = args[i].Substring("--appdataroot=".Length).Trim('"');
                    if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);
                }
                else if (string.Equals(args[i], "--appdataroot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string path = args[i + 1].Trim('"');
                    if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);
                }
            }
        }
        catch { }

        string? overrideRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(overrideRoot);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GXBRIEL_ZAPRET");
    }

    /// <summary>Alias for AppDataRoot for backward compatibility.</summary>
    public static string Root => AppDataRoot;

    /// <summary>
    /// System-wide elevated runtime location for winws2, WinDivert driver, lua, etc.
    /// Default: %ProgramData%\GXBRIEL_ZAPRET\runtime
    /// Fallback: %LOCALAPPDATA%\GXBRIEL_ZAPRET\runtime (if ProgramData is inaccessible).
    /// This is lazily resolved and can be reset via ResetSystemRuntimeDirCache() for test isolation.
    /// </summary>
    private static string? _resolvedSystemRuntimeDir;
    private static readonly object _systemRuntimeDirLock = new();

    public static string SystemRuntimeDir
    {
        get
        {
            if (_resolvedSystemRuntimeDir != null) return _resolvedSystemRuntimeDir;
            lock (_systemRuntimeDirLock)
            {
                if (_resolvedSystemRuntimeDir != null) return _resolvedSystemRuntimeDir;
                _resolvedSystemRuntimeDir = ResolveSystemRuntimeDir();
                return _resolvedSystemRuntimeDir;
            }
        }
    }

    internal static void ResetSystemRuntimeDirCache()
    {
        lock (_systemRuntimeDirLock) { _resolvedSystemRuntimeDir = null; }
    }

    private static string ResolveSystemRuntimeDir()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--programdataroot=", StringComparison.OrdinalIgnoreCase))
                {
                    string path = args[i].Substring("--programdataroot=".Length).Trim('"');
                    if (!string.IsNullOrWhiteSpace(path)) return Path.Combine(Path.GetFullPath(path), "runtime");
                }
                else if (string.Equals(args[i], "--programdataroot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string path = args[i + 1].Trim('"');
                    if (!string.IsNullOrWhiteSpace(path)) return Path.Combine(Path.GetFullPath(path), "runtime");
                }
            }
        }
        catch { }

        string? overrideRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.Combine(Path.GetFullPath(overrideRoot), "runtime");

        try
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
            {
                string target = Path.Combine(programData, "GXBRIEL_ZAPRET", "runtime");
                return target;
            }
        }
        catch { }
        return Path.Combine(AppDataRoot, "runtime");
    }

    // ==========================================
    // 2. Partitioned User Data Directories
    // ==========================================
    public static string ConfigDir => Path.Combine(AppDataRoot, "config");
    public static string ProfilesDir => Path.Combine(AppDataRoot, "profiles");
    public static string ProfilesWorkDir => Path.Combine(ProfilesDir, "work");
    public static string ProfilesBrokenDir => Path.Combine(ProfilesDir, "broken");
    public static string ProfilesListsDir => Path.Combine(ProfilesDir, "lists");

    public static string ListsDir => Path.Combine(AppDataRoot, "lists");
    public static string ListsKnownDir => Path.Combine(ListsDir, "known");
    public static string ListsDiscoveredDir => Path.Combine(ListsDir, "discovered");
    public static string ListsUserDir => Path.Combine(ListsDir, "user");
    public static string ListsExcludeDir => Path.Combine(ListsDir, "exclude");

    public static string NetworksDir => Path.Combine(AppDataRoot, "networks");
    public static string StrategiesDir => Path.Combine(AppDataRoot, "strategies");
    public static string StrategiesUserDir => Path.Combine(StrategiesDir, "user");
    public static string StrategiesGeneratedDir => Path.Combine(StrategiesDir, "generated");
    public static string StrategiesDiscoveredDir => Path.Combine(StrategiesDir, "discovered");
    public static string TestsDir => Path.Combine(AppDataRoot, "tests");
    public static string DiscoveryDir => Path.Combine(AppDataRoot, "discovery");
    public static string TelemetryDir => Path.Combine(AppDataRoot, "telemetry");
    public static string TelegramDir => Path.Combine(AppDataRoot, "telegram");

    public static string LogsDir => Path.Combine(AppDataRoot, "logs");
    public static string CacheDir => Path.Combine(AppDataRoot, "cache");
    public static string TempDir => Path.Combine(AppDataRoot, "temp");
    public static string BackupsDir => Path.Combine(AppDataRoot, "backups");
    public static string StateDir => Path.Combine(AppDataRoot, "state");

    // ==========================================
    // 3. User Files (Config, State, Discovery)
    // ==========================================
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string DiscoveryTargetsFile => Path.Combine(ConfigDir, "discovery-targets.json");
    public static string PresetsFile => Path.Combine(StateDir, "presets.json");
    public static string EngineStateFile => Path.Combine(StateDir, "engine-state.json");

    /// <summary>Path of a named CIDR ipset, e.g. ipset("telegram") -> lists\ipset-telegram.txt.</summary>
    public static string IpsetFile(string name) => Path.Combine(ListsDir, $"ipset-{name}.txt");

    /// <summary>Aggregated CIDR ipset for Discord (built from the discord hostlist).</summary>
    public static string IpsetDiscordFile => IpsetFile("discord");

    /// <summary>League game-server CIDRs used by the TCP 2099 launch/session path.</summary>
    public static string IpsetLolFile => IpsetFile("lol");

    /// <summary>Observed Blitz/Porofessor public IPs used when the app connects without useful SNI.</summary>
    public static string IpsetBlitzFile => IpsetFile("blitz");

    /// <summary>Minimal Riot auth endpoints needed before League can resolve the player/session.</summary>
    public static string RiotAuthHostlistFile => Path.Combine(ListsDir, "list-riot-auth.txt");

    /// <summary>Single endpoint that issues League entitlement tokens.</summary>
    public static string RiotEntitlementsHostlistFile => Path.Combine(ListsDir, "list-riot-entitlements.txt");

    /// <summary>Blitz app/web/update endpoints that need the same targeted TLS bypass as LoL helpers.</summary>
    public static string BlitzHostlistFile => Path.Combine(ListsDir, "list-blitz.txt");

    /// <summary>Steam web, authentication, CDN, and content endpoints routed through targeted TLS rules.</summary>
    public static string SteamHostlistFile => Path.Combine(ListsDir, "list-steam.txt");

    /// <summary>Hostlist of the Telegram fronting domains if configured.</summary>
    public static string TgProxyFrontsFile => Path.Combine(ListsDir, "tgproxy-fronts.txt");

    // ==========================================
    // 4. Engine Runtime (winws2, WinDivert, lua)
    // ==========================================
    private static string? _resolvedEngineDir;
    private static readonly object _engineDirLock = new();

    public static string EngineDir
    {
        get
        {
            if (_resolvedEngineDir != null) return _resolvedEngineDir;
            lock (_engineDirLock)
            {
                if (_resolvedEngineDir != null) return _resolvedEngineDir;
                try
                {
                    if (!Directory.Exists(SystemRuntimeDir))
                        Directory.CreateDirectory(SystemRuntimeDir);
                    // Verify write permissions
                    string testFile = Path.Combine(SystemRuntimeDir, ".write_test_" + Guid.NewGuid().ToString("N"));
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    _resolvedEngineDir = SystemRuntimeDir;
                    return _resolvedEngineDir;
                }
                catch
                {
                    string fallback = Path.Combine(AppDataRoot, "runtime");
                    try { Directory.CreateDirectory(fallback); } catch { }
                    _resolvedEngineDir = fallback;
                    return _resolvedEngineDir;
                }
            }
        }
    }

    internal static void ResetEngineDirCache()
    {
        lock (_engineDirLock) { _resolvedEngineDir = null; }
    }

    public static string EngineVersionFile => Path.Combine(EngineDir, "installed_version.txt");
    public static string WinwsExe => Path.Combine(EngineDir, "winws2.exe");
    public static string MdigExe => Path.Combine(EngineDir, "mdig.exe");
    public static string Ip2NetExe => Path.Combine(EngineDir, "ip2net.exe");
    public static string LuaDir => Path.Combine(EngineDir, "lua");
    public static string FilesDir => Path.Combine(EngineDir, "files");
    public static string WinDivertFilterDir => Path.Combine(EngineDir, "windivert.filter");
    public static string RuntimeListsDir => Path.Combine(EngineDir, "lists");
    public static string RuntimeManifestFile => Path.Combine(EngineDir, "engine-manifest.json");

    /// <summary>Windows binary subfolder inside the release zip for this OS.</summary>
    public static string ReleaseArchFolder =>
        Environment.Is64BitOperatingSystem ? "windows-x86_64" : "windows-x86";

    // BlitzFix directories (cached analysis & signatures)
    public static string BlitzFixDir => ResolveBlitzFixDir();
    public static string BlitzFixOutputDir => Path.Combine(BlitzFixDir, "output");

    private static string ResolveBlitzFixDir()
    {
        string cached = Path.Combine(CacheDir, "blitzfix");
        try
        {
            Directory.CreateDirectory(cached);
            SeedBundledBlitzFixIfMissing(cached);
        }
        catch { }

        try
        {
            string[] roots =
            {
                Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_ROOT") ?? "",
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory(),
                ProjectRoot
            };

            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string candidate = Path.Combine(Path.GetFullPath(root), "BlitzFix");
                if (File.Exists(Path.Combine(candidate, "tools", "blitzfix-trace.ps1")) ||
                    Directory.Exists(Path.Combine(candidate, "output")))
                    return candidate;
            }
        }
        catch { }

        try { return cached; }
        catch { }
        return Path.Combine(CacheDir, "blitzfix");
    }

    private static void SeedBundledBlitzFixIfMissing(string blitzDir)
    {
        string trace = Path.Combine(blitzDir, "tools", "blitzfix-trace.ps1");
        string tcpvcon = Path.Combine(blitzDir, "TCPView", Environment.Is64BitOperatingSystem ? "tcpvcon64.exe" : "tcpvcon.exe");
        if (File.Exists(trace))
        {
            Directory.CreateDirectory(Path.Combine(blitzDir, "output"));
            Directory.CreateDirectory(Path.Combine(blitzDir, "input"));
            return;
        }

        Directory.CreateDirectory(blitzDir);
        Directory.CreateDirectory(TempDir);
        string stage = Path.Combine(TempDir, "blitzfix-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using Stream? zipStream = asm.GetManifestResourceStream(BlitzFixZipResource);
            if (zipStream is null) return;

            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                    string fullPath = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                    string stageRoot = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("BlitzFix package contains an unsafe path.");

                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    entry.ExtractToFile(fullPath, overwrite: true);
                }
            }

            CopyDirectoryRecursivelyOverwrite(stage, blitzDir);
            Directory.CreateDirectory(Path.Combine(blitzDir, "output"));
            Directory.CreateDirectory(Path.Combine(blitzDir, "input"));
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    // Static project root for development / build reference only
    public static string ProjectRoot { get; } = FindProjectRoot();
    public static bool HasDevelopmentProjectRoot { get; } = !IsSameDirectory(ProjectRoot, AppDataRoot);

    static AppPaths()
    {
        try
        {
            string legacyGxbrielZapret = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GxbrielZapret");
            if (Directory.Exists(legacyGxbrielZapret) && !Directory.Exists(AppDataRoot))
                Directory.Move(legacyGxbrielZapret, AppDataRoot);
        }
        catch { /* keep going with a fresh folder */ }
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(ProfilesWorkDir);
        Directory.CreateDirectory(ProfilesBrokenDir);
        Directory.CreateDirectory(ProfilesListsDir);

        Directory.CreateDirectory(ListsDir);
        Directory.CreateDirectory(ListsKnownDir);
        Directory.CreateDirectory(ListsDiscoveredDir);
        Directory.CreateDirectory(ListsUserDir);
        Directory.CreateDirectory(ListsExcludeDir);

        Directory.CreateDirectory(NetworksDir);
        Directory.CreateDirectory(StrategiesDir);
        Directory.CreateDirectory(StrategiesUserDir);
        Directory.CreateDirectory(StrategiesGeneratedDir);
        Directory.CreateDirectory(StrategiesDiscoveredDir);
        Directory.CreateDirectory(TestsDir);
        Directory.CreateDirectory(DiscoveryDir);
        Directory.CreateDirectory(TelemetryDir);
        Directory.CreateDirectory(TelegramDir);

        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(StateDir);

        try
        {
            Directory.CreateDirectory(EngineDir);
            Directory.CreateDirectory(RuntimeListsDir);
        }
        catch { }

        SeedBundledEngineIfMissing();
    }

    public static void SeedBundledEngineIfMissing()
    {
        _ = TryRestoreBundledEngine(out _);
    }

    /// <summary>
    /// Restores the embedded zapret runtime from this application's resources without using the
    /// network. Returns true only when the runtime is complete after the operation.
    /// </summary>
    public static bool TryRestoreBundledEngine(out string? error)
    {
        try
        {
            var manifest = LoadEmbeddedRuntimeManifest();
            if (RuntimeMatchesManifest(manifest))
            {
                error = null;
                return true;
            }

            ExtractEmbeddedRuntime(manifest);
            bool restored = RuntimeMatchesManifest(manifest);
            error = restored ? null : "Embedded runtime extraction finished, but verification still failed.";
            return restored;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static RuntimeManifest LoadEmbeddedRuntimeManifest()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(RuntimeManifestResource);
        if (stream is not null)
        {
            return JsonSerializer.Deserialize<RuntimeManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Embedded runtime manifest is invalid.");
        }

        throw new InvalidOperationException("Embedded runtime manifest is missing.");
    }

    private static bool RuntimeMatchesManifest(RuntimeManifest manifest)
    {
        if (!File.Exists(WinwsExe) || !Directory.Exists(LuaDir) || !Directory.Exists(WinDivertFilterDir))
            return false;

        foreach (var (relativePath, entry) in manifest.Files)
        {
            string path = Path.Combine(EngineDir, NormalizeRelativePath(relativePath));
            if (!File.Exists(path)) return false;
            if (!string.Equals(Sha256File(path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        string installed = File.Exists(EngineVersionFile) ? File.ReadAllText(EngineVersionFile).Trim() : "";
        return string.Equals(installed, manifest.Version, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractEmbeddedRuntime(RuntimeManifest manifest)
    {
        string parent = Path.GetDirectoryName(EngineDir) ?? AppDataRoot;
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(TempDir);

        string stage = Path.Combine(TempDir, "runtime-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            Stream? zipStream = asm.GetManifestResourceStream(RuntimeZipResource);

            if (zipStream is null)
                throw new InvalidOperationException("Embedded runtime package is missing.");

            try
            {
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                    string fullPath = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                    string stageRoot = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Runtime package contains an unsafe path.");

                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    entry.ExtractToFile(fullPath, overwrite: true);
                }
            }
            finally { }

            VerifyDirectoryAgainstManifest(stage, manifest);

            Directory.CreateDirectory(EngineDir);
            CopyDirectoryRecursivelyOverwrite(stage, EngineDir);
            File.WriteAllText(EngineVersionFile, manifest.Version);
            WriteEmbeddedManifestToRuntime();
            Directory.CreateDirectory(RuntimeListsDir);
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    private static void VerifyDirectoryAgainstManifest(string directory, RuntimeManifest manifest)
    {
        foreach (var (relativePath, entry) in manifest.Files)
        {
            string path = Path.Combine(directory, NormalizeRelativePath(relativePath));
            if (!File.Exists(path))
                throw new FileNotFoundException("Runtime package is missing required component.", relativePath);

            string actual = Sha256File(path);
            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Runtime component hash mismatch: {relativePath}");
        }
    }

    private static void WriteEmbeddedManifestToRuntime()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(RuntimeManifestResource);
        if (stream is null) return;
        using var outStream = new FileStream(RuntimeManifestFile, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.CopyTo(outStream);
        outStream.Flush(true);
    }

    private static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static bool IsSameDirectory(string left, string right)
    {
        static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        try
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void CopyDirectoryRecursivelyOverwrite(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }
        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            string destSub = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryRecursivelyOverwrite(subDir, destSub);
        }
    }

    private sealed class RuntimeManifest
    {
        public string Version { get; set; } = "";
        public Dictionary<string, RuntimeManifestEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RuntimeManifestEntry
    {
        public string Sha256 { get; set; } = "";
    }

    private static string FindProjectRoot()
    {
        try
        {
            string? envRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_ROOT");
            if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
                return Path.GetFullPath(envRoot);

        }
        catch { /* fallback below */ }

        return AppDataRoot;
    }
}

/// <summary>
/// Alias for AppPaths to satisfy unified PathManager API requirements.
/// </summary>
public static class PathManager
{
    public static string AppDataRoot => AppPaths.AppDataRoot;
    public static string ConfigDir => AppPaths.ConfigDir;
    public static string ProfilesDir => AppPaths.ProfilesDir;
    public static string ListsDir => AppPaths.ListsDir;
    public static string ListsUserDir => AppPaths.ListsUserDir;
    public static string ListsDiscoveredDir => AppPaths.ListsDiscoveredDir;
    public static string ListsExcludeDir => AppPaths.ListsExcludeDir;
    public static string NetworksDir => AppPaths.NetworksDir;
    public static string StrategiesDir => AppPaths.StrategiesDir;
    public static string TestsDir => AppPaths.TestsDir;
    public static string DiscoveryDir => AppPaths.DiscoveryDir;
    public static string TelemetryDir => AppPaths.TelemetryDir;
    public static string TelegramDir => AppPaths.TelegramDir;
    public static string LogsDir => AppPaths.LogsDir;
    public static string CacheDir => AppPaths.CacheDir;
    public static string TempDir => AppPaths.TempDir;
    public static string BackupsDir => AppPaths.BackupsDir;
    public static string StateDir => AppPaths.StateDir;
    public static string SystemRuntimeDir => AppPaths.SystemRuntimeDir;
    public static string EngineDir => AppPaths.EngineDir;
    public static string RuntimeListsDir => AppPaths.RuntimeListsDir;

    public static void EnsureCreated() => AppPaths.EnsureCreated();
}
