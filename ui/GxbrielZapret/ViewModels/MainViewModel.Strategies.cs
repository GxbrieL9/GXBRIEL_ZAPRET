using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Strategies;

namespace GxbrielZapret.ViewModels;

/// <summary>
/// Strategy list (built-in + user) and hostlist management.
/// </summary>
public sealed partial class MainViewModel
{
    // ---- presets -----------------------------------------------------------

    private Preset? _selectedPreset;
    public Preset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetField(ref _selectedPreset, value))
            {
                if (value is not null)
                {
                    Settings.SelectedPresetName = value.Name;
                    _settingsSvc.Save();
                }
                SelectedPresetTestStatus = "";
                SelectedPresetTestVerdict = DiagStatus.Skip;
                LastApplicationFlowSummary = string.IsNullOrWhiteSpace(value?.VerifiedFlowSummary)
                    ? "Видео: не проверено · Gateway: не проверен · Голос: не проверен"
                    : "Проверка " + value.LastTestedDisplay + ": " + value.VerifiedFlowSummary;
                OnPropertyChanged(nameof(PresetArgsText));
                OnPropertyChanged(nameof(CommandPreview));
                OnPropertyChanged(nameof(SelectedPresetEditable));
                OnPropertyChanged(nameof(DiagEngineNote));
                OnPropertyChanged(nameof(IsSelectedPresetRunning));
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(CanApplyStrategy));
                OnPropertyChanged(nameof(ActivatePresetButtonText));
                OnPropertyChanged(nameof(RunStatusText));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(SelectedPresetOrHint));
                OnPropertyChanged(nameof(ActiveStrategyDisplayName));
                OnPropertyChanged(nameof(ActiveStrategySimpleName));
                OnPropertyChanged(nameof(ActiveStrategySubtitle));
                OnPropertyChanged(nameof(ActiveStrategySimpleSubtitle));
                OnPropertyChanged(nameof(HeroSubtitleText));
                OnPropertyChanged(nameof(StrategyCardBadgeText));
                OnPropertyChanged(nameof(StrategyCardBadgeStatus));
                OnPropertyChanged(nameof(ApplyPillText));
                OnPropertyChanged(nameof(CanTestSelectedPreset));
                OnPropertyChanged(nameof(HasSelectedPresetTestStatus));
                TestSelectedPresetCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(StatusActiveStrategyText));
                RaiseCommandStates();
            }
        }
    }

    public bool SelectedPresetEditable => SelectedPreset is { IsBuiltIn: false, BoundNetworkFingerprint: null };

    public enum PresetFilterMode
    {
        Catalog,
        All,
        Work,
        Broken,
        Untested,
        Blockcheck
    }

    private PresetFilterMode _filterMode = PresetFilterMode.Catalog;
    public PresetFilterMode FilterMode
    {
        get => _filterMode;
        set
        {
            if (SetField(ref _filterMode, value))
            {
                OnPropertyChanged(nameof(FilterAllChecked));
                OnPropertyChanged(nameof(FilterWorkChecked));
                OnPropertyChanged(nameof(FilterBrokenChecked));
                OnPropertyChanged(nameof(FilterUntestedChecked));
                ApplyPresetsFilter();
            }
        }
    }

    public bool FilterAllChecked
    {
        get => _filterMode is PresetFilterMode.Catalog or PresetFilterMode.All;
        set { if (value) FilterMode = PresetFilterMode.Catalog; }
    }

    public bool FilterWorkChecked
    {
        get => _filterMode == PresetFilterMode.Work;
        set { if (value) FilterMode = PresetFilterMode.Work; }
    }

    public bool FilterBrokenChecked
    {
        get => _filterMode == PresetFilterMode.Broken;
        set { if (value) FilterMode = PresetFilterMode.Broken; }
    }

    public bool FilterUntestedChecked
    {
        get => _filterMode == PresetFilterMode.Untested;
        set { if (value) FilterMode = PresetFilterMode.Untested; }
    }

    private static bool IsCanonicalCatalogPreset(Preset p) =>
        p.ReferenceId is not null && ReferenceStrategyCatalog.All.Any(r =>
            string.Equals(r.Id, p.ReferenceId, StringComparison.OrdinalIgnoreCase));

    public string PresetsFilterAllText => Loc.T("Каталог ({0})", CanonicalPresetCount);
    public string PresetsFilterWorkText => Loc.T("Рабочие ({0})", WorkPresetCount);
    public string PresetsFilterBrokenText => Loc.T("Нерабочие ({0})", FailedPresetCount);
    public string PresetsFilterUntestedText => Loc.T("Непроверенные ({0})", UntestedPresetCount);
    public int BlockcheckPresetCount => Presets.Count(p => p.Provenance == "UPSTREAM_BLOCKCHECK2");
    public bool HasBlockcheckPresets => BlockcheckPresetCount > 0;
    public string BlockcheckFilterText => $"blockcheck2 ({BlockcheckPresetCount})";
    public bool FilterBlockcheckChecked
    {
        get => FilterMode == PresetFilterMode.Blockcheck;
        set { if (value) FilterMode = PresetFilterMode.Blockcheck; }
    }
    private string _strategySearchText = "";
    public string StrategySearchText
    {
        get => _strategySearchText;
        set { if (SetField(ref _strategySearchText, value ?? "")) ApplyPresetsFilter(); }
    }
    private bool _showFavoritePresetsOnly;
    public bool ShowFavoritePresetsOnly
    {
        get => _showFavoritePresetsOnly;
        set { if (SetField(ref _showFavoritePresetsOnly, value)) ApplyPresetsFilter(); }
    }
    public int VisiblePresetCount => PresetsView?.Cast<Preset>().Count() ?? 0;
    public string VisiblePresetCountText => Loc.T("Показано {0} из {1}", VisiblePresetCount,
        FilterMode == PresetFilterMode.Blockcheck ? BlockcheckPresetCount : CanonicalPresetCount);
    private void ToggleFavoritePreset(Preset? preset)
    {
        if (preset?.ReferenceId is not { } id || !IsCanonicalCatalogPreset(preset)) return;
        if (Settings.FavoriteStrategyIds.Any(saved => string.Equals(saved, id, StringComparison.OrdinalIgnoreCase)))
            Settings.FavoriteStrategyIds.RemoveAll(saved => string.Equals(saved, id, StringComparison.OrdinalIgnoreCase));
        else
            Settings.FavoriteStrategyIds.Add(id);
        _settingsSvc.Save();
        ApplyPresetsFilter();
    }

    public bool HasVisiblePresets => PresetsView?.IsEmpty == false;
    public string EmptyPresetListHint => !string.IsNullOrWhiteSpace(StrategySearchText)
        ? Loc.T("По вашему запросу стратегии не найдены.")
        : ShowFavoritePresetsOnly ? Loc.T("В этом фильтре нет избранных стратегий.") : FilterMode switch
    {
        PresetFilterMode.Work => Loc.T("Для выбранных целей пока нет проверенных стратегий."),
        PresetFilterMode.Broken => Loc.T("В этом фильтре нет нерабочих стратегий."),
        PresetFilterMode.Untested => Loc.T("Все стратегии уже проверены."),
        _ => Loc.T("Каталог стратегий пуст.")
    };

    public void ApplyPresetsFilter()
    {
        var prev = _selectedPreset;
        if (PresetsView is null) return;
        foreach (var preset in Presets)
        {
            preset.SetDisplayTargets(ActiveProbeTargets);
            preset.IsFavorite = Settings.FavoriteStrategyIds.Any(id => string.Equals(id, preset.ReferenceId, StringComparison.OrdinalIgnoreCase));
        }

        Predicate<object>? scopeFilter = _filterMode switch
        {
            PresetFilterMode.Catalog => o => o is Preset p && IsCanonicalCatalogPreset(p),
            PresetFilterMode.Work => o => o is Preset p && IsCanonicalCatalogPreset(p) && p.IsWorkFor(ActiveProbeTargets),
            PresetFilterMode.Broken => o => o is Preset p && IsCanonicalCatalogPreset(p) && p.IsFailed,
            PresetFilterMode.Untested => o => o is Preset p && IsCanonicalCatalogPreset(p) && IsUntestedForActiveTargets(p),
            PresetFilterMode.Blockcheck => o => o is Preset p && p.Provenance == "UPSTREAM_BLOCKCHECK2",
            PresetFilterMode.All => null,
            _ => o => o is Preset p && p.IsReferenceStrategy
        };
        string[] terms = StrategySearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        PresetsView.Filter = item => item is Preset preset && (scopeFilter?.Invoke(item) ?? true)
            && (!ShowFavoritePresetsOnly || preset.IsFavorite)
            && (terms.Length == 0 || MatchesStrategySearch(preset, terms));
        PresetsView.Refresh();

        bool isPrevVisible = prev is not null && PresetsView.Contains(prev);

        if (!isPrevVisible || _selectedPreset is null)
        {
            SelectedPreset = PresetsView.Cast<Preset>().FirstOrDefault();
        }

        OnPropertyChanged(nameof(PresetsFilterAllText));
        OnPropertyChanged(nameof(PresetsFilterWorkText));
        OnPropertyChanged(nameof(PresetsFilterBrokenText));
        OnPropertyChanged(nameof(PresetsFilterUntestedText));
        OnPropertyChanged(nameof(ProfilePoolSummary));
        OnPropertyChanged(nameof(HasVisiblePresets));
        OnPropertyChanged(nameof(EmptyPresetListHint));
        OnPropertyChanged(nameof(VisiblePresetCountText));
        OnPropertyChanged(nameof(FilterBlockcheckChecked));
        OnPropertyChanged(nameof(HasBlockcheckPresets));
        OnPropertyChanged(nameof(BlockcheckFilterText));
    }

    private static bool MatchesStrategySearch(Preset preset, string[] terms)
    {
        string text = string.Join(' ', preset.Name, preset.Tagline, preset.Description, preset.ReferenceId, preset.TargetServices);
        return terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private bool _showPresetArgs;
    /// <summary>Whether the raw-args editor is revealed on the Стратегии tab (hidden by default to declutter).</summary>
    public bool ShowPresetArgs { get => _showPresetArgs; set => SetField(ref _showPresetArgs, value); }

    // The preset the engine is ACTUALLY running right now (captured at Start),
    // as opposed to SelectedPreset which is merely highlighted in the UI. A
    // strategy change needs an engine restart, so these can diverge until the
    // user confirms with ApplyStrategyCommand.
    private Preset? _runningPreset;
    public Preset? RunningPreset
    {
        get => _runningPreset;
        private set
        {
            if (SetField(ref _runningPreset, value))
            {
                OnPropertyChanged(nameof(RunningPresetName));
                OnPropertyChanged(nameof(DiagEngineNote));
                OnPropertyChanged(nameof(ActiveStrategyDisplayName));
                OnPropertyChanged(nameof(ActiveStrategySimpleName));
                OnPropertyChanged(nameof(ActiveStrategySubtitle));
                OnPropertyChanged(nameof(ActiveStrategySimpleSubtitle));
                OnPropertyChanged(nameof(RunningPresetLine));
                OnPropertyChanged(nameof(IsSelectedPresetRunning));
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(CanApplyStrategy));
                OnPropertyChanged(nameof(ActivatePresetButtonText));
                OnPropertyChanged(nameof(RunStatusText));
                OnPropertyChanged(nameof(HeroSubtitleText));
                OnPropertyChanged(nameof(StrategyCardBadgeText));
                OnPropertyChanged(nameof(StrategyCardBadgeStatus));
                OnPropertyChanged(nameof(StatusActiveStrategyText));
                RaiseCommandStates();
            }
        }
    }

    public string RunningPresetName => RunningPreset?.Name ?? "—";

    public string ActiveStrategyDisplayName =>
        IsManagedAutostartRunning
            ? Loc.T("managed-winws-options.txt")
            : IsRunning ? Loc.T(RunningPreset?.Name ?? "Неизвестная стратегия") : SelectedPresetOrHint;

    public string ActiveStrategySimpleName
    {
        get
        {
            string name = ActiveStrategyDisplayName;
            int marker = name.IndexOf(" (", StringComparison.Ordinal);
            return marker > 0 ? name[..marker] : name;
        }
    }

    public string ActiveStrategySimpleSubtitle
    {
        get
        {
            if (IsRecoveringEngine && State == EngineState.Stopped)
                return Loc.T("Движок остановлен. Ожидается автоматическое восстановление.");
            if (IsManagedAutostartRunning)
                return Loc.T("Параметры взяты из автозапуска Windows");
            if (SelectedPreset is null)
                return Loc.T("Выберите стратегию для этой сети");
            if (IsRunning)
                return Loc.T("Движок запущен. Доступность сервисов показана отдельно.");
            return Loc.T("Готова к запуску для текущей сети");
        }
    }

    public string ActiveStrategySubtitle =>
        IsManagedAutostartRunning
            ? Loc.T("Фактические параметры взяты из файла автозапуска")
            : (IsRunning ? RunningPreset : SelectedPreset)?.TaglineDisplay ?? "";

    /// <summary>Honest badge text for the strategy card on Home (never falsely claims Активна when stopped).</summary>
    public string StrategyCardBadgeText
    {
        get
        {
            if (IsRecoveringEngine && !IsRunning) return Loc.T("Восстановление…");
            if (IsTransitioning) return Loc.T("Переключение…");
            if (IsManagedAutostartRunning) return Loc.T("Автозапуск");
            if (IsStrategyChangePending) return Loc.T("Ожидает применения");
            if (IsRunning) return Loc.T("Активна");
            return SelectedPreset is null ? Loc.T("Не выбрана") : Loc.T("Готова к запуску");
        }
    }

    /// <summary>Semantic DiagStatus for coloring the strategy card badge.</summary>
    public DiagStatus StrategyCardBadgeStatus
    {
        get
        {
            if (IsManagedAutostartRunning) return DiagStatus.Timeout;
            if (IsTransitioning || IsStrategyChangePending) return DiagStatus.Timeout;
            if (IsRunning) return DiagStatus.Ok;
            return DiagStatus.Pending;
        }
    }

    // Localized text for XAML spots that can't use the {loc:Loc} markup extension directly: a binding
    // StringFormat / FallbackValue carrying Russian, or a literal that contains a brace ({0}, {IPSET}).
    // These route through Loc.T instead. Change-notified alongside the values they depend on.

    /// <summary>Caption-bar apply pill: "Применить: &lt;preset&gt;". The name is translated too.</summary>
    public string ApplyPillText => Loc.T("Применить: {0}", Loc.T(SelectedPreset?.Name ?? ""));

    /// <summary>Selected preset name (translated for display), or the hint shown when none is selected.</summary>
    public string SelectedPresetOrHint =>
        SelectedPreset is { } p ? Loc.T(p.Name) : Loc.T("Выберите пресет");

    /// <summary>Running-strategy line under the state badge on the Стратегии tab.</summary>
    public string RunningPresetLine =>
        IsManagedAutostartRunning
            ? Loc.T("Сейчас включён managed winws через автозапуск Windows. Для смены стратегии остановите и запустите выбранную.")
            : Loc.T("Сейчас включён: {0}. Смена стратегии требует перезапуска движка.",
                  RunningPreset is { } rp ? Loc.T(rp.Name) : RunningPresetName);

    /// <summary>Selected hostlist name, or the hint shown when none is selected.</summary>
    public string SelectedHostlistOrHint => SelectedHostlist ?? Loc.T("Выберите или создайте список");

    /// <summary>True when the selected preset is currently the active running strategy in the engine.</summary>
    public bool IsSelectedPresetRunning =>
        IsRunning && SelectedPreset is not null && RunningPreset is not null &&
        (ReferenceEquals(RunningPreset, SelectedPreset) ||
         (!string.IsNullOrEmpty(RunningPreset.Fingerprint) && RunningPreset.Fingerprint == SelectedPreset.Fingerprint) ||
         string.Equals(RunningPreset.Name, SelectedPreset.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the engine is running (with another preset or external autostart) and the user has selected a different strategy.</summary>
    public bool IsStrategyChangePending =>
        IsRunning && SelectedPreset is not null && !IsSelectedPresetRunning;

    /// <summary>True when the selected strategy can be activated (started if idle, or switched to if another strategy is running).</summary>
    public bool CanApplyStrategy =>
        SelectedPreset is not null && !IsDownloadingEngine && !IsTransitioning && !IsAutoSelecting && !IsTestingSelectedPreset && _updater.IsEngineInstalled &&
        (!IsRunning || !IsSelectedPresetRunning);

    /// <summary>Text for the primary activation button on the Стратегии tab.</summary>
    public string ActivatePresetButtonText =>
        IsRunning && !IsSelectedPresetRunning
            ? Loc.T("Применить стратегию")
            : Loc.T("Включить стратегию");

    /// <summary>Sub-line under the state badge: what is ENABLED (running), not just selected.</summary>
    public string RunStatusText =>
        IsManagedAutostartRunning
            ? Loc.T("Включён автозапуском: PID {0}", _engine.ActiveProcessId?.ToString() ?? "?")
            : IsRunning
            ? Loc.T("Включён: {0}", Loc.T(RunningPresetName))
            : SelectedPreset is null ? Loc.T("пресет не выбран") : Loc.T("Выбран: {0}", Loc.T(SelectedPreset.Name));

    /// <summary>Args of the selected preset, one per line, for editing.</summary>
    public string PresetArgsText
    {
        get => SelectedPreset is null ? "" : string.Join('\n', SelectedPreset.Args);
        set
        {
            if (SelectedPreset is { IsBuiltIn: false } p)
            {
                p.Args = value.Replace("\r\n", "\n").Split('\n')
                              .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                OnPropertyChanged(nameof(CommandPreview));
            }
        }
    }

    public string CommandPreview =>
        SelectedPreset is null
            ? ""
            : EngineService.PreviewCommandLine(SelectedPreset, ActiveHostlistPath, Settings.GameFilter,
                                               EffectiveBypassAllSites, Settings.DisableQuic, Settings.TgProxyCoverage,
                                               Settings.DebugLog, Settings.LeagueTcp2099Bypass,
                                               Settings.RiotEntitlementsBypass);

    // ---- hostlists ---------------------------------------------------------

    private string? _selectedHostlist;
    public string? SelectedHostlist
    {
        get => _selectedHostlist;
        set
        {
            if (SetField(ref _selectedHostlist, value))
            {
                Settings.ActiveHostlist = value;
                _settingsSvc.Save();
                HostlistContent = value is null ? "" : _hostlists.Read(value);
                OnPropertyChanged(nameof(CommandPreview));
                OnPropertyChanged(nameof(SelectedHostlistOrHint));
                if (!IsInitializing && !IsAutoSelecting && !IsTestingSelectedPreset) ReloadPresets();
                RaiseCommandStates();
            }
        }
    }

    private string _hostlistContent = "";
    public string HostlistContent
    {
        get => _hostlistContent;
        set => SetField(ref _hostlistContent, value);
    }

    private string _newDomain = "";
    public string NewDomain { get => _newDomain; set => SetField(ref _newDomain, value); }

    private string? ActiveHostlistPath =>
        SelectedHostlist is not null && _hostlists.Exists(SelectedHostlist)
            ? _hostlists.GetPath(SelectedHostlist)
            : null;

    // ---- external archive import -------------------------------------------

    private bool _isImportingPresets;
    public bool IsImportingPresets
    {
        get => _isImportingPresets;
        private set { if (SetField(ref _isImportingPresets, value)) RaiseCommandStates(); }
    }

    public async Task ImportPresetsFromArchiveAsync(string? explicitPath = null)
    {
        if (IsImportingPresets) return;
        IsImportingPresets = true;

        try
        {
            string targetDir = explicitPath ?? "";
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = Loc.T("Выберите папку со стратегиями (.bat, .cmd, .json)"),
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    targetDir = dlg.SelectedPath;
                }
                else
                {
                    return;
                }
            }

            AppendLog(Loc.T("Импорт стратегий из папки: {0}…", targetDir));
            var res = await Task.Run(() => PresetImporter.ImportFromDirectory(targetDir, _presets, msg => AppendLog(msg)));
            ReloadPresets();

            AppendLog(res.SummaryMessage);
            Notify?.Invoke("GXBRIEL_ZAPRET", res.SummaryMessage);
        }
        catch (Exception ex)
        {
            AppendLog(Loc.T("Ошибка импорта: {0}", ex.Message));
            ShowErrorModal(Loc.T("Ошибка импорта"), Loc.T("Не удалось импортировать стратегии из внешнего архива."), ex.ToString());
        }
        finally
        {
            IsImportingPresets = false;
        }
    }

    // ---- selective strategy ping & verification ----------------------------

    private bool _isTestingSelectedPreset;
    public bool IsTestingSelectedPreset
    {
        get => _isTestingSelectedPreset;
        private set
        {
            if (SetField(ref _isTestingSelectedPreset, value))
            {
                OnPropertyChanged(nameof(CanTestSelectedPreset));
                OnPropertyChanged(nameof(TestPresetButtonText));
                RaiseCommandStates();
                TestSelectedPresetCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanTestSelectedPreset => !IsInitializing && !IsDiagnosing && !IsTestingSelectedPreset && !IsAutoSelecting && !IsTransitioning && SelectedPreset is not null;

    public string TestPresetButtonText => IsTestingSelectedPreset
        ? Loc.T("Проверяю…")
        : Loc.T("Проверить пинг и работу");

    private string _selectedPresetTestStatus = "";
    public string SelectedPresetTestStatus
    {
        get => _selectedPresetTestStatus;
        internal set
        {
            if (SetField(ref _selectedPresetTestStatus, value))
                OnPropertyChanged(nameof(HasSelectedPresetTestStatus));
        }
    }

    public bool HasSelectedPresetTestStatus => !string.IsNullOrWhiteSpace(_selectedPresetTestStatus);

    private DiagStatus _selectedPresetTestVerdict = DiagStatus.Skip;
    public DiagStatus SelectedPresetTestVerdict
    {
        get => _selectedPresetTestVerdict;
        internal set => SetField(ref _selectedPresetTestVerdict, value);
    }

    public async Task TestSelectedPresetAsync()
    {
        var preset = SelectedPreset;
        if (preset is null || !CanTestSelectedPreset) return;

        IsTestingSelectedPreset = true;
        SelectedPresetTestVerdict = DiagStatus.Pending;
        SelectedPresetTestStatus = Loc.T("Проверяю пинг и доступность «{0}»…", preset.Name);

        bool wasRunning = IsRunning;
        var previousPreset = RunningPreset ?? _engine.ActivePreset;
        bool isCurrentRunning = wasRunning && RunningPreset?.Fingerprint == preset.Fingerprint;
        NetworkBoundRun? networkSession = null;
        bool bypassAll = EffectiveBypassAllSites, disableQuic = Settings.DisableQuic;
        var launchOptions = CurrentProbeLaunchOptions;
        string referenceOptionsKey = launchOptions.VerificationKey(bypassAll, disableQuic);
        string optionsKey = BlockcheckProfileStore.OptionsKey(bypassAll, disableQuic);
        void Checkpoint()
        {
            networkSession?.Checkpoint();
            if (BlockcheckProfileStore.OptionsKey(EffectiveBypassAllSites, Settings.DisableQuic) != optionsKey)
                throw new InvalidOperationException("Настройки изменились во время проверки. Результат не сохранён.");
            if (CurrentProbeLaunchOptions.VerificationKey(EffectiveBypassAllSites, Settings.DisableQuic) != referenceOptionsKey)
                throw new InvalidOperationException("Настройки запуска изменились во время проверки. Результат не сохранён.");
        }
        _healthMonitor.StopWatchdog();
        _healthMonitor.Reset();

        try
        {
            if (preset.BoundNetworkFingerprint is not null)
            {
                networkSession = new(NetworkFingerprint.Current, CancellationToken.None);
                if (networkSession.Fingerprint != preset.BoundNetworkFingerprint)
                    throw new InvalidOperationException("Этот профиль проверялся для другой сети. Запустите подбор для текущей сети.");
            }
            var ct = networkSession?.Token ?? CancellationToken.None;
            var checkScope = preset.BoundScope ?? SelectedAutoScope;
            ApplicationFlowResult? flows = null;
            bool engineConfirmed;
            (bool ok, long ms) pingDc = (false, 0);
            (bool ok, long ms) pingYt = (false, 0);
            Dictionary<string, bool> probes = new(StringComparer.OrdinalIgnoreCase);
            int passCount;
            IReadOnlyList<string> activeTargets = checkScope.GoalHosts();
            int totalCount = activeTargets.Count;
            long latencyMs;

            if (isCurrentRunning)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var host in activeTargets)
                {
                    probes[host] = await Canonical5ProbeService.ProbeTargetAsync(host, ct);
                }
                sw.Stop();

                passCount = probes.Values.Count(v => v);
                latencyMs = totalCount > 0 ? sw.ElapsedMilliseconds / totalCount : 0;
                if (passCount == totalCount)
                    flows = await ApplicationFlowProbe.CheckAsync(checkScope, ct);
                engineConfirmed = _engine.ActiveProcessId.HasValue && _engine.State == EngineState.Running &&
                    _engine.ActivePreset?.Fingerprint == preset.Fingerprint;
            }
            else
            {
                if (wasRunning)
                {
                    _engine.Stop();
                    await Task.Delay(300);
                }

                using var probeSvc = new Canonical5ProbeService();
                var res = await probeSvc.EvaluateCandidateAsync(
                    preset, null, ct, bypassAll, activeTargets, disableQuic, checkScope, launchOptions);
                if (res.CheckUnavailable) throw new InvalidOperationException(res.Summary);
                probes = res.Probes;
                passCount = res.PassCount;
                totalCount = res.TotalCount;
                latencyMs = res.LatencyMs;
                flows = res.Flows;
                engineConfirmed = res.IsWork;

            }

            pingDc = await NetProbe.PingAsync("discord.com", CancellationToken.None);
            pingYt = await NetProbe.PingAsync("www.youtube.com", CancellationToken.None);
            long lat = Math.Max(pingDc.ms, pingYt.ms);
            if (lat <= 0) lat = Math.Max(latencyMs, pingDc.ms > 0 ? pingDc.ms : pingYt.ms);
            Checkpoint();
            bool isStrictWork = engineConfirmed && flows?.Passes(checkScope) == true && passCount == totalCount
                                && activeTargets.All(h => probes.TryGetValue(h, out bool ok) && ok);
            string networkFp = await Task.Run(NetworkFingerprint.Current) ?? "default";
            if (networkSession is not null)
            {
                isStrictWork = isStrictWork && flows?.Passes(checkScope) == true;
                if (isStrictWork)
                {
                    var verification = new Canonical5ProbeResult(preset.Name, preset.Fingerprint, true, passCount, totalCount, latencyMs, probes, flows!.Summary, flows);
                    var saved = BlockcheckProfileStore.SaveVerified(networkSession.Fingerprint, checkScope,
                        optionsKey, preset, verification, Checkpoint);
                    ReloadPresets(networkSession.Fingerprint);
                    SelectedPreset = Presets.FirstOrDefault(p => p.Name == saved.Name);
                }
                SelectedPresetTestVerdict = isStrictWork ? DiagStatus.Ok : DiagStatus.Fail;
                SelectedPresetTestStatus = (isStrictWork ? "Профиль перепроверен. " : "Полная проверка не пройдена; сохранённый профиль не изменён. ") +
                    (flows?.Summary ?? $"HTTPS: {passCount}/{totalCount}. Видео и Gateway не подтверждены.");
                return;
            }

            if (isStrictWork)
            {
                SelectedPresetTestVerdict = DiagStatus.Ok;
                SelectedPresetTestStatus = flows!.Summary;
                preset = _presets.UpdateStrategyProbeState(
                    preset, "WORK", $"manual_{checkScope.Title()}_{passCount}_of_{totalCount}", probes, passCount, totalCount, latencyMs, networkFp, flows, referenceOptionsKey);
                ReloadPresets(networkFp);
                SelectedPreset = Presets.FirstOrDefault(p => p.Fingerprint == preset.Fingerprint) ?? preset;
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("«{0}» подтверждена и добавлена в WORK-пул!", preset.Name));
            }
            else if (passCount > 0)
            {
                SelectedPresetTestVerdict = DiagStatus.Timeout;
                SelectedPresetTestStatus = Loc.T("≈ ЧАСТИЧНО · {0}/{1} целей · не добавляю в WORK", passCount, totalCount);
                if (flows is not null) SelectedPresetTestStatus += " · " + flows.Summary;
                preset = _presets.UpdateStrategyProbeState(
                    preset, passCount == totalCount && flows?.IsConclusive(checkScope) != true ? "INCONCLUSIVE" : "BROKEN",
                    $"manual_failed_{passCount}_of_{totalCount}", probes, passCount, totalCount, latencyMs, networkFp, flows, referenceOptionsKey);
                ReloadPresets(networkFp);
                SelectedPreset = Presets.FirstOrDefault(p => p.Fingerprint == preset.Fingerprint) ?? preset;
            }
            else
            {
                SelectedPresetTestVerdict = DiagStatus.Fail;
                SelectedPresetTestStatus = Loc.T("✗ НЕ РАБОТАЕТ · 0/{0} целей · Пинг: {1} ms", totalCount, lat > 0 ? lat : 0);
                preset = _presets.UpdateStrategyProbeState(
                    preset, "BROKEN", $"manual_0_of_{totalCount}_failed", probes, passCount, totalCount, latencyMs, networkFp);
                ReloadPresets(networkFp);
                SelectedPreset = Presets.FirstOrDefault(p => p.Fingerprint == preset.Fingerprint) ?? preset;
            }
        }
        catch (Exception ex)
        {
            SelectedPresetTestVerdict = DiagStatus.Fail;
            SelectedPresetTestStatus = Loc.T("Ошибка проверки: {0}", ex.Message);
        }
        finally
        {
            bool networkChanged = networkSession?.NetworkChanged == true;
            if (networkSession is not null) await networkSession.DisposeAsync();
            IsTestingSelectedPreset = false;
            if (!networkChanged && wasRunning && !isCurrentRunning && previousPreset is not null && !IsRunning)
            {
                try
                {
                    _engine.Start(previousPreset, previousPreset.UsesHostlist ? ActiveHostlistPath : null);
                    State = _engine.State;
                    RunningPreset = _engine.State == EngineState.Running ? previousPreset : null;
                }
                catch (Exception ex) { AppendLog(Loc.T("Не удалось восстановить стратегию: {0}", ex.Message)); }
            }
            if (IsRunning) UpdateMonitor();
        }
    }
}
