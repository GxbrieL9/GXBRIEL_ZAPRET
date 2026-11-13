using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine.Discovery;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Engine;

/// <summary>
/// Manages discovery targets (built-in and custom), process tree tracking,
/// active process enumeration, and target endpoint correlation.
/// </summary>
public sealed class DiscoveryService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly List<DiscoveryTarget> _targets = new();

    public DiscoveryStore Store { get; } = new();

    public IReadOnlyList<DiscoveryTarget> Targets
    {
        get
        {
            lock (_lock)
            {
                return _targets.ToList();
            }
        }
    }

    public DiscoveryService()
    {
        Load();
    }

    // =========================================================================
    // Persistence & Built-in Seeding
    // =========================================================================

    public void Load()
    {
        lock (_lock)
        {
            _targets.Clear();
            var builtIns = CreateBuiltInTargets();
            var loadedMap = new Dictionary<string, DiscoveryTarget>(StringComparer.OrdinalIgnoreCase);

            string file = AppPaths.DiscoveryTargetsFile;
            if (File.Exists(file))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var list = JsonSerializer.Deserialize<List<DiscoveryTarget>>(json);
                    if (list is not null)
                    {
                        foreach (var t in list)
                        {
                            if (!string.IsNullOrWhiteSpace(t.Id))
                                loadedMap[t.Id] = t;
                        }
                    }
                }
                catch { /* fallback to built-ins if corrupted */ }
            }

            // Merge built-ins (preserving user's enabled status or custom edits if saved)
            foreach (var b in builtIns)
            {
                if (loadedMap.TryGetValue(b.Id, out var existing))
                {
                    b.Enabled = existing.Enabled;
                    if (existing.SeedDomains.Count > 0) b.SeedDomains = existing.SeedDomains;
                    if (existing.KnownDomains.Count > 0) b.KnownDomains = existing.KnownDomains;
                    if (existing.DiscoveredDomains.Count > 0) b.DiscoveredDomains = existing.DiscoveredDomains;
                    if (existing.UserDomains.Count > 0) b.UserDomains = existing.UserDomains;
                    if (existing.KnownIpRanges.Count > 0) b.KnownIpRanges = existing.KnownIpRanges;
                    if (existing.DiscoveredCandidates.Count > 0) b.DiscoveredCandidates = existing.DiscoveredCandidates;
                    _targets.Add(b);
                    loadedMap.Remove(b.Id);
                }
                else
                {
                    _targets.Add(b);
                }
            }

            // Add custom user targets
            foreach (var custom in loadedMap.Values)
            {
                custom.IsBuiltIn = false;
                _targets.Add(custom);
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                AppPaths.EnsureCreated();
                string file = AppPaths.DiscoveryTargetsFile;
                string tmp = file + ".tmp";
                string json = JsonSerializer.Serialize(_targets, JsonOpts);
                File.WriteAllText(tmp, json);
                File.Move(tmp, file, overwrite: true);
            }
            catch { /* non-fatal */ }
        }
    }

    public static List<DiscoveryTarget> CreateBuiltInTargets()
    {
        return new List<DiscoveryTarget>
        {
            // --- GAMES (ProcessTarget) ---
            new()
            {
                Id = "league_of_legends",
                DisplayName = "League of Legends",
                Type = TargetType.Process,
                Category = TargetCategory.Games,
                Enabled = true,
                IsBuiltIn = true,
                Executables = new List<string>
                {
                    "LeagueClient.exe", "LeagueClientUx.exe", "League of Legends.exe"
                },
                ProcessNamePatterns = new List<string> { "LeagueClient", "League of Legends" },
                TrackChildProcesses = true,
                KnownDomains = new List<string>(HostlistService.AllowedLolSuffixes)
            },
            new()
            {
                Id = "rockstar_games",
                DisplayName = "Rockstar Games / GTA",
                Type = TargetType.Process,
                Category = TargetCategory.Games,
                Enabled = true,
                IsBuiltIn = true,
                Executables = new List<string>
                {
                    "Launcher.exe", "PlayGTAV.exe", "GTA5.exe", "LauncherPatcher.exe", "RockstarService.exe", "RockstarSteamHelper.exe"
                },
                ProcessNamePatterns = new List<string> { "Rockstar", "GTA", "LauncherPatcher" },
                TrackChildProcesses = true,
                KnownDomains = new List<string>(HostlistService.AllowedRockstarSuffixes)
            },
            new()
            {
                Id = "dead_by_daylight",
                DisplayName = "Dead by Daylight",
                Type = TargetType.Process,
                Category = TargetCategory.Games,
                Enabled = false,
                IsBuiltIn = true,
                Executables = new List<string>
                {
                    "DeadByDaylight-Win64-Shipping.exe", "DeadByDaylight.exe", "DeadByDaylight-EAC.exe"
                },
                ProcessNamePatterns = new List<string> { "DeadByDaylight" },
                TrackChildProcesses = true,
                KnownDomains = new List<string> { "deadbydaylight.com", "bhvr.com" }
            },
            new()
            {
                Id = "roblox",
                DisplayName = "Roblox",
                Type = TargetType.Process,
                Category = TargetCategory.Games,
                Enabled = false,
                IsBuiltIn = true,
                Executables = new List<string>
                {
                    "RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe", "RobloxCrashHandler.exe"
                },
                ProcessNamePatterns = new List<string> { "RobloxPlayer", "Roblox" },
                TrackChildProcesses = true,
                KnownDomains = new List<string> { "roblox.com", "rbxcdn.com" }
            },
            new()
            {
                Id = "fortnite",
                DisplayName = "Fortnite / Epic Games",
                Type = TargetType.Process,
                Category = TargetCategory.Games,
                Enabled = false,
                IsBuiltIn = true,
                Executables = new List<string>
                {
                    "FortniteClient-Win64-Shipping.exe", "FortniteLauncher.exe", "EpicGamesLauncher.exe"
                },
                ProcessNamePatterns = new List<string> { "Fortnite", "EpicGamesLauncher" },
                TrackChildProcesses = true,
                KnownDomains = new List<string> { "fortnite.com", "epicgames.com", "epicgames.dev" }
            },
            new()
            {
                Id = "arc_raiders",
                DisplayName = "ARC Raiders",
                Type = TargetType.Process,
                Category = TargetCategory.Games,
                Enabled = false,
                IsBuiltIn = true,
                Executables = new List<string>
                {
                    "Pioneer.exe", "Pioneer-Win64-Shipping.exe"
                },
                ProcessNamePatterns = new List<string> { "Pioneer", "ARCRaiders" },
                TrackChildProcesses = true,
                KnownDomains = new List<string> { "embark-studios.com" }
            },

            // --- PROGRAMS (ProcessTarget) ---
            new()
            {
                Id = "discord",
                DisplayName = "Discord",
                Type = TargetType.Process,
                Category = TargetCategory.Programs,
                Enabled = true,
                IsBuiltIn = true,
                Executables = new List<string>
                {
                    "Discord.exe", "DiscordCanary.exe", "DiscordPTB.exe", "DiscordDevelopment.exe"
                },
                ProcessNamePatterns = new List<string> { "Discord" },
                TrackChildProcesses = true,
                KnownDomains = new List<string>(HostlistService.AllowedDiscordSuffixes)
            },
            new()
            {
                Id = "riot_client",
                DisplayName = "Riot Client",
                Type = TargetType.Process,
                Category = TargetCategory.Programs,
                Enabled = true,
                IsBuiltIn = true,
                Executables = new List<string>
                {
                    "RiotClientServices.exe", "RiotClientUx.exe"
                },
                ProcessNamePatterns = new List<string> { "RiotClient" },
                TrackChildProcesses = true,
                KnownDomains = new List<string>(HostlistService.AllowedLolSuffixes)
            },
            new()
            {
                Id = "blitz",
                DisplayName = "Blitz",
                Type = TargetType.Process,
                Category = TargetCategory.Programs,
                Enabled = true,
                IsBuiltIn = true,
                Executables = new List<string>
                {
                    "Blitz.exe", "BlitzApp.exe"
                },
                ProcessNamePatterns = new List<string> { "Blitz", "Porofessor" },
                TrackChildProcesses = true,
                KnownDomains = new List<string> { "blitz.gg", "porofessor.gg" }
            },

            // --- WEB SERVICES (DomainTarget) ---
            new()
            {
                Id = "soundcloud",
                DisplayName = "SoundCloud",
                Type = TargetType.Domain,
                Category = TargetCategory.WebServices,
                Enabled = false,
                IsBuiltIn = true,
                Executables = new List<string>(), // NO fake SoundCloud.exe!
                ProcessNamePatterns = new List<string>(),
                TrackChildProcesses = false,
                SeedDomains = new List<string> { "soundcloud.com", "sndcdn.com" },
                KnownDomains = new List<string> { "soundcloud.com", "sndcdn.com" }
            }
        };
    }

    // =========================================================================
    // Target Management (Add / Edit / Remove)
    // =========================================================================

    public DiscoveryTarget AddCustomTargetFromProcess(ProcessSnapshotItem item)
    {
        string exeName = item.ProcessName;
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        string displayName = !string.IsNullOrWhiteSpace(item.Description)
            ? item.Description
            : (!string.IsNullOrWhiteSpace(item.WindowTitle) ? item.WindowTitle : item.ProcessName);

        var target = new DiscoveryTarget
        {
            Id = "custom_" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = displayName,
            Category = TargetCategory.Custom,
            Enabled = true,
            IsBuiltIn = false,
            Executables = new List<string> { exeName },
            ProcessNamePatterns = new List<string> { item.ProcessName },
            ExecutablePath = !string.IsNullOrWhiteSpace(item.ExecutablePath) ? item.ExecutablePath : null,
            TrackChildProcesses = true,
            CreatedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            _targets.Add(target);
        }
        Save();
        return target;
    }

    public DiscoveryTarget AddCustomTargetFromExe(string exePath)
    {
        string fileName = Path.GetFileName(exePath);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(exePath);
        string displayName = nameWithoutExt;

        try
        {
            if (File.Exists(exePath))
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                    displayName = vi.FileDescription.Trim();
                else if (!string.IsNullOrWhiteSpace(vi.ProductName))
                    displayName = vi.ProductName.Trim();
            }
        }
        catch { }

        var target = new DiscoveryTarget
        {
            Id = "custom_" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = displayName,
            Type = TargetType.Process,
            Category = TargetCategory.Custom,
            Enabled = true,
            IsBuiltIn = false,
            Executables = new List<string> { fileName },
            ProcessNamePatterns = new List<string> { nameWithoutExt },
            ExecutablePath = exePath,
            TrackChildProcesses = true,
            CreatedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            _targets.Add(target);
        }
        Save();
        return target;
    }

    public DiscoveryTarget AddCustomDomainTarget(string urlOrDomain, string? customName = null)
    {
        string raw = (urlOrDomain ?? "").Trim();
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = raw[8..];
        else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            raw = raw[7..];

        int slashIdx = raw.IndexOfAny(new[] { '/', '?', '#', ':' });
        if (slashIdx >= 0)
            raw = raw[..slashIdx];

        string domain = HostlistService.CleanDomain(raw);
        if (string.IsNullOrWhiteSpace(domain))
            domain = "example.com";

        string displayName = !string.IsNullOrWhiteSpace(customName)
            ? customName.Trim()
            : (char.ToUpperInvariant(domain[0]) + domain[1..]);

        var target = new DiscoveryTarget
        {
            Id = "custom_web_" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = displayName,
            Type = TargetType.Domain,
            Category = TargetCategory.Custom,
            Enabled = true,
            IsBuiltIn = false,
            Executables = new List<string>(),
            ProcessNamePatterns = new List<string>(),
            TrackChildProcesses = false,
            SeedDomains = new List<string> { domain },
            KnownDomains = new List<string> { domain },
            CreatedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            _targets.Add(target);
        }
        Save();
        return target;
    }

    public bool RemoveTarget(string id)
    {
        bool removed = false;
        lock (_lock)
        {
            var target = _targets.FirstOrDefault(t => t.Id == id && !t.IsBuiltIn);
            if (target is not null)
            {
                removed = _targets.Remove(target);
            }
        }
        if (removed) Save();
        return removed;
    }

    public void UpdateTarget(DiscoveryTarget target)
    {
        lock (_lock)
        {
            var existing = _targets.FirstOrDefault(t => t.Id == target.Id);
            if (existing is not null)
            {
                existing.DisplayName = target.DisplayName;
                existing.Enabled = target.Enabled;
                existing.TrackChildProcesses = target.TrackChildProcesses;
                existing.ExecutablePath = target.ExecutablePath;
                if (target.Executables.Count > 0)
                    existing.Executables = new List<string>(target.Executables);
                if (target.ProcessNamePatterns.Count > 0)
                    existing.ProcessNamePatterns = new List<string>(target.ProcessNamePatterns);
            }
        }
        Save();
    }

    // =========================================================================
    // Active Process Enumeration & System Filter
    // =========================================================================

    private static readonly HashSet<string> KnownSystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "[System Process]", "Idle", "Registry", "smss", "csrss", "wininit", "services",
        "lsass", "svchost", "fontdrvhost", "dwm", "Memory Compression", "conhost", "spoolsv",
        "explorer", "RuntimeBroker", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "ctfmon", "TextInputHost", "taskhostw", "sihost", "SecurityHealthSystray", "SecurityHealthService",
        "audiodg", "smartscreen", "WmiPrvSE", "dllhost", "wlanext", "dasHost", "AggregatorHost",
        "SearchIndexer", "StandardCollector.Service", "MSBuild", "VBCSCompiler", "dotnet"
    };

    public static List<ProcessSnapshotItem> GetActiveProcesses(bool includeSystem = false)
    {
        var items = new List<ProcessSnapshotItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var procs = Process.GetProcesses();
            foreach (var p in procs)
            {
                try
                {
                    int pid = p.Id;
                    if (pid <= 4) continue;

                    string pName = p.ProcessName;
                    bool isSystem = KnownSystemProcessNames.Contains(pName);

                    if (isSystem && !includeSystem)
                        continue;

                    string title = "";
                    string desc = "";
                    string exePath = "";

                    try { title = p.MainWindowTitle; } catch { }

                    try
                    {
                        var module = p.MainModule;
                        if (module is not null)
                        {
                            exePath = module.FileName ?? "";
                            var vi = module.FileVersionInfo;
                            if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                                desc = vi.FileDescription.Trim();
                            else if (!string.IsNullOrWhiteSpace(vi.ProductName))
                                desc = vi.ProductName.Trim();
                        }
                    }
                    catch { /* Restricted access or 32/64 bit mismatch */ }

                    items.Add(new ProcessSnapshotItem
                    {
                        Pid = pid,
                        ProcessName = pName,
                        WindowTitle = title,
                        Description = desc,
                        ExecutablePath = exePath,
                        IsSystem = isSystem
                    });
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        return items
            .OrderBy(x => x.IsSystem)
            .ThenByDescending(x => !string.IsNullOrEmpty(x.WindowTitle))
            .ThenBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // =========================================================================
    // Process Status Check & Matching Engine
    // =========================================================================

    public void RefreshRunningStatus()
    {
        var procs = Process.GetProcesses();
        var procNames = new List<string>(procs.Length);
        foreach (var p in procs)
        {
            try { procNames.Add(p.ProcessName); } catch { }
            p.Dispose();
        }

        lock (_lock)
        {
            foreach (var target in _targets)
            {
                int count = 0;
                foreach (var pName in procNames)
                {
                    if (MatchesTargetName(target, pName))
                        count++;
                }
                target.RunningProcessCount = count;
                target.IsRunning = count > 0;
            }
        }
    }

    public static bool MatchesTargetName(DiscoveryTarget target, string processName)
    {
        if (target.Type == TargetType.Domain)
            return false;

        if (string.IsNullOrWhiteSpace(processName))
            return false;

        string clean = processName;
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^4];

        foreach (var exe in target.Executables)
        {
            string exeClean = exe;
            if (exeClean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exeClean = exeClean[..^4];

            if (clean.Equals(exeClean, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var pat in target.ProcessNamePatterns)
        {
            if (clean.Contains(pat, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // =========================================================================
    // Process Tree Tracking (Child Processes)
    // =========================================================================

    public sealed class ProcessTreeContext
    {
        public Dictionary<int, int> ChildToParentPid { get; } = new();
        public Dictionary<int, DiscoveryTarget> PidToTarget { get; } = new();
        public Dictionary<string, DiscoveryTarget> ActiveTargetsByPattern { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    public ProcessTreeContext BuildProcessTreeContext()
    {
        var ctx = new ProcessTreeContext();
        var enabledTargets = Targets.Where(t => t.Enabled).ToList();
        if (enabledTargets.Count == 0) return ctx;

        IntPtr handle = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return ctx;

        try
        {
            var pe = new PROCESSENTRY32();
            pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

            if (Process32First(handle, ref pe))
            {
                do
                {
                    int pid = (int)pe.th32ProcessID;
                    int parentPid = (int)pe.th32ParentProcessID;
                    string exe = pe.szExeFile ?? "";

                    ctx.ChildToParentPid[pid] = parentPid;

                    foreach (var target in enabledTargets)
                    {
                        if (MatchesTargetName(target, exe))
                        {
                            ctx.PidToTarget[pid] = target;
                            break;
                        }
                    }
                } while (Process32Next(handle, ref pe));
            }

            // Propagate child process tracking (up to 4 levels)
            foreach (var target in enabledTargets.Where(t => t.TrackChildProcesses))
            {
                bool expanded;
                int depth = 0;
                do
                {
                    expanded = false;
                    depth++;
                    foreach (var kv in ctx.ChildToParentPid)
                    {
                        int childPid = kv.Key;
                        int parentPid = kv.Value;

                        if (!ctx.PidToTarget.ContainsKey(childPid) &&
                            ctx.PidToTarget.TryGetValue(parentPid, out var parentTarget) &&
                            parentTarget.Id == target.Id)
                        {
                            ctx.PidToTarget[childPid] = parentTarget;
                            expanded = true;
                        }
                    }
                } while (expanded && depth < 4);
            }
        }
        catch { }
        finally
        {
            CloseHandle(handle);
        }

        return ctx;
    }

    /// <summary>
    /// Matches a captured socket/process to an enabled target.
    /// </summary>
    public DiscoveryTarget? MatchTarget(string processName, int pid, ProcessTreeContext? treeCtx)
    {
        if (treeCtx is not null)
        {
            if (pid > 0 && treeCtx.PidToTarget.TryGetValue(pid, out var fromPid))
                return fromPid;
        }

        var enabled = Targets.Where(t => t.Enabled).ToList();
        foreach (var target in enabled)
        {
            if (MatchesTargetName(target, processName))
                return target;
        }

        return null;
    }

    // =========================================================================
    // Candidate Management & Export
    // =========================================================================

    /// <summary>
    /// Ingests captured network endpoints, matches them with active targets, filters noise,
    /// updates candidates, and saves the targets state.
    /// </summary>
    public int IngestCapturedEndpoints(IEnumerable<LiveCaptureService.CapturedEndpoint> endpoints)
    {
        int newCandidates = 0;
        lock (_lock)
        {
            var nameToTarget = new Dictionary<string, DiscoveryTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _targets)
            {
                nameToTarget[t.DisplayName] = t;
            }

            foreach (var ep in endpoints)
            {
                DiscoveryTarget? target = null;
                if (!string.IsNullOrWhiteSpace(ep.targetName))
                {
                    nameToTarget.TryGetValue(ep.targetName, out target);
                }

                if (target is null)
                {
                    target = MatchTarget(ep.processName, ep.pid, null);
                }

                if (target is null) continue;

                // Process domain candidates
                if (ep.domainCandidates is not null)
                {
                    foreach (var domain in ep.domainCandidates)
                    {
                        if (string.IsNullOrWhiteSpace(domain)) continue;
                        string clean = HostlistService.CleanDomain(domain);
                        if (NoiseFilter.IsDomainNoise(clean)) continue;

                        var existing = target.DiscoveredCandidates.FirstOrDefault(c =>
                            !c.IsIp && c.Value.Equals(clean, StringComparison.OrdinalIgnoreCase));

                        if (existing is not null)
                        {
                            existing.HitCount++;
                            existing.LastSeen = DateTime.UtcNow;
                        }
                        else
                        {
                            target.DiscoveredCandidates.Add(new DiscoveredItem
                            {
                                Value = clean,
                                IsIp = false,
                                RemotePort = ep.remotePort,
                                ProcessName = ep.processName,
                                HitCount = 1,
                                FirstSeen = DateTime.UtcNow,
                                LastSeen = DateTime.UtcNow,
                                IsSelected = true
                            });
                            newCandidates++;
                        }
                    }
                }

                // Process IP candidate
                if (!string.IsNullOrWhiteSpace(ep.remoteAddress) && !NoiseFilter.IsIpNoise(ep.remoteAddress))
                {
                    string ip = ep.remoteAddress.Trim();
                    var existing = target.DiscoveredCandidates.FirstOrDefault(c =>
                        c.IsIp && c.Value.Equals(ip, StringComparison.OrdinalIgnoreCase));

                    if (existing is not null)
                    {
                        existing.HitCount++;
                        existing.LastSeen = DateTime.UtcNow;
                    }
                    else
                    {
                        target.DiscoveredCandidates.Add(new DiscoveredItem
                        {
                            Value = ip,
                            IsIp = true,
                            RemotePort = ep.remotePort,
                            ProcessName = ep.processName,
                            HitCount = 1,
                            FirstSeen = DateTime.UtcNow,
                            LastSeen = DateTime.UtcNow,
                            IsSelected = true
                        });
                        newCandidates++;
                    }
                }

                target.NotifyCandidatesChanged();
            }

            if (newCandidates > 0)
            {
                Save();
            }
        }

        return newCandidates;
    }

    /// <summary>
    /// Exports selected candidates of a target into hostlists and/or ipset files.
    /// </summary>
    public void ExportCandidates(string targetId, IEnumerable<DiscoveredItem> itemsToExport, out int addedDomains, out int addedIps)
    {
        addedDomains = 0;
        addedIps = 0;

        DiscoveryTarget? target;
        lock (_lock)
        {
            target = _targets.FirstOrDefault(t => t.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));
        }
        if (target is null) return;

        var selected = itemsToExport.Where(i => i.IsSelected).ToList();
        string slug = DiscoveryStore.NormalizeTargetSlug(target.Id);

        foreach (var item in selected)
        {
            var rawInput = new RawEndpointInput(
                RawValue: item.Value,
                Source: "discovery_export",
                TargetSlug: slug,
                ProcessName: item.ProcessName,
                Port: item.RemotePort);

            var res = Store.IngestEndpoint(rawInput);
            if (res.IsAccepted && res.StageLog.Any(s => s.Contains("Created new persistent record")))
            {
                if (res.Kind == EndpointKind.Domain) addedDomains++;
                else addedIps++;
            }

            if (item.IsIp && System.Net.IPAddress.TryParse(item.Value, out var parsedIp) && parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] b = parsedIp.GetAddressBytes();
                string cidr = $"{b[0]}.{b[1]}.{b[2]}.0/24";
                Store.IngestEndpoint(new RawEndpointInput(cidr, "discovery_export_subnet", slug, item.ProcessName, item.RemotePort));
            }
        }

        // Materialize target lists using DiscoveryStore (merge + deterministic compile)
        Store.MaterializeTargetList(slug);
    }

    /// <summary>
    /// Adds accepted learning or discovered candidates to the target's discovered/known collections.
    /// </summary>
    public void AddAcceptedCandidates(string targetId, IEnumerable<DiscoveredItem> items)
    {
        lock (_lock)
        {
            var target = _targets.FirstOrDefault(t => t.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;

            string slug = DiscoveryStore.NormalizeTargetSlug(target.Id);

            foreach (var item in items)
            {
                if (!item.IsIp)
                {
                    if (EndpointNormalizer.TryNormalizeDomain(item.Value, out string clean, out _))
                    {
                        if (!target.DiscoveredDomains.Contains(clean, StringComparer.OrdinalIgnoreCase) &&
                            !target.KnownDomains.Contains(clean, StringComparer.OrdinalIgnoreCase) &&
                            !target.SeedDomains.Contains(clean, StringComparer.OrdinalIgnoreCase))
                        {
                            target.DiscoveredDomains.Add(clean);
                        }

                        Store.IngestEndpoint(new RawEndpointInput(clean, "accepted_learning", slug, item.ProcessName, 0, item.RemotePort));
                    }
                }
                else
                {
                    if (EndpointNormalizer.TryNormalizeIp(item.Value, out string ip, out _) ||
                        EndpointNormalizer.TryNormalizeCidr(item.Value, out ip, out _))
                    {
                        if (!target.KnownIpRanges.Contains(ip, StringComparer.OrdinalIgnoreCase))
                        {
                            target.KnownIpRanges.Add(ip);
                        }

                        Store.IngestEndpoint(new RawEndpointInput(ip, "accepted_learning", slug, item.ProcessName, 0, item.RemotePort));
                    }
                }
            }
            target.NotifyCandidatesChanged();
            Save();
        }
    }

    /// <summary>
    /// Clears candidates for a given target.
    /// </summary>
    public void ClearCandidates(string targetId)
    {
        lock (_lock)
        {
            var target = _targets.FirstOrDefault(t => t.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                target.DiscoveredCandidates.Clear();
                target.NotifyCandidatesChanged();
                Save();
            }
        }
    }
}
