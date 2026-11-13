using System.Windows;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.ViewModels;

/// <summary>
/// Engine state and the start/stop actions that drive winws2.
/// </summary>
public sealed partial class MainViewModel
{
    // ---- engine state ------------------------------------------------------

    private System.Windows.Threading.DispatcherTimer? _uptimeTimer;
    private DateTime? _runningStartTime;
    private string _runningDurationFormatted = "";
    public string RunningDurationFormatted
    {
        get => _runningDurationFormatted;
        private set => SetField(ref _runningDurationFormatted, value);
    }

    private void UpdateUptimeTimer(EngineState state)
    {
        if (state == EngineState.Running)
        {
            // When attached to an external autostart process we don't know how long it has been
            // running, so avoid the misleading "Работает 00:01" that starts from zero.
            // Show a static badge instead; the uptime timer stays stopped.
            if (_engine.IsAttachedToExternalProcess)
            {
                _uptimeTimer?.Stop();
                _runningStartTime = null;
                RunningDurationFormatted = Loc.T("⚡ Автозапуск");
                return;
            }

            if (_runningStartTime is null) _runningStartTime = DateTime.UtcNow;
            if (_uptimeTimer is null)
            {
                _uptimeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _uptimeTimer.Tick += (_, _) =>
                {
                    if (_runningStartTime is not null)
                    {
                        var elapsed = DateTime.UtcNow - _runningStartTime.Value;
                        RunningDurationFormatted = elapsed.TotalHours >= 1
                            ? $"Работает {elapsed:hh\\:mm\\:ss}"
                            : $"Работает {elapsed:mm\\:ss}";
                    }
                };
            }
            _uptimeTimer.Start();
            RunningDurationFormatted = "Работает 00:01";
        }
        else
        {
            _uptimeTimer?.Stop();
            _runningStartTime = null;
            RunningDurationFormatted = "";
        }
    }

    private EngineState _state = EngineState.Stopped;
    public EngineState State
    {
        get => _state;
        internal set
        {
            if (SetField(ref _state, value))
            {
                UpdateUptimeTimer(value);
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsManagedAutostartRunning));
                OnPropertyChanged(nameof(IsStarting));
                OnPropertyChanged(nameof(IsStopping));
                OnPropertyChanged(nameof(IsTransitioning));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(DiagEngineNote));
                OnPropertyChanged(nameof(RunningDurationFormatted));
                OnPropertyChanged(nameof(HeroButtonText));
                OnPropertyChanged(nameof(HeroSubtitleText));
                OnPropertyChanged(nameof(StrategyCardBadgeText));
                OnPropertyChanged(nameof(StrategyCardBadgeStatus));
                OnPropertyChanged(nameof(ActiveStrategyDisplayName));
                OnPropertyChanged(nameof(ActiveStrategySimpleName));
                OnPropertyChanged(nameof(ActiveStrategySubtitle));
                OnPropertyChanged(nameof(ActiveStrategySimpleSubtitle));
                RaiseCommandStates();
                // Once the engine is fully stopped, nothing is running anymore.
                if (value == EngineState.Stopped)
                {
                    _healthMonitor.Reset();
                    RunningPreset = null;
                    EngineStateStore.Save(new EngineStateRecord
                    {
                        BypassActive = false,
                        ActivePresetName = null,
                        ActiveHostlist = null,
                        LastStoppedUtc = DateTime.UtcNow,
                        ProcessId = null
                    });
                    Settings.BypassActual = "STOPPED";
                    _settingsSvc.Save();
                }
                // Engine came up with a preset → remember it for this network (re-suggested next time).
                if (value == EngineState.Running)
                {
                    RunningPreset = _engine.ActivePreset ?? RunningPreset;
                    RememberNetworkStrategy();
                    EngineStateStore.Save(new EngineStateRecord
                    {
                        BypassActive = true,
                        ActivePresetName = RunningPreset?.Name,
                        ActiveHostlist = _engine.ActiveHostlistPath is { } activeList
                            ? System.IO.Path.GetFileNameWithoutExtension(activeList) : null,
                        LastStartedUtc = DateTime.UtcNow,
                        ProcessId = _engine.ActiveProcessId
                    });
                    Settings.BypassActual = "RUNNING";
                    if (RunningPreset?.Name is { } pName)
                    {
                        Settings.ActivePresetId = pName;
                    }
                    _settingsSvc.Save();
                }
                OnPropertyChanged(nameof(IsSelectedPresetRunning));
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(CanApplyStrategy));
                OnPropertyChanged(nameof(ActivatePresetButtonText));
                OnPropertyChanged(nameof(RunStatusText));
                OnPropertyChanged(nameof(ExternalEngineStatusText));
                UpdateMonitor();
                RaiseCommandStates();
            }
        }
    }

    public bool IsRunning => State == EngineState.Running;
    public bool IsManagedAutostartRunning => IsRunning && _engine.IsAttachedToExternalProcess;
    public bool IsStarting => State == EngineState.Starting;
    public bool IsStopping => State == EngineState.Stopping;
    public bool IsTransitioning => State is EngineState.Starting or EngineState.Stopping;
    public bool CanStart => State == EngineState.Stopped && !IsRecoveringEngine && !IsInitializing && !IsDownloadingEngine && !IsAutoSelecting && !IsTestingSelectedPreset && _updater.IsEngineInstalled;
    public bool CanStop => State == EngineState.Running || IsRecoveringEngine;

    public string HeroButtonText => IsRecoveringEngine && State == EngineState.Stopped
        ? Loc.T("Отменить восстановление") : State switch
    {
        EngineState.Starting => Loc.T("Запуск…"),
        EngineState.Running => Loc.T("Остановить обход"),
        EngineState.Stopping => Loc.T("Остановка…"),
        _ => Loc.T("Включить обход"),
    };

    public string HeroSubtitleText
    {
        get
        {
            if (IsRecoveringEngine && State == EngineState.Stopped)
                return Loc.T("Движок остановлен. Ожидается автоматическое восстановление.");
            if (State == EngineState.Starting)
                return Loc.T("Инициализация драйвера WinDivert и применение стратегии «{0}»…", SelectedPresetOrHint);
            if (State == EngineState.Stopping)
                return Loc.T("Остановка перехвата пакетов и освобождение сетевого стека…");
            if (State == EngineState.Running)
            {
                if (IsManagedAutostartRunning)
                    return Loc.T("Активен через автозапуск Windows (GXBRIEL_ZAPRET_Autostart) · PID {0}", _engine.ActiveProcessId?.ToString() ?? "?");
                string activeName = RunningPreset is { } rp ? Loc.T(rp.Name) : SelectedPresetOrHint;
                return Loc.T("Движок запущен · Активна стратегия «{0}» · Доступность сервисов проверяется отдельно", activeName);
            }
            if (!string.IsNullOrWhiteSpace(SimpleStatus))
                return SimpleStatus;
            return Loc.T("Обход DPI сейчас выключен · Выбрана стратегия «{0}» для {1}", SelectedPresetOrHint, ScopeTitle);
        }
    }

    // Engine state for the manual environment check (Настройки → «Проверить окружение»), which lives
    // in the view: showing a dialog is a view concern, but only the VM owns the updater.
    public bool IsEngineInstalled => _updater.IsEngineInstalled;
    public bool IsEngineComplete => _updater.IsEngineComplete;

    public string ExternalEngineStatusText =>
        IsManagedAutostartRunning
            ? Loc.T("Обход запущен через автозапуск Windows (PID {0}). Кнопка «Остановить» завершит этот процесс — после этого можно выбрать другой пресет и запустить его вручную.", _engine.ActiveProcessId?.ToString() ?? "?")
            : "";

    internal void SetSimulatedStateForHarness(EngineState state)
    {
        AppPaths.EnsureCreated();
        if (state == EngineState.Running && SelectedPreset is not null)
            RunningPreset = SelectedPreset;
        State = state;
    }

    // ---- actions -----------------------------------------------------------

    private void Start()
    {
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        _unexpectedRecovery.Cancel();
        if (!await _engineTransitionGate.WaitAsync(0)) return;
        try { await StartCoreAsync(); }
        finally { _engineTransitionGate.Release(); RaiseCommandStates(); }
    }

    private async Task StartCoreAsync()
    {
        if (IsInitializing || IsAutoSelecting || IsTestingSelectedPreset) return;
        if (SelectedPreset is null)
        {
            SelectedPreset = RecommendedPreset
                             ?? Presets.FirstOrDefault(p => p.IsWorkFor(ActiveProbeTargets))
                             ?? PresetsView?.Cast<Models.Preset>().FirstOrDefault()
                             ?? Presets.FirstOrDefault();
        }

        if (SelectedPreset is null)
        {
            AppendLog(Loc.T("Не выбран пресет."));
            return;
        }
        if (IsTransitioning || IsRunning) return;

        var preset = SelectedPreset;
        string? hostlistPath = !string.IsNullOrWhiteSpace(ActiveHostlistPath) ? ActiveHostlistPath : null;

        // Explicit user action: persist desired intention
        Settings.DesiredBypassEnabled = true;
        Settings.ActivePresetId = preset.Name;
        Settings.ActivePresetName = preset.Name;
        _settingsSvc.Save();

        try
        {
            State = EngineState.Starting;
            await Task.Delay(150); // allow smooth UI transition (Включить -> Запуск... -> Работает)

            var cleanup = await new BypassCleanupService().StopAndVerifyCleanAsync(CancellationToken.None);
            AppendLog(cleanup.Message);
            foreach (string remaining in cleanup.Remaining)
                AppendLog(Loc.T("Остался конфликтующий обход: {0}", remaining));
            if (!cleanup.Clean) throw new InvalidOperationException(cleanup.Message);

            _engine.GameFilter = Settings.GameFilter;
            _engine.LeagueTcp2099Bypass = Settings.LeagueTcp2099Bypass;
            _engine.RiotEntitlementsBypass = Settings.RiotEntitlementsBypass;
            _engine.BypassAllSites = EffectiveBypassAllSites;
            _engine.DisableQuic = Settings.DisableQuic;
            _engine.CoverTgProxy = false;
            _engine.DebugLog = Settings.DebugLog;

            await Task.Run(() => _engine.Start(preset, hostlistPath));
            State = _engine.State;
            if (State == EngineState.Running)
            {
                if (!_engine.ActiveProcessId.HasValue)
                    throw new InvalidOperationException("Движок сообщил о запуске, но его процесс не найден.");
                RunningPreset = preset;
                if (Settings.AutoHeal)
                    _healthMonitor.StartWatchdog(SelectedAutoScope);
                else
                    _healthMonitor.StopWatchdog();

                _ = _healthMonitor.ProbeAllAsync(CancellationToken.None);
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Обход включён: «{0}»", Loc.T(preset.Name)));
            }
            else
            {
                RunningPreset = null;
                _healthMonitor.StopWatchdog();
                _healthMonitor.Reset();
            }
        }
        catch (Exception ex)
        {
            RunningPreset = null;
            _healthMonitor.StopWatchdog();
            _healthMonitor.Reset();
            State = EngineState.Stopped;
            AppendLog(Loc.T("Ошибка запуска: {0}", ex.Message));
            ShowErrorModal(
                Loc.T("Не удалось запустить обход DPI"),
                Loc.T("Движок winws2 не смог запуститься. Обычно это происходит, если другой обход или антивирус блокирует драйвер WinDivert, либо отсутствуют файлы движка."),
                ex.ToString(),
                Loc.T("Открыть журнал"),
                () => SelectedTabIndex = 4);
        }
        finally
        {
            if (State == EngineState.Starting)
                State = _engine.State == EngineState.Starting ? EngineState.Stopped : _engine.State;
            if (State != EngineState.Running)
            {
                RunningPreset = null;
                _healthMonitor.StopWatchdog();
                _healthMonitor.Reset();
            }
            RaiseCommandStates();
        }
    }

    private async Task StopEngineSmoothAsync()
    {
        _unexpectedRecovery.Cancel();
        Settings.DesiredBypassEnabled = false;
        _settingsSvc.Save();
        if (!await _engineTransitionGate.WaitAsync(0)) return;
        try { await StopEngineCoreAsync(); }
        finally { _engineTransitionGate.Release(); RaiseCommandStates(); }
    }

    private async Task StopEngineCoreAsync(bool clearDesired = true)
    {
        if (State == EngineState.Stopped || IsStopping) return;

        _healthMonitor.StopWatchdog();
        _healthMonitor.Reset();

        // Explicit user action: user intentionally stopped bypass
        if (clearDesired)
        {
            Settings.DesiredBypassEnabled = false;
            _settingsSvc.Save();
        }

        try
        {
            State = EngineState.Stopping;
            await Task.Delay(150);
            await Task.Run(() => _engine.Stop());
            State = _engine.State;
            if (State == EngineState.Stopped)
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Обход выключен"));
        }
        catch (Exception ex)
        {
            AppendLog(Loc.T("Ошибка остановки: {0}", ex.Message));
        }
        finally
        {
            State = _engine.State == EngineState.Stopping ? EngineState.Stopped : _engine.State;
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// Apply the currently selected strategy to the engine. Starts the engine if stopped,
    /// or restarts it on the newly selected preset if already running.
    /// </summary>
    private async Task ApplyStrategyAsync()
    {
        _unexpectedRecovery.Cancel();
        if (IsAutoSelecting || IsTestingSelectedPreset) return;
        if (!await _engineTransitionGate.WaitAsync(0)) return;
        try
        {
            if (SelectedPreset is null)
            {
                SelectedPreset = RecommendedPreset
                                 ?? Presets.FirstOrDefault(p => p.IsWorkFor(ActiveProbeTargets))
                                 ?? PresetsView?.Cast<Models.Preset>().FirstOrDefault()
                                 ?? Presets.FirstOrDefault();
            }
            if (SelectedPreset is null) return;
            if (IsTransitioning)
            {
                AppendLog(Loc.T("Смена стратегии уже выполняется. Подождите завершения текущего перехода."));
                return;
            }

            var preset = SelectedPreset;
            string strategyName = Loc.T(preset.Name);
            Settings.DesiredBypassEnabled = true;
            Settings.BypassDesired = true;
            Settings.ActivePresetId = preset.Name;
            Settings.ActivePresetName = preset.Name;
            _settingsSvc.Save();

            if (!IsRunning)
            {
                await StartCoreAsync();
                return;
            }

            AppendLog(Loc.T("Смена стратегии → «{0}». Перезапуск движка…", strategyName));
            await RestartEngineForStrategyAsync(preset);
            if (IsRunning)
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Стратегия «{0}» применена.", strategyName));
        }
        catch (Exception ex)
        {
            AppendLog(Loc.T("Ошибка смены стратегии: {0}", ex.Message));
            ShowErrorModal(
                Loc.T("Не удалось сменить стратегию"),
                Loc.T("Приложение сохранило выбранную стратегию, но движок не смог безопасно перезапуститься."),
                ex.ToString(),
                Loc.T("Открыть журнал"),
                () => SelectedTabIndex = 4);
        }
        finally
        {
            _engineTransitionGate.Release();
            RaiseCommandStates();
        }
    }

    /// <summary>Relaunch a running engine because an OPTION changed — scope, capture width, QUIC, one of
    /// the coverage profiles, the target list. Separate from <see cref="ApplyStrategyAsync"/> only for
    /// the log line: every one of those used to announce «Смена стратегии», and reading a strategy change
    /// you did not make, fifteen times in a row, is how a log stops being trusted.</summary>
    private async Task ApplyEngineOptionsAsync()
    {
        await _engineTransitionGate.WaitAsync();
        try
        {
            if (!IsRunning || IsAutoSelecting || IsTestingSelectedPreset) return;
            AppendLog(Loc.T("Настройки изменились. Перезапуск движка…"));
            await RestartEngineAsync();
        }
        finally { _engineTransitionGate.Release(); RaiseCommandStates(); }
    }

    /// <summary>Stop, wait for WinDivert to be released, start again.</summary>
    private async Task RestartEngineAsync()
    {
        await StopEngineCoreAsync(clearDesired: false);
        // Wait for the process to release WinDivert before relaunching.
        for (int i = 0; i < 60 && State != EngineState.Stopped; i++)
            await Task.Delay(50);
        await Task.Delay(200);
        if (CanStart) await StartCoreAsync();
    }

    private async Task RestartEngineForStrategyAsync(Models.Preset preset)
    {
        var previousPreset = RunningPreset ?? _engine.ActivePreset;
        string? previousHostlistPath = _engine.ActiveHostlistPath;
        _healthMonitor.StopWatchdog();
        _healthMonitor.Reset();

        try
        {
            State = EngineState.Stopping;
            await Task.Delay(150);
            await Task.Run(() => _engine.Stop());

            for (int i = 0; i < 80 && _engine.State != EngineState.Stopped; i++)
                await Task.Delay(50);
            await Task.Delay(350);

            string? hostlistPath = !string.IsNullOrWhiteSpace(ActiveHostlistPath) ? ActiveHostlistPath : null;
            State = EngineState.Starting;

            _engine.GameFilter = Settings.GameFilter;
            _engine.LeagueTcp2099Bypass = Settings.LeagueTcp2099Bypass;
            _engine.RiotEntitlementsBypass = Settings.RiotEntitlementsBypass;
            _engine.BypassAllSites = EffectiveBypassAllSites;
            _engine.DisableQuic = Settings.DisableQuic;
            _engine.CoverTgProxy = false;
            _engine.DebugLog = Settings.DebugLog;

            await Task.Run(() => _engine.Start(preset, hostlistPath));

            State = _engine.State;
            if (State == EngineState.Running)
            {
                if (!_engine.ActiveProcessId.HasValue)
                    throw new InvalidOperationException(Loc.T("Движок сообщил о запуске, но его процесс не найден."));
                RunningPreset = preset;
                Settings.DesiredBypassEnabled = true;
                Settings.BypassDesired = true;
                Settings.ActivePresetId = preset.Name;
                Settings.ActivePresetName = preset.Name;
                _settingsSvc.Save();
                RememberNetworkStrategy();
            }
        }
        catch
        {
            // Keep the last known-good strategy alive when the newly selected one fails.
            if (previousPreset is not null)
            {
                try
                {
                    AppendLog(Loc.T("Новая стратегия не запустилась. Возвращаю предыдущую «{0}»…", Loc.T(previousPreset.Name)));
                    State = EngineState.Starting;
                    await Task.Run(() => _engine.Start(previousPreset, previousHostlistPath));
                    State = _engine.State;
                    if (State == EngineState.Running)
                    {
                        if (!_engine.ActiveProcessId.HasValue)
                            throw new InvalidOperationException("Восстановленный движок не имеет активного процесса.");
                        SelectedPreset = previousPreset;
                        RunningPreset = previousPreset;
                        Settings.ActivePresetId = previousPreset.Name;
                        Settings.ActivePresetName = previousPreset.Name;
                        Settings.DesiredBypassEnabled = true;
                        Settings.BypassDesired = true;
                        _settingsSvc.Save();
                        AppendLog(Loc.T("Предыдущая стратегия восстановлена."));
                    }
                }
                catch (Exception rollbackEx)
                {
                    AppendLog(Loc.T("Не удалось восстановить предыдущую стратегию: {0}", rollbackEx.Message));
                }
            }
            throw;
        }
        finally
        {
            if (_engine.State == EngineState.Running)
            {
                UpdateMonitor();
                _ = _healthMonitor.ProbeAllAsync(CancellationToken.None);
            }
            else
            {
                RunningPreset = null;
                // A technical failure is not a user request to disable recovery.
                if (previousPreset is not null)
                    Settings.ActivePresetId = previousPreset.Name;
                State = _engine.State == EngineState.Starting || _engine.State == EngineState.Stopping
                    ? EngineState.Stopped
                    : _engine.State;
                Settings.BypassActual = "ERROR";
                _settingsSvc.Save();
            }
        }
    }

}
