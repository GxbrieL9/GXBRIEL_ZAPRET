using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Mvvm;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Engine.Discovery;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Services.Strategies;
using GxbrielZapret.Services.Telegram;

namespace GxbrielZapret.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 3000;

    private readonly UpdaterService _updater = new();
    private readonly EngineService _engine = new();
    private readonly PresetService _presets = new();
    private readonly HostlistService _hostlists = new();
    private readonly SettingsService _settingsSvc = new();
    private readonly AutostartService _autostart = new();
    private readonly DiagnosticsService _diag = new();
    private readonly AutoSelectService _autoSelect = new();
    private readonly StrategyGeneratorService _generator = new();
    private readonly TargetService _targets = new();
    private readonly IpsetService _ipset = new();
    private readonly LiveCaptureService _liveCapture = new();
    private readonly DiscoveryService _discovery = new();
    private readonly DiscoveryReapplyCoordinator _reapplyCoordinator;
    private readonly WebsiteLearningService _webLearning = new();
    private readonly ExclusionService _exclusions = new();
    private readonly BackupService _backup = new();
    private readonly MonitorService _monitor = new();
    private readonly ServiceHealthMonitor _healthMonitor = new();
    private readonly DynamicDiscoveryEngine _dynamicDiscovery = new();
    private readonly Blockcheck2Runner _blockcheck2 = new();
    private volatile bool _isShuttingDown;
    private readonly UnexpectedEngineRecovery _unexpectedRecovery;
    private readonly TelegramProxyService _tgProxy = new();
    private CancellationTokenSource? _diagCts;
    private CancellationTokenSource? _dpiCts;
    private CancellationTokenSource? _autoCts;
    private readonly SemaphoreSlim _engineTransitionGate = new(1, 1);
    private readonly Func<Task> _initializer;

    /// <summary>Raised to show a soft tray notification (title, message).</summary>
    public event Action<string, string>? Notify;

    /// <summary>Raised when a user-initiated auto-select starts — the View shows the popup.</summary>
    public event Action? AutoCheckStarted;
    /// <summary>Raised when the auto-select finishes/cancels — the View closes the popup.</summary>
    public event Action? AutoCheckFinished;

    public AppSettings Settings => _settingsSvc.Settings;

    public MainViewModel()
    {
        _initializer = InitializeCoreAsync;
        _unexpectedRecovery = new UnexpectedEngineRecovery(
            () => !_isShuttingDown && Settings.AutoHeal && Settings.DesiredBypassEnabled,
            RecoverCrashedEngineOnUiAsync,
            message => OnUi(() => { if (!_isShuttingDown) { AppendLog(message); SimpleStatus = message; } }));
        _unexpectedRecovery.StateChanged += () =>
        {
            if (!_isShuttingDown) OnUi(OnUnexpectedRecoveryStateChanged);
        };
        _engine.UnexpectedExit += exit => OnUi(() =>
        {
            _crashedPreset = exit.Preset;
            _crashedHostlist = exit.HostlistPath;
            _ = _unexpectedRecovery.NotifyUnexpectedExitAsync();
        });
        _reapplyCoordinator = new DiscoveryReapplyCoordinator(_engine, _discovery.Store);
        _reapplyCoordinator.ReapplyTriggered += reason =>
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Список обновлён: {0}", reason));

        _engine.StateChanged += _ => OnUi(() =>
        {
            State = _engine.State;
            OnPropertyChanged(nameof(StatusActiveStrategyText));
        });
        _engine.LogLine += AppendLog;
        _tgProxy.LogLine += AppendProxyLog; // proxy output goes to its own log/tab, separate from the engine
        _tgProxy.StateChanged += () => OnUi(OnTelegramProxyStateChanged);

        _healthMonitor.HealthChanged += snap => OnUi(() => OnHealthSnapshotReceived(snap));
        _healthMonitor.HealthDegraded += () => OnUi(OnWatchdogDegradedTriggered);

        StartCommand = new RelayCommand(async _ => await StartAsync(), _ => CanStart);
        RetrySaveSettingsCommand = new RelayCommand(_ => { _settingsSvc.Save(); }, _ => HasUnsavedSettings);
        StopCommand = new RelayCommand(async _ => await StopEngineSmoothAsync(), _ => CanStop);
        ToggleCommand = new RelayCommand(async _ => { if (IsRunning || IsRecoveringEngine) await StopEngineSmoothAsync(); else await StartAsync(); },
                                         _ => !IsDownloadingEngine && !IsTransitioning && (IsRunning || IsRecoveringEngine || CanStart));
        CheckUpdateCommand = new RelayCommand(async _ => await CheckAndUpdateAsync(silent: false),
                                              _ => !IsUpdating);
        ClearLogCommand = new RelayCommand(_ => { LogLines.Clear(); Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Журнал очищен.")); });
        ClearProxyLogCommand = new RelayCommand(_ => ProxyLogLines.Clear());
        CopyLogCommand = new RelayCommand(_ => CopyLinesToClipboard(LogView?.Cast<string>() ?? Array.Empty<string>(), Loc.T("Журнал движка")));
        CopyProxyLogCommand = new RelayCommand(_ => CopyLinesToClipboard(ProxyLogLines, Loc.T("Журнал Telegram")));
        // Generic "open a URL or in-app help section" — handles '#' or local docs gracefully instead of silently failing.
        OpenLinkCommand = new RelayCommand(p => HandleOpenLink(p as string));
        CloseErrorModalCommand = new RelayCommand(_ => ShowErrorDialog = false);
        ErrorModalActionCommand = new RelayCommand(_ => ExecuteErrorModalAction());
        CopyErrorDetailsCommand = new RelayCommand(_ =>
        {
            if (!string.IsNullOrWhiteSpace(ErrorDialogDetails))
            {
                try { Clipboard.SetText(ErrorDialogDetails); Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Технические подробности скопированы.")); }
                catch { }
            }
        });

        NewHostlistCommand = new RelayCommand(_ => NewHostlist());
        DeleteHostlistCommand = new RelayCommand(_ => DeleteHostlist(), _ => SelectedHostlist is not null);
        SaveHostlistCommand = new RelayCommand(_ => SaveHostlist(), _ => SelectedHostlist is not null);
        AddDomainCommand = new RelayCommand(_ => AddDomain(), _ => SelectedHostlist is not null);

        ApplyStrategyCommand = new RelayCommand(async _ => await ApplyStrategyAsync(),
                                                _ => CanApplyStrategy);
        DuplicatePresetCommand = new RelayCommand(_ => DuplicatePreset(), _ => SelectedPreset is not null);
        DeletePresetCommand = new RelayCommand(_ => DeletePreset(),
                                               _ => SelectedPresetEditable);
        SavePresetCommand = new RelayCommand(_ => SavePreset(),
                                             _ => SelectedPresetEditable);
        ImportArchiveCommand = new RelayCommand(async _ => await ImportPresetsFromArchiveAsync(),
                                                _ => !IsImportingPresets);
        TestSelectedPresetCommand = new RelayCommand(async _ => await TestSelectedPresetAsync(),
                                                    _ => CanTestSelectedPreset);

        SimpleToggleCommand = new RelayCommand(_ => SimpleToggle(),
            _ => !IsDownloadingEngine && !IsAutoSelecting && !IsTransitioning && (IsRunning || CanStart));
        SetSimpleModeCommand = new RelayCommand(_ => IsSimpleMode = true);
        SetAdvancedModeCommand = new RelayCommand(_ => IsSimpleMode = false);
        SetLanguageCommand = new RelayCommand(p => SetLanguage(p as string));
        GoToHomeCommand = new RelayCommand(_ => SelectedTabIndex = 0);
        GoToSettingsCommand = new RelayCommand(_ => { SelectedTabIndex = SettingsTabIndex; });
        GoToDiagnosticsCommand = new RelayCommand(_ => { SelectedTabIndex = 3; });
        GoToStrategiesCommand = new RelayCommand(_ => { SelectedTabIndex = 1; });
        GoToHostlistsCommand = new RelayCommand(_ => { IsSimpleMode = false; SelectedTabIndex = 2; });
        GoToJournalCommand = new RelayCommand(_ => { IsSimpleMode = false; SelectedTabIndex = 4; });
        GoToPatcherCommand = new RelayCommand(_ => { SelectedTabIndex = PatcherTabIndex; _ = RefreshPatcherStatusAsync(); });
        InitializePatcherCommands();
        GoToHelpCommand = new RelayCommand(_ => ShowHowItWorks = true);
        GoToWarpTabCommand = new RelayCommand(_ =>
        {
            ShowHowItWorks = true;
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Раздел WARP пока доступен только в инструкции."));
        });
        // Single big button on the Home screen — starts/stops the currently selected preset.
        HomeToggleCommand = ToggleCommand;

        RunDiagnosticsCommand = new RelayCommand(async _ => await RunDiagnosticsAsync(), _ => !IsDiagnosing && !IsAutoRunning);
        StopDiagnosticsCommand = new RelayCommand(_ => _diagCts?.Cancel(), _ => IsDiagnosing);
        RunDpiCheckCommand = new RelayCommand(async _ => await RunDpiCheckAsync(),
            _ => !IsDpiChecking && !IsDiagnosing && !IsAutoRunning);
        StopDpiCheckCommand = new RelayCommand(_ => _dpiCts?.Cancel(), _ => IsDpiChecking);
        RunAutoSelectCommand = new RelayCommand(async _ => await RunAutoSelectAsync(),
            _ => !IsInitializing && !IsTestingSelectedPreset && !IsTransitioning && !IsAutoSelecting && !IsDiagnosing && !IsDownloadingEngine && _updater.IsEngineInstalled);
        RecheckWorkPoolCommand = new RelayCommand(async _ => await RecheckWorkPoolAsync(),
            _ => !IsInitializing && !IsTestingSelectedPreset && !IsTransitioning && !IsAutoSelecting && !IsDiagnosing && !IsDownloadingEngine && _updater.IsEngineInstalled && WorkPresetCount > 0);
        FullVerifyProfilesCommand = new RelayCommand(async _ => await RunFullScanAsync(stopOnFirstPerfect: false),
            _ => !IsInitializing && !IsTestingSelectedPreset && !IsTransitioning && !IsAutoSelecting && !IsDiagnosing && !IsDownloadingEngine && !IsProfileVerifying);
        QuickScanCommand = new RelayCommand(async _ => await RunFullScanAsync(stopOnFirstPerfect: true),
            _ => !IsInitializing && !IsTestingSelectedPreset && !IsTransitioning && !IsAutoSelecting && !IsDiagnosing && !IsDownloadingEngine && _updater.IsEngineInstalled);
        Blockcheck2Command = new RelayCommand(async _ => await RunBlockcheck2Async(),
            _ => !IsInitializing && !IsTestingSelectedPreset && !IsTransitioning && !IsAutoSelecting && !IsDiagnosing && !IsDownloadingEngine && !IsProfileVerifying);
        StopAutoSelectCommand = new RelayCommand(_ => { _autoCts?.Cancel(); },
            _ => IsAutoSelecting);
        GenerateStrategyCommand = new RelayCommand(async _ => await RunFullScanAsync(),
            _ => !IsAutoSelecting && !IsDiagnosing && !IsDownloadingEngine && _updater.IsEngineInstalled);
        ApplyScoreCommand = new RelayCommand(p => ApplyScore(p as AutoScore),
            p => p is AutoScore);
        ApplyScoreAndStartCommand = new RelayCommand(async p => await ApplyScoreAndStartAsync(p as AutoScore),
            p => p is AutoScore && !IsAutoRunning);
        BuildDiscordIpsetCommand = new RelayCommand(async _ => await BuildIpsetAsync(),
            _ => !IsBuildingIpset);
        UpdateAllListsCommand = new RelayCommand(async _ => await UpdateAllListsAsync(),
            _ => !IsBuildingIpset);
        StopListUpdateCommand = new RelayCommand(_ => _listUpdateCts?.Cancel(),
            _ => IsBuildingIpset);
        LiveTraceCommand = new RelayCommand(async _ => await RunLiveTraceAsync(30),
            _ => !IsBuildingIpset);
        ApplyExclusionsCommand = new RelayCommand(async _ => await ApplyExclusionsAsync(),
            _ => !IsApplyingExclusions);
        ResetSettingsCommand = new RelayCommand(async _ => await FactoryResetAsync());
        ExportBackupCommand = new RelayCommand(_ => ExportBackup());
        ImportBackupCommand = new RelayCommand(_ => ImportBackup());
        ClearLogFilesCommand = new RelayCommand(_ => ClearLogFiles());
        RunProductHealthCommand = new RelayCommand(async _ => await RunProductHealthAsync(), _ => !IsCheckingProductHealth);
        CopyHealthReportCommand = new RelayCommand(_ => CopyHealthReport());
        OpenDataFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.AppDataRoot));
        OpenEngineFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.EngineDir));
        OpenLogsFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.LogsDir));
        TogglePresetArgsCommand = new RelayCommand(_ => ShowPresetArgs = !ShowPresetArgs);
        ToggleFavoritePresetCommand = new RelayCommand(p => ToggleFavoritePreset(p as Preset), p => p is Preset);
        ClearStrategySearchCommand = new RelayCommand(_ => { StrategySearchText = ""; ShowFavoritePresetsOnly = false; });
        OpenHowItWorksCommand = new RelayCommand(_ => ShowHowItWorks = true);
        CloseHowItWorksCommand = new RelayCommand(_ => ShowHowItWorks = false);
        // Reopened from Настройки → no countdown; the first-run path passes true (MainWindow.OnLoaded).
        OpenWelcomeCommand = new RelayCommand(_ => OpenWelcome(withCountdown: false));
        CloseWelcomeCommand = new RelayCommand(_ => MarkWelcomeSeen(), _ => WelcomeCountdown == 0);
        // "Подробнее" in the walkthrough → hand off to the full reference modal.
        WelcomeToHowItWorksCommand = new RelayCommand(_ => { MarkWelcomeSeen(); ShowHowItWorks = true; });
        // Telegram card → built-in tg-ws-proxy (needs no winws2 engine and no admin rights).
        EnableTelegramCommand = new RelayCommand(_ => ToggleTelegramProxy());
        OpenTelegramProxyLinkCommand = new RelayCommand(_ =>
        {
            OpenUrl(_tgProxy.ProxyLink);
            _tgProxy.IsReconfigureRequired = false;
            RefreshTelegramProxyStatus();
        });
        CopyTelegramProxyLinkCommand = new RelayCommand(_ => CopyToClipboard(_tgProxy.ProxyLink));
        CheckTelegramProxyCommand = new RelayCommand(async _ => await CheckTelegramProxyAsync(),
                                                     _ => !_isCheckingTgProxy);
        OpenAppReleaseCommand = new RelayCommand(async _ => await InstallAppUpdateAsync(), _ => !IsAppUpdating && AppUpdateAvailable);
        CheckAppUpdatesCommand = new RelayCommand(async _ => await CheckAppUpdateAsync(), _ => !IsAppUpdating);

        // ---- custom targets (Диагностика tab) ----
        AddTargetCommand = new RelayCommand(_ => OpenTargetPopup(null));
        OpenTargetCommand = new RelayCommand(p => OpenTargetPopup(p as CustomTarget));
        DeleteTargetCommand = new RelayCommand(p => DeleteTarget(p as CustomTarget));
        ExpandTargetCommand = new RelayCommand(async _ => await ExpandTargetAsync(), _ => !IsExpandingTarget);
        SaveTargetCommand = new RelayCommand(_ => SaveTarget(), _ => !IsExpandingTarget);
        CloseTargetPopupCommand = new RelayCommand(_ => ShowTargetPopup = false);

        // Group the presets list into "Авторские (встроенные)" vs "Личные" (IsBuiltIn).
        PresetsView = CollectionViewSource.GetDefaultView(Presets);
        PresetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Preset.GroupTitle)));
        _selectedAutoScope = Settings.SelectedStrategyScope;
        ResetAutoTargetsForScope();
        LogView = CollectionViewSource.GetDefaultView(LogLines);
        LogView.Filter = LogMatches;
        LogView.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVisibleLogLines));
        // The normal Strategies screen is the stable 39-entry catalog. Imported and
        // generated profiles remain available to scans and persistence, but do not
        // silently expand the everyday list to hundreds of near-duplicates.
        FilterMode = PresetFilterMode.Catalog;

        RebuildDiagRows();
        ReloadTargets();
        InitializeDiscovery();

        _diag.Status += s => OnUi(() => DiagStatusText = s);
        _autoSelect.Status += s => OnUi(() => SetAutoStatus(s));
        _autoSelect.CandidateStarted += name => OnUi(() => MarkCandidateRunning(name));
        _autoSelect.HostProbed += (host, t12, t13, https) => OnUi(() => OnHostProbed(host, t12 == DiagStatus.Ok || https == DiagStatus.Ok));
        _monitor.ConnectivityLost += () => OnUi(() => _ = AutoHealAsync());
        _monitor.Tick += ok => OnUi(() => OnMonitorTick(ok));

        _settingsSvc.SaveStatusChanged += _ => OnUi(OnSettingsSaveStatusChanged);
        _tgProxy.Configure(Settings.TgProxyPort, Settings.TgProxySecret);
        if (Settings.TgProxyAutostart) StartTelegramProxy(autoOpenClient: false);
        RefreshTelegramProxyStatus();
    }

    internal MainViewModel(Func<Task> initializer) : this() => _initializer = initializer;

    // ---- collections -------------------------------------------------------

    public ObservableCollection<Preset> Presets { get; } = new();
    /// <summary>Grouped view of <see cref="Presets"/> (Авторские/встроенные vs Личные) for the list.</summary>
    public ICollectionView PresetsView { get; }
    public ObservableCollection<string> Hostlists { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ICollectionView LogView { get; }
    /// <summary>Telegram-proxy output — kept separate from the engine log so the Журнал tab can show
    /// each on its own sub-tab (engine vs proxy) instead of one interleaved stream.</summary>
    public ObservableCollection<string> ProxyLogLines { get; } = new();
    public ObservableCollection<DiagRow> DiagRows { get; } = new();

    /// <summary>User-defined bypass targets, shown on the Диагностика tab (left column).</summary>
    public ObservableCollection<CustomTarget> CustomTargets { get; } = new();

    /// <summary>Live per-candidate rows, shown in the popup while a run is in progress.</summary>
    public ObservableCollection<AutoCandidateRow> AutoCandidates { get; } = new();

    /// <summary>Goal endpoints of the current run, shown as chips in the popup.</summary>
    public ObservableCollection<string> AutoTargets { get; } = new();

    /// <summary>The current candidate's per-target probe state (popup main area).</summary>
    public ObservableCollection<CheckTargetRow> CheckTargets { get; } = new();

    /// <summary>Strategies that passed (Ok &gt; 0), accumulating live (popup right panel).</summary>
    public ObservableCollection<AutoScore> PassedStrategies { get; } = new();

    /// <summary>Per-service «РАБОТАЕТ / ЧАСТИЧНО / НЕ РАБОТАЕТ» verdict for the CHOSEN strategy, shown
    /// as big chips when a run finishes — the visible validation (built from the real HTTP/2 reach
    /// of the applied strategy + the no-bypass baseline).</summary>
    public ObservableCollection<ServiceVerdict> Verdicts { get; } = new();

    /// <summary>No-bypass reachability of the goal hosts, measured once at the start of a run
    /// (host → reachable). Lets the verdict say "разблокировано обходом" vs "и так работал".</summary>
    private Dictionary<string, bool> _baseline = new(StringComparer.OrdinalIgnoreCase);

    // ---- commands ----------------------------------------------------------

    public RelayCommand StartCommand { get; }
    public RelayCommand RetrySaveSettingsCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ClearProxyLogCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public RelayCommand ToggleFavoritePresetCommand { get; }
    public RelayCommand ClearStrategySearchCommand { get; }
    public RelayCommand CopyProxyLogCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand NewHostlistCommand { get; }
    public RelayCommand DeleteHostlistCommand { get; }
    public RelayCommand SaveHostlistCommand { get; }
    public RelayCommand AddDomainCommand { get; }
    public RelayCommand ApplyStrategyCommand { get; }
    public RelayCommand DuplicatePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand ImportArchiveCommand { get; }
    public RelayCommand TestSelectedPresetCommand { get; }
    public RelayCommand SimpleToggleCommand { get; }
    public RelayCommand SetSimpleModeCommand { get; }
    public RelayCommand SetAdvancedModeCommand { get; }
    public RelayCommand SetLanguageCommand { get; }
    public RelayCommand GoToHomeCommand { get; }
    public RelayCommand GoToSettingsCommand { get; }
    public RelayCommand GoToDiagnosticsCommand { get; }
    public RelayCommand GoToStrategiesCommand { get; }
    public RelayCommand GoToHostlistsCommand { get; }
    public RelayCommand GoToJournalCommand { get; }
    public RelayCommand GoToPatcherCommand { get; }
    public RelayCommand GoToHelpCommand { get; }
    public RelayCommand RunDiagnosticsCommand { get; }
    public RelayCommand StopDiagnosticsCommand { get; }
    public RelayCommand RunDpiCheckCommand { get; }
    public RelayCommand StopDpiCheckCommand { get; }
    public RelayCommand RunAutoSelectCommand { get; }
    public RelayCommand RecheckWorkPoolCommand { get; }
    public RelayCommand FullVerifyProfilesCommand { get; }
    public RelayCommand QuickScanCommand { get; }
    public RelayCommand Blockcheck2Command { get; }
    public RelayCommand StopAutoSelectCommand { get; }
    public RelayCommand GenerateStrategyCommand { get; }
    public RelayCommand ApplyScoreCommand { get; }
    public RelayCommand ApplyScoreAndStartCommand { get; }
    public RelayCommand BuildDiscordIpsetCommand { get; }
    public RelayCommand UpdateAllListsCommand { get; }
    public RelayCommand StopListUpdateCommand { get; }
    public RelayCommand LiveTraceCommand { get; }
    public RelayCommand ApplyExclusionsCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand ExportBackupCommand { get; }
    public RelayCommand ImportBackupCommand { get; }
    public RelayCommand ClearLogFilesCommand { get; }
    public RelayCommand RunProductHealthCommand { get; }
    public RelayCommand CopyHealthReportCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenEngineFolderCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand TogglePresetArgsCommand { get; }
    public RelayCommand OpenHowItWorksCommand { get; }
    public RelayCommand CloseHowItWorksCommand { get; }
    public RelayCommand OpenWelcomeCommand { get; }
    public RelayCommand CloseWelcomeCommand { get; }
    public RelayCommand WelcomeToHowItWorksCommand { get; }
    public RelayCommand CloseErrorModalCommand { get; }
    public RelayCommand ErrorModalActionCommand { get; }
    public RelayCommand CopyErrorDetailsCommand { get; }
    public RelayCommand EnableTelegramCommand { get; }
    public RelayCommand OpenTelegramProxyLinkCommand { get; }
    public RelayCommand CopyTelegramProxyLinkCommand { get; }
    public RelayCommand CheckTelegramProxyCommand { get; }
    public RelayCommand OpenAppReleaseCommand { get; }

    /// <summary>Jump from the Home WARP card to the WARP tab (leaves Simple mode, which hides tabs).</summary>
    public RelayCommand GoToWarpTabCommand { get; }
    public RelayCommand HomeToggleCommand { get; }
    public RelayCommand AddTargetCommand { get; }
    public RelayCommand OpenTargetCommand { get; }
    public RelayCommand DeleteTargetCommand { get; }
    public RelayCommand ExpandTargetCommand { get; }
    public RelayCommand SaveTargetCommand { get; }
    public RelayCommand CloseTargetPopupCommand { get; }

    // ---- status bar & toast notifications ---------------------------------
    private string _statusMessage = Loc.T("Готов к работе");
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public string StatusActiveStrategyText =>
        IsRunning
            ? Loc.T(RunningPresetName)
            : SelectedPreset is not null
            ? Loc.T(SelectedPreset.Name)
            : Loc.T("Не выбрана");

    public string StatusScopeText =>
        Settings.BypassAllSites
            ? Loc.T("Все сайты (Wide)")
            : SelectedAutoScope switch
            {
                AutoScope.Discord => Loc.T("Только Discord"),
                AutoScope.YouTube => Loc.T("Только YouTube"),
                _ => Loc.T("Discord + YouTube")
            };


    private bool _isScanningEnvironment;
    public bool IsScanningEnvironment
    {
        get => _isScanningEnvironment;
        set => SetField(ref _isScanningEnvironment, value);
    }

    // ---- lifecycle ---------------------------------------------------------

    public bool IsInitialized { get; private set; }
    private Task? _initializationTask;
    private bool _isInitializing;
    public bool IsInitializing
    {
        get => _isInitializing;
        private set { if (SetField(ref _isInitializing, value)) RaiseCommandStates(); }
    }

    public async Task InitializeAsync()
    {
        if (IsInitialized) return;
        if (_initializationTask is null || _initializationTask.IsCompleted)
            _initializationTask = InitializeTrackedAsync();
        await _initializationTask;
    }

    private async Task InitializeTrackedAsync()
    {
        IsInitializing = true;
        try
        {
            await _initializer();
            IsInitialized = true;
            OnPropertyChanged(nameof(IsInitialized));
        }
        finally
        {
            IsInitializing = false;
            if (IsInitialized && _patcherMonitorTimer is not null) _ = CheckPatcherMonitorAsync();
        }
    }

    private async Task InitializeCoreAsync()
    {

        // Prune old session logs off the UI thread — the current session hasn't opened its own log yet
        // (the engine does that on Start), so nothing here can be locked out.
        _ = Task.Run(() => LogMaintenance.TrimOldLogs());

        await RunProductHealthAsync(logToJournal: true);

        _hostlists.SeedDefaults();
        ReloadHostlists();
        _engine.GameFilter = Settings.GameFilter;
        _engine.LeagueTcp2099Bypass = Settings.LeagueTcp2099Bypass;
        _engine.RiotEntitlementsBypass = Settings.RiotEntitlementsBypass;
        _engine.BypassAllSites = EffectiveBypassAllSites;
        _engine.DisableQuic = Settings.DisableQuic;
        _engine.CoverTgProxy = false;
        // Before anything else touches the network: a system proxy left applied by a crash points at a
        // socket that no longer exists, which reads to the user as «интернет пропал».
        RestoreStaleSystemProxy();
        _engine.DebugLog = Settings.DebugLog;

        // Prefer the user's persisted explicit choice. Per-network memory is only a fallback when that
        // choice no longer exists; otherwise a previously remembered experimental Flowseal preset can
        // silently replace the safe default on startup and break sensitive apps such as Riot/League.
        string? fp = await Task.Run(NetworkFingerprint.Current);
        ReloadPresets(fp);
        string? recalledName = ResolveRememberedStrategyName(Settings, fp);
        var recalledPreset = recalledName is not null
            ? Presets.FirstOrDefault(p => p.Name == recalledName) : null;
        var persistedPreset = Presets.FirstOrDefault(p => p.Name == (Settings.SelectedPresetName ?? Settings.ActivePresetName));
        SelectedPreset = persistedPreset
                         ?? recalledPreset
                         ?? Presets.FirstOrDefault();
        if (persistedPreset is null && recalledPreset is not null)
            SimpleStatus = Loc.T("Стратегия для этой сети: «{0}»", Loc.T(recalledPreset.Name));
        if (Settings.ActiveHostlist is not null && _hostlists.Exists(Settings.ActiveHostlist))
            SelectedHostlist = Settings.ActiveHostlist;
        else
            SelectedHostlist = Hostlists.FirstOrDefault();

        ReloadPresets(fp);

        EngineVersion = _updater.InstalledVersion ?? Loc.T("не установлен");

        // Ensure engine is unpacked from embedded resources (100% offline portable)
        if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete)
        {
            if (!AppPaths.TryRestoreBundledEngine(out _))
            {
                await _exclusions.ApplyAsync();
                AppPaths.TryRestoreBundledEngine(out _);
            }
            EngineVersion = _updater.InstalledVersion ?? Loc.T("не установлен");
            OnPropertyChanged(nameof(CanStart));
            RaiseCommandStates();
        }

        // Only check GitHub in the background if the user explicitly enabled AutoUpdateEngine
        if (Settings.AutoUpdateEngine)
            _ = CheckAndUpdateAsync(silent: true);

        await EnsureMaintainedExclusionsAsync();

        if (Settings.RiotEntitlementsBypass)
        {
            var riotRepair = await Task.Run(RiotRepairService.EnsureMinimalEntitlementsHelperRunning);
            AppendLog(riotRepair.Message);
        }

        var recoveryCoordinator = new RecoveryCoordinator(
            _settingsSvc,
            _engine,
            _tgProxy,
            _discovery.Store,
            _autostart);

        var progress = new Progress<string>(msg =>
        {
            AppendLog(msg);
            SimpleStatus = msg;
        });

        var result = await recoveryCoordinator.ExecuteRecoveryAsync(Presets, progress);
        if (result.BypassRestored && result.BypassPresetName != null)
        {
            var match = Presets.FirstOrDefault(p => p.Name == result.BypassPresetName);
            if (match != null)
            {
                SelectedPreset = match;
                RunningPreset = match;
            }
        }
        State = _engine.State;

        RefreshAutostartStatus();
        RefreshTelegramProxyStatus();
        StartPatcherMonitoring();
        _ = CheckAppUpdateAsync();
    }

    // ---- helpers -----------------------------------------------------------

    internal static string? ResolveRememberedStrategyName(AppSettings settings, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        if (settings.NetworkStrategies.TryGetValue(fingerprint, out var settingsName) &&
            !string.IsNullOrWhiteSpace(settingsName))
            return settingsName;

        var profile = NetworkKnowledgeStore.Load(fingerprint);
        return !string.IsNullOrWhiteSpace(profile?.BestStrategyName) ? profile.BestStrategyName : null;
    }

    // Journal lines carry the wall-clock time they were PRODUCED (stamped before the OnUi post, which
    // is asynchronous now — stamping inside the lambda would record dispatch time instead). Without it
    // a line like "соединение закрыто через 383,8с" is unverifiable: the journal accumulates from app
    // start (the proxy keeps running while the window is in the tray), so durations legitimately exceed
    // how long the window has been open.
    private void AppendLog(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        OnUi(() =>
        {
            LogLines.Add(stamped);
            TrimLogCollection(LogLines);
        });
    }

    private void AppendProxyLog(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        OnUi(() =>
        {
            ProxyLogLines.Add(stamped);
            TrimLogCollection(ProxyLogLines);
        });
    }

    private static void TrimLogCollection(ObservableCollection<string> lines)
    {
        int overflow = lines.Count - MaxLogLines;
        if (overflow <= 0) return;

        int remove = Math.Max(overflow, MaxLogLines / 10);
        remove = Math.Min(remove, lines.Count);
        for (int i = 0; i < remove; i++)
            lines.RemoveAt(0);
    }

    /// <summary>Copy a whole log to the clipboard (the «Копировать» button on the Журнал tab). Joined
    /// with newlines so it pastes as plain text — the support workflow of "скопируйте последние строки".</summary>
    private void CopyLinesToClipboard(IEnumerable<string> lines, string what)
    {
        string text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0)
        {
            SimpleStatus = Loc.T("{0}: пусто, нечего копировать.", what);
            Notify?.Invoke("GXBRIEL_ZAPRET", SimpleStatus);
            return;
        }
        try
        {
            Clipboard.SetText(text);
            SimpleStatus = Loc.T("{0} скопирован в буфер обмена.", what);
            Notify?.Invoke("GXBRIEL_ZAPRET", SimpleStatus);
        }
        catch { /* clipboard busy — ignore */ }
    }

    private void CopyHealthReport()
    {
        try
        {
            string path = System.IO.Path.Combine(AppPaths.LogsDir, "product-health.txt");
            if (System.IO.File.Exists(path))
            {
                Clipboard.SetText(System.IO.File.ReadAllText(path));
                SimpleStatus = Loc.T("Health-report скопирован в буфер обмена.");
            }
            else
            {
                SimpleStatus = Loc.T("Health-report ещё не создан. Перезапустите приложение или откройте настройки позже.");
            }
            Notify?.Invoke("GXBRIEL_ZAPRET", SimpleStatus);
        }
        catch (Exception ex)
        {
            SimpleStatus = Loc.T("Не удалось скопировать health-report: {0}", ex.Message);
            Notify?.Invoke("GXBRIEL_ZAPRET", SimpleStatus);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            OpenUrl(path);
            StatusMessage = Loc.T("Открыта папка: {0}", System.IO.Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            SimpleStatus = Loc.T("Не удалось открыть папку: {0}", ex.Message);
            Notify?.Invoke("GXBRIEL_ZAPRET", SimpleStatus);
        }
    }

    private void RaiseCommandStates()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ToggleCommand.RaiseCanExecuteChanged();
        CheckUpdateCommand.RaiseCanExecuteChanged();
        DeleteHostlistCommand.RaiseCanExecuteChanged();
        SaveHostlistCommand.RaiseCanExecuteChanged();
        AddDomainCommand.RaiseCanExecuteChanged();
        ApplyStrategyCommand.RaiseCanExecuteChanged();
        DuplicatePresetCommand.RaiseCanExecuteChanged();
        DeletePresetCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
        ImportArchiveCommand.RaiseCanExecuteChanged();
        TestSelectedPresetCommand?.RaiseCanExecuteChanged();
        SimpleToggleCommand.RaiseCanExecuteChanged();
        RunDiagnosticsCommand.RaiseCanExecuteChanged();
        StopDiagnosticsCommand.RaiseCanExecuteChanged();
        RunDpiCheckCommand.RaiseCanExecuteChanged();
        StopDpiCheckCommand.RaiseCanExecuteChanged();
        RunAutoSelectCommand.RaiseCanExecuteChanged();
        RecheckWorkPoolCommand.RaiseCanExecuteChanged();
        FullVerifyProfilesCommand.RaiseCanExecuteChanged();
        QuickScanCommand.RaiseCanExecuteChanged();
        Blockcheck2Command.RaiseCanExecuteChanged();
        StopAutoSelectCommand.RaiseCanExecuteChanged();
        GenerateStrategyCommand.RaiseCanExecuteChanged();
        BuildDiscordIpsetCommand.RaiseCanExecuteChanged();
        UpdateAllListsCommand.RaiseCanExecuteChanged();
        StopListUpdateCommand.RaiseCanExecuteChanged();
        LiveTraceCommand.RaiseCanExecuteChanged();
        ApplyExclusionsCommand.RaiseCanExecuteChanged();
        HomeToggleCommand.RaiseCanExecuteChanged();
        ApplyScoreAndStartCommand.RaiseCanExecuteChanged();
        RefreshPatcherCommandStates();
    }

    // ---- shared plumbing ---------------------------------------------------

    private void HandleOpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "#" || url.StartsWith("section:", StringComparison.OrdinalIgnoreCase))
        {
            ShowHowItWorks = true;
            return;
        }
        if (url.Equals("readme", StringComparison.OrdinalIgnoreCase))
        {
            string readmePath = System.IO.Path.Combine(AppContext.BaseDirectory, "README.md");
            if (!System.IO.File.Exists(readmePath))
                readmePath = System.IO.Path.Combine(AppPaths.Root, "README.md");
            if (System.IO.File.Exists(readmePath))
            {
                OpenUrl(readmePath);
                return;
            }
            ShowHowItWorks = true;
            return;
        }
        OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — ignore */ }
    }

    internal void ReloadPresets(string? networkFp = null)
    {
        string? prevSelectedFp = _selectedPreset?.Fingerprint;
        string? prevSelectedName = _selectedPreset?.Name ?? Settings.SelectedPresetName ?? Settings.ActivePresetName;
        string? prevRunningFp = _runningPreset?.Fingerprint;
        string? prevRunningName = _runningPreset?.Name;

        _presets.LoadForNetwork(networkFp ?? _presets.CurrentNetworkFingerprint,
            BlockcheckProfileStore.OptionsKey(EffectiveBypassAllSites, Settings.DisableQuic),
            CurrentProbeLaunchOptions.VerificationKey(EffectiveBypassAllSites, Settings.DisableQuic));

        Presets.Clear();
        // Keep the everyday UI limited to the fixed 39-strategy catalog.
        foreach (var p in _presets.ReferencePresets) Presets.Add(p);
        foreach (var p in _presets.All.Where(p => p.Provenance == "UPSTREAM_BLOCKCHECK2")) Presets.Add(p);

        if (prevRunningFp is not null || prevRunningName is not null)
        {
            var matchedRunning = Presets.FirstOrDefault(p =>
                !string.IsNullOrEmpty(prevRunningFp) ? p.Fingerprint == prevRunningFp :
                string.Equals(p.Name, prevRunningName, StringComparison.OrdinalIgnoreCase));
            if (matchedRunning is not null)
                RunningPreset = matchedRunning;
        }

        OnPropertyChanged(nameof(RecommendedPreset));
        OnPropertyChanged(nameof(WorkPresetCount));
        OnPropertyChanged(nameof(BrokenPresetCount));
        OnPropertyChanged(nameof(UntestedPresetCount));
        OnPropertyChanged(nameof(FailedPresetCount));
        OnPropertyChanged(nameof(ProfilePoolSummary));
        OnPropertyChanged(nameof(AutoSelectPrimaryText));
        OnPropertyChanged(nameof(AutoSelectPrimaryTooltip));
        OnPropertyChanged(nameof(PresetsFilterAllText));
        OnPropertyChanged(nameof(PresetsFilterWorkText));
        ApplyPresetsFilter();

        if (_selectedPreset is null && HasVisiblePresets)
        {
            var restored = Presets.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(prevSelectedFp) && p.Fingerprint == prevSelectedFp) ||
                string.Equals(p.Name, prevSelectedName, StringComparison.OrdinalIgnoreCase))
                ?? PresetsView?.Cast<Models.Preset>().FirstOrDefault();
            if (restored is not null && !PresetsView!.Contains(restored))
                restored = PresetsView.Cast<Models.Preset>().FirstOrDefault();
            if (restored is not null)
                SelectedPreset = restored;
        }

        RecheckWorkPoolCommand?.RaiseCanExecuteChanged();
        RaiseCommandStates();
    }

    private void ReloadHostlists()
    {
        string? previous = SelectedHostlist;
        Hostlists.Clear();
        foreach (var h in _hostlists.GetLists()) Hostlists.Add(h);

        if (!string.IsNullOrEmpty(previous) && Hostlists.Contains(previous))
            SelectedHostlist = previous;
        else if (SelectedHostlist is null || !Hostlists.Contains(SelectedHostlist))
            SelectedHostlist = Hostlists.FirstOrDefault(h => h == "discord") ?? Hostlists.FirstOrDefault();
    }

    private void RestoreStaleSystemProxy()
    {
        if (!string.IsNullOrEmpty(Settings.SystemProxyBackup))
        {
            GxbrielZapret.Services.Platform.SystemProxyService.Restore(Settings.SystemProxyBackup, Settings.MasqueListenPort);
            Settings.SystemProxyBackup = "";
            _settingsSvc.Save();
        }
    }

    /// <summary>Marshal an action onto the UI thread. Cross-thread calls POST asynchronously
    /// (BeginInvoke) instead of blocking: a synchronous Invoke from a background thread that also holds
    /// a lock the UI thread wants (EngineService._lock in OnProcessExited) deadlocks the whole UI — the
    /// "app freezes, kill it in Task Manager" bug. On the UI thread it still runs inline.</summary>
    private static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.BeginInvoke(a);
    }
}
