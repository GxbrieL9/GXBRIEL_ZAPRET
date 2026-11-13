using System.Diagnostics;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Engine.Discovery;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Services.Telegram;

namespace GxbrielZapret.Services.Infrastructure;

public sealed record RecoveryResult(
    bool NetworkReady,
    bool BypassDesired,
    bool BypassRestored,
    string? BypassPresetName,
    bool TelegramDesired,
    bool TelegramRestored,
    string? ErrorMessage = null,
    TimeSpan Elapsed = default
);

/// <summary>
/// Deterministic startup and post-reboot recovery coordinator.
/// Orchestrates runtime bootstrapping, network readiness gating,
/// independent bypass and Telegram restoration, and health verification.
/// </summary>
public sealed class RecoveryCoordinator
{
    private readonly SettingsService _settingsService;
    private readonly EngineService _engineService;
    private readonly TelegramProxyService _tgProxyService;
    private readonly DiscoveryStore _discoveryStore;
    private readonly AutostartService _autostartService;
    private readonly Action<Preset, string?> _startEngine;
    private readonly Action<bool> _syncAutostart;

    public RecoveryCoordinator(
        SettingsService settingsService,
        EngineService engineService,
        TelegramProxyService tgProxyService,
        DiscoveryStore? discoveryStore = null,
        AutostartService? autostartService = null,
        Action<Preset, string?>? startEngine = null,
        Action<bool>? syncAutostart = null)
    {
        _settingsService = settingsService;
        _engineService = engineService;
        _tgProxyService = tgProxyService;
        _discoveryStore = discoveryStore ?? new DiscoveryStore();
        _autostartService = autostartService ?? new AutostartService();
        _startEngine = startEngine ?? engineService.Start;
        _syncAutostart = syncAutostart ?? (enabled => { _autostartService.SyncAutostart(enabled); });
    }

    public static bool ShouldRestoreBypass(AppSettings settings) =>
        settings.DesiredBypassEnabled || settings.AutostartEngine;

    /// <summary>
    /// Executes the complete deterministic recovery flow.
    /// </summary>
    public async Task<RecoveryResult> ExecuteRecoveryAsync(
        IReadOnlyList<Preset> availablePresets,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();
        var settings = _settingsService.Settings;

        // 1. Bootstrap and self-heal runtime lists from persistent discovery + base lists
        try
        {
            _discoveryStore.SelfHealAllRuntimeLists();
        }
        catch (Exception ex)
        {
            progress?.Report(Loc.T("Предупреждение: ошибка самовосстановления списков: {0}", ex.Message));
        }

        bool bypassDesired = ShouldRestoreBypass(settings);
        // An explicit startup preference becomes the desired state for this session too.
        if (bypassDesired) settings.DesiredBypassEnabled = true;
        bool tgDesired = settings.DesiredTelegramEnabled;

        // If neither service is desired, sync autostart and exit cleanly
        if (!bypassDesired && !tgDesired)
        {
            _syncAutostart(settings.AutostartEnabled);
            settings.BypassActual = "STOPPED";
            settings.TelegramActual = "STOPPED";
            _settingsService.Save();
            return new RecoveryResult(
                NetworkReady: true,
                BypassDesired: false,
                BypassRestored: false,
                BypassPresetName: null,
                TelegramDesired: false,
                TelegramRestored: false,
                Elapsed: sw.Elapsed);
        }

        // 2. Network Readiness Gate with polling and backoff
        progress?.Report(Loc.T("Ожидание готовности сети…"));
        var networkTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.NetworkWaitTimeoutSeconds, 5, 60));
        bool networkReady = await NetworkReadinessGate.WaitForNetworkReadyAsync(
            networkTimeout,
            initialPollInterval: TimeSpan.FromMilliseconds(400),
            progress: progress,
            ct: ct);

        if (!networkReady)
        {
            progress?.Report(Loc.T("Сеть не подтверждена вовремя. Попытка восстановления с текущим состоянием стека..."));
        }

        bool bypassRestored = false;
        string? activePresetName = null;
        string? lastError = null;

        // 3. Restore Bypass Engine if desired
        if (bypassDesired)
        {
            progress?.Report(Loc.T("Восстановление обхода DPI…"));
            var targetPreset = ResolvePresetToRestore(settings, availablePresets);
            if (targetPreset != null)
            {
                activePresetName = targetPreset.Name;
                string? hostlist = settings.ActiveHostlist;

                const int maxAttempts = 2;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        if (_engineService.IsRunning)
                        {
                            bypassRestored = true;
                            break;
                        }

                        ct.ThrowIfCancellationRequested();
                        await Task.Run(() => _startEngine(targetPreset, hostlist), ct);

                        // Health check: verify process actually launched and PID is valid
                        if (_engineService.IsRunning && _engineService.ActiveProcessId.HasValue)
                        {
                            bypassRestored = true;
                            settings.BypassActual = "RUNNING";
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        if (attempt < maxAttempts)
                        {
                            await Task.Delay(500, ct);
                        }
                    }
                }

                if (!bypassRestored)
                {
                    settings.BypassActual = "ERROR";
                    progress?.Report(Loc.T("Ошибка восстановления обхода: {0}", lastError ?? "winws2 не запущен"));
                }
            }
            else
            {
                lastError = "Пресет для восстановления не найден в каталоге";
                settings.BypassActual = "ERROR";
                progress?.Report(Loc.T(lastError));
            }
        }
        else
        {
            settings.BypassActual = "STOPPED";
        }

        // 4. Restore Telegram Proxy if desired
        bool tgRestored = false;
        if (tgDesired)
        {
            progress?.Report(Loc.T("Восстановление Telegram…"));
            try
            {
                if (!_tgProxyService.IsRunning)
                {
                    _tgProxyService.Configure(settings.TgProxyPort, settings.TgProxySecret);
                    if (_tgProxyService.Start())
                    {
                        tgRestored = _tgProxyService.IsRunning;
                        settings.TelegramActual = tgRestored ? "RUNNING" : "ERROR";
                    }
                    else
                    {
                        settings.TelegramActual = "ERROR";
                    }
                }
                else
                {
                    tgRestored = true;
                    settings.TelegramActual = "RUNNING";
                }
            }
            catch (Exception ex)
            {
                settings.TelegramActual = "ERROR";
                lastError = (lastError != null ? lastError + " | " : "") + ex.Message;
            }
        }
        else
        {
            settings.TelegramActual = "STOPPED";
        }

        // 5. Persist actual status without destroying desired intent
        _settingsService.Save();

        // 6. Sync autostart task self-healing
        _syncAutostart(settings.AutostartEnabled);

        progress?.Report(Loc.T("Восстановление завершено (обход: {0}, tg: {1})",
            bypassRestored ? Loc.T("работает") : Loc.T("выключен"),
            tgRestored ? Loc.T("работает") : Loc.T("выключен")));

        return new RecoveryResult(
            NetworkReady: networkReady,
            BypassDesired: bypassDesired,
            BypassRestored: bypassRestored,
            BypassPresetName: activePresetName,
            TelegramDesired: tgDesired,
            TelegramRestored: tgRestored,
            ErrorMessage: lastError,
            Elapsed: sw.Elapsed);
    }

    private static Preset? ResolvePresetToRestore(AppSettings settings, IReadOnlyList<Preset> availablePresets)
    {
        if (availablePresets.Count == 0) return null;

        // 1. Explicitly saved ActivePresetId/ActivePresetName
        if (!string.IsNullOrWhiteSpace(settings.ActivePresetId))
        {
            var match = availablePresets.FirstOrDefault(p =>
                p.Name.Equals(settings.ActivePresetId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(p.ReferenceId) && p.ReferenceId.Equals(settings.ActivePresetId, StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match;
        }

        // 2. EngineStateStore fallback
        var stateRecord = EngineStateStore.Load();
        if (!string.IsNullOrWhiteSpace(stateRecord?.ActivePresetName))
        {
            var match = availablePresets.FirstOrDefault(p =>
                p.Name.Equals(stateRecord.ActivePresetName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(p.ReferenceId) && p.ReferenceId.Equals(stateRecord.ActivePresetName, StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match;
        }

        // 3. First available preset in catalog
        return availablePresets.FirstOrDefault();
    }
}
