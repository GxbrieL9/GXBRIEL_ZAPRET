using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Engine;

/// <summary>
/// Manages domain hostlists and IP sets stored as plain .txt files under profiles/lists
/// and %LOCALAPPDATA%\GXBRIEL_ZAPRET\lists. Provides high-performance native parsing
/// of BlitzFix network traces, multi-mirror upstream list updates, DNS resolution,
/// and automated subnet aggregation.
/// </summary>
public sealed class HostlistService
{
    private const string BundledListResourcePrefix = "GXBRIEL_ZAPRET.bundled-lists.";
    private static readonly object _seedLock = new();
    [ThreadStatic]
    private static bool _isSeeding;

    public HostlistService()
    {
        AppPaths.EnsureCreated();
        SeedDefaults();
    }

    public static void EnsureDefaultsSeeded()
    {
        if (_isSeeding) return;

        string discordPath = Path.Combine(AppPaths.ListsDir, "list-discord.txt");
        string youtubePath = Path.Combine(AppPaths.ListsDir, "list-youtube.txt");
        string riotEntitlementsPath = Path.Combine(AppPaths.ListsDir, "list-riot-entitlements.txt");
        string blitzPath = Path.Combine(AppPaths.ListsDir, "list-blitz.txt");
        string steamPath = Path.Combine(AppPaths.ListsDir, "list-steam.txt");
        if (!File.Exists(discordPath) || !File.Exists(youtubePath) || !File.Exists(riotEntitlementsPath) || !File.Exists(blitzPath) || !File.Exists(steamPath))
        {
            lock (_seedLock)
            {
                if (!File.Exists(discordPath) || !File.Exists(youtubePath) || !File.Exists(riotEntitlementsPath) || !File.Exists(blitzPath) || !File.Exists(steamPath))
                {
                    _isSeeding = true;
                    try
                    {
                        new HostlistService().SeedDefaults();
                    }
                    finally
                    {
                        _isSeeding = false;
                    }
                }
            }
        }
    }

    /// <summary>Names (without extension) of all available lists.</summary>
    public IReadOnlyList<string> GetLists()
    {
        try
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new[] { AppPaths.ListsDir, AppPaths.ListsKnownDir, AppPaths.ListsDiscoveredDir, AppPaths.ListsUserDir, AppPaths.ListsExcludeDir };

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.txt"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Hide internal/resolved IP sets and custom-target internal files from hostlists UI
                    if (name.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("target-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("custom-", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("cleanapp", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("concurrent-target", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("targets", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("exclude-eff", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("tgproxy-fronts", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("warp-api", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Normalize name display (strip list- prefix for cleaner UI)
                    if (name.StartsWith("list-", StringComparison.OrdinalIgnoreCase))
                        name = name["list-".Length..];

                    set.Add(name);
                }
            }

            return set.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public string GetPath(string name) => ResolveListFilePath(name);

    public static string MaterializeRuntimeList(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return "";
        string fileName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "runtime-list.txt";

        AppPaths.EnsureCreated();
        string targetDir = AppPaths.RuntimeListsDir;
        string target = Path.Combine(targetDir, fileName);

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return target;

        if (!File.Exists(sourcePath))
            return File.Exists(target) ? target : sourcePath;

        try
        {
            Directory.CreateDirectory(targetDir);

            if (File.Exists(target))
            {
                var srcInfo = new FileInfo(sourcePath);
                var dstInfo = new FileInfo(target);
                if (srcInfo.Length == dstInfo.Length && srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                    return target;

                File.SetAttributes(target, FileAttributes.Normal);
            }

            File.Copy(sourcePath, target, overwrite: true);
            return target;
        }
        catch
        {
            // If ProgramData runtime/lists is write-protected or locked, fallback to LocalAppData runtime lists
            try
            {
                string fallbackDir = Path.Combine(AppPaths.AppDataRoot, "runtime", "lists");
                Directory.CreateDirectory(fallbackDir);
                string fallbackTarget = Path.Combine(fallbackDir, fileName);
                if (File.Exists(target)) return target;

                if (File.Exists(fallbackTarget))
                {
                    var srcInfo = new FileInfo(sourcePath);
                    var dstInfo = new FileInfo(fallbackTarget);
                    if (srcInfo.Length == dstInfo.Length && srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                        return fallbackTarget;
                    File.SetAttributes(fallbackTarget, FileAttributes.Normal);
                }
                File.Copy(sourcePath, fallbackTarget, overwrite: true);
                return fallbackTarget;
            }
            catch
            {
                return File.Exists(target) ? target : sourcePath;
            }
        }
    }

    public static string ResolveListFilePath(string name)
    {
        EnsureDefaultsSeeded();
        string baseName = name.StartsWith("list-", StringComparison.OrdinalIgnoreCase)
            ? name["list-".Length..]
            : name;

        string[] candidates =
        {
            Path.Combine(AppPaths.ListsDir, $"list-{baseName}.txt"),
            Path.Combine(AppPaths.ListsDir, $"{baseName}.txt"),
            Path.Combine(AppPaths.ListsDir, $"{name}.txt"),
            Path.Combine(AppPaths.ListsUserDir, $"list-{baseName}.txt"),
            Path.Combine(AppPaths.ListsUserDir, $"{baseName}.txt"),
            Path.Combine(AppPaths.ListsKnownDir, $"list-{baseName}.txt"),
            Path.Combine(AppPaths.ListsKnownDir, $"{baseName}.txt"),
            Path.Combine(AppPaths.ListsDiscoveredDir, $"list-{baseName}.txt"),
            Path.Combine(AppPaths.ListsDiscoveredDir, $"{baseName}.txt"),
            Path.Combine(AppPaths.ListsExcludeDir, $"list-{baseName}.txt"),
            Path.Combine(AppPaths.ListsExcludeDir, $"{baseName}.txt")
        };

        return candidates.FirstOrDefault(File.Exists) ?? Path.Combine(AppPaths.ListsDir, $"list-{baseName}.txt");
    }

    private static IEnumerable<string> GetListSourcePaths(string name)
    {
        string baseName = name.StartsWith("list-", StringComparison.OrdinalIgnoreCase)
            ? name["list-".Length..]
            : name;

        string[] dirs =
        {
            AppPaths.ListsDir,
            AppPaths.ListsKnownDir,
            AppPaths.ListsDiscoveredDir,
            AppPaths.ListsUserDir
        };

        foreach (string dir in dirs)
        {
            yield return Path.Combine(dir, $"list-{baseName}.txt");
            yield return Path.Combine(dir, $"{baseName}.txt");
            yield return Path.Combine(dir, $"{name}.txt");
        }
    }

    public static string ResolveRuntimeListFilePath(string name)
    {
        string source = ResolveListFilePath(name);
        return CompileRuntimeList(GetListSourcePaths(name), Path.GetFileName(source));
    }

    public static string ResolveExcludeFilePath(string name)
    {
        EnsureDefaultsSeeded();
        string baseName = name.StartsWith("list-", StringComparison.OrdinalIgnoreCase)
            ? name["list-".Length..]
            : name;

        string[] candidates =
        {
            Path.Combine(AppPaths.ListsDir, "list-exclude.txt"),
            Path.Combine(AppPaths.ListsDir, $"{baseName}.txt"),
            Path.Combine(AppPaths.ListsDir, "exclude.txt"),
            Path.Combine(AppPaths.ListsExcludeDir, "list-exclude.txt"),
            Path.Combine(AppPaths.ListsExcludeDir, "exclude.txt"),
            Path.Combine(AppPaths.ListsUserDir, "list-exclude.txt"),
            Path.Combine(AppPaths.ListsUserDir, "exclude.txt")
        };

        return candidates.FirstOrDefault(File.Exists) ?? Path.Combine(AppPaths.ListsDir, "list-exclude.txt");
    }

    private static IEnumerable<string> GetExcludeSourcePaths(string name)
    {
        string baseName = name.StartsWith("list-", StringComparison.OrdinalIgnoreCase)
            ? name["list-".Length..]
            : name;

        foreach (string dir in new[] { AppPaths.ListsDir, AppPaths.ListsExcludeDir, AppPaths.ListsUserDir })
        {
            yield return Path.Combine(dir, "list-exclude.txt");
            yield return Path.Combine(dir, $"{baseName}.txt");
            yield return Path.Combine(dir, "exclude.txt");
        }
    }

    public static string ResolveRuntimeExcludeFilePath(string name)
    {
        string source = ResolveExcludeFilePath(name);
        return CompileRuntimeList(GetExcludeSourcePaths(name), Path.GetFileName(source));
    }

    public static string ResolveIpsetFilePath(string name)
    {
        string baseName = name.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase)
            ? name["ipset-".Length..]
            : name;

        string[] candidates =
        {
            Path.Combine(AppPaths.ListsDir, $"ipset-{baseName}.txt"),
            Path.Combine(AppPaths.ListsDir, $"{name}.txt"),
            Path.Combine(AppPaths.ListsUserDir, $"ipset-{baseName}.txt"),
            Path.Combine(AppPaths.ListsKnownDir, $"ipset-{baseName}.txt"),
            Path.Combine(AppPaths.ListsDiscoveredDir, $"ipset-{baseName}.txt"),
            Path.Combine(AppPaths.ListsExcludeDir, $"ipset-{baseName}.txt")
        };

        return candidates.FirstOrDefault(File.Exists) ?? Path.Combine(AppPaths.ListsDir, $"ipset-{baseName}.txt");
    }

    private static IEnumerable<string> GetIpsetSourcePaths(string name)
    {
        string baseName = name.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase)
            ? name["ipset-".Length..]
            : name;

        foreach (string dir in new[] { AppPaths.ListsDir, AppPaths.ListsKnownDir, AppPaths.ListsDiscoveredDir, AppPaths.ListsUserDir })
        {
            yield return Path.Combine(dir, $"ipset-{baseName}.txt");
            yield return Path.Combine(dir, $"{name}.txt");
        }
    }

    public static string ResolveRuntimeIpsetFilePath(string name)
    {
        string source = ResolveIpsetFilePath(name);
        return CompileRuntimeList(GetIpsetSourcePaths(name), Path.GetFileName(source));
    }

    private static string CompileRuntimeList(IEnumerable<string> sourcePaths, string fileName)
    {
        AppPaths.EnsureCreated();
        string targetDir = AppPaths.RuntimeListsDir;
        string safeFileName = string.IsNullOrWhiteSpace(fileName) ? "runtime-list.txt" : fileName;
        string target = Path.Combine(targetDir, safeFileName);
        var content = new StringBuilder();
        bool found = false;
        foreach (string source in sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(source)) continue;
            found = true;
            content.AppendLine(File.ReadAllText(source));
        }

        if (found)
        {
            string normalized = NormalizeListContent(content.ToString());
            try
            {
                Directory.CreateDirectory(targetDir);
                File.WriteAllText(target, normalized);
            }
            catch
            {
                // Fallback to LocalAppData runtime lists if ProgramData is write-protected in test runner
                try
                {
                    string fallbackDir = Path.Combine(AppPaths.AppDataRoot, "runtime", "lists");
                    Directory.CreateDirectory(fallbackDir);
                    string fallbackTarget = Path.Combine(fallbackDir, safeFileName);
                    File.WriteAllText(fallbackTarget, normalized);
                    return fallbackTarget;
                }
                catch { }
            }
        }

        return target;
    }

    public bool Exists(string name) => File.Exists(GetPath(name));

    public string Read(string name)
    {
        string p = GetPath(name);
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    public List<string> ReadDomains(string name) =>
        Read(name)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    public void Write(string name, string content)
    {
        AppPaths.EnsureCreated();
        string path = GetPath(name);
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        string normalized = NormalizeListContent(content);
        File.WriteAllText(path, normalized);

        if (!BundledListNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ListsUserDir);
                File.WriteAllText(Path.Combine(AppPaths.ListsUserDir, Path.GetFileName(path)), normalized);
            }
            catch { }
        }

        try { MaterializeRuntimeList(path); } catch { }
    }

    public void Create(string name)
    {
        if (!Exists(name)) Write(name, "");
    }

    public void Delete(string name)
    {
        string p = GetPath(name);
        if (File.Exists(p)) File.Delete(p);
        string userP = Path.Combine(AppPaths.ListsUserDir, Path.GetFileName(p));
        if (File.Exists(userP)) { try { File.Delete(userP); } catch { } }
        string runtimeP = Path.Combine(AppPaths.RuntimeListsDir, Path.GetFileName(p));
        if (File.Exists(runtimeP)) { try { File.Delete(runtimeP); } catch { } }
    }

    public void AddDomain(string name, string domain)
    {
        domain = domain.Trim();
        if (domain.Length == 0) return;
        var domains = Exists(name) ? ReadDomains(name) : new List<string>();
        if (!domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            domains.Add(domain);
            Write(name, string.Join('\n', domains));
        }
    }

    /// <summary>The bundled "authored" lists, kept in sync with the code below.</summary>
    public static readonly string[] BundledListNames = { "youtube", "discord", "exclude", "general", "lol" };

    public sealed record OnlineUpdateSummary(
        int DiscordDomains,
        int YoutubeDomains,
        int GeneralDomains,
        int LolDomains,
        int DiscordSubnets,
        int TotalNewDomainsAdded,
        string StatusMessage,
        int BlitzFilesCount = 0,
        int BlitzEndpointsCount = 0,
        int BlitzDomainsCount = 0,
        int BlitzIpsCount = 0,
        int MirrorsDownloaded = 0,
        int TotalNewIpsAdded = 0,
        int TotalListsChanged = 0
    );

    public sealed record BlitzFixParseResult(
        int FilesParsed,
        int EndpointsParsed,
        HashSet<string> LolDomains,
        HashSet<string> LolIps,
        HashSet<string> BlitzDomains,
        HashSet<string> BlitzIps,
        HashSet<string> DiscordDomains,
        HashSet<string> DiscordIps,
        HashSet<string> RockstarDomains,
        HashSet<string> RockstarIps,
        HashSet<string> GeneralDomains
    );

    // =========================================================================
    // Domain & IP Validation Rules
    // =========================================================================

    public static readonly string[] AllowedLolSuffixes =
    {
        "riotgames.com", "riotcdn.net", "lol.pvp.net", "sgp.pvp.net",
        "lolesports.com", "leagueoflegends.com", "vivox.com", "blitz.gg",
        "porofessor.gg", "iesdev.com", "edgesuite.net", "b-cdn.net",
        "akamai.net", "pacloudflare.com", "pvp.net", "playvalorant.com",
        "teamfighttactics.com", "legendsofruneterra.com", "wildrift.com",
        "rdatasrv.net"
    };

    public static readonly string[] AllowedBlitzSuffixes =
    {
        "blitz.gg", "blitzapp.gg", "porofessor.gg", "iesdev.com", "b-cdn.net"
    };

    public static readonly string[] AllowedDiscordSuffixes =
    {
        "discord.com", "discord.gg", "discordapp.com", "discordapp.net",
        "discord.media", "discordapp.io", "discordcdn.com", "dis.gd",
        "discord.co", "discord.gift", "discord.gifts", "discord.new",
        "discord.store", "discord.tools", "discord.dev", "discord.design",
        "discord-activities.com", "discordactivities.com",
        "cloudflare-ech.com", "encryptedsni.com", "challenges.cloudflare.com"
    };

    public static readonly string[] AllowedRockstarSuffixes =
    {
        "rockstargames.com", "socialclub.rockstargames.com", "rsg.sc",
        "take2games.com", "2kgames.com", "2k.com"
    };

    public static readonly string[] AllowedGeneralSuffixes =
    {
        "cloudflare-ech.com", "encryptedsni.com", "cloudflareaccess.com", "cloudflareapps.com",
        "cloudflarebolt.com", "cloudflareclient.com", "cloudflareinsights.com", "cloudflareok.com",
        "cloudflarepartners.com", "cloudflareportal.com", "cloudflarepreview.com", "cloudflareresolve.com",
        "cloudflaressl.com", "cloudflarestatus.com", "cloudflarestorage.com", "cloudflarestream.com",
        "cloudflaretest.com", "cloudfront.net", "frankerfacez.com", "ffzap.com", "betterttv.net",
        "7tv.app", "7tv.io", "localizeapi.com", "klipy.com"
    };

    private static readonly string[] ProtectedGameSuffixes =
    {
        "riotgames.com", "riotcdn.net", "leagueoflegends.com", "pvp.net",
        "playvalorant.com", "teamfighttactics.com", "legendsofruneterra.com",
        "wildrift.com", "vivox.com", "splice.com", "cloudinary.com", "auth0.com",
        "s3-us-west-1.amazonaws.com", "spliceproduction.s3.us-west-1.amazonaws.com", "spliceproduction.s3.amazonaws.com"
    };

    private static bool IsProtectedGameDomain(string domain) =>
        MatchesSuffix(domain, ProtectedGameSuffixes);

    private static bool IsActiveBypassList(string name) =>
        name.Equals("general", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ultimate", StringComparison.OrdinalIgnoreCase);

    public static bool TestPublicIPv4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!IPAddress.TryParse(value.Trim(), out var addr)) return false;
        if (addr.AddressFamily != AddressFamily.InterNetwork) return false;

        byte[] b = addr.GetAddressBytes();
        if (b[0] == 0) return false;
        if (b[0] == 10) return false;
        if (b[0] == 127) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] >= 224) return false;
        return true;
    }

    public static string CleanDomain(string raw)
    {
        string d = raw.Trim().ToLowerInvariant();
        if (d.StartsWith("*.")) d = d[2..];
        if (d.EndsWith(":*")) d = d[..^2];
        if (d.EndsWith(":443")) d = d[..^4];
        if (d.EndsWith(":80")) d = d[..^3];
        int hash = d.IndexOf('#');
        if (hash >= 0) d = d[..hash].Trim();
        int semi = d.IndexOf(';');
        if (semi >= 0) d = d[..semi].Trim();
        return d.Trim().TrimEnd('.');
    }

    public static bool IsValidDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253) return false;
        if (domain.Contains('/') || domain.Contains('\\') || domain.Contains(' ') || domain.Contains(':')) return false;
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }

    public static bool MatchesSuffix(string domain, IEnumerable<string> suffixes)
    {
        string clean = CleanDomain(domain);
        foreach (var suffix in suffixes)
        {
            string s = suffix.Trim().TrimStart('.').ToLowerInvariant();
            if (clean.Equals(s, StringComparison.OrdinalIgnoreCase) ||
                clean.EndsWith("." + s, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // =========================================================================
    // Native BlitzFix Parsing
    // =========================================================================

    /// <summary>
    /// Deeply inspects all BlitzFix capture traces (output/*.json, output/*.txt, subfolder traces),
    /// extracts live endpoints, classifies domains & public IPs, and builds discovered databases.
    /// </summary>
    public BlitzFixParseResult ParseBlitzFixTraces(Action<string>? progressCallback = null)
    {
        var lolDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lolIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blitzDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blitzIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discordDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discordIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rockstarDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rockstarIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generalDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int filesParsed = 0;
        int endpointsParsed = 0;

        string blitzDir = AppPaths.BlitzFixDir;
        if (!Directory.Exists(blitzDir))
        {
            return new BlitzFixParseResult(0, 0, lolDomains, lolIps, blitzDomains, blitzIps, discordDomains, discordIps, rockstarDomains, rockstarIps, generalDomains);
        }

        progressCallback?.Invoke("Поиск файлов трассировки в BlitzFix...");

        var jsonFiles = Directory.EnumerateFiles(blitzDir, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("list-update-state.json", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).Equals("sources.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var txtFiles = Directory.EnumerateFiles(blitzDir, "*.txt", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).StartsWith("apptrace-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 1. Parse JSON endpoint files
        foreach (var file in jsonFiles)
        {
            try
            {
                string json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json) || json.Length < 4) continue;

                using var doc = JsonDocument.Parse(json);
                filesParsed++;

                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in root.EnumerateArray())
                    {
                        endpointsParsed++;
                        ProcessJsonEndpoint(elem, lolDomains, lolIps, blitzDomains, blitzIps, discordDomains, discordIps, rockstarDomains, rockstarIps, generalDomains);
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    endpointsParsed++;
                    ProcessJsonEndpoint(root, lolDomains, lolIps, blitzDomains, blitzIps, discordDomains, discordIps, rockstarDomains, rockstarIps, generalDomains);
                }
            }
            catch { /* skip malformed file */ }
        }

        // 2. Parse supplementary TXT trace files (domains & ips)
        foreach (var file in txtFiles)
        {
            try
            {
                string name = Path.GetFileName(file).ToLowerInvariant();
                var lines = File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

                if (name.Contains("domain"))
                {
                    foreach (var line in lines)
                    {
                        string d = CleanDomain(line);
                        if (!IsValidDomain(d)) continue;

                        if (MatchesSuffix(d, AllowedBlitzSuffixes)) blitzDomains.Add(d);
                        else if (MatchesSuffix(d, AllowedLolSuffixes)) lolDomains.Add(d);
                        else if (MatchesSuffix(d, AllowedDiscordSuffixes)) discordDomains.Add(d);
                        else if (MatchesSuffix(d, AllowedRockstarSuffixes)) rockstarDomains.Add(d);
                        else if (MatchesSuffix(d, AllowedGeneralSuffixes)) generalDomains.Add(d);
                    }
                }
                else if (name.Contains("ip"))
                {
                    foreach (var line in lines)
                    {
                        string ip = line.Trim();
                        if (TestPublicIPv4(ip))
                        {
                            if (name.Contains("blitz") || name.Contains("porofessor")) blitzIps.Add(ip);
                            else if (name.Contains("lol") || name.Contains("games")) lolIps.Add(ip);
                            else if (name.Contains("discord")) discordIps.Add(ip);
                            else if (name.Contains("rockstar") || name.Contains("gta")) rockstarIps.Add(ip);
                        }
                    }
                }
            }
            catch { }
        }

        return new BlitzFixParseResult(
            filesParsed,
            endpointsParsed,
            lolDomains,
            lolIps,
            blitzDomains,
            blitzIps,
            discordDomains,
            discordIps,
            rockstarDomains,
            rockstarIps,
            generalDomains
        );
    }

    private static void ProcessJsonEndpoint(
        JsonElement item,
        HashSet<string> lolDomains,
        HashSet<string> lolIps,
        HashSet<string> blitzDomains,
        HashSet<string> blitzIps,
        HashSet<string> discordDomains,
        HashSet<string> discordIps,
        HashSet<string> rockstarDomains,
        HashSet<string> rockstarIps,
        HashSet<string> generalDomains)
    {
        string processName = item.TryGetProperty("processName", out var pProp) ? pProp.GetString() ?? "" : "";
        string remoteAddress = item.TryGetProperty("remoteAddress", out var rProp) ? rProp.GetString() ?? "" : "";
        int remotePort = 0;
        if (item.TryGetProperty("remotePort", out var portProp))
        {
            if (portProp.ValueKind == JsonValueKind.Number) remotePort = portProp.GetInt32();
            else if (portProp.ValueKind == JsonValueKind.String && int.TryParse(portProp.GetString(), out int p)) remotePort = p;
        }

        bool isPvpPort = remotePort is 2099 or 5222 or 5223 or 8088 or 8393 or 8443;
        bool isBlitzProcess = processName.Contains("Blitz", StringComparison.OrdinalIgnoreCase) ||
                              processName.Contains("Porofessor", StringComparison.OrdinalIgnoreCase);
        bool isLeagueProcess = !isBlitzProcess &&
                               (processName.Contains("League", StringComparison.OrdinalIgnoreCase) ||
                                processName.Contains("Riot", StringComparison.OrdinalIgnoreCase));

        bool isDiscordProcess = processName.Contains("Discord", StringComparison.OrdinalIgnoreCase);
        bool isRockstarProcess = processName.Contains("Rockstar", StringComparison.OrdinalIgnoreCase) ||
                                 processName.Contains("GTA", StringComparison.OrdinalIgnoreCase) ||
                                 processName.Contains("LauncherPatcher", StringComparison.OrdinalIgnoreCase);

        bool hasLolCandidates = false;
        bool hasBlitzCandidates = false;
        bool hasDiscordCandidates = false;
        bool hasRockstarCandidates = false;

        if (item.TryGetProperty("domainCandidates", out var candProp) && candProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var candElem in candProp.EnumerateArray())
            {
                string? raw = candElem.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string d = CleanDomain(raw);
                if (!IsValidDomain(d)) continue;

                if (MatchesSuffix(d, AllowedBlitzSuffixes))
                {
                    blitzDomains.Add(d);
                    hasBlitzCandidates = true;
                }
                else if (MatchesSuffix(d, AllowedLolSuffixes))
                {
                    lolDomains.Add(d);
                    hasLolCandidates = true;
                }
                else if (MatchesSuffix(d, AllowedDiscordSuffixes))
                {
                    discordDomains.Add(d);
                    hasDiscordCandidates = true;
                }
                else if (MatchesSuffix(d, AllowedRockstarSuffixes))
                {
                    rockstarDomains.Add(d);
                    hasRockstarCandidates = true;
                }
                else if (MatchesSuffix(d, AllowedGeneralSuffixes))
                {
                    generalDomains.Add(d);
                }
            }
        }

        if (TestPublicIPv4(remoteAddress))
        {
            if (hasBlitzCandidates || isBlitzProcess) blitzIps.Add(remoteAddress);
            if (hasLolCandidates || isLeagueProcess || isPvpPort) lolIps.Add(remoteAddress);
            if (hasDiscordCandidates || isDiscordProcess) discordIps.Add(remoteAddress);
            if (hasRockstarCandidates || isRockstarProcess) rockstarIps.Add(remoteAddress);
        }
    }

    // =========================================================================
    // Upstream Multi-Mirror Downloader
    // =========================================================================

    private static readonly string[] GoogleMirrors =
    {
        "https://cdn.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/.service/list-google.txt",
        "https://fastly.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/.service/list-google.txt",
        "https://gcore.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/.service/list-google.txt",
        "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/list-google.txt",
        "https://ghfast.top/https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/list-google.txt"
    };

    private static readonly string[] GeneralMirrors =
    {
        "https://cdn.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/.service/list-general.txt",
        "https://fastly.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/.service/list-general.txt",
        "https://gcore.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/.service/list-general.txt",
        "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/list-general.txt",
        "https://ghfast.top/https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/list-general.txt"
    };

    private static readonly string[] HostsMirrors =
    {
        "https://cdn.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/.service/hosts",
        "https://fastly.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/.service/hosts",
        "https://gcore.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/.service/hosts",
        "https://raw.githubusercontent.com/upstream/zapret-discord-youtube/refs/heads/main/.service/hosts",
        "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts",
        "https://ghfast.top/https://raw.githubusercontent.com/upstream/zapret-discord-youtube/refs/heads/main/.service/hosts"
    };

    private static async Task<string?> DownloadFromMirrorsAsync(HttpClient http, IReadOnlyList<string> mirrors, CancellationToken ct)
    {
        foreach (var url in mirrors)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(content)) return content;
                }
            }
            catch
            {
                // Fall back to next mirror
            }
        }
        return null;
    }

    // =========================================================================
    // Orchestrated Online & BlitzFix Database Update
    // =========================================================================

    public async Task<OnlineUpdateSummary> UpdateAndParseOnlineDatabasesAsync(
        IpsetService ipsetSvc,
        Action<string>? progressCallback,
        CancellationToken ct = default)
    {
        // -------------------------------------------------------------
        // Stage 1: Parse BlitzFix local traces
        // -------------------------------------------------------------
        progressCallback?.Invoke("[1/5] Парсинг трассировок BlitzFix (100+ файлов JSON/TXT)...");
        var blitzResult = ParseBlitzFixTraces(msg => progressCallback?.Invoke($"[1/5] {msg}"));

        var fetchedYoutube = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedDiscord = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedGeneral = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedLol = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedBlitz = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedRiotAuth = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedRiotEntitlements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedRockstar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var discordIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lolIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blitzIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rockstarIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Incorporate BlitzFix findings
        foreach (var d in blitzResult.LolDomains) fetchedLol.Add(d);
        foreach (var ip in blitzResult.LolIps) lolIps.Add(ip);
        foreach (var d in blitzResult.BlitzDomains) fetchedBlitz.Add(d);
        foreach (var ip in blitzResult.BlitzIps) blitzIps.Add(ip);
        foreach (var ip in DefaultBlitzSubnets) blitzIps.Add(ip);
        foreach (var d in blitzResult.DiscordDomains) fetchedDiscord.Add(d);
        foreach (var ip in blitzResult.DiscordIps) discordIps.Add(ip);
        foreach (var d in blitzResult.RockstarDomains) fetchedRockstar.Add(d);
        foreach (var ip in blitzResult.RockstarIps) rockstarIps.Add(ip);
        foreach (var d in blitzResult.GeneralDomains) fetchedGeneral.Add(d);

        // -------------------------------------------------------------
        // Stage 2: Multi-mirror upstream download (jsDelivr / GitHub)
        // -------------------------------------------------------------
        progressCallback?.Invoke("[2/5] Загрузка актуальных баз с зеркал (jsDelivr / GitHub)...");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) GXBRIEL_ZAPRET/2.0");

        int mirrorsDownloaded = 0;

        // 2a. YouTube/Google
        string? googleContent = await DownloadFromMirrorsAsync(http, GoogleMirrors, ct).ConfigureAwait(false);
        if (googleContent is not null)
        {
            mirrorsDownloaded++;
            foreach (var line in googleContent.Split('\n'))
            {
                string d = CleanDomain(line);
                if (IsValidDomain(d)) fetchedYoutube.Add(d);
            }
        }

        // 2b. General & Discord
        string? generalContent = await DownloadFromMirrorsAsync(http, GeneralMirrors, ct).ConfigureAwait(false);
        if (generalContent is not null)
        {
            mirrorsDownloaded++;
            foreach (var line in generalContent.Split('\n'))
            {
                string d = CleanDomain(line);
                if (IsValidDomain(d))
                {
                    if (MatchesSuffix(d, AllowedDiscordSuffixes)) fetchedDiscord.Add(d);
                    else fetchedGeneral.Add(d);
                }
            }
        }

        // 2c. Hosts mappings (Discord IPs)
        string? hostsContent = await DownloadFromMirrorsAsync(http, HostsMirrors, ct).ConfigureAwait(false);
        if (hostsContent is not null)
        {
            mirrorsDownloaded++;
            foreach (var line in hostsContent.Split('\n'))
            {
                string clean = line.Trim();
                if (clean.Length == 0 || clean.StartsWith('#') || clean.StartsWith(';')) continue;
                var parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string ip = parts[0].Trim();
                    string domain = CleanDomain(parts[1]);
                    if (TestPublicIPv4(ip))
                    {
                        if (MatchesSuffix(domain, AllowedDiscordSuffixes))
                        {
                            discordIps.Add(ip);
                            if (IsValidDomain(domain)) fetchedDiscord.Add(domain);
                        }
                    }
                }
            }
        }

        // -------------------------------------------------------------
        // Stage 3: Merge default built-ins & user-defined overrides
        // -------------------------------------------------------------
        progressCallback?.Invoke("[3/5] Объединение доменов и сохранение списков...");

        foreach (var d in DefaultYoutube) fetchedYoutube.Add(d);
        foreach (var d in DefaultDiscord) fetchedDiscord.Add(d);
        foreach (var d in DefaultGeneral) fetchedGeneral.Add(d);
        foreach (var d in DefaultLol) fetchedLol.Add(d);
        foreach (var d in DefaultBlitz) fetchedBlitz.Add(d);
        foreach (var d in DefaultRiotAuth) fetchedRiotAuth.Add(d);
        foreach (var d in DefaultRiotEntitlements) fetchedRiotEntitlements.Add(d);
        foreach (var ip in DefaultBlitzSubnets) blitzIps.Add(ip);

        int newDomainsAdded = 0;
        int newIpsAdded = 0;
        int listsChanged = 0;

        TrackDomainMerge("youtube", fetchedYoutube);
        TrackDomainMerge("discord", fetchedDiscord);
        TrackDomainMerge("general", fetchedGeneral);
        TrackDomainMerge("lol", fetchedLol);
        TrackDomainMerge("blitz", fetchedBlitz);
        TrackDomainMerge("riot-auth", fetchedRiotAuth);
        TrackDomainMerge("riot-entitlements", fetchedRiotEntitlements);
        TrackDomainMerge("exclude", DefaultExclude);
        TrackIpsetWrite("ipset-lol", DefaultLolGameSubnets);
        TrackIpsetWrite("ipset-blitz", DefaultBlitzSubnets.Concat(blitzIps));

        // Also write list-google.txt mirror
        SyncListAlias("youtube", "list-google");

        // Write discovered LoL domains & IPs
        if (blitzResult.LolDomains.Count > 0)
        {
            TrackDomainMerge("lol-discovered", blitzResult.LolDomains);
        }
        if (blitzResult.LolIps.Count > 0)
        {
            TrackIpsetWrite("ipset-lol-discovered", blitzResult.LolIps);
        }
        if (blitzResult.BlitzDomains.Count > 0)
        {
            TrackDomainMerge("blitz-discovered", blitzResult.BlitzDomains);
        }
        if (blitzResult.BlitzIps.Count > 0)
        {
            var blitzSubnets = IpsetService.AggregateIpsToSubnets(blitzResult.BlitzIps);
            TrackIpsetWrite("ipset-blitz", blitzSubnets);
            TrackIpsetWrite("ipset-blitz-discovered", blitzSubnets);
        }

        // Write list-ultimate for bypass traffic only. Riot/LoL endpoints are
        // intentionally excluded: touching Riot auth/entitlement TLS can break
        // login state and make the League client appear as an unknown player.
        var ultimateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in fetchedDiscord) ultimateSet.Add(d);
        foreach (var d in fetchedYoutube) ultimateSet.Add(d);
        foreach (var d in fetchedGeneral) ultimateSet.Add(d);
        TrackDomainMerge("ultimate", ultimateSet);

        // -------------------------------------------------------------
        // Stage 4: DNS Resolution & Subnet Aggregation
        // -------------------------------------------------------------
        progressCallback?.Invoke("[4/5] DNS-резолв и построение IP-подсетей Discord & LoL...");

        // 4a. Refresh core Discord DNS
        string[] discordDnsRefresh =
        {
            "discord.com", "voice.discord.com", "gateway.discord.gg", "cdn.discordapp.com",
            "media.discordapp.net", "images-ext-1.discordapp.net", "discordapp.com", "discord.gg"
        };
        foreach (var domain in discordDnsRefresh)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(domain, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                foreach (var a in addrs)
                {
                    string ipStr = a.ToString();
                    if (TestPublicIPv4(ipStr)) discordIps.Add(ipStr);
                }
            }
            catch { }
        }

        // 4b. Refresh core LoL DNS
        string[] lolDnsRefresh =
        {
            "riotgames.com", "riotcdn.net", "auth.riotgames.com", "clientconfig.rpg.riotgames.com",
            "spectator.euw1.lol.pvp.net", "blitz.gg", "porofessor.gg", "vivox.com"
        };
        foreach (var domain in lolDnsRefresh)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(domain, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                foreach (var a in addrs)
                {
                    string ipStr = a.ToString();
                    if (TestPublicIPv4(ipStr)) lolIps.Add(ipStr);
                }
            }
            catch { }
        }

        // 4c. Refresh Blitz helper DNS separately. Blitz often opens direct CDN/IP
        // connections, so keeping its own active ipset prevents those flows from
        // being lost inside diagnostic-only LoL observations.
        string[] blitzDnsRefresh =
        {
            "blitz.gg", "blitzapp.gg", "cdn.blitz.gg", "rt.blitz.gg",
            "blitz-main.blitz.gg", "blitz-cdn.blitz.gg", "porofessor.gg",
            "cdn.porofessor.gg"
        };
        foreach (var domain in blitzDnsRefresh)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(domain, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                foreach (var a in addrs)
                {
                    string ipStr = a.ToString();
                    if (TestPublicIPv4(ipStr)) blitzIps.Add(ipStr);
                }
            }
            catch { }
        }

        // 4d. Build Discord IP set
        var allDiscordDomains = ReadDomains("discord");
        var ipsetRes = await ipsetSvc.BuildDiscordIpsetAsync(allDiscordDomains, ct).ConfigureAwait(false);

        // Merge discovered Discord IPs into ipset file
        if (discordIps.Count > 0 && File.Exists(AppPaths.IpsetDiscordFile))
        {
            var existingSubnets = new HashSet<string>(File.ReadAllLines(AppPaths.IpsetDiscordFile).Select(l => l.Trim()).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase);
            var aggDiscovered = IpsetService.AggregateIpsToSubnets(discordIps);
            int beforeDiscordSubnets = existingSubnets.Count;
            foreach (var sub in aggDiscovered)
            {
                existingSubnets.Add(sub);
            }
            int addedDiscordSubnets = existingSubnets.Count - beforeDiscordSubnets;
            if (addedDiscordSubnets > 0)
            {
                File.WriteAllLines(AppPaths.IpsetDiscordFile, existingSubnets.OrderBy(x => x, StringComparer.Ordinal));
                newIpsAdded += addedDiscordSubnets;
                listsChanged++;
            }
        }

        // 4e. Store observed LoL IPs separately. Do not auto-promote DNS/parser
        // observations into the active ipset-lol bypass list: Riot uses broad
        // CDN/AWS/Cloudflare ranges, and turning every observation into an
        // active desync target can break the client/auth/statistics.
        if (lolIps.Count > 0)
        {
            var lolSubnets = IpsetService.AggregateIpsToSubnets(lolIps);
            TrackIpsetWrite("ipset-lol-discovered", lolSubnets);
        }

        // 4f. Promote Blitz IP observations into its active app-specific set.
        if (blitzIps.Count > 0)
        {
            var blitzSubnets = IpsetService.AggregateIpsToSubnets(blitzIps);
            TrackIpsetWrite("ipset-blitz", blitzSubnets);
            TrackIpsetWrite("ipset-blitz-discovered", blitzSubnets);
        }

        // 4g. Build ipset-all for active bypass traffic only. Keep Riot/LoL
        // IPs in ipset-lol for diagnostics, but do not include them in the
        // generic active set.
        var allIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(AppPaths.IpsetDiscordFile))
        {
            foreach (var l in File.ReadAllLines(AppPaths.IpsetDiscordFile).Select(x => x.Trim()).Where(x => x.Length > 0))
                allIps.Add(l);
        }
        if (allIps.Count > 0)
        {
            TrackIpsetWrite("ipset-all", allIps);
        }

        // -------------------------------------------------------------
        // Stage 5: Synchronize all lists between profiles/lists and AppData
        // -------------------------------------------------------------
        progressCallback?.Invoke("[5/5] Синхронизация списков и финализация...");

        SyncAllListsToAppData();

        int dcCount = ReadDomains("discord").Count;
        int ytCount = ReadDomains("youtube").Count;
        int genCount = ReadDomains("general").Count;
        int lolCount = ReadDomains("lol").Count;
        int blitzCount = ReadDomains("blitz").Count;
        int subnets = File.Exists(AppPaths.IpsetDiscordFile) ? File.ReadAllLines(AppPaths.IpsetDiscordFile).Count(l => l.Trim().Length > 0) : ipsetRes.Subnets;
        int lolIpsCount = lolIps.Count;
        int blitzIpsCount = File.Exists(AppPaths.IpsetBlitzFile)
            ? File.ReadAllLines(AppPaths.IpsetBlitzFile).Count(l => l.Trim().Length > 0)
            : blitzIps.Count;

        string summary = $"Успешно спарсено: BlitzFix ({blitzResult.FilesParsed} трассировок, {blitzResult.EndpointsParsed} эндпоинтов), Зеркала ({mirrorsDownloaded}/3). Базы: Discord ({subnets} подсетей / {dcCount} доменов), YouTube ({ytCount} доменов), LoL/Riot ({lolIpsCount} IP / {lolCount} доменов), Blitz ({blitzIpsCount} IP/подсетей / {blitzCount} доменов), Общие ({genCount} доменов). Новых доменов: {newDomainsAdded}; новых IP/подсетей: {newIpsAdded}; изменено списков: {listsChanged}.";

        return new OnlineUpdateSummary(
            dcCount,
            ytCount,
            genCount,
            lolCount,
            subnets,
            newDomainsAdded,
            summary,
            blitzResult.FilesParsed,
            blitzResult.EndpointsParsed,
            blitzResult.LolDomains.Count + blitzResult.BlitzDomains.Count + blitzResult.DiscordDomains.Count + blitzResult.RockstarDomains.Count + blitzResult.GeneralDomains.Count,
            blitzResult.LolIps.Count + blitzResult.BlitzIps.Count + blitzResult.DiscordIps.Count + blitzResult.RockstarIps.Count,
            mirrorsDownloaded,
            newIpsAdded,
            listsChanged
        );

        void TrackDomainMerge(string name, IEnumerable<string> domains)
        {
            int added = MergeListDefaults(name, domains);
            if (added > 0)
            {
                newDomainsAdded += added;
                listsChanged++;
            }
        }

        void TrackIpsetWrite(string baseName, IEnumerable<string> lines)
        {
            var oldValues = ReadIpsetValues(baseName);
            var newValues = lines.Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int added = newValues.Count(v => !oldValues.Contains(v));
            bool changed = added > 0 || oldValues.Count != newValues.Count || oldValues.Any(v => !newValues.Contains(v));

            WriteIpsetFile(baseName, newValues);
            if (changed)
            {
                newIpsAdded += added;
                listsChanged++;
            }
        }
    }

    private static HashSet<string> ReadIpsetValues(string baseName)
    {
        try
        {
            string path = Path.Combine(AppPaths.ListsDir, baseName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? baseName : $"{baseName}.txt");
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteIpsetFile(string baseName, IEnumerable<string> lines)
    {
        try
        {
            string appDataFile = Path.Combine(AppPaths.ListsDir, baseName.EndsWith(".txt") ? baseName : $"{baseName}.txt");

            var sorted = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
            string content = string.Join("\n", sorted) + "\n";

            Directory.CreateDirectory(AppPaths.ListsDir);
            File.WriteAllText(appDataFile, content);
        }
        catch { }
    }

    private void SyncListAlias(string sourceName, string aliasName)
    {
        try
        {
            string src = GetPath(sourceName);
            if (File.Exists(src))
            {
                string dst = Path.Combine(AppPaths.ListsDir, aliasName.EndsWith(".txt") ? aliasName : $"{aliasName}.txt");
                File.Copy(src, dst, overwrite: true);
            }
        }
        catch { }
    }

    private void SyncAllListsToAppData()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ListsDir);
        }
        catch { }
    }

    public void SeedDefaults()
    {
        SeedBundledListsFromResources();
        MergeListDefaults("youtube", DefaultYoutube);
        MergeListDefaults("discord", DefaultDiscord);
        MergeListDefaults("exclude", DefaultExclude);
        MergeListDefaults("general", DefaultGeneral);
        MergeListDefaults("lol", DefaultLol);
        MergeListDefaults("blitz", DefaultBlitz);
        MergeListDefaults("steam", DefaultSteam);
        MergeListDefaults("riot-auth", DefaultRiotAuth);
        MergeListDefaults("riot-entitlements", DefaultRiotEntitlements);
        WriteIpsetFile("ipset-lol", DefaultLolGameSubnets);
        WriteIpsetFile("ipset-blitz", DefaultBlitzSubnets);
    }

    private static void SeedBundledListsFromResources()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (string resourceName in asm.GetManifestResourceNames()
                         .Where(n => n.StartsWith(BundledListResourcePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                string fileName = resourceName[BundledListResourcePrefix.Length..];
                if (string.IsNullOrWhiteSpace(fileName) ||
                    fileName.Contains('\\') ||
                    fileName.Contains('/') ||
                    !fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    continue;

                using Stream? stream = asm.GetManifestResourceStream(resourceName);
                if (stream is null) continue;

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                MergeBundledListFile(fileName, reader.ReadToEnd());
            }
        }
        catch { }
    }

    private static void MergeBundledListFile(string fileName, string content)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ListsDir);
            string target = Path.Combine(AppPaths.ListsDir, fileName);

            string baseName = fileName.StartsWith("list-", StringComparison.OrdinalIgnoreCase)
                ? fileName["list-".Length..]
                : fileName;
            if (baseName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[..^4];

            bool isActiveBypass = IsActiveBypassList(baseName);

            var lines = new List<string>();
            if (File.Exists(target))
            {
                foreach (var line in File.ReadAllLines(target))
                {
                    string trimmed = line.Trim();
                    if (isActiveBypass && IsProtectedGameDomain(trimmed))
                        continue;
                    lines.Add(line);
                }
            }

            foreach (var line in NormalizeNewlines(content).Split('\n'))
            {
                string trimmed = line.Trim();
                if (isActiveBypass && IsProtectedGameDomain(trimmed))
                    continue;
                lines.Add(line);
            }

            string normalized = NormalizeListContent(string.Join('\n', lines));
            File.WriteAllText(target, normalized);
            try { MaterializeRuntimeList(target); } catch { }
        }
        catch { }
    }

    private int MergeListDefaults(string name, IEnumerable<string> defaults)
    {
        try
        {
            AppPaths.EnsureCreated();
            Directory.CreateDirectory(AppPaths.ListsDir);
            string path = Path.Combine(AppPaths.ListsDir, $"list-{name}.txt");
            if (!File.Exists(path))
            {
                var list = defaults.Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => d.Trim())
                    .Where(d => !IsActiveBypassList(name) || !IsProtectedGameDomain(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string normalized = NormalizeListContent(string.Join('\n', list));
                File.WriteAllText(path, normalized);
                try { MaterializeRuntimeList(path); } catch { }
                return list.Count;
            }

            var existingLines = File.ReadAllLines(path);
            var mergedLines = new List<string>();
            var domainSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in existingLines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (IsActiveBypassList(name) && IsProtectedGameDomain(trimmed)) continue;
                if (trimmed.StartsWith('#'))
                {
                    mergedLines.Add(trimmed);
                    continue;
                }

                if (domainSet.Add(trimmed))
                    mergedLines.Add(trimmed);
            }

            int before = domainSet.Count;
            foreach (var raw in defaults)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string trimmed = raw.Trim();
                if (IsActiveBypassList(name) && IsProtectedGameDomain(trimmed)) continue;
                if (domainSet.Add(trimmed))
                    mergedLines.Add(trimmed);
            }

            string mergedContent = NormalizeListContent(string.Join('\n', mergedLines));
            File.WriteAllText(path, mergedContent);
            try { MaterializeRuntimeList(path); } catch { }
            return domainSet.Count - before;
        }
        catch { return 0; }
    }

    // =========================================================================
    // Bundled Default Lists
    // =========================================================================

    private static readonly string[] DefaultYoutube =
    {
        "yt3.ggpht.com", "yt4.ggpht.com", "ggpht.com", "yt3.googleusercontent.com", "googlevideo.com",
        "gvt1.com", "gvt2.com", "gvt3.com",
        "jnn-pa.googleapis.com", "wide-youtube.l.google.com",
        "youtube-nocookie.com", "youtube-ui.l.google.com", "youtube.com",
        "youtubeembeddedplayer.googleapis.com", "youtubekids.com", "youtube.googleapis.com",
        "youtubei.googleapis.com", "youtu.be", "yt-video-upload.l.google.com", "ytimg.com",
        "ytimg.l.google.com", "play.google.com", "google.ru"
    };

    private static readonly string[] DefaultDiscord =
    {
        "dis.gd", "discord.com", "discord.gg", "discord.media", "discord.app", "discord.co",
        "discord.dev", "discord.design", "discord.gift", "discord.gifts",
        "discord.new", "discord.store", "discord.status",
        "discordapp.com", "discordapp.net", "discordcdn.com", "discordstatus.com",
        "discordmerch.com", "discord-activities.com", "discordactivities.com",
        "discordsays.com", "discordsez.com", "discordpartygames.com",
        "discord-attachments-uploads-prd.storage.googleapis.com",
        "challenges.cloudflare.com",
        "cloudflare-ech.com", "encryptedsni.com"
    };

    private static readonly string[] DefaultGeneral =
    {
        "cloudflare-ech.com", "encryptedsni.com", "cloudflareaccess.com", "cloudflareapps.com",
        "cloudflarebolt.com", "cloudflareclient.com", "cloudflareinsights.com", "cloudflareok.com",
        "cloudflarepartners.com", "cloudflareportal.com", "cloudflarepreview.com", "cloudflareresolve.com",
        "cloudflaressl.com", "cloudflarestatus.com", "cloudflarestorage.com", "cloudflarestream.com",
        "cloudflaretest.com", "cloudfront.net",
        "frankerfacez.com", "ffzap.com", "betterttv.net", "7tv.app", "7tv.io",
        "localizeapi.com", "klipy.com", "api.telegram.org", "telegram.org"
    };

    private static readonly string[] DefaultExclude =
    {
        "pusher.com", "live-video.net", "ttvnw.net", "twitch.tv", "mail.ru", "citilink.ru",
        "yandex.com", "yandex.net", "yandex.org", "yandex.md", "yandex.ru", "yandexadexchange.net",
        "yandexcloud.net", "yandexcom.net", "yandexmetrica.com", "yandexwebcache.net",
        "yandexwebcache.org", "yastat.net", "yastatic-net.ru", "yastatic.net", "ya.ru",
        "adfox.ru", "admetrica.ru", "naydex.net", "rostaxi.org", "turbopages.org", "webvisor.com",
        "webvisor.org", "nvidia.com", "donationalerts.com", "vk.com", "yandex.kz", "mts.ru",
        "multimc.org", "dns-shop.ru", "habr.com", "3dnews.ru", "microsoft.com", "microsoftonline.com",
        "google.com", "google.ru", "accounts.google.com", "docs.google.com", "drive.google.com", "mail.google.com",
        "translate.google.com", "play.google.com",
        "googleapis.com", "clients6.google.com", "clients4.google.com", "clients2.google.com", "clients.google.com",
        "antigravity.google", "gemini.google.com", "aistudio.google.com", "google.dev", "deepmind.google",
        "alkalimakersuite-pa.clients6.google.com", "generativelanguage.googleapis.com",
        "live.com", "sharepoint.com", "minecraft.net", "xboxlive.com", "akamaitechnologies.com",
        "msi.com", "2ip.ru", "boosty.to", "tanki.su", "lesta.ru", "korabli.su", "tanksblitz.ru",
        "reg.ru", "epicgames.dev", "epicgames.com", "unrealengine.com", "riotgames.com", "riot.com",
        "riotcdn.net", "riotcdn.com", "riotgames.es", "rgpub.io", "leagueoflegends.com",
        "RiotClientServices.com", "launcher.RiotClientServices.com", "launcher.LeagueofLegends.com",
        "playvalorant.com", "marketplace.visualstudio.com", "gallery.vsassets.io",
        "gallerycdn.vsassets.io", "gosuslugi.ru", "gov.ru", "nalog.ru", "spb.ru", "mos.ru", "vk.ru",
        "vk.me", "vkvideo.ru", "ok.ru", "mycdn.me", "okcdn.ru", "odkl.ru", "wb.ru", "geobasket.ru",
        "paywb.com", "rwb.ru", "wb-basket.ru", "wbbasket.ru", "wbpay.ru", "wibes.ru", "wildberries.ru",
        "ozon.by", "ozon.com", "ozon.com.by", "ozon.com.kz", "ozon.kz", "ozon.ru", "ozon.tm",
        "ozone.ru", "ozonru.me", "ozonusercontent.com", "alfabank.ru", "gazprombank.ru", "gpb.ru",
        "dbo-dengi.online", "mtsdengi.ru", "psbank.ru", "bankline.ru", "rosbank.ru", "abr.ru",
        "rshb.ru", "sber.ru", "sberbank.com", "sberbank.ru", "cdn-tinkoff.ru", "tbank-online.com",
        "tbank.ru", "t-bank-app.ru", "tochka-tech.com", "tochka.com", "vtb.ru", "steamcommunity.com",
        "steampowered.com", "steamstatic.com", "steamcontent.com", "steamusercontent.com",
        "steamserver.net", "valvesoftware.com", "steamgames.com",
        "ea.com", "eaassets-a.akamaihd.net", "origin.com", "dice.se",
        "battle.net", "battlenet.com.cn", "blizzard.com", "blz-contentstack.com",
        "ubisoft.com", "ubi.com", "ubisoftconnect.com",
        "rockstargames.com", "socialclub.rockstargames.com",
        "playstation.com", "playstation.net", "sonyentertainmentnetwork.com",
        "xbox.com", "nintendo.com", "nintendo.net", "nintendowifi.net",
        "gog.com", "gog-statics.com", "mojang.com",
        "wargaming.net", "faceit.com", "supercell.com",
        "hoyoverse.com", "mihoyo.com", "yuanshen.com",
        "xboxservices.com", "msftauth.net", "msauth.net", "msftauthimages.net", "msauthimages.net",
        "microsoftonline-p.com", "s-microsoft.com", "login.windows.net", "gamepass.com",
        "forzamotorsport.net", "forzaracing.com", "playfab.com", "playfabapi.com",
        "easyanticheat.net", "eac-cdn.com", "battleye.com",
        "fortnite.com", "activision.com", "callofduty.com", "demonware.net",
        "roblox.com", "rbxcdn.com", "2k.com", "take2games.com",
        "bethesda.net", "bethesda.com", "zenimax.com", "easports.com",
        "square-enix.com", "finalfantasyxiv.com", "bungie.net", "minecraftservices.com",
        "pubg.com", "krafton.com", "garena.com", "levelinfinite.com",
        "gaijin.net", "warthunder.com", "vkplay.ru", "hoyolab.com", "nvidiagrid.net",
        "pvp.net", "teamfighttactics.com", "legendsofruneterra.com", "wildrift.com",
        "lolstatic.com", "lolstatic-a.akamaihd.net", "lolcdn.net", "lolpvp.net",
        "lcu.riotgames.com", "client.riotgames.com", "static.leagueoflegends.com",
        "cdn.riotgames.com", "data.riotgames.com", "auth.riotgames.com",
        "authenticate.riotgames.com", "entitlements.auth.riotgames.com",
        "clientconfig.rpg.riotgames.com", "riot-geo.pas.si.riotgames.com",
        "permissions.sgp.pvp.net", "dradis-prod.rdatasrv.net",
        "leaguereplays.com", "replay.leagueoflegends.com", "matchhistory.leagueoflegends.com",
        "riotgamespatcher-a.akamaihd.net", "riotgamespatcher.akamaized.net",
        "lol.secure.dyn.riotcdn.net", "cf.dyn.riotcdn.net.cdn.cloudflare.net",
        "ru-red.lol.sgp.pvp.net", "ru-red.lol.sgp.pvp.net.cdn.cloudflare.net",
        "eu.edge.rms.si.riotgames.com", "eu.edge.rms.si.riotgames.com.cdn.cloudflare.net",
        "netease.com", "neteasegames.com", "easebar.com", "marvelrivals.com",
        "kurogames.com", "kurogame.com",
        "warframe.com", "digitalextremes.com", "pathofexile.com",
        "amazongames.com", "playlostark.com", "deadbydaylight.com", "bhvr.com",
        "escapefromtarkov.com", "battlestategames.com", "halowaypoint.com",
        "jagex.com", "arena.net", "guildwars2.com", "ncsoft.com",
        "nexon.com", "nexon.net", "pearlabyss.com", "embark-studios.com",
        "moonton.com", "mobilelegends.com",
        "denuvo.com", "wellbia.com", "nprotect.com",
        "photonengine.com", "exitgames.com",
        "unity.com", "unity3d.com", "vivox.com",
        "heroiclabs.com", "accelbyte.io",
        "nitrado.net", "gamefabric.com", "g-portal.com", "i3d.net", "edgegap.com"
    };

    private static readonly string[] DefaultLol =
    {
        "riotgames.com", "riotcdn.net", "leagueoflegends.com", "pvp.net", "playvalorant.com",
        "teamfighttactics.com", "legendsofruneterra.com", "wildrift.com",
        "blitz.gg", "porofessor.gg", "iesdev.com", "rdatasrv.net", "vivox.com",
        "account.riotgames.com", "auth.riotgames.com", "clientconfig.rpg.riotgames.com",
        "data.riotgames.com", "entitlements.auth.riotgames.com", "spectator.euw1.lol.pvp.net"
    };

    private static readonly string[] DefaultBlitz =
    {
        "blitz.gg",
        "blitzapp.gg",
        "www.blitz.gg",
        "www.blitzapp.gg",
        "cdn.blitz.gg",
        "blitz-desktop.blitz.gg",
        "blitz-desktop.b-cdn.net",
        "blitz-deltas.blitz.gg",
        "blitz-cdn.blitz.gg",
        "blitz-cdn-images.b-cdn.net",
        "blitz-cdn-plain.b-cdn.net",
        "blitz-cdn-plain.blitz.gg",
        "blitz-data-api.b-cdn.net",
        "blitz-datalake-server.b-cdn.net",
        "blitz-lol-server.b-cdn.net",
        "blitz-main.b-cdn.net",
        "blitz-main.blitz.gg",
        "rt.blitz.gg",
        "auth.blitz.gg",
        "blitz-auth-server.b-cdn.net",
        "agentselect.net",
        "championselect.net",
        "lolstats.com",
        "tftcomps.gg",
        "blitz-native.blitz.gg",
        "blitz-canary.blitz.gg",
        "sentry.blitz.gg",
        "frontend-utilities.b-cdn.net",
        "iesdev.com",
        "data.v2.iesdev.com",
        "science.v2.iesdev.com",
        "utils.iesdev.com",
        "cs2.v2.iesdev.com",
        "valorant.v2.iesdev.com",
        "realtime.va1.iesdev.com",
        "porofessor.gg",
        "cdn.porofessor.gg",
        "b-cdn.net"
    };

    private static readonly string[] DefaultSteam =
    {
        "steamcommunity.com", "steampowered.com", "steamstatic.com", "steamcontent.com",
        "steamusercontent.com", "steamserver.net", "steamgames.com", "valvesoftware.com",
        "steam-chat.com", "steam-api.com", "steamcdn-a.akamaihd.net"
    };

    private static readonly string[] DefaultRiotAuth =
    {
        "auth.riotgames.com",
        "authenticate.riotgames.com",
        "entitlements.auth.riotgames.com",
        "clientconfig.rpg.riotgames.com",
        "riot-geo.pas.si.riotgames.com",
        "permissions.sgp.pvp.net"
    };

    private static readonly string[] DefaultRiotEntitlements =
    {
        // League can show <unknown player> when entitlement token refresh is reset. Keep this
        // list intentionally narrow: auth/bootstrap/social session endpoints only, not game traffic.
        "entitlements.auth.riotgames.com",
        "entitlements.auth.riotgames.com.cdn.cloudflare.net",
        "auth.riotgames.com",
        "authenticate.riotgames.com",
        "riot-geo.pas.si.riotgames.com",
        "clientconfig.rpg.riotgames.com",
        "permissions.sgp.pvp.net",
        "player-preferences-usw2.pp.sgp.pvp.net",
        "eu.edge.rms.si.riotgames.com",
        "us.edge.rms.si.riotgames.com",
        "euw1.chat.si.riotgames.com"
    };

    private static readonly string[] DefaultBlitzSubnets =
    {
        // Blitz/Porofessor desktop clients frequently pin Bunny CDN edge IPs directly
        // before useful SNI/domain candidates are visible to the parser.
        "103.231.98.0/24",
        "138.199.36.0/24",
        "138.199.37.0/24",
        "138.199.46.0/24",
        "169.150.247.0/24",
        "185.111.111.0/24",
        "195.181.166.0/24"
    };

    private static readonly string[] DefaultLolGameSubnets =
    {
        // Flowseal community workaround for League session/game traffic on TCP 2099.
        // EUW
        "3.64.0.0/12",
        "18.165.180.0/22",
        "44.224.0.0/11",
        // EUNE
        "18.156.0.0/14",
        "35.156.0.0/14",
        // RU
        "13.248.192.0/20",
        "99.83.128.0/20",
        // PBE
        "3.33.240.0/20",
        "13.248.128.0/20"
    };

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string NormalizeListContent(string content)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();
        foreach (var raw in NormalizeNewlines(content ?? string.Empty).Split('\n'))
        {
            string line = NormalizeListEntry(raw);
            if (line.Length == 0) continue;
            if (line.Contains("!vs_my_project", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("gxbriel_zapret\\ui", StringComparison.OrdinalIgnoreCase))
                continue;

            if (seen.Add(line))
                output.Add(line);
        }

        return output.Count == 0 ? string.Empty : string.Join('\n', output) + "\n";
    }

    private static string NormalizeListEntry(string raw)
    {
        string line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return "";

        int hash = line.IndexOf('#');
        if (hash >= 0) line = line[..hash].Trim();
        int semi = line.IndexOf(';');
        if (semi >= 0) line = line[..semi].Trim();
        if (line.Length == 0 || line.Contains(',') || line.Any(char.IsWhiteSpace)) return "";

        if (line.Contains('/') && GxbrielZapret.Services.Engine.Discovery.EndpointNormalizer.TryNormalizeCidr(line, out string canonicalCidr, out _))
            return canonicalCidr;

        if (GxbrielZapret.Services.Engine.Discovery.EndpointNormalizer.TryNormalizeIp(line, out string canonicalIp, out _))
            return canonicalIp;

        if (GxbrielZapret.Services.Engine.Discovery.EndpointNormalizer.TryNormalizeDomain(line, out string canonicalDomain, out _))
            return canonicalDomain;

        return "";
    }
}
