using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Platform;

/// <summary>How badly a finding affects the bypass.</summary>
public enum EnvSeverity
{
    /// <summary>Hard conflict — the bypass will not work until it's resolved.</summary>
    Conflict,
    /// <summary>Likely to break the bypass, but not guaranteed.</summary>
    Warning,
    /// <summary>Nothing wrong (used for the "all clear" card).</summary>
    Ok,
}

/// <summary>
/// One environment-check result. <paramref name="Title"/> says WHAT was found, <paramref name="Detail"/>
/// why it matters, and <paramref name="Action"/> the concrete steps to fix it — the point is that the
/// user can act on it without asking anyone. <paramref name="Action"/> is empty on the "all clear" card.
/// </summary>
public sealed record EnvFinding(EnvSeverity Severity, string Title, string Detail, string Action, string FixKey = "");

/// <summary>
/// Environment check for things that commonly stop the DPI-bypass engine from working:
///   • another running DPI-bypass tool — they all grab the WinDivert driver, and two filters on the
///     same packets conflict;
///   • an active VPN client or tunnel adapter — it re-routes traffic, so the desync never reaches the
///     provider and the bypass looks "broken";
///   • (manual check only) a missing or half-installed engine.
/// Process-name + network-adapter based; needs no admin. Advisory only — it never blocks anything.
/// </summary>
public static class ConflictScanService
{
    // Other DPI-bypass tools — the hard conflict (shared WinDivert driver). Process names, lowercase.
    private static readonly (string proc, string label)[] DpiTools =
    {
        ("winws",        Loc.T("zapret (winws) — другой обход блокировок")),
        ("winws2",       Loc.T("zapret2 (winws2) — другой обход блокировок")),
        ("nfqws",        Loc.T("zapret (nfqws) — другой обход блокировок")),
        ("nfqws2",       Loc.T("zapret2 (nfqws2) — другой обход блокировок")),
        ("zapret",       Loc.T("zapret — другой обход блокировок")),
        ("goodbyedpi",   Loc.T("GoodbyeDPI — другой обход блокировок")),
        ("ciadpi",       Loc.T("ByeDPI (ciadpi) — другой обход блокировок")),
        ("byedpi",       Loc.T("ByeDPI — другой обход блокировок")),
        ("spoofdpi",     Loc.T("SpoofDPI — другой обход блокировок")),
        ("dpitunnel",    Loc.T("DPITunnel — другой обход блокировок")),
        ("green_tunnel", Loc.T("GreenTunnel — другой обход блокировок")),
    };

    // Common VPN / proxy-tunnel clients — re-route traffic, so the engine's desync may never apply.
    private static readonly (string proc, string label)[] VpnApps =
    {
        ("openvpn",       "OpenVPN"),
        ("openvpn-gui",   "OpenVPN"),
        ("wireguard",     "WireGuard"),
        ("nordvpn",       "NordVPN"),
        ("expressvpn",    "ExpressVPN"),
        ("protonvpn",     "Proton VPN"),
        ("surfshark",     "Surfshark"),
        ("cyberghost",    "CyberGhost"),
        ("windscribe",    "Windscribe"),
        ("mullvad",       "Mullvad"),
        ("tunnelbear",    "TunnelBear"),
        ("hotspotshield", "Hotspot Shield"),
        ("hamachi",       "LogMeIn Hamachi"),
        ("radminvpn",     "Radmin VPN"),
        ("warp-svc",      "Cloudflare WARP"),
        ("outline",       "Outline"),
        ("hiddify",       "Hiddify"),
        ("nekoray",       "NekoRay"),
        ("nekobox",       "NekoBox"),
        ("v2rayn",        "v2rayN"),
        ("xray",          "Xray"),
        ("sing-box",      "sing-box"),
        ("mihomo",        "Mihomo / Clash"),
        ("clash-verge",   "Clash Verge"),
        ("clash",         "Clash"),
        ("amneziavpn",    "AmneziaVPN"),
        ("amnezia",       "Amnezia"),
        ("psiphon",       "Psiphon"),
        ("browsec",       "Browsec"),
    };

    // Kernel-level anti-cheats that can block or restrict WinDivert packet filter
    private static readonly (string proc, string label)[] AntiCheatApps =
    {
        ("vgtray",        "Riot Vanguard (Valorant)"),
        ("vgc",           "Riot Vanguard (Valorant)"),
        ("faceitclient",  "FACEIT Anti-Cheat"),
        ("faceitservice", "FACEIT Anti-Cheat"),
    };

    private static readonly Dictionary<string, string> DpiToolLabels =
        DpiTools.ToDictionary(x => x.proc, x => x.label, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> VpnAppLabels =
        VpnApps.ToDictionary(x => x.proc, x => x.label, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> AntiCheatLabels =
        AntiCheatApps.ToDictionary(x => x.proc, x => x.label, StringComparer.OrdinalIgnoreCase);

    // Explanations live here so every finding of the same kind reads identically.
    private static readonly string DpiToolDetail =
        Loc.T("Эта программа уже держит драйвер WinDivert. Два обхода не могут править одни и те же пакеты: " +
        "движок либо не запустится совсем, либо будет срабатывать через раз — и стратегия, которая на " +
        "самом деле рабочая, покажется нерабочей.");
    private static readonly string DpiToolAction =
        Loc.T("Закройте её полностью — не только окно, но и значок рядом с часами (правой кнопкой → выход). " +
        "Если она прописана в автозапуске, отключите его, иначе она вернётся после перезагрузки. " +
        "Затем включите обход здесь.");

    private static readonly string VpnDetail =
        Loc.T("VPN заворачивает весь трафик в свой туннель. Движок правит пакеты уже после этого, и до " +
        "провайдера они доходят запечатанными внутри туннеля — обходить становится нечего, поэтому обход " +
        "выглядит сломанным, даже если стратегия верная.");
    private static readonly string VpnAction =
        Loc.T("Отключитесь от VPN, пока пользуетесь обходом. Обход решает ту же задачу, что и VPN, — вместе " +
        "они не нужны. Если VPN обязателен для работы, включайте их по очереди, а не одновременно.");

    private static readonly string AntiCheatDetail =
        Loc.T("Античит режима ядра (Riot Vanguard или FACEIT) активен в системе. Некоторые версии блокируют сторонние драйверы пакетной фильтрации (WinDivert64.sys).");
    private static readonly string AntiCheatAction =
        Loc.T("Если обход не может запуститься (ошибка WinDivert), временно закройте Vanguard/FACEIT через значок у часов перед включением обхода.");

    private static readonly string AdapterDetail =
        Loc.T("Сетевой адаптер туннеля включён. Такое бывает и при закрытом VPN-клиенте: адаптер остаётся в " +
        "системе и продолжает уводить часть трафика мимо движка.");
    private static readonly string AdapterAction =
        Loc.T("Выйдите из VPN-клиента полностью. Если адаптер остался от уже удалённой программы, отключите " +
        "его: Параметры Windows → Сеть и Интернет → Дополнительные сетевые параметры → выбрать адаптер → «Отключить».");

    /// <summary>
    /// Conflicts only (running software + tunnel adapters). This is the startup advisory: it runs
    /// before the engine auto-download, so engine state is deliberately NOT checked here — on a first
    /// launch the engine is legitimately absent for a few seconds.
    /// </summary>
    public static List<EnvFinding> ScanConflicts()
    {
        var findings = new List<EnvFinding>();

        try
        {
            string installedWinws = Path.GetFullPath(AppPaths.WinwsExe);
            string systemRuntimeDir = Path.GetFullPath(AppPaths.SystemRuntimeDir);
            string appDataRoot = Path.GetFullPath(AppPaths.Root);

            var runningDpi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runningVpn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runningAntiCheat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string name = p.ProcessName.ToLowerInvariant();
                    if (name is "winws" or "winws2")
                    {
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            string full = Path.GetFullPath(path);
                            if (string.Equals(full, installedWinws, StringComparison.OrdinalIgnoreCase) ||
                                full.StartsWith(systemRuntimeDir, StringComparison.OrdinalIgnoreCase) ||
                                full.StartsWith(appDataRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                continue; // Our own engine — not a conflict
                            }
                        }
                    }

                    if (DpiToolLabels.ContainsKey(name))
                        runningDpi.Add(name);

                    if (VpnAppLabels.ContainsKey(name))
                        runningVpn.Add(name);

                    if (AntiCheatLabels.ContainsKey(name))
                        runningAntiCheat.Add(name);
                }
                catch { }
                finally { p.Dispose(); }
            }

            foreach (var (proc, label) in DpiTools)
                if (runningDpi.Contains(proc))
                    findings.Add(new EnvFinding(EnvSeverity.Conflict, label, DpiToolDetail, DpiToolAction, "dpi"));

            var seenVpn = new HashSet<string>();
            foreach (var (proc, label) in VpnApps)
                if (runningVpn.Contains(proc) && seenVpn.Add(label))
                    findings.Add(new EnvFinding(EnvSeverity.Warning, "VPN: " + label, VpnDetail, VpnAction));

            var seenAntiCheat = new HashSet<string>();
            foreach (var (proc, label) in AntiCheatApps)
                if (runningAntiCheat.Contains(proc) && seenAntiCheat.Add(label))
                    findings.Add(new EnvFinding(EnvSeverity.Warning, "Античит: " + label, AntiCheatDetail, AntiCheatAction));
        }
        catch { /* best-effort — a failed scan just warns about nothing */ }

        try
        {
            foreach (var name in VpnAdapters())
                findings.Add(new EnvFinding(
                    EnvSeverity.Warning, Loc.T("Активен VPN-адаптер: ") + name, AdapterDetail, AdapterAction));
        }
        catch { /* ignore adapter enumeration failures */ }

        return findings;
    }

    /// <summary>
    /// Full check for the manual "Проверить окружение" button: everything <see cref="ScanConflicts"/>
    /// finds, plus the state of the engine itself. Safe to include the engine here because the user
    /// runs this after startup has settled.
    /// </summary>
    public static List<EnvFinding> ScanEnvironment(bool engineInstalled, bool engineComplete)
    {
        var findings = ScanConflicts();

        if (!engineInstalled)
            findings.Add(new EnvFinding(EnvSeverity.Conflict,
                Loc.T("Движок не установлен"),
                Loc.T("winws2 — это то, что собственно обходит блокировку; без него кнопка «Включить обход» " +
                "работать не будет. Обычно он скачивается сам при первом запуске, так что если его до сих " +
                "пор нет — загрузка не прошла."),
                Loc.T("Откройте «Настройки» → «Проверить обновления» и дождитесь окончания загрузки. Если она " +
                "падает с ошибкой сети, дело в блокировке githubusercontent.com у вашего провайдера: " +
                "приложение уже пробует обойти её через DoH, но надёжнее всего включить VPN на время " +
                "одной этой загрузки, а потом выключить.")));
        else if (!engineComplete)
            findings.Add(new EnvFinding(EnvSeverity.Warning,
                Loc.T("Движок установлен не полностью"),
                Loc.T("Сам winws2 на месте, но часть файлов отсутствует — обычно это набор фильтров WinDivert, " +
                "который появился в более новых версиях. Стратегии, которые их используют, не запустятся."),
                Loc.T("«Настройки» → «Проверить обновления». Недостающие файлы докачаются поверх текущей версии, " +
                "переустанавливать и удалять ничего не нужно.")));

        return findings;
    }

    // Active tunnel/VPN network adapters (WireGuard/OpenVPN-TAP/WARP…), excluding Windows' own IPv6
    /// <summary>Tunnel adapters that belong to somebody else — every one <see cref="VpnAdapters"/> finds
    /// except the named one.
    ///
    /// Used before raising our own WARP tunnel. Two full tunnels at once is not a slow setup, it is a
    /// broken one: WireGuard picks the interface for its own socket from the routing table, and with a
    /// second default route in play it can end up sending from an address that path will not carry — the
    /// handshake then fails for a reason nothing on screen can explain.</summary>
    public static List<string> ForeignTunnelAdapters(string ownAdapterName) =>
        VpnAdapters()
            .Where(n => !string.Equals(n, ownAdapterName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    // transition pseudo-tunnels so they don't raise a false alarm. Yields the adapter name.
    private static IEnumerable<string> VpnAdapters()
    {
        string[] markers = { "tap-windows", "tap-nord", "wireguard", "wintun", "openvpn", "vpn",
                             "warp", "proton", "mullvad", "amnezia", "radmin vpn" };
        string[] ignore = { "isatap", "teredo", "6to4", "loopback", "kernel debug" };

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
            if (ignore.Any(m => desc.Contains(m))) continue;

            bool isVpn = ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                         || markers.Any(m => desc.Contains(m));
            if (isVpn) yield return ni.Name;
        }
    }

    /// <summary>
    /// Explicit user-directed action to terminate every detected DPI-bypass conflict while leaving
    /// GXBRIEL_ZAPRET's own installed winws/winws2 alone.
    /// </summary>
    public static int StopConflictingDpiTools()
    {
        int stopped = 0;
        foreach (var (procName, _) in DpiTools)
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                try
                {
                    if (IsOwnEngineProcess(p))
                        continue;

                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                    stopped++;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        return stopped;
    }

    /// <summary>
    /// Explicit user-directed action to terminate a conflicting process.
    /// </summary>
    public static bool TryKillConflictingProcess(string procName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                try
                {
                    if (IsOwnEngineProcess(p))
                        continue;

                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(1000);
                }
                finally { try { p.Dispose(); } catch { } }
            }
            return true;
        }
        catch { return false; }
    }

    private static bool IsOwnEngineProcess(Process p)
    {
        string name = p.ProcessName;
        if (!name.Equals("winws", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("winws2", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string installedWinws = Path.GetFullPath(AppPaths.WinwsExe);
            string systemRuntimeDir = Path.GetFullPath(AppPaths.SystemRuntimeDir);
            string appDataRoot = Path.GetFullPath(AppPaths.Root);
            string? path = p.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string full = Path.GetFullPath(path);
            return string.Equals(full, installedWinws, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(systemRuntimeDir, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(appDataRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
