using System.IO;
using System.Text.Json;
using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Infrastructure;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; }

    public string? ActivePresetId { get; set; }
    public string? SelectedPresetName { get; set; }
    public List<string> FavoriteStrategyIds { get; set; } = new();
    public AutoScope SelectedStrategyScope { get; set; } = AutoScope.Both;
    public bool FollowLog { get; set; } = true;
    public string? ActivePresetName
    {
        get => ActivePresetId;
        set => ActivePresetId = value;
    }
    public string? ActiveHostlist { get; set; }

    public bool DesiredBypassEnabled { get; set; }
    public bool BypassDesired
    {
        get => DesiredBypassEnabled;
        set => DesiredBypassEnabled = value;
    }

    public string BypassActual { get; set; } = "STOPPED";
    public bool AutoUpdateEngine { get; set; } = false;

    public bool AutostartEnabled { get; set; }
    public bool Autostart
    {
        get => AutostartEnabled;
        set => AutostartEnabled = value;
    }
    public bool AutostartEngine { get; set; }   // also start the engine on launch
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    public bool DesiredTelegramEnabled { get; set; }
    public bool TgProxyAutostart
    {
        get => DesiredTelegramEnabled;
        set => DesiredTelegramEnabled = value;
    }
    public string TelegramActual { get; set; } = "STOPPED";

    public int NetworkWaitTimeoutSeconds { get; set; } = 20;

    /// <summary>Simple (one-click) vs Advanced (full tabs) interface. Defaults to simple for user-friendly out-of-the-box experience.</summary>
    public bool SimpleMode { get; set; } = true;

    /// <summary>UI language: "ru" (default) or "en". Applied once at startup — the switch on
    /// Главная/Настройки is restart-to-apply (the XAML strings resolve at parse time). Only the
    /// interface is translated; engine arguments and lists are unaffected.</summary>
    public string Language { get; set; } = "ru";
    public string AntigravityInstallRoot { get; set; } = "";
    public bool AntigravityMonitorEnabled { get; set; } = true;
    public bool AntigravityAutoPatchEnabled { get; set; } = true;
    public bool AntigravityDirectConnection { get; set; }

    /// <summary>Background watchdog: silently re-pick a strategy if the bypass stops working. Enabled by default for automatic failover.</summary>
    public bool AutoHeal { get; set; } = true;

    /// <summary>Game filter (Flowseal-style): when true, the bypass capture is widened to all high
    /// ports (>1023) so throttled games get desynced too. When false (default), capture stays narrow
    /// (80,443 + Discord voice) so game traffic is left untouched and games run natively.</summary>
    public bool GameFilter { get; set; }

    /// <summary>Targeted League of Legends session bypass from Flowseal community recipes:
    /// only TCP 2099 against ipset-lol, using syndata. Off by default because Riot often works
    /// natively and can break if the client/game traffic is touched unnecessarily.</summary>
    public bool LeagueTcp2099Bypass { get; set; }

    /// <summary>Targeted Riot entitlement-token bypass: only entitlements.auth.riotgames.com on
    /// TCP 443. Off by default because Riot Client/League is very sensitive to TLS desync on auth,
    /// chat and config endpoints; the safest baseline is to leave Riot direct unless explicitly needed.</summary>
    public bool RiotEntitlementsBypass { get; set; }

    /// <summary>Optimised Anycast routing for Riot Games &amp; Blitz via hosts. Redirects throttled
    /// Cloudflare edge IPs to accessible ones. Off by default; cleaned up on exit.</summary>
    public bool GameRoutingHostsFix { get; set; } = true;

    /// <summary>Bypass EVERY site (catch-all) vs allow-list. When false (default), only the explicit
    /// lists (YouTube/Discord) + your custom targets/hostlists are desynced — like Flowseal,
    /// so games/apps not in any list never break. When true, all other TLS/QUIC is desynced too
    /// (kept safe by the exclude list); convenient but may break a game/app that isn't excluded.</summary>
    public bool BypassAllSites { get; set; }

    /// <summary>Drop the desynced services' QUIC (HTTP/3) so the browser falls back to TCP/H2. Turn on
    /// where the ISP/TSPU throttles or drops QUIC (YouTube stutters over HTTP/3 but is fine over TCP).</summary>
    public bool DisableQuic { get; set; } = true;

    /// <summary>Also cover the built-in Telegram proxy's own Cloudflare upstream (443) with the DPI engine,
    /// so its tunnel survives mobile DPI (TSPU) that corrupts it mid-stream. Off by default — most users
    /// don't need it; turn on only if the proxy connects but keeps dropping. Needs the engine running.</summary>
    public bool TgProxyCoverage { get; set; }

    /// <summary>Local port the MASQUE proxy listens on. 1080 is the conventional SOCKS port; it is
    /// offered as a setting because another proxy sitting on it is common rather than exotic.</summary>
    public int MasqueListenPort { get; set; } = 1080;

    /// <summary>The transport that last carried traffic, so the next connection starts where the last one
    /// succeeded instead of walking the whole sweep again.
    ///
    /// <para>Defaults measured on a censored Russian ISP: HTTP/2 over TCP on 443 connected on every
    /// attempt with the bypass running, while QUIC failed on all seven ports. Cloudflare's own client
    /// calls this its «H3 with H2 fallback» — here the fallback is simply the one that works.</para></summary>
    public bool MasqueHttp2 { get; set; } = true;
    public int MasqueConnectPort { get; set; } = 443;

    /// <summary>Send the whole system through the WARP proxy while it runs, by pointing Windows' own
    /// proxy setting at it. Off by default: the proxy is opt-in per application by design, and this
    /// makes it opt-out instead — every browser tab, updater and game goes the long way round through
    /// Cloudflare. Firefox keeps its own proxy setting and is NOT covered.</summary>
    public bool MasqueSystemProxy { get; set; }

    /// <summary>Windows' proxy setting as it was before <see cref="MasqueSystemProxy"/> pointed it at
    /// the local proxy, encoded as <c>enabled|server|override</c>. Empty when nothing is applied.
    /// Persisted rather than held in memory precisely because it has to survive a crash: a registry
    /// setting left pointing at a proxy that is no longer listening is a machine with no browsing, and
    /// the next launch undoes it.</summary>
    public string SystemProxyBackup { get; set; } = "";

    /// <summary>Verbose engine log (<c>--debug=1</c>): winws2 reports per-connection decisions, which is
    /// what you need to see WHY a desync did or didn't fire. Off by default — it is noisy, and the
    /// Журнал buffer is capped, so ordinary startup messages scroll away faster while it is on.</summary>
    public bool DebugLog { get; set; }

    /// <summary>Per-network memory: a local network fingerprint (see <see cref="NetworkFingerprint"/>) →
    /// the last strategy that ran there. Lets the app re-suggest a known-good preset when you return to
    /// a network, instead of the generic default. Keyed locally; no external calls, no IPs stored.</summary>
    public Dictionary<string, string> NetworkStrategies { get; set; } = new();

    /// <summary>Local listen port for the built-in Telegram MTProto→WS proxy (TelegramProxyService).</summary>
    public int TgProxyPort { get; set; } = 1443;

    /// <summary>Persisted MTProto secret (32 hex chars) so the tg:// proxy link stays stable across
    /// runs. Empty on first run; filled in once the proxy is configured/started.</summary>
    public string TgProxySecret { get; set; } = "";

    /// <summary>Last tg:// link opened automatically. Prevents every proxy restart from adding
    /// another identical 127.0.0.1 entry to Telegram Desktop.</summary>
    public string TelegramLastOpenedLink { get; set; } = "";

    /// <summary>Live parser scopes: which application families are sampled before list parsing/merge.</summary>
    public bool ParseDiscordApps { get; set; } = true;
    public bool ParseRiotApps { get; set; } = true;
    public bool ParseRockstarApps { get; set; } = true;
    public bool ParseOtherApps { get; set; }

    /// <summary>App-wide UI zoom (1.0–2.5), applied on TOP of the OS DPI scaling via a ScaleTransform.
    /// Lets the whole interface be enlarged on high-res/4K panels where Windows scaling is set low and
    /// everything looks tiny — independent of the system DPI. 1.0 = no extra zoom.</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>Show the app's own corner toast notifications (start/stop, auto-heal). Off = no popups.</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Play a soft sound with each toast notification. Off = silent toasts.</summary>
    public bool NotificationSound { get; set; } = true;

    /// <summary>Keep Windows Firewall allow-rules for the UI executable and bundled winws2 runtime.
    /// Set after the user clicks "Добавить в исключения"; re-applied on startup because security
    /// suites and Windows profile changes can remove or disable local firewall rules.</summary>
    public bool MaintainFirewallExclusions { get; set; }

    /// <summary>The first-run walkthrough has already been shown. Set when the user closes it; the
    /// walkthrough stays available from Настройки → «Показать вводную».</summary>
    public bool WelcomeShown { get; set; }
}

/// <summary>Loads/saves <see cref="AppSettings"/> as settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const int CurrentSchemaVersion = 6;
    private const int MaxBackups = 3;
    private static readonly object SaveLock = new();

    public AppSettings Settings { get; private set; } = new();
    public string? LastSaveError { get; private set; }
    public event Action<string?>? SaveStatusChanged;

    public SettingsService() => Load();

    public bool Save()
    {
        bool saved;
        lock (SaveLock)
        {
            string? tmp = null;
            try
            {
                AppPaths.EnsureCreated();
                Settings.SchemaVersion = CurrentSchemaVersion;
                RotateBackups(AppPaths.SettingsFile);
                tmp = AppPaths.SettingsFile + $".{Guid.NewGuid():N}.tmp";
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, Settings, JsonOpts);
                    stream.Flush(true);
                }
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
                LastSaveError = null;
                saved = true;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                saved = false;
            }
            finally
            {
                try { if (tmp is not null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        SaveStatusChanged?.Invoke(LastSaveError);
        return saved;
    }

    /// <summary>Reset every preference to its default, then save. A handful of identity/data fields are
    /// carried over rather than wiped: the interface <see cref="AppSettings.Language"/> and
    /// <see cref="AppSettings.SimpleMode"/> view mode (resetting them would yank the live UI), the current
    /// <see cref="AppSettings.ActivePresetName"/>/<see cref="AppSettings.ActiveHostlist"/> selection, the
    /// persisted <see cref="AppSettings.TgProxySecret"/> (so an already-configured tg:// link keeps
    /// working), the <see cref="AppSettings.NetworkStrategies"/> per-network memory and the
    /// <see cref="AppSettings.WelcomeShown"/> flag. The user's saved strategies live in presets.json and
    /// their host lists in lists\ — separate files this never touches.</summary>
    public void ResetToDefaults()
    {
        Settings = BuildReset(Settings);
        Save();
    }

    /// <summary>The reset result for a given current state: a fresh <see cref="AppSettings"/> with the
    /// identity/data fields carried over from <paramref name="current"/> and everything else at its
    /// default. Pure (no I/O) so the preserve-vs-reset split can be unit-tested.</summary>
    internal static AppSettings BuildReset(AppSettings current) => new()
    {
        Language = current.Language,
        SimpleMode = current.SimpleMode,
        ActivePresetName = current.ActivePresetName,
        SelectedPresetName = current.SelectedPresetName,
        FavoriteStrategyIds = new List<string>(current.FavoriteStrategyIds),
        SelectedStrategyScope = current.SelectedStrategyScope,
        ActiveHostlist = current.ActiveHostlist,
        TgProxySecret = current.TgProxySecret,
        TelegramLastOpenedLink = current.TelegramLastOpenedLink,
        TgProxyPort = current.TgProxyPort,
        TgProxyAutostart = current.TgProxyAutostart,
        BypassDesired = current.BypassDesired,
        NetworkStrategies = current.NetworkStrategies,
        MaintainFirewallExclusions = current.MaintainFirewallExclusions,
        WelcomeShown = current.WelcomeShown,
        DisableQuic = current.DisableQuic,
        LeagueTcp2099Bypass = current.LeagueTcp2099Bypass,
        RiotEntitlementsBypass = current.RiotEntitlementsBypass,
        GameRoutingHostsFix = true,
        // Not a preference: it is how Windows' own proxy setting gets put back. Wiping it on a reset
        // would strand the machine on a proxy that is about to stop listening.
        SystemProxyBackup = current.SystemProxyBackup,
    };

    private void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                Settings = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
                if (MigrateInMemory(Settings))
                    Save();
            }
        }
        catch
        {
            AppSettings? backupSettings = TryLoadBackup();
            bool recoveredFromBackup = backupSettings != null;
            Settings = backupSettings ?? new AppSettings();
            bool migrated = MigrateInMemory(Settings);
            try
            {
                string corrupt = Path.Combine(AppPaths.BackupsDir, $"settings-corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
                if (File.Exists(AppPaths.SettingsFile)) File.Move(AppPaths.SettingsFile, corrupt, overwrite: true);
            }
            catch { }

            if (recoveredFromBackup || migrated)
                Save();
        }
    }

    private static bool MigrateInMemory(AppSettings settings)
    {
        bool changed = false;
        if (settings.FavoriteStrategyIds is null)
        {
            settings.FavoriteStrategyIds = new List<string>();
            changed = true;
        }
        if (!Enum.IsDefined(settings.SelectedStrategyScope))
        {
            settings.SelectedStrategyScope = AutoScope.Both;
            changed = true;
        }

        if (settings.SchemaVersion <= 0)
        {
            settings.SchemaVersion = 1;
            changed = true;
        }

        if (settings.SchemaVersion < 2)
        {
            if (!settings.BypassDesired) settings.BypassDesired = settings.AutostartEngine;
            settings.SchemaVersion = 2;
            changed = true;
        }

        if (settings.SchemaVersion < 3)
        {
            settings.RiotEntitlementsBypass = true;
            settings.SchemaVersion = 3;
            changed = true;
        }

        if (settings.SchemaVersion < 4)
        {
            settings.RiotEntitlementsBypass = false;
            settings.SchemaVersion = 4;
            changed = true;
        }

        if (settings.SchemaVersion < 5)
        {
            // Drop QUIC (HTTP/3) by default: on Russian ISPs/TSPU, QUIC to YouTube is dropped/blackholed,
            // which causes Chrome to hang indefinitely on a blank/dark screen waiting for UDP.
            // Dropping QUIC forces browsers to immediately fall back to TCP TLS, where DPI desync succeeds.
            settings.DisableQuic = true;
            settings.SchemaVersion = CurrentSchemaVersion;
            changed = true;
        }

        if (IsLegacyAbsolutePath(settings.ActiveHostlist))
        {
            settings.ActiveHostlist = null;
            changed = true;
        }

        return changed;
    }

    private static bool IsLegacyAbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Path.IsPathFullyQualified(value)) return false;

        string normalized = value.Replace('/', '\\');
        return normalized.Contains("\\profiles\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\SOURCE AND REFERENCE\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\!VS_MY_PROJECT\\", StringComparison.OrdinalIgnoreCase);
    }

    private static AppSettings? TryLoadBackup()
    {
        try
        {
            for (int i = 1; i <= MaxBackups; i++)
            {
                string path = BackupPath(AppPaths.SettingsFile, i);
                if (!File.Exists(path)) continue;
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts);
                if (loaded is not null) return loaded;
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
}
