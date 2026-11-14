using System.Diagnostics;
using System.IO;
using System.Text;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.Services.Engine;

public enum EngineState { Stopped, Running, Starting, Stopping }
public sealed record EngineProcessExit(Preset? Preset, string? HostlistPath);

/// <summary>
/// Owns the winws2.exe child process: builds its arguments from a preset,
/// starts/stops it, and streams its output to subscribers.
/// </summary>
public sealed partial class EngineService : IDisposable
{
    private readonly object _lock = new();
    private readonly object _transitionLock = new();
    private Task _sessionCleanup = Task.CompletedTask;
    private bool _disposed;
    private readonly object _diagnosticLock = new();
    private readonly Queue<string> _diagnosticTail = new();
    private long _outputGeneration;
    private Process? _proc;
    private StreamWriter? _logFile;
    private bool _stopRequested;
    // Job object with KILL_ON_JOB_CLOSE: winws2 is assigned to it so the OS terminates the engine
    // whenever this (the parent) process dies — including a crash or Task Manager kill, where the
    // normal Stop()/Dispose() path never runs and would otherwise orphan winws2 + WinDivert.

    // Enables Windows TCP timestamps for the duration of a run (and restores the prior value on stop),
    // so the tcp_ts/tcp_ts_up fooling used by most presets isn't a silent no-op. See TcpTimestampsService.
    private readonly TcpTimestampsService _tsTune = new();

    public EngineState State { get; private set; } = EngineState.Stopped;

    /// <summary>Flowseal-style game filter: widen the WF capture to all high ports (&gt;1023) when true.
    /// Set from settings by the VM; consumed by <see cref="Start"/> and the {WF_TCP}/{WF_UDP} tokens.</summary>
    public bool GameFilter { get; set; }

    /// <summary>Optional LoL-only workaround: capture TCP 2099 and apply syndata only to ipset-lol.
    /// Disabled by default because LoL/Riot is often fixed by excluding it from catch-all desync,
    /// not by touching the game session path.</summary>
    public bool LeagueTcp2099Bypass { get; set; }

    /// <summary>Optional Riot entitlement-token workaround: only entitlements.auth.riotgames.com on
    /// TCP 443. Used when direct entitlement requests time out while other Riot HTTPS endpoints work.</summary>
    public bool RiotEntitlementsBypass { get; set; }

    /// <summary>When false (default) the engine runs in allow-list mode like Flowseal: only the
    /// per-service hostlists (YouTube/Discord/Telegram) + the user's custom targets/hostlists are
    /// desynced, and the catch-all "all other TLS/QUIC" profiles are re-scoped to targets or dropped,
    /// so games/apps not in any list are never touched. True = bypass every site (catch-all, kept
    /// safe by the exclude list). Set from settings by the VM; consumed by <see cref="Start"/>.</summary>
    public bool BypassAllSites { get; set; }

    /// <summary>When true, the desynced services' QUIC (HTTP/3) is DROPPED so the browser falls back to
    /// TCP/H2 (which the TLS profiles handle). The July-2026 TSPU drops QUIC v1 to :443 with payload
    /// ≥1001 bytes — there the fake-QUIC bypass can't win, but forcing TCP does. Set from settings.</summary>
    public bool DisableQuic { get; set; }

    /// <summary>When true, the engine ALSO covers the built-in Telegram proxy's own Cloudflare upstream
    /// (443) with a TLS desync so mobile DPI can't kill its tunnel mid-stream. Off by default — most
    /// users don't need it, and it's inert unless the proxy is actually connecting. Set from settings.</summary>
    public bool CoverTgProxy { get; set; }

    /// <summary>Verbose engine log: adds <c>--debug=1</c>, which makes winws2 write per-connection
    /// decisions to stdout — the only way to see WHY a desync did or didn't fire. Off by default
    /// because it is noisy. Captured by the same stdout pipe as the normal output, so it lands in the
    /// Журнал tab and the session log file. Set from settings by the VM; consumed by <see cref="Start"/>.</summary>
    public bool DebugLog { get; set; }

    /// <summary>The preset that is currently running (null when stopped).</summary>
    public Preset? ActivePreset { get; private set; }

    /// <summary>The active hostlist path supplied at launch.</summary>
    public string? ActiveHostlistPath { get; private set; }

    /// <summary>Raised on the thread pool for every line of engine output.</summary>
    public event Action<string>? LogLine;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event Action<EngineState>? StateChanged;
    public event Action<EngineProcessExit>? UnexpectedExit;

    public bool IsRunning => State == EngineState.Running;

    public bool IsAttachedToExternalProcess { get; private set; }

    public int? ActiveProcessId
    {
        get
        {
            try { return _proc is { HasExited: false } ? _proc.Id : null; }
            catch { return null; }
        }
    }

    /// <summary>Build the full winws2 argument list for a preset (for preview/launch).
    /// <paramref name="forLaunch"/> must be true ONLY for a real engine start: it lets the
    /// effective-exclude list be materialised to disk. The preview path passes false so that
    /// merely showing the command line never writes a file.</summary>
    public static List<string> BuildArguments(Preset preset, string? hostlistPath,
        bool gameFilter = false, bool bypassAll = false, bool disableQuic = false,
        bool coverTgProxy = false, bool debugLog = false, bool leagueTcp2099Bypass = false,
        bool riotEntitlementsBypass = false,
        bool forLaunch = false)
    {
        var args = new List<string>();

        // Verbose log. Must come before the --lua-init loads so the library loading itself is traced;
        // =1 means "to console", which is exactly what the redirected stdout pipe reads.
        if (debugLog) args.Add("--debug=1");

        args.AddRange(new[]
        {
            // Mandatory: load the bundled Lua libraries, in the SAME order and set as the canonical
            // zapret2 launch (init.d/.../functions): base helpers, the DPI-attack verbs, AND the
            // automation/orchestration library. zapret-auto.lua is where the orchestrators live
            // (circular/repeater/stopif/condition) — without it winws2 rejects e.g. the adaptive
            // circular preset with "desync function 'circular' does not exist" (exit 87). It's inert
            // for presets that don't use an orchestrator, so it's safe to always load.
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-lib.lua"),
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-antidpi.lua"),
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-auto.lua"),
        });

        // {WF_TCP}/{WF_UDP}: WinDivert capture width (the ports the kernel hands to winws2 — a profile's
        // --filter-tcp can only match what was captured). Keep the default capture narrow so Riot/LoL
        // client ports (5222/5223/2099) remain direct unless the user explicitly enables either the
        // global game filter or the targeted LoL TCP 2099 workaround. This matters because Riot's LCU
        // stack is sensitive to even pass-through WinDivert/desync mistakes on auth, XMPP and profile
        // connections.
        // {WF_TCP}/{WF_UDP}: WinDivert capture width.
        // GameFilter widens capture to high ports. LeagueTcp2099Bypass adds only TCP 2099 without
        // enabling the broad game filter.
        string tcpPorts = "80,443,2053,2083,2087,2096,8443";
        if (leagueTcp2099Bypass) tcpPorts = "80,443,2053,2083,2087,2096,2099,8443";
        string wfTcp = gameFilter ? "--wf-tcp-out=80,443-65535" : $"--wf-tcp-out={tcpPorts}";
        string wfUdp = gameFilter ? "--wf-udp-out=443,19294-19344,50000-65535" : "--wf-udp-out=443,19294-19344,50000-50100";

        string launchHostlistPath = !string.IsNullOrWhiteSpace(hostlistPath) && File.Exists(hostlistPath)
            ? HostlistService.MaterializeRuntimeList(hostlistPath)
            : hostlistPath ?? "";

        string hostlistArg = !string.IsNullOrWhiteSpace(launchHostlistPath)
            ? $"--hostlist={launchHostlistPath}"
            : "";

        // {IPSET} expands to --ipset=<file> only if the Discord ipset has been built.
        string ipsetArg = File.Exists(AppPaths.IpsetDiscordFile)
            ? $"--ipset={HostlistService.MaterializeRuntimeList(AppPaths.IpsetDiscordFile)}"
            : "";

        // Custom targets: the catch-all profile must actually desync the user's target domains,
        // so subtract them from the exclude list (otherwise targets like yandex.ru — protected by
        // the default exclude — would never be bypassed). Done by swapping the catch-all's exclude
        // for a filtered copy; if there are no targets, the original exclude is used unchanged.
        // Only needed when actually bypassing everything — in allow-list mode the catch-all is
        // re-scoped/dropped afterwards (see ScopeCatchAllToTargets), so the exclude is irrelevant.
        string excludeName = bypassAll ? EffectiveExcludeName(forLaunch) : "exclude";

        foreach (var raw in preset.Args)
        {
            string a = raw
                .Replace("{FILES}", AppPaths.FilesDir, StringComparison.Ordinal)
                .Replace("{WF}", AppPaths.WinDivertFilterDir, StringComparison.Ordinal)
                .Replace("{WF_TCP}", wfTcp, StringComparison.Ordinal)
                .Replace("{WF_UDP}", wfUdp, StringComparison.Ordinal)
                .Replace("{HOSTLIST}", hostlistArg, StringComparison.Ordinal)
                .Replace("{EXCLUDE:exclude}", "{EXCLUDE:" + excludeName + "}", StringComparison.Ordinal)
                .Replace("{IPSET}", ipsetArg, StringComparison.Ordinal);

            // Named hostlist token {HOSTLIST:name} -> --hostlist=<lists>\name.txt (or "" if the
            // list is missing) — lets one preset route different SNIs to different strategies.
            a = ExpandNamedHostlists(a);
            if (preset.Provenance == "UPSTREAM_BLOCKCHECK2" && raw.StartsWith("{HOSTLIST:", StringComparison.Ordinal) && a.Length == 0)
                throw new IOException("Не найден обязательный хостлист сетевого профиля: " + raw);
            // Named exclude token {EXCLUDE:name} -> --hostlist-exclude=<lists>\name.txt (or "")
            // — protects sensitive domains (banks/gov/…) from a catch-all desync profile.
            a = ExpandNamedExcludes(a);
            // Named ipset token {IPSET:name} -> --ipset=<lists>\ipset-name.txt (or "" if missing)
            // — IP-based matching for protocols without SNI (e.g. Telegram MTProto).
            a = ExpandNamedIpsets(a);

            // A {HOSTLIST}/{IPSET} that resolved to nothing leaves an empty token — drop it.
            if (a.Length == 0) continue;
            args.Add(a);
        }

        // Allow-list mode (default): the catch-all "all other TLS/QUIC" profiles are what break
        // non-listed games/apps. Re-scope them to the user's custom targets or active hostlists, so only
        // the explicit lists + targets get desynced (like Flowseal).
        string? scopeList = launchHostlistPath;
        if (!bypassAll && preset.ReferenceId?.StartsWith("GX-C", StringComparison.Ordinal) == true)
            scopeList = BuildCatalogServiceList(launchHostlistPath);
        var scoped = bypassAll ? args : ScopeCatchAllToTargets(args, scopeList);
        // "QUIC off": force the desynced services onto TCP by dropping their QUIC instead of faking it.
        var result = disableQuic ? ForceQuicDrop(scoped) : scoped;
        AddBlitzRules(result);
        AddSteamRules(result);
        if (leagueTcp2099Bypass) AddRiotLeagueRules(result);
        if (riotEntitlementsBypass) AddRiotEntitlementsRules(result);
        AddRiotDirectRules(result, riotEntitlementsBypass);
        AddSpliceDirectRules(result);
        AddAntigravityDirectRules(result);
        return result;
    }

    private static string BuildCatalogServiceList(string? activeList)
    {
        // The legacy C recipes are advertised for Discord + YouTube. Selecting "discord"
        // as the custom/fallback list must not silently remove YouTube from those recipes.
        var sources = new[] { HostlistService.ResolveRuntimeListFilePath("discord"),
            HostlistService.ResolveRuntimeListFilePath("youtube"), activeList };
        var lines = sources.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .SelectMany(p => File.ReadAllLines(p!)).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (lines.Length == 0) throw new IOException("Не найдены списки Discord/YouTube для проверки каталога.");
        string path = Path.Combine(AppPaths.RuntimeListsDir, "catalog-services.txt");
        Directory.CreateDirectory(AppPaths.RuntimeListsDir);
        string text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        return path;
    }

    private static void AddRiotDirectRules(List<string> args, bool entitlementsBypass)
    {
        // Riot TLS succeeds directly on the affected network. General/discovered hostlists can
        // contain the same domains; a first-match pass-through must precede their desync rules.
        // Do not widen capture or exempt shared Cloudflare IPs used by unrelated services.
        int firstProfile = args.FindIndex(a => a.StartsWith("--filter-", StringComparison.Ordinal)
            || a.StartsWith("--hostlist", StringComparison.Ordinal) || a.StartsWith("--ipset", StringComparison.Ordinal)
            || a.StartsWith("--payload=", StringComparison.Ordinal) || a.StartsWith("--lua-desync=", StringComparison.Ordinal));
        if (firstProfile < 0) return;
        var direct = new List<string>
        {
            "--filter-tcp=80,443,8443", "--filter-l7=http,tls",
            "--hostlist-domains=riotgames.com,riotcdn.net,pvp.net,leagueoflegends.com"
        };
        // Preserve the user's explicit targeted entitlement workaround when enabled.
        if (entitlementsBypass)
            direct.Add("--hostlist-exclude=" + RiotRepairService.EnsureMinimalEntitlementsHostlist());
        direct.Add("--new");
        args.InsertRange(firstProfile, direct);
    }

    private static void AddAntigravityDirectRules(List<string> args)
    {
        // These endpoints work directly on the affected network. Legacy YouTube/general lists
        // include their parent domains (gstatic.com, googleusercontent.com). A first-match profile
        // with no desync action protects them even from custom/IP-based profiles and QUIC drop.
        // Insert AFTER scoping/drop transformations, but BEFORE the first real profile; keep all
        // global Lua/blob/capture options in place. Do not exempt YouTube's yt3/googlevideo hosts.
        int firstProfile = args.FindIndex(a => a.StartsWith("--filter-", StringComparison.Ordinal)
            || a.StartsWith("--hostlist", StringComparison.Ordinal) || a.StartsWith("--ipset", StringComparison.Ordinal)
            || a.StartsWith("--payload=", StringComparison.Ordinal) || a.StartsWith("--lua-desync=", StringComparison.Ordinal));
        if (firstProfile < 0) return;
        args.InsertRange(firstProfile, new[]
        {
            "--filter-tcp=80,443-65535", "--filter-udp=443-65535", "--filter-l7=http,tls,quic",
            "--hostlist-domains=antigravity.google,accounts.google.com,www.googleapis.com,oauth2.googleapis.com,generativelanguage.googleapis.com,alkalimakersuite-pa.clients6.google.com,cloudaicompanion.googleapis.com,cloudcode-pa.googleapis.com,daily-cloudcode-pa.googleapis.com,autopush-cloudcode-pa.sandbox.googleapis.com,staging-cloudcode-pa.sandbox.googleapis.com,proactivebackend-pa.googleapis.com,aiplatform.googleapis.com,aistudio.google.com,gemini.google.com,alkali.googleapis.com,deepmind.google,ai.google.dev,google.dev,play.googleapis.com,play.google.com,mail.google.com,docs.google.com,drive.google.com,translate.google.com,maps.google.com,calendar.google.com,lh3.googleusercontent.com,www.gstatic.com",
            "--new"
        });
    }

    private static void AddSpliceDirectRules(List<string> args)
    {
        // Splice desktop client and sample streaming endpoints (splice.com, cloudinary CDN, auth0).
        // These endpoints do not require DPI desync and fail when TLS is split or faked.
        // A first-match pass-through profile protects them from catch-all desync and QUIC drops.
        int firstProfile = args.FindIndex(a => a.StartsWith("--filter-", StringComparison.Ordinal)
            || a.StartsWith("--hostlist", StringComparison.Ordinal) || a.StartsWith("--ipset", StringComparison.Ordinal)
            || a.StartsWith("--payload=", StringComparison.Ordinal) || a.StartsWith("--lua-desync=", StringComparison.Ordinal));
        if (firstProfile < 0) return;
        args.InsertRange(firstProfile, new[]
        {
            "--filter-tcp=80,443-65535", "--filter-udp=443-65535", "--filter-l7=http,tls,quic",
            "--hostlist-domains=splice.com,api.splice.com,desktop.splice.com,spliceblob.splice.com,s3-us-west-1.amazonaws.com,spliceproduction.s3.us-west-1.amazonaws.com,spliceproduction.s3.amazonaws.com,splice-res.cloudinary.com,res.cloudinary.com,auth0.com,cdn.auth0.com,ps.pndsn.com,pubnub.net,pubsub.pubnub.com",
            "--new"
        });
    }
    private static void AddBlitzRules(List<string> args)
    {
        string hostlist = File.Exists(AppPaths.BlitzHostlistFile)
            ? $"--hostlist={HostlistService.MaterializeRuntimeList(AppPaths.BlitzHostlistFile)}"
            : "";
        string ipset = File.Exists(AppPaths.IpsetBlitzFile)
            ? $"--ipset={HostlistService.MaterializeRuntimeList(AppPaths.IpsetBlitzFile)}"
            : "";

        if (!string.IsNullOrWhiteSpace(hostlist))
        {
            args.Add("--new");
            args.AddRange(new[]
            {
                "--filter-tcp=443,8443",
                "--filter-l7=tls",
                hostlist,
                "--payload=tls_client_hello",
                "--lua-desync=multisplit:pos=2"
            });
        }

        if (!string.IsNullOrWhiteSpace(ipset))
        {
            args.Add("--new");
            args.AddRange(new[]
            {
                "--filter-tcp=443,8443",
                "--filter-l7=tls",
                ipset,
                "--payload=tls_client_hello",
                "--lua-desync=multisplit:pos=2"
            });
        }
    }

    private static void AddSteamRules(List<string> args)
    {
        string hostlist = File.Exists(AppPaths.SteamHostlistFile)
            ? $"--hostlist={HostlistService.MaterializeRuntimeList(AppPaths.SteamHostlistFile)}"
            : "";
        if (string.IsNullOrWhiteSpace(hostlist)) return;

        args.Add("--new");
        args.AddRange(new[]
        {
            "--filter-tcp=443,8443",
            "--filter-l7=tls",
            hostlist,
            "--payload=tls_client_hello",
            "--lua-desync=multisplit:pos=2",
            "--new",
            "--filter-udp=443",
            "--filter-l7=quic",
            hostlist,
            "--payload=quic_initial",
            "--lua-desync=drop"
        });
    }

    private static void AddRiotLeagueRules(List<string> args)
    {
        string lolIpset = File.Exists(AppPaths.IpsetLolFile)
            ? $"--ipset={HostlistService.MaterializeRuntimeList(AppPaths.IpsetLolFile)}"
            : "";
        if (!string.IsNullOrWhiteSpace(lolIpset))
        {
            args.Add("--new");
            args.AddRange(new[]
            {
                "--filter-tcp=2099",
                lolIpset,
                "--lua-desync=syndata"
            });
        }
    }

    private static void AddRiotEntitlementsRules(List<string> args)
    {
        string hostlist = RiotRepairService.EnsureMinimalEntitlementsHostlist();

        args.Add("--new");
        args.AddRange(new[]
        {
            "--filter-tcp=443",
            "--filter-l7=tls",
            "--hostlist=" + hostlist,
            "--payload=tls_client_hello",
            "--lua-desync=multisplit:pos=2"
        });
    }

    /// <summary>
    /// Rewrite every QUIC profile (<c>--filter-l7=quic</c>) so its desync becomes a plain
    /// <c>--lua-desync=drop</c>: winws2 drops the outbound QUIC ClientHello, the browser gives up on
    /// HTTP/3 and falls back to TCP/H2 (handled by the TLS profiles). Used when the TSPU kills QUIC v1
    /// anyway, so a fake-QUIC bypass is pointless. The Discord VOICE profile (<c>--filter-l7=discord,stun</c>,
    /// not <c>quic</c>) is left untouched — dropping it would kill voice. Profiles are delimited by
    /// <c>--new</c>; the filter/payload/hostlist args are kept, only the <c>--lua-desync=</c> line(s) change.
    /// </summary>
    private static List<string> ForceQuicDrop(List<string> args)
    {
        var result = new List<string>();
        var profile = new List<string>();

        void Flush()
        {
            if (profile.Count == 0) return;
            bool isQuic = profile.Any(a =>
                a.StartsWith("--filter-l7=", StringComparison.Ordinal) &&
                a.Contains("quic", StringComparison.Ordinal));
            if (isQuic)
            {
                bool dropAdded = false;
                foreach (var a in profile)
                {
                    if (a.StartsWith("--lua-desync=", StringComparison.Ordinal))
                    {
                        if (!dropAdded) { result.Add("--lua-desync=drop"); dropAdded = true; }
                        // drop the remaining fake desync lines of this QUIC profile
                    }
                    else result.Add(a);
                }
                if (!dropAdded) result.Add("--lua-desync=drop");
            }
            else result.AddRange(profile);
            profile.Clear();
        }

        foreach (var a in args)
        {
            if (a == "--new") Flush();
            profile.Add(a);
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Allow-list mode: neutralise every "catch-all" profile so non-listed sites are never touched.
    /// A catch-all is a broad TLS/QUIC/HTTP desync that isn't pinned to a hostlist/ipset — either it
    /// carries <c>--hostlist-exclude</c> ("desync everything except these"), or it's a BARE
    /// <c>--filter-l7=tls/quic/http</c> with no list at all (e.g. a proxy profile whose
    /// <c>{IPSET:proxy}</c> resolved to nothing, or a stale global auto-select preset). Exclude
    /// catch-alls are re-pointed at the user's custom targets (<c>--hostlist=&lt;targets&gt;</c>) so
    /// those still bypass; bare globals carry no scope intent and are dropped. Profiles are delimited
    /// by <c>--new</c>; dropping one together with its leading <c>--new</c> keeps the arg list valid.
    /// The FIRST segment is always kept — it holds the global setup (<c>--wf-*</c>, blobs) plus the
    /// first real profile.
    /// </summary>
    private static List<string> ScopeCatchAllToTargets(List<string> args, string? activeHostlistPath = null)
    {
        string? effectiveListPath = null;
        if (!string.IsNullOrWhiteSpace(activeHostlistPath) && File.Exists(activeHostlistPath))
        {
            effectiveListPath = HostlistService.MaterializeRuntimeList(activeHostlistPath);
        }
        else
        {
            string targetsPath = HostlistService.ResolveListFilePath(TargetService.AggregateName);
            if (File.Exists(targetsPath) && File.ReadAllLines(targetsPath).Any(l => l.Trim().Length > 0))
            {
                effectiveListPath = HostlistService.MaterializeRuntimeList(targetsPath);
            }
            else
            {
                string generalPath = HostlistService.ResolveRuntimeListFilePath("general");
                if (File.Exists(generalPath))
                {
                    effectiveListPath = generalPath;
                }
            }
        }

        var result = new List<string>();
        var profile = new List<string>();

        void Flush()
        {
            if (profile.Count == 0) return;
            int ex = profile.FindIndex(a => a.StartsWith("--hostlist-exclude=", StringComparison.Ordinal));
            bool scoped = profile.Any(a => a.StartsWith("--hostlist=", StringComparison.Ordinal)
                                        || a.StartsWith("--hostlist-domains=", StringComparison.Ordinal)
                                        || a.StartsWith("--ipset=", StringComparison.Ordinal));
            // The first segment also contains a real profile: it must be scoped too.
            bool bareGlobal = ex < 0 && !scoped && profile.Any(a =>
                a.StartsWith("--filter-l7=", StringComparison.Ordinal) &&
                a["--filter-l7=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(p => p is "tls" or "quic" or "http"));

            if (ex < 0 && !bareGlobal)
            {
                result.AddRange(profile); // normal (listed/scoped) profile — keep
            }
            else if (ex >= 0)
            {
                // Catch-all profile with exclude: in allow-list mode, pin it to the effective hostlist if available
                if (!string.IsNullOrWhiteSpace(effectiveListPath))
                {
                    profile[ex] = $"--hostlist={effectiveListPath}";
                    result.AddRange(profile);
                }
                // else: no list available, drop profile so unlisted games/apps aren't touched
            }
            else if (bareGlobal)
            {
                // Bare global profile (e.g. general candidate or custom preset without a list):
                // In allow-list mode, pin it to the effective hostlist rather than discarding it!
                if (!string.IsNullOrWhiteSpace(effectiveListPath))
                {
                    int insertIdx = profile.FindIndex(a => a.StartsWith("--payload=", StringComparison.Ordinal)
                                                        || a.StartsWith("--lua-desync=", StringComparison.Ordinal)
                                                        || a.StartsWith("--dpi-desync=", StringComparison.Ordinal));
                    if (insertIdx >= 0)
                        profile.Insert(insertIdx, $"--hostlist={effectiveListPath}");
                    else
                        profile.Add($"--hostlist={effectiveListPath}");
                    result.AddRange(profile);
                }
                else
                {
                    throw new IOException("В режиме списков отсутствует хостлист для ограничения стратегии.");
                }
            }
            profile.Clear();
        }

        foreach (var a in args)
        {
            if (a == "--new") Flush(); // close the previous profile before starting a new one
            profile.Add(a);
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Expand every <c>{HOSTLIST:name}</c> token in an argument to
    /// <c>--hostlist=&lt;lists&gt;\name.txt</c>, or to an empty string if that list does
    /// not exist (the profile then matches by its other filters).
    /// </summary>
    private static string ExpandNamedHostlists(string a)
    {
        const string marker = "{HOSTLIST:";
        int i;
        while ((i = a.IndexOf(marker, StringComparison.Ordinal)) >= 0)
        {
            int end = a.IndexOf('}', i + marker.Length);
            if (end < 0) break;
            string name = a.Substring(i + marker.Length, end - i - marker.Length);
            string path = HostlistService.ResolveRuntimeListFilePath(name);
            string repl = File.Exists(path) ? $"--hostlist={path}" : "";
            a = a[..i] + repl + a[(end + 1)..];
        }
        return a;
    }

    /// <summary>
    /// Expand every <c>{EXCLUDE:name}</c> token to
    /// <c>--hostlist-exclude=&lt;lists&gt;\name.txt</c>, or to an empty string if that
    /// list does not exist. Used on catch-all profiles so excluded domains
    /// (banks, gov, etc.) are left untouched.
    /// </summary>
    private static string ExpandNamedExcludes(string a)
    {
        const string marker = "{EXCLUDE:";
        int i;
        while ((i = a.IndexOf(marker, StringComparison.Ordinal)) >= 0)
        {
            int end = a.IndexOf('}', i + marker.Length);
            if (end < 0) break;
            string name = a.Substring(i + marker.Length, end - i - marker.Length);
            string path = HostlistService.ResolveRuntimeExcludeFilePath(name);
            string repl = File.Exists(path) ? $"--hostlist-exclude={path}" : "";
            a = a[..i] + repl + a[(end + 1)..];
        }
        return a;
    }

    /// <summary>
    /// Expand every <c>{IPSET:name}</c> token to <c>--ipset=&lt;lists&gt;\ipset-name.txt</c>,
    /// or to an empty string if that ipset file does not exist yet.
    /// </summary>
    private static string ExpandNamedIpsets(string a)
    {
        const string marker = "{IPSET:";
        int i;
        while ((i = a.IndexOf(marker, StringComparison.Ordinal)) >= 0)
        {
            int end = a.IndexOf('}', i + marker.Length);
            if (end < 0) break;
            string name = a.Substring(i + marker.Length, end - i - marker.Length);
            string path = HostlistService.ResolveRuntimeIpsetFilePath(name);
            string repl = File.Exists(path) ? $"--ipset={path}" : "";
            a = a[..i] + repl + a[(end + 1)..];
        }
        return a;
    }

    /// <summary>
    /// Returns the exclude-list name the catch-all profiles should use. When the user has custom
    /// targets, returns "exclude-eff" (= exclude.txt minus the target domains) so the active strategy
    /// desyncs those domains; otherwise "exclude". The exclude-eff.txt file is (re)written ONLY when
    /// <paramref name="write"/> is true (a real launch) — never from the preview path. When not
    /// writing, the eff name is claimed only if the file already exists, so the preview stays honest.
    /// </summary>
    private static string EffectiveExcludeName(bool write)
    {
        try
        {
            string targetsPath = HostlistService.ResolveListFilePath(TargetService.AggregateName);
            string excludePath = HostlistService.ResolveExcludeFilePath("exclude");
            if (!File.Exists(targetsPath) || !File.Exists(excludePath)) return "exclude";

            var targets = new HashSet<string>(
                File.ReadAllLines(targetsPath).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            if (targets.Count == 0) return "exclude";

            string effPath = Path.Combine(AppPaths.RuntimeListsDir, "exclude-eff.txt");
            if (write)
            {
                var kept = File.ReadAllLines(excludePath)
                    .Where(line =>
                    {
                        string t = line.Trim().ToLowerInvariant();
                        return t.Length == 0 || !targets.Contains(t);
                    });
                File.WriteAllLines(effPath, kept);
                return "exclude-eff";
            }
            return File.Exists(effPath) ? "exclude-eff" : "exclude";
        }
        catch { return "exclude"; }
    }

    /// <summary>Human-readable preview of the command line that will be launched.</summary>
    public static string PreviewCommandLine(Preset preset, string? hostlistPath,
        bool gameFilter = false, bool bypassAll = false, bool disableQuic = false,
        bool coverTgProxy = false, bool debugLog = false, bool leagueTcp2099Bypass = false,
        bool riotEntitlementsBypass = false)
    {
        var sb = new StringBuilder("winws2.exe");
        foreach (var a in BuildArguments(preset, hostlistPath, gameFilter, bypassAll, disableQuic, coverTgProxy, debugLog, leagueTcp2099Bypass, riotEntitlementsBypass))
            sb.Append(a.Contains(' ') ? $" \"{a}\"" : $" {a}");
        return sb.ToString();
    }

    public void Start(Preset preset, string? hostlistPath)
    {
        lock (_transitionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_lock)
                if (State != EngineState.Stopped) throw new InvalidOperationException(Loc.T("Движок уже запущен."));
            _sessionCleanup.GetAwaiter().GetResult();
            try { StartCore(preset, hostlistPath); }
            catch { StopCore(); throw; }
        }
    }

    private void StartCore(Preset preset, string? hostlistPath)
    {
        if (preset.BoundNetworkFingerprint is { } network && NetworkFingerprint.Current() != network)
            throw new InvalidOperationException("Профиль blockcheck2 относится к другой сети. Повторите подбор для текущей сети.");
        lock (_lock)
        {
            if (State is EngineState.Running)
                throw new InvalidOperationException(Loc.T("Движок уже запущен."));

            if (!File.Exists(AppPaths.WinwsExe))
                throw new FileNotFoundException(Loc.T("winws2.exe не найден. Дождитесь загрузки движка."));

            // Supply-chain integrity gate: verify SHA-256 against pinned manifest before launching
            EngineValidator.EnsureEngineVerified();

            SetState(EngineState.Starting);
            _stopRequested = false;

            // Clean up any rogue/orphaned winws processes before starting
            KillOrphanedBypassProcesses();
            try
            {
                if (new SettingsService().Settings.GameRoutingHostsFix)
                    RiotRoutingService.ApplyHostsFix();
            }
            catch { }

            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.WinwsExe,
                WorkingDirectory = AppPaths.EngineDir, // so WinDivert.dll/.sys resolve
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in BuildArguments(preset, hostlistPath, GameFilter, BypassAllSites, DisableQuic,
                                             CoverTgProxy, DebugLog, LeagueTcp2099Bypass, RiotEntitlementsBypass, forLaunch: true))
                psi.ArgumentList.Add(a);

            OpenLogFile(preset);
            Emit(Loc.T("[engine] Путь: {0}", psi.FileName));
            Emit(Loc.T("[engine] Рабочая папка: {0}", psi.WorkingDirectory));
            Emit(Loc.T("[engine] Аргументов запуска: {0}", psi.ArgumentList.Count));
            string fullCmd = $"{psi.FileName} " + string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            Emit(Loc.T("[engine] Полная строка запуска: {0}", fullCmd));

            Process? launchedProc = null;
            string? lastEarlyError = null;

            // Retry loop (up to 4 attempts with progressive kernel driver release backoff)
            const int maxAttempts = 4;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                long outputGeneration = Interlocked.Increment(ref _outputGeneration);
                var recentOutput = new List<string>();
                var readiness = new EngineReadinessSignal();

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null || outputGeneration != Interlocked.Read(ref _outputGeneration)) return;
                    OnOutput(e.Data);
                    lock (recentOutput)
                    {
                        if (recentOutput.Count >= 25) recentOutput.RemoveAt(0);
                        recentOutput.Add(e.Data);
                    }
                    readiness.ObserveOutput(e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data) || outputGeneration != Interlocked.Read(ref _outputGeneration)) return;
                    OnOutput(e.Data);
                    lock (recentOutput)
                    {
                        if (recentOutput.Count >= 25) recentOutput.RemoveAt(0);
                        recentOutput.Add(e.Data);
                    }
                    readiness.ObserveOutput(e.Data);
                };
                proc.Exited += (_, _) => readiness.ObserveExit();

                try
                {
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                }
                catch
                {
                    try { proc.Dispose(); } catch { }
                    if (attempt == maxAttempts)
                    {
                        CloseLogFile();
                        SetState(EngineState.Stopped);
                        throw;
                    }
                    KillOrphanedBypassProcesses();
                    Thread.Sleep(500);
                    continue;
                }

                // Wait for WinDivert readiness log line or early process exit (up to 3500ms)
                bool captureReady = readiness.Wait(3500);

                // Crucial: in nfqws.c, "windivert initialized. capture is started." is flushed
                // immediately BEFORE win_sandbox() (RelaxCygwinNTDir -> LowMandatoryLevel -> JobSandbox)
                // and lua_init(). We must let win_sandbox() and lua_init() finish before checking
                // proc.HasExited and BEFORE attaching any parent Job Object (otherwise our High-Integrity
                // Job Object causes Low-Integrity JobSandbox() in winws2 to fail with ERROR_ACCESS_DENIED
                // -> "Cannot init Windows sandbox").
                bool hasExited = false;
                try
                {
                    if (!proc.HasExited)
                        proc.WaitForExit(450);
                    hasExited = proc.HasExited;
                }
                catch { }

                int exitCode = 0;
                try
                {
                    if (hasExited) exitCode = proc.ExitCode;
                }
                catch { }

                bool hasSandboxOrFatalError = false;
                lock (recentOutput)
                {
                    hasSandboxOrFatalError = recentOutput.Any(l =>
                        l.Contains("Cannot init Windows sandbox", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("error opening windivert", StringComparison.OrdinalIgnoreCase));
                }

                if (hasExited || hasSandboxOrFatalError || !captureReady)
                {
                    if (!hasExited)
                    {
                        try { proc.Kill(entireProcessTree: true); proc.WaitForExit(1000); } catch { }
                        try { exitCode = proc.ExitCode; } catch { }
                    }
                    string errDetail;
                    lock (recentOutput)
                    {
                        errDetail = !captureReady && !hasExited
                            ? Loc.T("Готовность захвата WinDivert не подтверждена. Запуск отменён.")
                            : recentOutput.Count > 0
                            ? string.Join(" | ", recentOutput.TakeLast(3))
                            : Loc.T("Процесс завершился с кодом {0} сразу после старта.", exitCode);
                    }
                    lastEarlyError = errDetail;
                    try { proc.Dispose(); } catch { }

                    if (attempt < maxAttempts)
                    {
                        int delayMs = attempt switch
                        {
                            1 => 600,
                            2 => 1000,
                            _ => 1500,
                        };
                        Emit(Loc.T("Предыдущая сессия WinDivert ещё освобождается ядром. Очистка и повтор через {0}мс (попытка {1}/{2})...", delayMs, attempt, maxAttempts));
                        KillOrphanedBypassProcesses();
                        Thread.Sleep(delayMs);
                        continue;
                    }

                    CloseLogFile();
                    SetState(EngineState.Stopped);
                    throw new InvalidOperationException(
                        Loc.T("winws2 не смог запуститься (код {0}): {1}", exitCode, lastEarlyError));
                }

                launchedProc = proc;
                break;
            }

            if (launchedProc is null)
            {
                CloseLogFile();
                SetState(EngineState.Stopped);
                throw new InvalidOperationException(
                    Loc.T("Не удалось запустить winws2: {0}", lastEarlyError ?? "неизвестная ошибка"));
            }

            _proc = launchedProc;
            ActivePreset = preset;
            ActiveHostlistPath = hostlistPath;
            // Assign ownership before subscribing, so an immediate exit cannot be missed.
            launchedProc.Exited += OnProcessExited;
            if (launchedProc.HasExited) throw new InvalidOperationException("Движок завершился до подтверждения запуска.");

            // Tie winws2 to the kill-on-close job so it can never outlive this app process.
            EnsureJobAndAssign(launchedProc);

            // Turn on TCP timestamps for the session so tcp_ts/tcp_ts_up fooling actually applies
            // (Windows leaves them "allowed" by default → the fake never dies). Restored on stop.
            _tsTune.EnableForSession(Emit);

            _proc = launchedProc;
            IsAttachedToExternalProcess = false;
            ActivePreset = preset;
            ActiveHostlistPath = hostlistPath;
            if (launchedProc.HasExited) throw new InvalidOperationException("Движок завершился во время настройки сеанса.");
            SetState(EngineState.Running);
            Emit(Loc.T("=== Запущен пресет «{0}» (PID {1}) ===", Loc.T(preset.Name), launchedProc.Id));
        }
    }

    /// <summary>
    /// Safely re-applies the currently running strategy with updated hostlists/arguments.
    /// Preserves the selected preset, stops the existing winws2 instance, and launches a fresh
    /// instance using newly materialized lists without changing the selected strategy.
    /// If the engine is stopped or not running, returns false.
    /// </summary>
    public bool ReapplyActiveStrategy(string reason = "list_update")
    {
        lock (_transitionLock) return ReapplyActiveStrategyCore(reason);
    }

    private bool ReapplyActiveStrategyCore(string reason)
    {
        Preset? preset;
        string? hostlistPath;
        lock (_lock)
        {
            if (State != EngineState.Running || _proc is null || ActivePreset is null)
                return false;

            preset = ActivePreset;
            hostlistPath = ActiveHostlistPath;
        }

        Emit(Loc.T("=== Переприменение активной стратегии «{0}» ({1}) ===", Loc.T(preset.Name), reason));

        try
        {
            Stop();
            Start(preset, hostlistPath);
            return true;
        }
        catch (Exception ex)
        {
            Emit(Loc.T("Ошибка переприменения стратегии: {0}", ex.Message));
            return false;
        }
    }

    public async Task<bool> ReapplyActiveStrategyAsync(string reason = "list_update", CancellationToken ct = default)
    {
        return await Task.Run(() => ReapplyActiveStrategy(reason), ct).ConfigureAwait(false);
    }

    public bool AttachToExistingManagedProcess()
    {
        lock (_transitionLock)
        {
            if (_disposed) return false;
            return AttachToExistingManagedProcessCore();
        }
    }

    private bool AttachToExistingManagedProcessCore()
    {
        lock (_lock)
        {
            if (State is EngineState.Running or EngineState.Starting)
                return false;

            var proc = FindExistingManagedProcess();
            if (proc is null) return false;

            try
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += OnProcessExited;
                _proc = proc;
                ActivePreset = null;
                ActiveHostlistPath = null;
                IsAttachedToExternalProcess = true;
                if (proc.HasExited) throw new InvalidOperationException("Managed-процесс уже завершился.");
                SetState(EngineState.Running);
                Emit(Loc.T("=== Найден уже запущенный managed winws (PID {0}) ===", proc.Id));
                return true;
            }
            catch
            {
                try { proc.Dispose(); } catch { }
                _proc = null;
                IsAttachedToExternalProcess = false;
                ActivePreset = null;
                SetState(EngineState.Stopped);
                return false;
            }
        }
    }

    public bool RefreshAttachedProcessState()
    {
        lock (_lock)
        {
            if (_proc is { } proc)
            {
                try
                {
                    if (!proc.HasExited) return true;
                }
                catch { }

                FinalizeStoppedProcess(proc);
                return false;
            }
        }

        return AttachToExistingManagedProcess();
    }

    public void Stop()
    {
        lock (_transitionLock) StopCore();
    }

    private void StopCore()
    {
        Process? proc;
        lock (_lock)
        {
            if (_proc is null && State == EngineState.Stopped) return;
            _stopRequested = true;
            SetState(EngineState.Stopping);
            proc = _proc;
        }
        // Wait OUTSIDE the lock: OnProcessExited needs the lock to finalize, and
        // event marshaling to the UI thread must not be blocked by a held lock.
        try
        {
            if (proc is not null && !proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                if (!proc.WaitForExit(4000))
                {
                    Emit(Loc.T("Движок не завершился за 4 секунды, повторная остановка..."));
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    proc.WaitForExit(2000);
                }
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Emit(Loc.T("Ошибка остановки: {0}", ex.Message));
        }
        bool stillAlive;
        lock (_lock)
        {
            stillAlive = ReferenceEquals(_proc, proc) && proc is not null;
            if (stillAlive)
            {
                try { stillAlive = !proc!.HasExited; } catch { }
                if (stillAlive) SetState(EngineState.Running);
            }
        }
        if (stillAlive) throw new IOException("Не удалось завершить процесс движка. Он остаётся активным; новый запуск запрещён.");
        FinalizeStoppedProcess(proc);
        _sessionCleanup.GetAwaiter().GetResult();

        // Kernel release grace period: WinDivert driver needs 350-400ms to clean up handles in kernel
        Thread.Sleep(350);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        FinalizeStoppedProcess(sender as Process);
    }

    private void FinalizeStoppedProcess(Process? expectedProcess)
    {
        Process? procToDispose = null;
        int? code = null;
        EngineProcessExit? unexpectedExit = null;
        lock (_lock)
        {
            if (_proc is null)
            {
                ActivePreset = null;
                ActiveHostlistPath = null;
                IsAttachedToExternalProcess = false;
                CloseLogFile();
                SetState(EngineState.Stopped);
                return;
            }
            if (expectedProcess is not null && !ReferenceEquals(_proc, expectedProcess))
            {
                return;
            }

            try { code = _proc.ExitCode; } catch { }
            if (!_stopRequested && !_disposed && State == EngineState.Running)
                unexpectedExit = new EngineProcessExit(ActivePreset, ActiveHostlistPath);
            procToDispose = _proc;
            _proc = null;
            Interlocked.Increment(ref _outputGeneration);
            ActivePreset = null;
            ActiveHostlistPath = null;
            IsAttachedToExternalProcess = false;
            CloseLogFile();
            bool requested = _stopRequested;
            string[] tail;
            lock (_diagnosticLock) tail = _diagnosticTail.ToArray();
            _sessionCleanup = Task.Run(() =>
            {
                try
                {
                    _tsTune.RestoreAfterSession(Emit);
                    string reason = requested ? Loc.T("запрошено приложением") : Loc.T("процесс завершился самостоятельно");
                    Emit(Loc.T("=== Движок остановлен (код {0}; {1}) ===", code?.ToString() ?? "?", reason));
                    if (!requested && tail.Length > 0)
                        Emit(Loc.T("[engine] Последние сообщения перед завершением: {0}", string.Join(" | ", tail.TakeLast(12))));
                }
                finally { try { procToDispose?.Dispose(); } catch { } }
            });
            SetState(EngineState.Stopped);
        }
        // Subscribers may start another transition; never invoke them under the engine lock.
        if (unexpectedExit is not null) UnexpectedExit?.Invoke(unexpectedExit);
    }

    // ---- output / logging --------------------------------------------------

    private void OnOutput(string? line)
    {
        if (line is null) return;
        lock (_diagnosticLock)
        {
            if (_diagnosticTail.Count >= 40) _diagnosticTail.Dequeue();
            _diagnosticTail.Enqueue(line);
        }
        Emit(line);
    }

    private void Emit(string line)
    {
        try { _logFile?.WriteLine(line); _logFile?.Flush(); } catch { }
        if (LogLine is { } handlers)
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(line); } catch (Exception ex) { Trace.WriteLine(ex); }
    }

    private void OpenLogFile(Preset preset)
    {
        try
        {
            lock (_diagnosticLock) _diagnosticTail.Clear();
            Directory.CreateDirectory(AppPaths.LogsDir);
            string path = Path.Combine(AppPaths.LogsDir,
                $"engine-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _logFile = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
            _logFile.WriteLine($"# preset: {preset.Name}");
        }
        catch { _logFile = null; }
    }

    private void CloseLogFile()
    {
        try { _logFile?.Dispose(); } catch { }
        _logFile = null;
    }

    private void SetState(EngineState s)
    {
        State = s;
        if (StateChanged is { } handlers)
            foreach (Action<EngineState> handler in handlers.GetInvocationList())
                try { handler(s); } catch (Exception ex) { Trace.WriteLine(ex); }
    }

    private static Process? FindExistingManagedProcess()
    {
        Process? selected = null;
        foreach (var proc in EngineProcessOwnership.FindOwned())
        {
            if (selected is null) selected = proc;
            else proc.Dispose();
        }
        return selected;
    }

    public static void KillOrphanedBypassProcesses(int? excludePid = null)
    {
        EngineProcessOwnership.StopOwned(excludePid);
    }

    public void Dispose()
    {
        lock (_transitionLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { StopCore(); } catch { }
            _sessionCleanup.GetAwaiter().GetResult();
            CloseLogFile();
        // Stop() already killed winws2; releasing the job handle now is just cleanup. (If we ever
        // crash before here, the OS closes the handle for us and kill-on-close ends winws2 anyway.)
            if (_job != IntPtr.Zero) { try { CloseHandle(_job); } catch { } _job = IntPtr.Zero; }
        }
    }
}
