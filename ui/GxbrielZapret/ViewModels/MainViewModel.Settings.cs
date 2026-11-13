using GxbrielZapret.Localization;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Views;

namespace GxbrielZapret.ViewModels;

/// <summary>
/// The toggles, the UI scale and the language — everything that is simply a value in settings.json.
/// </summary>
public sealed partial class MainViewModel
{
    // ---- settings toggles (bound to checkboxes) ----------------------------

    public bool AutoUpdateEngine
    {
        get => Settings.AutoUpdateEngine;
        set { Settings.AutoUpdateEngine = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    /// <summary>Show the app's own corner toasts (start/stop, auto-heal).</summary>
    public bool NotificationsEnabled
    {
        get => Settings.NotificationsEnabled;
        set { Settings.NotificationsEnabled = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    /// <summary>Play a soft sound with each toast notification.</summary>
    public bool NotificationSound
    {
        get => Settings.NotificationSound;
        set { Settings.NotificationSound = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    public bool ParseDiscordApps
    {
        get => Settings.ParseDiscordApps;
        set { Settings.ParseDiscordApps = value; _settingsSvc.Save(); OnPropertyChanged(); OnPropertyChanged(nameof(ParserScopeStatus)); }
    }

    public bool ParseRiotApps
    {
        get => Settings.ParseRiotApps;
        set { Settings.ParseRiotApps = value; _settingsSvc.Save(); OnPropertyChanged(); OnPropertyChanged(nameof(ParserScopeStatus)); }
    }

    public bool ParseRockstarApps
    {
        get => Settings.ParseRockstarApps;
        set { Settings.ParseRockstarApps = value; _settingsSvc.Save(); OnPropertyChanged(); OnPropertyChanged(nameof(ParserScopeStatus)); }
    }

    public bool ParseOtherApps
    {
        get => Settings.ParseOtherApps;
        set { Settings.ParseOtherApps = value; _settingsSvc.Save(); OnPropertyChanged(); OnPropertyChanged(nameof(ParserScopeStatus)); }
    }

    // ---- UI scale (DPI-independent zoom; applied by MainWindow via a ScaleTransform) -------
    public double UiScale
    {
        get => Settings.UiScale is >= 1.0 and <= 2.5 ? Settings.UiScale : 1.0;
        set
        {
            double v = Math.Clamp(Math.Round(value, 2), 1.0, 2.5);
            if (Math.Abs(UiScale - v) < 0.001) return;
            Settings.UiScale = v;
            _settingsSvc.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(UiScalePercentText));
            OnPropertyChanged(nameof(Scale100));
            OnPropertyChanged(nameof(Scale125));
            OnPropertyChanged(nameof(Scale150));
            OnPropertyChanged(nameof(Scale175));
            OnPropertyChanged(nameof(Scale200));
        }
    }

    public string UiScalePercentText => $"{(int)Math.Round(UiScale * 100)}%";

    // Discrete scale presets bound to themed chips (mutually exclusive RadioButtons).

    private string _portableStatus = Loc.T("Проверка portable-статуса ещё не выполнялась.");
    public string PortableStatus { get => _portableStatus; private set => SetField(ref _portableStatus, value); }
    public System.Collections.ObjectModel.ObservableCollection<string> ProductHealthNotes { get; } = new();

    private bool _isCheckingProductHealth;
    public bool IsCheckingProductHealth
    {
        get => _isCheckingProductHealth;
        private set
        {
            if (SetField(ref _isCheckingProductHealth, value))
            {
                RunProductHealthCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private async Task RunProductHealthAsync(bool logToJournal = false)
    {
        if (IsCheckingProductHealth) return;
        IsCheckingProductHealth = true;
        try
        {
            PortableStatus = Loc.T("Проверяю установку, runtime, списки и окружение…");
        var productHealth = await Task.Run(() => ProductHealthService.RunStartupMaintenance(Settings));
        ProductHealthNotes.Clear();
        foreach (string note in productHealth.Notes)
            ProductHealthNotes.Add(note);
        PortableStatus = productHealth.Summary;
        if (logToJournal)
        {
            foreach (string note in productHealth.Notes.Take(12))
                AppendLog("[health] " + note);
        }
        Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Проверка продукта завершена."));
        }
        finally
        {
            IsCheckingProductHealth = false;
        }
    }

    public bool Scale100 { get => NearScale(1.0); set { if (value) UiScale = 1.0; } }
    public bool Scale125 { get => NearScale(1.25); set { if (value) UiScale = 1.25; } }
    public bool Scale150 { get => NearScale(1.5); set { if (value) UiScale = 1.5; } }
    public bool Scale175 { get => NearScale(1.75); set { if (value) UiScale = 1.75; } }
    public bool Scale200 { get => NearScale(2.0); set { if (value) UiScale = 2.0; } }
    private bool NearScale(double v) => Math.Abs(UiScale - v) < 0.001;

    public bool AutostartEnabled
    {
        get => Settings.Autostart;
        set
        {
            if (Settings.Autostart == value && AutostartTaskMatchesSetting) return;

            if (!_autostart.IsSupported)
            {
                Settings.Autostart = false;
                _settingsSvc.Save();
                AutostartStatus = Loc.T("Автозапуск доступен только в собранном .exe, а не при запуске через dotnet.");
                OnPropertyChanged();
                Notify?.Invoke("GXBRIEL_ZAPRET", AutostartStatus);
                return;
            }

            bool ok = value ? _autostart.Enable() : _autostart.Disable();
            bool real = _autostart.IsEnabled();
            Settings.Autostart = value && ok && real;
            _settingsSvc.Save();
            OnPropertyChanged();
            RefreshAutostartStatus();

            if (Settings.Autostart == value)
                Notify?.Invoke("GXBRIEL_ZAPRET", value
                    ? Loc.T("Автозапуск включён.")
                    : Loc.T("Автозапуск выключен."));
            else
                ShowErrorModal(
                    Loc.T("Не удалось изменить автозапуск"),
                    Loc.T("Windows не подтвердила создание задачи автозапуска. Проверьте права администратора и планировщик заданий."),
                    Loc.T("Ожидалось: {0}; schtasks вернул: {1}; задача найдена: {2}", value ? "ON" : "OFF", ok ? "OK" : "ERROR", real ? "YES" : "NO"));
        }
    }

    private string _autostartStatus = "";
    public string AutostartStatus { get => _autostartStatus; private set => SetField(ref _autostartStatus, value); }

    private bool AutostartTaskMatchesSetting => !_autostart.IsSupported || _autostart.IsEnabled() == Settings.Autostart;

    private void RefreshAutostartStatus()
    {
        if (!_autostart.IsSupported)
        {
            AutostartStatus = Loc.T("Автозапуск проверяется только у собранного .exe. В dev-запуске через dotnet задача не создаётся.");
            return;
        }

        bool real = _autostart.IsEnabled();
        if (Settings.Autostart != real)
        {
            Settings.Autostart = real;
            _settingsSvc.Save();
            OnPropertyChanged(nameof(AutostartEnabled));
        }

        AutostartStatus = real
            ? Loc.T("Автозапуск установлен в Планировщике заданий Windows. Приложение стартует в трей.")
            : Loc.T("Автозапуск выключен. Задача в Планировщике заданий не установлена.");
    }

    public bool AutostartEngine
    {
        get => Settings.AutostartEngine;
        set { Settings.AutostartEngine = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => Settings.MinimizeToTray;
        set { Settings.MinimizeToTray = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    public bool AutoHeal
    {
        get => Settings.AutoHeal;
        set
        {
            if (Settings.AutoHeal == value) return;
            Settings.AutoHeal = value;
            if (!value) _unexpectedRecovery.Cancel();
            _settingsSvc.Save();
            OnPropertyChanged();
            UpdateMonitor();
            Notify?.Invoke("GXBRIEL_ZAPRET", value
                ? Loc.T("Авто-починка включена.")
                : Loc.T("Авто-починка выключена."));
        }
    }

    /// <summary>Flowseal-style game filter: widen capture to game ports (&gt;1023) when on.
    /// Pushes the value into the engine; a running engine is relaunched so it takes effect now.</summary>
    public bool GameFilter
    {
        get => Settings.GameFilter;
        set
        {
            if (value == Settings.GameFilter) return;
            Settings.GameFilter = value;
            _settingsSvc.Save();
            _engine.GameFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (!IsInitializing && !IsAutoSelecting && !IsTestingSelectedPreset) ReloadPresets();
            if (IsRunning) _ = ApplyEngineOptionsAsync(); // relaunch so the new capture width applies
            Notify?.Invoke("GXBRIEL_ZAPRET", value
                ? Loc.T("Игровой фильтр включён (порты > 1023).")
                : Loc.T("Игровой фильтр выключен (игры работают напрямую)."));
        }
    }

    /// <summary>Optional League of Legends TCP 2099 workaround. Off by default: Riot/LoL usually
    /// should stay excluded from generic desync, and this rule is only for the game-session path.</summary>
    public bool LeagueTcp2099Bypass
    {
        get => Settings.LeagueTcp2099Bypass;
        set
        {
            if (value == Settings.LeagueTcp2099Bypass) return;
            Settings.LeagueTcp2099Bypass = value;
            _settingsSvc.Save();
            _engine.LeagueTcp2099Bypass = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (!IsInitializing && !IsAutoSelecting && !IsTestingSelectedPreset) ReloadPresets();
            if (IsRunning) _ = ApplyEngineOptionsAsync();
            Notify?.Invoke("GXBRIEL_ZAPRET", value
                ? Loc.T("Обход League of Legends TCP 2099 включён.")
                : Loc.T("Обход League of Legends TCP 2099 выключен."));
        }
    }

    /// <summary>Optional workaround for the entitlement-token endpoint. This is intentionally narrower
    /// than a generic Riot auth bypass because most Riot HTTPS endpoints should remain direct.</summary>
    public bool RiotEntitlementsBypass
    {
        get => Settings.RiotEntitlementsBypass;
        set
        {
            if (value == Settings.RiotEntitlementsBypass) return;
            Settings.RiotEntitlementsBypass = value;
            _settingsSvc.Save();
            _engine.RiotEntitlementsBypass = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (!IsInitializing && !IsAutoSelecting && !IsTestingSelectedPreset) ReloadPresets();
            if (value)
            {
                _ = Task.Run(RiotRepairService.EnsureMinimalEntitlementsHelperRunning)
                    .ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion)
                            AppendLog(t.Result.Message);
                    });
            }
            if (IsRunning) _ = ApplyEngineOptionsAsync();
            Notify?.Invoke("GXBRIEL_ZAPRET", value
                ? Loc.T("Обход Riot Entitlements включён.")
                : Loc.T("Обход Riot Entitlements выключен."));
        }
    }

    /// <summary>Optional hosts-based routing optimization for League of Legends and Blitz desktop.</summary>
    public bool GameRoutingHostsFix
    {
        get => Settings.GameRoutingHostsFix;
        set
        {
            if (value == Settings.GameRoutingHostsFix) return;
            Settings.GameRoutingHostsFix = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            if (value)
            {
                if (GxbrielZapret.Services.Platform.RiotRoutingService.ApplyHostsFix())
                    AppendLog(Loc.T("[Riot/Blitz] Маршрутизация через hosts включена (Cloudflare edge fix)."));
                else
                    AppendLog(Loc.T("[Riot/Blitz] Не удалось обновить hosts (требуются права администратора)."));
            }
            else
            {
                GxbrielZapret.Services.Platform.RiotRoutingService.RemoveHostsFix();
                AppendLog(Loc.T("[Riot/Blitz] Маршрутизация через hosts отключена, системный файл очищен."));
            }

            Notify?.Invoke("GXBRIEL_ZAPRET", value
                ? Loc.T("Маршрутизация Riot/Blitz через hosts включена.")
                : Loc.T("Маршрутизация Riot/Blitz через hosts отключена."));
        }
    }

    /// <summary>Bypass every site (catch-all) vs allow-list (default off, like Flowseal). Off keeps
    /// games/apps not in any list untouched. Relaunches a running engine so it takes effect now.
    ///
    /// <para>This is the user's answer, not necessarily the engine's: while the WARP proxy is up the
    /// engine runs wide regardless — see <see cref="EffectiveBypassAllSites"/>. Turning the preference
    /// off in that state records it and leaves the engine alone, because restarting it would change
    /// nothing.</para></summary>
    public bool BypassAllSites
    {
        get => Settings.BypassAllSites;
        set
        {
            if (value == Settings.BypassAllSites) return;
            bool wasEffective = EffectiveBypassAllSites;
            Settings.BypassAllSites = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            if (EffectiveBypassAllSites == wasEffective) return;
            _engine.BypassAllSites = EffectiveBypassAllSites;
            OnPropertyChanged(nameof(CommandPreview));
            if (!IsAutoSelecting && !IsTestingSelectedPreset) ReloadPresets();
            _healthMonitor.Reset();
            if (IsRunning) _ = ApplyEngineOptionsAsync();
            OnPropertyChanged(nameof(StatusScopeText));
            Notify?.Invoke("GXBRIEL_ZAPRET", value
                ? Loc.T("Включён обход всех заблокированных сайтов (Catch-All).")
                : Loc.T("Включён обход только сайтов из списков (YouTube, Discord)."));
        }
    }

    /// <summary>The scope the engine actually runs with: the user's <see cref="BypassAllSites"/>, or
    /// every site while the WARP proxy is up.
    ///
    /// <para>WARP is not a tunnel that swallows the machine's traffic — it is a SOCKS proxy on
    /// loopback, and only the applications pointed at it use it. What needs the bypass is therefore
    /// one connection: usque's own link to Cloudflare's MASQUE entry point. Measured on a censored
    /// Russian ISP, that link stood up only with the scope widened to every site — i.e. only when the
    /// strategy's full pipeline was the one handling it, rather than a narrower profile aimed at those
    /// addresses. So WARP raises the scope for as long as it is on, and the saved preference is left
    /// untouched: a setting silently rewritten by another tab is a setting the user no longer
    /// owns.</para></summary>
    private bool EffectiveBypassAllSites => Settings.BypassAllSites;

    /// <summary>"QUIC off": drop the desynced services' HTTP/3 so the browser falls back to TCP/H2.
    /// Turn on where the ISP/TSPU throttles QUIC. Relaunches a running engine so it takes effect now.</summary>
    public bool DisableQuic
    {
        get => Settings.DisableQuic;
        set
        {
            if (value == Settings.DisableQuic) return;
            Settings.DisableQuic = value;
            _settingsSvc.Save();
            _engine.DisableQuic = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (!IsAutoSelecting && !IsTestingSelectedPreset) ReloadPresets();
            _healthMonitor.Reset();
            if (IsRunning) _ = ApplyEngineOptionsAsync();
            Notify?.Invoke("GXBRIEL_ZAPRET", value
                ? Loc.T("QUIC (HTTP-3) отключён, форсирован TCP.")
                : Loc.T("QUIC (HTTP-3) включён."));
        }
    }

    /// <summary>Verbose engine log (<c>--debug=1</c>) toggled from the Журнал tab. Relaunches a running
    /// engine, because winws2 only reads the flag at startup — without the relaunch the switch would
    /// look like it did nothing until the next manual restart.</summary>
    public bool DebugLog
    {
        get => Settings.DebugLog;
        set
        {
            if (value == Settings.DebugLog) return;
            Settings.DebugLog = value;
            _settingsSvc.Save();
            _engine.DebugLog = value;
            _tgProxy.Verbose = value; // the same switch governs both panes of the Журнал tab
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning)
            {
                AppendLog(value
                    ? Loc.T("Подробный режим включён (--debug=1), перезапуск движка…")
                    : Loc.T("Подробный режим выключен, перезапуск движка…"));
                _ = ApplyEngineOptionsAsync(); // relaunch so the new log level applies
            }
        }
    }

    /// <summary>Let the engine also cover the built-in Telegram proxy's own 443 upstream so its tunnel
    /// survives mobile DPI that corrupts it mid-stream. Off by default — turn on only if the proxy
    /// connects but keeps dropping. Relaunches a running engine so it applies immediately.</summary>
    public bool TgProxyCoverage
    {
        get => Settings.TgProxyCoverage;
        set
        {
            if (value == Settings.TgProxyCoverage) return;
            Settings.TgProxyCoverage = value;
            _settingsSvc.Save();
            _engine.CoverTgProxy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning) _ = ApplyEngineOptionsAsync();
            Notify?.Invoke("GXBRIEL_ZAPRET", value
                ? Loc.T("Защита Telegram-прокси включена.")
                : Loc.T("Защита Telegram-прокси выключена."));
        }
    }

    /// <summary>Run the watchdog only while the engine is up and auto-heal is on.</summary>
    private void UpdateMonitor()
    {
        _monitor.Stop();
        if (IsRunning && Settings.AutoHeal)
        {
            _healthMonitor.StartWatchdog(SelectedAutoScope);
        }
        else
        {
            _monitor.Stop();
            _healthMonitor.StopWatchdog();
        }
    }

    /// <summary>Watchdog tripped: silently re-pick the best strategy and restart.</summary>
    private async Task AutoHealAsync()
    {
        if (!Settings.AutoHeal || IsAutoRunning || IsUpdating) return;
        Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Обход упал — переподбор…"));
        AppendLog(Loc.T("Авто-починка: обход не отвечает, переподбор."));
        await RunAutoSelectAsync(showWindow: false);
        // Re-check: user may have turned AutoHeal off while RunAutoSelectAsync was in-flight.
        if (!Settings.AutoHeal) return;
        if (IsRunning)
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Обход восстановлен."));
    }

    /// <summary>Remember which strategy is running on the CURRENT network (local fingerprint) so it can
    /// be re-suggested next time we're on that network. Computed off the UI thread (ARP can block for a
    /// moment on a cache miss); the settings write is marshaled back. Best-effort — no fingerprint (e.g.
    /// offline) just skips it.</summary>
    private void RememberNetworkStrategy()
    {
        string? name = _engine.ActivePreset?.Name;
        if (string.IsNullOrEmpty(name)) return;
        _ = Task.Run(() =>
        {
            string? fp = NetworkFingerprint.Current();
            if (fp is null) return;
            try
            {
                var profile = NetworkKnowledgeStore.GetOrCreate(fp);
                profile.BestStrategyName = name;
                profile.LastSeen = DateTime.UtcNow;
                NetworkKnowledgeStore.Save(profile);
            }
            catch { }
            OnUi(() =>
            {
                Settings.NetworkStrategies[fp] = name;
                // Cap growth: drop one old entry once the map gets large (rare).
                if (Settings.NetworkStrategies.Count > 40)
                    Settings.NetworkStrategies.Remove(Settings.NetworkStrategies.Keys.First());
                _settingsSvc.Save();
            });
        });
    }

    // ---- UI language (restart-to-apply) -----------------------------------
    // The RU|EN switch on Главная and Настройки binds these two read-only flags (so the active side is
    // filled) and calls SetLanguageCommand on click. Reflects the language THIS run was started in — it
    // only changes after the restart below.

    public bool LanguageIsRussian => !Loc.IsEnglish;
    public bool LanguageIsEnglish => Loc.IsEnglish;

    /// <summary>Switch the UI language. No-op if already active; otherwise confirm (it restarts the app,
    /// stopping any running bypass/proxy), persist, and relaunch — the strings resolve at parse time.</summary>
    private void SetLanguage(string? lang)
    {
        string norm = lang == Loc.English ? Loc.English : Loc.Russian;
        if (Settings.Language == norm) return;

        bool ok = ConfirmDialog.Show(
            Loc.T("Сменить язык интерфейса?"),
            Loc.T("Приложение перезапустится. Если обход или прокси запущены — они остановятся."),
            confirmText: Loc.T("Перезапустить"),
            danger: false);
        if (!ok)
        {
            // The clicked RadioButton toggled itself before the dialog; re-assert the bound state so the
            // active language stays highlighted after a cancel.
            OnUi(() =>
            {
                OnPropertyChanged(nameof(LanguageIsRussian));
                OnPropertyChanged(nameof(LanguageIsEnglish));
            });
            return;
        }

        Settings.Language = norm;
        _settingsSvc.Save();
        App.RelaunchSelf();
    }
}
