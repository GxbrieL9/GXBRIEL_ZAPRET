using System.Collections.ObjectModel;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Services.Strategies;

namespace GxbrielZapret.ViewModels;

/// <summary>
/// Strategy Selection and Scanning model for GXBRIEL_ZAPRET 1.0.
/// Immutable baseline of 39 entries plus separately identified upstream candidates.
/// States: UNTESTED, TESTING, WORK, BROKEN, INCONCLUSIVE.
/// WORK requires all selected HTTPS targets plus application-flow evidence.
/// Missing test media is INCONCLUSIVE, not proof of failure.
/// - AutoSelect: selects ONLY from WORK pool in deterministic order. If empty: prompts for Full Scan.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _isBlockcheckSession;
    public bool IsBlockcheckSession
    {
        get => _isBlockcheckSession;
        private set { if (SetField(ref _isBlockcheckSession, value)) RaiseAutoRunState(); }
    }
    private bool _isBlockcheckRunning;
    public bool IsBlockcheckRunning
    {
        get => _isBlockcheckRunning;
        private set
        {
            if (SetField(ref _isBlockcheckRunning, value))
            {
                OnPropertyChanged(nameof(IsAutoProgressIndeterminate));
                RaiseAutoRunState();
            }
        }
    }
    public bool IsAutoProgressIndeterminate => IsBlockcheckRunning;
    private bool _isAutoSelecting;
    public bool IsAutoSelecting
    {
        get => _isAutoSelecting;
        private set { if (SetField(ref _isAutoSelecting, value)) { RaiseCommandStates(); RaiseAutoRunState(); } }
    }

    public bool IsAutoRunning => IsAutoSelecting;
    public bool HasAutoCheckSession => AutoCandidates.Count > 0;

    public string AutoPopupTitle => IsBlockcheckSession ? "blockcheck2 · " + (IsBlockcheckRunning ? Loc.T("Проверка DPI") : Loc.T("Результаты")) : IsAutoRunning
        ? Loc.T("Проверка стратегий")
        : Loc.T("Готово — выбор стратегии");

    public string AutoPopupSubtitle => IsBlockcheckSession
        ? Loc.T("Один профиль для этой сети. Комбинация правил перепроверяется на HTTPS, передаче видеоданных YouTube и WebSocket Gateway Discord. Голос не проверяется.") : IsAutoRunning
        ? Loc.T("WORK требует проверки выбранных сервисов: HTTPS, видеоданных YouTube и/или WebSocket Discord. Голос и длительное воспроизведение не проверяются.")
        : Loc.T("Проверка завершена. WORK подтверждает выбранные HTTPS-цели и передачу видеоданных YouTube и/или Gateway Discord. Голос не проверен.");

    private void RaiseAutoRunState()
    {
        OnPropertyChanged(nameof(IsAutoRunning));
        OnPropertyChanged(nameof(HasAutoCheckSession));
        OnPropertyChanged(nameof(AutoPopupTitle));
        OnPropertyChanged(nameof(AutoPopupSubtitle));
        OnPropertyChanged(nameof(AutoStatsSummary));
    }

    private string _autoStatusText = Loc.T("«Полный скан» проверит {0} стратегий каталога. «WORK-пул» перепроверит найденные стратегии.", ReferenceStrategyCatalog.All.Count);
    public string AutoStatusText { get => _autoStatusText; private set => SetField(ref _autoStatusText, value); }

    private void SetAutoStatus(string s) { AutoStatusText = s; SimpleStatus = s; }

    private AutoScope _selectedAutoScope = AutoScope.Both;
    public AutoScope SelectedAutoScope
    {
        get => _selectedAutoScope;
        set
        {
            if (IsAutoSelecting || IsTestingSelectedPreset) return;
            if (!SetField(ref _selectedAutoScope, value)) return;
            Settings.SelectedStrategyScope = value;
            _settingsSvc.Save();
            OnPropertyChanged(nameof(ScopeBoth));
            OnPropertyChanged(nameof(ScopeDiscord));
            OnPropertyChanged(nameof(ScopeYouTube));
            OnPropertyChanged(nameof(ScopeTitle));
            ResetAutoTargetsForScope();
            OnPropertyChanged(nameof(WorkPresetCount));
            OnPropertyChanged(nameof(UntestedPresetCount));
            OnPropertyChanged(nameof(CanonicalPresetCount));
            OnPropertyChanged(nameof(ProfilePoolSummary));
            OnPropertyChanged(nameof(AutoSelectPrimaryText));
            OnPropertyChanged(nameof(AutoSelectPrimaryTooltip));
            RaiseAutoRunState();
            RaiseCommandStates();
            ApplyPresetsFilter();
            UpdateMonitor();
        }
    }

    public bool ScopeBoth { get => SelectedAutoScope == AutoScope.Both; set { if (value) SelectedAutoScope = AutoScope.Both; } }
    public bool ScopeDiscord { get => SelectedAutoScope == AutoScope.Discord; set { if (value) SelectedAutoScope = AutoScope.Discord; } }
    public bool ScopeYouTube { get => SelectedAutoScope == AutoScope.YouTube; set { if (value) SelectedAutoScope = AutoScope.YouTube; } }

    public string ScopeTitle => Loc.T(SelectedAutoScope.Title());
    private IReadOnlyList<string> ActiveProbeTargets => SelectedAutoScope.GoalHosts();

    private void ResetAutoTargetsForScope()
    {
        AutoTargets.Clear();
        foreach (var h in ActiveProbeTargets) AutoTargets.Add(h);
        CheckTargets.Clear();
        foreach (var h in ActiveProbeTargets) CheckTargets.Add(new CheckTargetRow(h));
    }

    internal void SetAutoSelectPopupHarnessState()
    {
        AutoCandidates.Clear();
        for (int i = 1; i <= ReferenceStrategyCatalog.All.Count; i++)
        {
            var row = new AutoCandidateRow(Loc.T("Стратегия {0}", i));
            row.State = i < 14 ? AutoCandidateState.Done : i == 14 ? AutoCandidateState.Running : AutoCandidateState.Pending;
            AutoCandidates.Add(row);
        }

        AutoTargets.Clear();
        foreach (var h in ActiveProbeTargets) AutoTargets.Add(h);

        CheckTargets.Clear();
        foreach (var host in AutoTargets)
        {
            CheckTargets.Add(new CheckTargetRow(host)
            {
                Http = DiagStatus.Timeout,
                Tls12 = DiagStatus.Skip,
                Tls13 = DiagStatus.Skip
            });
        }

        PassedStrategies.Clear();
        Verdicts.Clear();
        IsAutoSelecting = true;
        CurrentCandidateName = Loc.T("GX-B03: Flowseal ALT10 Combo");
        AutoProbeDetail = "Видеоданные YouTube · замер 2/3 · 9 с · до тайм-аута 16 с\nYouTube: 1232 / 2048 КиБ · 4,85 Мбит/с · первые данные 380 мс";
        AutoProgress = 14d / ReferenceStrategyCatalog.All.Count;
        AutoProgressText = Loc.T("{0}/{1} стратегий", 14, ReferenceStrategyCatalog.All.Count);
        SetAutoStatus(Loc.T("Проверяю стратегию на {0} выбранных HTTPS-целях.", ActiveProbeTargets.Count));
        AutoPassedCount = 0;
        AutoFailedCount = 13;
    }

    private double _autoProgress;
    public double AutoProgress { get => _autoProgress; set => SetField(ref _autoProgress, value); }

    private string _autoProgressText = "";
    public string AutoProgressText { get => _autoProgressText; set => SetField(ref _autoProgressText, value); }

    private int _autoPassedCount;
    public int AutoPassedCount { get => _autoPassedCount; private set { if (SetField(ref _autoPassedCount, value)) OnPropertyChanged(nameof(AutoStatsSummary)); } }

    private int _autoFailedCount;
    public int AutoFailedCount { get => _autoFailedCount; private set { if (SetField(ref _autoFailedCount, value)) OnPropertyChanged(nameof(AutoStatsSummary)); } }
    private int _autoInconclusiveCount;
    public int AutoInconclusiveCount { get => _autoInconclusiveCount; private set { if (SetField(ref _autoInconclusiveCount, value)) OnPropertyChanged(nameof(AutoStatsSummary)); } }

    private static bool IsCanonicalPreset(Preset p) =>
        p.ReferenceId is not null && ReferenceStrategyCatalog.All.Any(r =>
            string.Equals(r.Id, p.ReferenceId, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<Preset> CanonicalPresets => Presets.Where(IsCanonicalPreset);

    public int CanonicalPresetCount => ReferenceStrategyCatalog.All.Count;
    public string FullScanButtonText => Loc.T("Полный скан ({0})", CanonicalPresetCount);
    public string FullScanTooltip => Loc.T("Проверить {0} стратегий: HTTPS, видеоданные YouTube и Gateway Discord для выбранных сервисов.", CanonicalPresetCount);
    public string CatalogDescription => Loc.T("Каталог: {0} стратегий, включая {1} дополнительных upstream-кандидатов", CanonicalPresetCount, ReferenceStrategyCatalog.Extensions.Count);
    public int WorkPresetCount => CanonicalPresets.Count(p => p.IsWorkFor(ActiveProbeTargets));
    private bool IsUntestedForActiveTargets(Preset p) => p.IsUntested || (p.IsWork && !p.IsWorkFor(ActiveProbeTargets));
    public int UntestedPresetCount => CanonicalPresets.Count(IsUntestedForActiveTargets);
    public int FailedPresetCount => CanonicalPresets.Count(p => p.IsFailed);
    public int BrokenPresetCount => FailedPresetCount;

    public string ProfilePoolSummary => Loc.T("Каталог: {0} · Все цели: {1} · Не прошли: {2} · Не проверены: {3}",
        CanonicalPresetCount, WorkPresetCount, FailedPresetCount, UntestedPresetCount);

    public string AutoSelectPrimaryText => WorkPresetCount > 0
        ? Loc.T("Выбрать из WORK ({0})", WorkPresetCount)
        : Loc.T("Быстрый подбор");

    public string AutoSelectPrimaryTooltip => WorkPresetCount > 0
        ? Loc.T("Выбрать проверенную рабочую стратегию из пула WORK.")
        : Loc.T("Для выбранных целей нет проверенных стратегий. Запустить быстрый подбор до первой рабочей стратегии.");

    public string AutoStatsSummary
    {
        get
        {
            int total = AutoCandidates.Count;
            if (IsBlockcheckSession) return IsBlockcheckRunning
                ? "blockcheck2 · " + CurrentCandidateName
                : Loc.T("blockcheck2 завершён · Проверенных стратегий: {0}", PassedStrategies.Count);
            if (total == 0) return "";
            int done = AutoCandidates.Count(c => c.IsDone);
            return Loc.T("Проверено: {0}/{1} · WORK: {2} · BROKEN: {3} · Не подтверждено: {4}", done, total, AutoPassedCount, AutoFailedCount, AutoInconclusiveCount);
        }
    }

    private string _autoProbeDetail = "";
    public string AutoProbeDetail { get => _autoProbeDetail; private set => SetField(ref _autoProbeDetail, value); }
    private int _probeProgressGeneration;
    private Action<string> CreateProbeProgress()
    {
        int generation = ++_probeProgressGeneration;
        AutoProbeDetail = "Подготовка проверки…";
        return text => OnUi(() =>
        {
            if (IsAutoRunning && generation == _probeProgressGeneration) AutoProbeDetail = text;
        });
    }

    private string _currentCandidateName = "";
    public string CurrentCandidateName
    {
        get => _currentCandidateName;
        private set { if (SetField(ref _currentCandidateName, value) && IsBlockcheckSession) OnPropertyChanged(nameof(AutoStatsSummary)); }
    }

    private int _autoCursor = -1;

    private void MarkCandidateRunning(string name)
    {
        CurrentCandidateName = name;
        CheckTargets.Clear();
        foreach (var h in ActiveProbeTargets)
            CheckTargets.Add(new CheckTargetRow(h));

        for (int i = 0; i < AutoCandidates.Count; i++)
        {
            if (AutoCandidates[i].Name == name)
            {
                AutoCandidates[i].State = AutoCandidateState.Running;
                _autoCursor = i;
                return;
            }
        }

        var newRow = new AutoCandidateRow(name) { State = AutoCandidateState.Running };
        AutoCandidates.Add(newRow);
        _autoCursor = AutoCandidates.Count - 1;
    }

    private void OnHostProbed(string host, bool ok)
    {
        foreach (var t in CheckTargets)
        {
            if (string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                var status = ok ? DiagStatus.Ok : DiagStatus.Fail;
                t.Http = status;
                t.Tls12 = DiagStatus.Skip;
                t.Tls13 = DiagStatus.Skip;
                return;
            }
        }
    }

    private ProbeLaunchOptions CurrentProbeLaunchOptions => new(ActiveHostlistPath, Settings.GameFilter,
        Settings.LeagueTcp2099Bypass, Settings.RiotEntitlementsBypass);

    private void EnsureProbeContext(string network, AutoScope scope, string optionsKey)
    {
        if (NetworkFingerprint.Current() != network || SelectedAutoScope != scope ||
            CurrentProbeLaunchOptions.VerificationKey(EffectiveBypassAllSites, Settings.DisableQuic) != optionsKey)
            throw new InvalidOperationException("Сеть, цели или настройки изменились во время проверки. Повторите скан.");
    }

    internal static AutoScore ScoreFromCanonicalResult(Canonical5ProbeResult res) =>
        new(
            res.StrategyName,
            res.PassCount,
            res.TotalCount - res.PassCount,
            res.TotalCount,
            Hosts: res.Probes
                .Select(p => new AutoHostResult(
                    p.Key,
                    DiagStatus.Skip,
                    DiagStatus.Skip,
                    p.Value ? DiagStatus.Ok : DiagStatus.Fail))
                .ToList(),
            ApplicationVerified: res.IsWork,
            VerificationSummary: res.Summary);

    private void ShowCanonicalProbeResult(Canonical5ProbeResult res)
    {
        CurrentCandidateName = res.StrategyName;
        CheckTargets.Clear();
        foreach (var target in ActiveProbeTargets)
        {
            bool ok = res.Probes.TryGetValue(target, out bool passed) && passed;
            CheckTargets.Add(new CheckTargetRow(target)
            {
                Http = ok ? DiagStatus.Ok : DiagStatus.Fail,
                Tls12 = DiagStatus.Skip,
                Tls13 = DiagStatus.Skip
            });
        }
    }

    /// <summary>
    /// Deterministic ordering of WORK strategies:
    /// Repeated service checks must pass; prefer lower worst-request latency, then catalog order.
    /// </summary>
    public Preset? GetBestWorkPreset()
    {
        var workPresets = _presets.ReferencePresets.Where(p => p.IsWorkFor(ActiveProbeTargets)).ToList();
        if (workPresets.Count == 0) return null;

        var refOrder = ReferenceStrategyCatalog.All
            .Select((r, idx) => (r.Id, Index: idx))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.OrdinalIgnoreCase);

        return workPresets
            .OrderByDescending(p => SelectedAutoScope != AutoScope.Discord ? p.VerifiedVideoMbps ?? -1 : 0)
            .ThenBy(p => p.LatencyMs > 0 ? p.LatencyMs : long.MaxValue)
            .ThenBy(p => p.ReferenceId != null && refOrder.TryGetValue(p.ReferenceId, out int idx) ? idx : 999)
            .FirstOrDefault();
    }

    /// <summary>
    /// AutoSelect: Selects the best strategy from WORK pool.
    /// If WORK pool is empty, displays message: "Рабочие стратегии не найдены. Выполните полный скан."
    /// </summary>
    public async Task RunAutoSelectAsync(bool showWindow = true)
    {
        if (IsAutoSelecting || IsTestingSelectedPreset || IsTransitioning) return;

        ReloadPresets(NetworkFingerprint.Current() ?? "default");
        var best = GetBestWorkPreset();
        if (best is not null)
        {
            SelectedPreset = Presets.FirstOrDefault(p => p.Fingerprint == best.Fingerprint) ?? best;
            SetAutoStatus(Loc.T("Выбрана рабочая стратегия «{0}» ({1}).", best.Name, best.ProbeScore));
            Notify?.Invoke("GXBRIEL_ZAPRET", AutoStatusText);
            if (CanApplyStrategy) await ApplyStrategyAsync();
            return;
        }

        // WORK pool is empty: open the visible verification flow in Fast Pick mode (stop on first working strategy).
        SetAutoStatus(Loc.T("Рабочих стратегий для этой сети пока нет. Запускаю быстрый подбор…"));
        Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Запуск быстрого подбора стратегии для текущей сети…"));
        if (showWindow)
            await RunFullScanAsync(stopOnFirstPerfect: true);
    }

    /// <summary>
    /// Scans reference baseline strategies against mandatory endpoints.
    /// stopOnFirstPerfect = true: Fast Pick mode, compares up to two verified WORK candidates
    /// (with bounded lookahead after the first WORK) instead of stalling on 40+ broken entries.
    /// stopOnFirstPerfect = false: Full Scan mode, scans the complete catalog.
    /// </summary>
    public async Task RunFullScanAsync(bool stopOnFirstPerfect = false)
    {
        if (IsInitializing || IsAutoSelecting || IsTestingSelectedPreset || IsTransitioning || IsDiagnosing) return;
        IsBlockcheckSession = false;
        if (!_updater.IsEngineInstalled)
        {
            SetAutoStatus(Loc.T("Движок ещё не установлен — дождитесь загрузки."));
            return;
        }

        string networkFp = NetworkFingerprint.Current() ?? "default";
        var scope = SelectedAutoScope;
        var targets = scope.GoalHosts();
        var rawCandidates = _presets.ReferencePresets.ToList();
        var candidates = stopOnFirstPerfect
            ? BuildFastPickOrder(rawCandidates, targets)
            : rawCandidates;
        var launchOptions = CurrentProbeLaunchOptions;
        bool bypassAll = EffectiveBypassAllSites, disableQuic = Settings.DisableQuic;
        string optionsKey = launchOptions.VerificationKey(bypassAll, disableQuic);

        AutoCandidates.Clear();
        foreach (var c in candidates)
        {
            AutoCandidates.Add(new AutoCandidateRow(c.Name));
        }

        AutoTargets.Clear();
        foreach (var h in ActiveProbeTargets) AutoTargets.Add(h);
        CheckTargets.Clear();
        PassedStrategies.Clear();
        Verdicts.Clear();
        AutoPassedCount = 0;
        AutoFailedCount = 0;
        AutoInconclusiveCount = 0;
        CurrentCandidateName = "";
        _autoCursor = -1;
        AutoProgress = 0;
        AutoProgressText = Loc.T("0/{0} стратегий", candidates.Count);
        OnPropertyChanged(nameof(AutoStatsSummary));

        IsAutoSelecting = true;
        _autoCts = new CancellationTokenSource();
        _healthMonitor.StopWatchdog();
        _healthMonitor.Reset();


        var previousPreset = RunningPreset ?? _engine.ActivePreset;
        bool wasRunning = IsRunning;
        bool wasAttachedExternal = _engine.IsAttachedToExternalProcess;
        bool startWinner = false;
        try
        {
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Проверка стратегий запущена. Окно можно свернуть."));
            AutoCheckStarted?.Invoke();
            if (wasRunning)
            {
                AppendLog(wasAttachedExternal
                    ? Loc.T("Остановка старого managed winws перед сканированием…")
                    : Loc.T("Остановка движка для сканирования…"));
                await Task.Run(() => _engine.Stop());
                await Task.Delay(300, _autoCts.Token);
            }

            SetAutoStatus(Loc.T("Очищаю окружение перед сканированием…"));
            var cleanup = await new BypassCleanupService().StopAndVerifyCleanAsync(_autoCts.Token);
            if (!cleanup.Clean)
            {
                SetAutoStatus(cleanup.Message);
                ShowErrorModal(
                    Loc.T("Окружение не очищено"),
                    Loc.T("Скан остановлен: перед проверкой не должно быть активного winws/GoodbyeDPI."),
                    string.Join(Environment.NewLine, cleanup.Remaining));
                return;
            }

            using var probeSvc = new Canonical5ProbeService();

            int? firstWorkIndex = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                _autoCts.Token.ThrowIfCancellationRequested();
                var preset = candidates[i];
                EnsureProbeContext(networkFp, scope, optionsKey);
                MarkCandidateRunning(preset.Name);
                SetAutoStatus(stopOnFirstPerfect
                    ? Loc.T("[{0}/{1}] Быстрый подбор: {2}…", i + 1, candidates.Count, preset.Name)
                    : Loc.T("[{0}/{1}] Проверка: {2}…", i + 1, candidates.Count, preset.Name));

                var res = await probeSvc.EvaluateCandidateAsync(
                    preset,
                    (host, ok) => OnUi(() => OnHostProbed(host, ok)),
                    _autoCts.Token,
                    bypassAll, targets, disableQuic, scope, launchOptions, CreateProbeProgress());

                EnsureProbeContext(networkFp, scope, optionsKey);

                ++_probeProgressGeneration;
                AutoProbeDetail = res.Flows?.Summary ?? res.Summary;
                ShowCanonicalProbeResult(res);
                AppendLog(res.StrategyName + ": " + res.Summary);
                if (res.CheckUnavailable) throw new InvalidOperationException(res.Summary);

                bool isTargetWork = res.IsWork && res.Flows?.Passes(scope) == true && res.PassCount == res.TotalCount
                    && ActiveProbeTargets.All(t => res.Probes.TryGetValue(t, out bool ok) && ok);

                string newStatus = res.Status;
                string reason = isTargetWork
                    ? $"verified_{scope.Title()}_{res.PassCount}_of_{res.TotalCount}"
                    : $"{(res.IsInconclusive ? "inconclusive" : "failed")}_{scope.Title()}_{res.PassCount}_of_{res.TotalCount}";

                _presets.UpdateStrategyProbeState(
                    preset,
                    newStatus,
                    reason,
                    res.Probes,
                    res.PassCount,
                    res.TotalCount,
                    res.LatencyMs,
                    networkFp, res.Flows, optionsKey);

                var score = ScoreFromCanonicalResult(res);
                if (_autoCursor >= 0 && _autoCursor < AutoCandidates.Count)
                {
                    AutoCandidates[_autoCursor].Apply(score);
                }

                AutoProgress = (double)(i + 1) / candidates.Count;
                AutoProgressText = Loc.T("{0}/{1} стратегий", i + 1, candidates.Count);

                if (isTargetWork)
                {
                    AutoPassedCount++;
                    PassedStrategies.Add(score);
                    firstWorkIndex ??= i;
                    if (stopOnFirstPerfect && AutoPassedCount >= 2)
                    {
                        AppendLog("[Быстрый подбор] Найдены две подтверждённые WORK-стратегии; выбирается лучшая по скорости видео и задержке.");
                        break;
                    }
                }
                else if (res.IsInconclusive)
                {
                    AutoInconclusiveCount++;
                }
                else
                {
                    AutoFailedCount++;
                }

                if (stopOnFirstPerfect && firstWorkIndex.HasValue && i - firstWorkIndex.Value >= 2)
                {
                    AppendLog("[Быстрый подбор] Рабочая стратегия найдена и проверена с запасом; завершаю подбор.");
                    break;
                }

                AutoProgress = (double)(i + 1) / candidates.Count;
                AutoProgressText = Loc.T("{0}/{1} стратегий", i + 1, candidates.Count);
                OnPropertyChanged(nameof(AutoStatsSummary));
            }

            ReloadPresets(networkFp);

            int workCount = WorkPresetCount;
            if (workCount > 0)
            {
                var winner = GetBestWorkPreset();
                if (winner is not null)
                {
                    SelectedPreset = Presets.FirstOrDefault(p => p.Fingerprint == winner.Fingerprint) ?? winner;
                    if (PassedStrategies.LastOrDefault(s => string.Equals(s.Name, winner.Name, StringComparison.OrdinalIgnoreCase)) is { } winnerScore)
                    {
                        CurrentCandidateName = winnerScore.Name;
                        CheckTargets.Clear();
                        foreach (var h in winnerScore.HostList)
                        {
                            CheckTargets.Add(new CheckTargetRow(h.Host)
                            {
                                Http = h.Https,
                                Tls12 = h.Tls12,
                                Tls13 = h.Tls13
                            });
                        }
                    }
                    SetAutoStatus(stopOnFirstPerfect
                        ? Loc.T("Быстрый подбор завершён: выбрана «{0}».", winner.Name)
                        : Loc.T("Полный скан: {0} WORK / {1} BROKEN / {2} не подтверждено. Выбрана «{3}».",
                            workCount, AutoFailedCount, AutoInconclusiveCount, winner.Name));
                    Notify?.Invoke("GXBRIEL_ZAPRET", AutoStatusText);
                    startWinner = true;
                }
            }
            else
            {
                FilterMode = PresetFilterMode.All;
                SelectedPreset = Presets.FirstOrDefault(p => p.IsWorkFor(ActiveProbeTargets))
                                  ?? Presets.FirstOrDefault();
                SetAutoStatus(Loc.T("Скан: {0} BROKEN / {1} не подтверждено. WORK не найден; подробные причины — в журнале.", AutoFailedCount, AutoInconclusiveCount));
                Notify?.Invoke("GXBRIEL_ZAPRET", AutoStatusText);
            }
        }
        catch (OperationCanceledException)
        {
            SetAutoStatus(stopOnFirstPerfect
                ? Loc.T("Быстрый подбор остановлен пользователем.")
                : Loc.T("Полный скан остановлен пользователем."));
            ReloadPresets(networkFp);
        }
        catch (Exception ex)
        {
            SetAutoStatus(stopOnFirstPerfect
                ? Loc.T("Ошибка быстрого подбора: {0}", ex.Message)
                : Loc.T("Ошибка полного скана: {0}", ex.Message));
            ReloadPresets(networkFp);
        }
        finally
        {
            IsAutoSelecting = false;
            _autoCts?.Dispose();
            _autoCts = null;
            AutoCheckFinished?.Invoke();
            if (!startWinner && wasRunning && previousPreset is not null)
                SelectedPreset = previousPreset;
            if ((startWinner || (wasRunning && previousPreset is not null)) && CanStart)
                await StartAsync();
        }
    }

    /// <summary>
    /// RECHECK WORK: Tests ONLY strategies currently in the WORK pool.
    /// If 5/5 PASS -> remains WORK.
    /// If < 5/5 -> demoted to BROKEN (WORK pool decreases).
    /// BROKEN and UNTESTED do NOT participate.
    /// </summary>
    public async Task RecheckWorkPoolAsync()
    {
        if (IsInitializing || IsAutoSelecting || IsTestingSelectedPreset || IsTransitioning || IsDiagnosing) return;
        IsBlockcheckSession = false;
        if (!_updater.IsEngineInstalled)
        {
            SetAutoStatus(Loc.T("Движок ещё не установлен."));
            return;
        }

        string networkFp = NetworkFingerprint.Current() ?? "default";
        ReloadPresets(networkFp);
        var workCandidates = _presets.ReferencePresets.Where(p => p.IsWorkFor(ActiveProbeTargets)).ToList();
        var scope = SelectedAutoScope;
        var targets = scope.GoalHosts();
        var launchOptions = CurrentProbeLaunchOptions;
        bool bypassAll = EffectiveBypassAllSites, disableQuic = Settings.DisableQuic;
        string optionsKey = launchOptions.VerificationKey(bypassAll, disableQuic);
        if (workCandidates.Count == 0)
        {
            SetAutoStatus(Loc.T("Рабочие стратегии не найдены. Выполните полный скан."));
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Рабочие стратегии не найдены. Выполните полный скан."));
            return;
        }

        AutoCandidates.Clear();
        foreach (var c in workCandidates)
        {
            AutoCandidates.Add(new AutoCandidateRow(c.Name));
        }

        AutoTargets.Clear();
        foreach (var h in ActiveProbeTargets) AutoTargets.Add(h);
        CheckTargets.Clear();
        PassedStrategies.Clear();
        Verdicts.Clear();
        AutoPassedCount = 0;
        AutoFailedCount = 0;
        AutoInconclusiveCount = 0;
        CurrentCandidateName = "";
        _autoCursor = -1;
        AutoProgress = 0;
        AutoProgressText = Loc.T("0/{0} WORK", workCandidates.Count);
        OnPropertyChanged(nameof(AutoStatsSummary));

        IsAutoSelecting = true;
        _autoCts = new CancellationTokenSource();
        _healthMonitor.StopWatchdog();
        _healthMonitor.Reset();


        var previousPreset = RunningPreset ?? _engine.ActivePreset;
        bool wasRunning = IsRunning;
        bool startWinner = false;
        try
        {
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Перепроверка WORK-стратегий запущена. Окно можно свернуть."));
            AutoCheckStarted?.Invoke();
            if (wasRunning)
            {
                AppendLog(Loc.T("Остановка движка для перепроверки WORK…"));
                await Task.Run(() => _engine.Stop());
                await Task.Delay(300, _autoCts.Token);
            }

            var cleanup = await new BypassCleanupService().StopAndVerifyCleanAsync(_autoCts.Token);
            if (!cleanup.Clean)
            {
                SetAutoStatus(cleanup.Message);
                return;
            }

            using var probeSvc = new Canonical5ProbeService();

            for (int i = 0; i < workCandidates.Count; i++)
            {
                _autoCts.Token.ThrowIfCancellationRequested();
                var preset = workCandidates[i];
                EnsureProbeContext(networkFp, scope, optionsKey);
                MarkCandidateRunning(preset.Name);
                SetAutoStatus(Loc.T("[{0}/{1}] Recheck WORK: {2}…", i + 1, workCandidates.Count, preset.Name));

                var res = await probeSvc.EvaluateCandidateAsync(
                    preset,
                    (host, ok) => OnUi(() => OnHostProbed(host, ok)),
                    _autoCts.Token,
                    bypassAll, targets, disableQuic, scope, launchOptions, CreateProbeProgress());

                EnsureProbeContext(networkFp, scope, optionsKey);

                ++_probeProgressGeneration;
                AutoProbeDetail = res.Flows?.Summary ?? res.Summary;
                ShowCanonicalProbeResult(res);
                AppendLog(res.StrategyName + ": " + res.Summary);
                if (res.CheckUnavailable) throw new InvalidOperationException(res.Summary);

                bool isTargetWork = res.IsWork && res.Flows?.Passes(scope) == true && res.PassCount == res.TotalCount
                    && ActiveProbeTargets.All(t => res.Probes.TryGetValue(t, out bool ok) && ok);

                string newStatus = res.Status;
                string reason = isTargetWork
                    ? $"verified_{scope.Title()}_{res.PassCount}_of_{res.TotalCount}"
                    : $"{(res.IsInconclusive ? "inconclusive" : "failed")}_{scope.Title()}_{res.PassCount}_of_{res.TotalCount}";

                _presets.UpdateStrategyProbeState(
                    preset,
                    newStatus,
                    reason,
                    res.Probes,
                    res.PassCount,
                    res.TotalCount,
                    res.LatencyMs,
                    networkFp, res.Flows, optionsKey);

                var score = ScoreFromCanonicalResult(res);
                if (_autoCursor >= 0 && _autoCursor < AutoCandidates.Count)
                {
                    AutoCandidates[_autoCursor].Apply(score);
                }

                if (isTargetWork)
                {
                    AutoPassedCount++;
                    PassedStrategies.Add(score);
                }
                else if (res.IsInconclusive)
                {
                    AutoInconclusiveCount++;
                }
                else
                {
                    AutoFailedCount++;
                }

                AutoProgress = (double)(i + 1) / workCandidates.Count;
                AutoProgressText = Loc.T("{0}/{1} WORK", i + 1, workCandidates.Count);
                OnPropertyChanged(nameof(AutoStatsSummary));
            }

            ReloadPresets();

            int remainingWork = WorkPresetCount;
            if (remainingWork > 0)
            {
                var winner = GetBestWorkPreset();
                if (winner is not null)
                {
                    SelectedPreset = Presets.FirstOrDefault(p => p.Fingerprint == winner.Fingerprint) ?? winner;
                    if (PassedStrategies.LastOrDefault(s => string.Equals(s.Name, winner.Name, StringComparison.OrdinalIgnoreCase)) is { } winnerScore)
                    {
                        CurrentCandidateName = winnerScore.Name;
                        CheckTargets.Clear();
                        foreach (var h in winnerScore.HostList)
                        {
                            CheckTargets.Add(new CheckTargetRow(h.Host)
                            {
                                Http = h.Https,
                                Tls12 = h.Tls12,
                                Tls13 = h.Tls13
                            });
                        }
                    }
                    SetAutoStatus(Loc.T("Recheck завершён: {0} осталось в WORK. Выбрана «{1}».", remainingWork, winner.Name));
                    Notify?.Invoke("GXBRIEL_ZAPRET", AutoStatusText);
                    startWinner = true;
                }
            }
            else
            {
                SetAutoStatus(Loc.T("Рабочие стратегии не найдены. Выполните полный скан."));
                Notify?.Invoke("GXBRIEL_ZAPRET", AutoStatusText);
            }
        }
        catch (OperationCanceledException)
        {
            SetAutoStatus(Loc.T("Recheck остановлен пользователем."));
            ReloadPresets();
        }
        catch (Exception ex)
        {
            SetAutoStatus(Loc.T("Ошибка Recheck: {0}", ex.Message));
            ReloadPresets();
        }
        finally
        {
            IsAutoSelecting = false;
            _autoCts?.Dispose();
            _autoCts = null;
            AutoCheckFinished?.Invoke();
            if (!startWinner && wasRunning && previousPreset is not null)
                SelectedPreset = previousPreset;
            if ((startWinner || (wasRunning && previousPreset is not null)) && CanStart)
                await StartAsync();
        }
    }

    /// <summary>
    /// Handles Watchdog failure without mutating the verified WORK pool. A short connectivity drop,
    /// startup race, DNS hiccup, or provider wobble must not erase Full Scan results.
    /// </summary>
    public async Task HandleActiveStrategyFailureAsync()
    {
        if (!Settings.AutoHeal || !IsRunning || IsAutoSelecting || IsTestingSelectedPreset || IsTransitioning) return;

        await Task.Yield();
        if (!Settings.AutoHeal || !IsRunning || IsAutoSelecting || IsTestingSelectedPreset || IsTransitioning) return;

        var currentFp = (RunningPreset ?? _engine.ActivePreset)?.Fingerprint;
        var refOrder = ReferenceStrategyCatalog.All
            .Select((r, idx) => (r.Id, Index: idx))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var nextWork = _presets.ReferencePresets
            .Where(p => p.IsWorkFor(ActiveProbeTargets) && (string.IsNullOrEmpty(currentFp) ||
                                     !string.Equals(p.Fingerprint, currentFp, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.ReferenceId != null && refOrder.TryGetValue(p.ReferenceId, out int idx) ? idx : 999)
            .FirstOrDefault();

        if (nextWork is not null)
        {
            SelectedPreset = Presets.FirstOrDefault(p => p.Fingerprint == nextWork.Fingerprint) ?? nextWork;
            AppendLog(Loc.T("[Watchdog] Переключение на рабочий пресет без изменения WORK-пула: «{0}»…", nextWork.Name));
            if (CanApplyStrategy && Settings.AutoHeal) await ApplyStrategyAsync();
        }
        else
        {
            AppendLog(Loc.T("[Watchdog] Рабочие стратегии не найдены. Выполните полный скан; текущий пул не изменялся."));
            SetAutoStatus(Loc.T("Рабочие стратегии не найдены. Выполните полный скан."));
        }
    }

    internal static List<Preset> BuildFastPickOrder(IReadOnlyList<Preset> candidates, IReadOnlyList<string> targets)
    {
        var priorityIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["GX-B01"] = 1,
            ["GX-C16"] = 2,
            ["GX-C15"] = 3,
            ["GX-B04"] = 4,
            ["GX-B03"] = 5,
            ["GX-B05"] = 6,
            ["GX-B06"] = 7,
            ["GX-B02"] = 8
        };

        return candidates
            .Select((preset, index) => (
                preset,
                index,
                isWork: preset.IsWorkFor(targets) ? 0 : 1,
                tier: preset.ReferenceId is { } id && priorityIds.TryGetValue(id, out int rank) ? rank : 100))
            .OrderBy(x => x.isWork)
            .ThenBy(x => x.tier)
            .ThenBy(x => x.index)
            .Select(x => x.preset)
            .ToList();
    }
}
