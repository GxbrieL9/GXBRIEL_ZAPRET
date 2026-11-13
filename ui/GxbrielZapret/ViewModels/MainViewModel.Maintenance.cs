using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Mvvm;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Services.Strategies;
using GxbrielZapret.Views;
using GxbrielZapret.Startup;

namespace GxbrielZapret.ViewModels;

/// <summary>
/// The action cards at the bottom of Настройки. Unlike the toggles above them each of these DOES
/// something — builds an IP list, touches Defender, rewrites our own files — so every one of them
/// reports what happened in its own status line.
/// </summary>
public sealed partial class MainViewModel
{
    private async Task RunBlockcheck2Async()
    {
        if (IsInitializing || IsProfileVerifying || IsAutoSelecting || IsTransitioning || IsDiagnosing || IsTestingSelectedPreset) return;
        IsProfileVerifying = true;
        IsBlockcheckSession = true;
        IsBlockcheckRunning = true;
        IsAutoSelecting = true;
        _autoCts = new CancellationTokenSource();
        var previous = RunningPreset ?? _engine.ActivePreset;
        bool restore = IsRunning && previous is not null;
        var timestamps = new TcpTimestampsService();
        Preset? found = null;
        var scope = SelectedAutoScope;
        var domains = scope.GoalHosts();
        bool bypassAll = EffectiveBypassAllSites, disableQuic = Settings.DisableQuic;
        string optionsKey = BlockcheckProfileStore.OptionsKey(bypassAll, disableQuic);
        GxbrielZapret.Services.Network.NetworkBoundRun? session = null;
        AutoCandidates.Clear();
        AutoCandidates.Add(new AutoCandidateRow("blockcheck2") { State = AutoCandidateState.Running });
        PassedStrategies.Clear();
        Verdicts.Clear();
        CheckTargets.Clear();
        foreach (string domain in domains) CheckTargets.Add(new CheckTargetRow(domain));
        AutoProgress = 0;
        AutoProgressText = "blockcheck2";
        AutoPassedCount = AutoFailedCount = 0;
        _healthMonitor.StopWatchdog();
        _healthMonitor.Reset();
        try
        {
            session = new(GxbrielZapret.Services.Network.NetworkFingerprint.Current, _autoCts.Token);
            void Checkpoint()
            {
                session.Checkpoint();
                if (BlockcheckProfileStore.OptionsKey(EffectiveBypassAllSites, Settings.DisableQuic) != optionsKey)
                    throw new InvalidOperationException("Настройки обхода изменились. Результат не сохранён; повторите подбор.");
            }
            AutoCheckStarted?.Invoke();
            if (IsRunning) await Task.Run(() => _engine.Stop());
            SetAutoStatus(Loc.T("Запускаю upstream blockcheck2 для {0} целей…", domains.Length));
            var cleanup = await new BypassCleanupService().StopAndVerifyCleanAsync(session.Token);
            AppendLog(cleanup.Message);
            if (!cleanup.Clean) throw new InvalidOperationException(cleanup.Message);
            await Task.Run(() => timestamps.EnableForSession(line => OnUi(() => AppendLog(line))));
            var result = await _blockcheck2.RunAsync(domains, session.Token,
                new Progress<string>(line =>
                {
                    AppendLog("[blockcheck2] " + line);
                    if (line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- checking", StringComparison.Ordinal))
                    {
                        CurrentCandidateName = line;
                        SetAutoStatus(line);
                    }
                }));
            AppendLog(Loc.T("blockcheck2 завершён: код {0}", result.ExitCode));
            AppendLog("[blockcheck2] Полный отчёт: " + result.LogPath);
            Checkpoint();
            if (result.ExitCode == 0)
            {
                using var verifier = new Canonical5ProbeService();
                var selection = await BlockcheckSelectionService.SelectAsync(result.Output, scope, session.Fingerprint, optionsKey, disableQuic, Checkpoint,
                    (candidate, ct) => verifier.EvaluateCandidateAsync(candidate, (host, ok) => OnUi(() => OnHostProbed(host, ok)),
                        ct, bypassAll, domains, disableQuic, scope, CurrentProbeLaunchOptions),
                    (candidate, verified, index, count) =>
                    {
                        CurrentCandidateName = $"Комбинация {Math.Min(verified is null ? index + 1 : index, count)}/{count}";
                        SetAutoStatus("blockcheck2: проверяю единый профиль, включая видео и Gateway…");
                        if (verified is not null) { ShowCanonicalProbeResult(verified); AppendLog("[blockcheck2] " + verified.Summary); }
                    }, session.Token);
                Checkpoint();
                if (selection.Profile is { } saved && selection.Verification is { } verified)
                {
                    ReloadPresets(session.Fingerprint);
                    StrategySearchText = "";
                    ShowFavoritePresetsOnly = false;
                    FilterMode = PresetFilterMode.Blockcheck;
                    SelectedPreset = found = Presets.FirstOrDefault(p => p.Name == saved.Name);
                    AutoCandidates[0].Apply(ScoreFromCanonicalResult(verified));
                    PassedStrategies.Add(ScoreFromCanonicalResult(verified));
                    AutoPassedCount = 1;
                    Settings.NetworkStrategies[session.Fingerprint] = saved.Name;
                    _settingsSvc.Save();
                    LastApplicationFlowSummary = saved.VerifiedFlowSummary;
                    SetAutoStatus("blockcheck2: единый профиль этой сети проверен и сохранён. Применение — отдельной кнопкой.");
                    AppendLog("[blockcheck2] Обновлён профиль сети: " + saved.Name);
                }
                else
                {
                    AutoFailedCount = selection.Attempted;
                    SetAutoStatus(selection.CandidateCount == 0
                        ? "blockcheck2: подходящих команд в итоговом отчёте нет. Предыдущий профиль сохранён."
                        : "blockcheck2: общий профиль не прошёл полную проверку. Предыдущий рабочий профиль не изменён.");
                }
            }
            else
            {
                SetAutoStatus(result.ExitCode == 0
                    ? Loc.T("blockcheck2 завершён, но рабочая строка стратегии не найдена. Подробности в журнале.")
                    : Loc.T("blockcheck2 завершился с кодом {0}. Подробности в журнале.", result.ExitCode));
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) SetAutoStatus(session?.NetworkChanged == true
                ? "Сеть изменилась. Подбор остановлен, результат не сохранён."
                : Loc.T("blockcheck2 остановлен."));
            else { CrashLog.Write(ex); SetAutoStatus(Loc.T("Не удалось запустить blockcheck2: {0}", ex.Message)); AppendLog("[blockcheck2] " + ex); }
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
            AutoProgress = 1;
            AutoCandidates[0].State = AutoCandidateState.Done;
            IsAutoSelecting = false;
            _autoCts?.Dispose();
            _autoCts = null;
            await Task.Run(() => timestamps.RestoreAfterSession(line => OnUi(() => AppendLog(line))));
            try
            {
                if (!_isShuttingDown && session?.NetworkChanged != true && restore && previous is not null)
                {
                    SelectedPreset = previous;
                    if (CanStart) await StartAsync();
                    if (found is not null) SelectedPreset = found;
                }
            }
            finally
            {
                IsBlockcheckRunning = false;
                IsProfileVerifying = false;
                AutoCheckFinished?.Invoke();
                RaiseCommandStates();
            }
        }
    }
    private bool _isProfileVerifying;
    public bool IsProfileVerifying
    {
        get => _isProfileVerifying;
        private set
        {
            if (SetField(ref _isProfileVerifying, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(ProfileVerificationButtonText));
            }
        }
    }

    private string _profileVerificationStatus = Loc.T("Полный скан нужен редко: он заново сортирует все стратегии в WORK/BROKEN.");
    public string ProfileVerificationStatus
    {
        get => _profileVerificationStatus;
        private set => SetField(ref _profileVerificationStatus, value);
    }

    public string ProfileVerificationButtonText => IsProfileVerifying
        ? Loc.T("Идёт полный скан…")
        : Loc.T("Полный скан стратегий");

    private async Task RunFullProfileVerificationAsync()
    {
        if (IsProfileVerifying) return;
        IsProfileVerifying = true;
        try
        {
            if (IsRunning)
            {
                ProfileVerificationStatus = Loc.T("Останавливаю обход перед полной проверкой…");
                _engine.Stop();
                await Task.Delay(700);
            }

            ProfileVerificationStatus = Loc.T("Полный скан: проверяю все стратегии и формирую move-plan…");
            AppendLog(ProfileVerificationStatus);
            int verifyCode = await RunEngineModeAsync("full-verify", line => OnUi(() =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                    ProfileVerificationStatus = line;
            }));
            if (verifyCode != 0)
                throw new InvalidOperationException(Loc.T("full-verify завершился с кодом {0}", verifyCode));

            ProfileVerificationStatus = Loc.T("Применяю move-plan: перемещаю WORK/BROKEN без дублей…");
            AppendLog(ProfileVerificationStatus);
            int moveCode = await RunEngineModeAsync("apply-moves", line => OnUi(() =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                    ProfileVerificationStatus = line;
            }));
            if (moveCode != 0)
                throw new InvalidOperationException(Loc.T("apply-moves завершился с кодом {0}", moveCode));

            ReloadPresets();
            ProfileVerificationStatus = Loc.T("Готово: пул обновлён. {0}", ProfilePoolSummary);
            Notify?.Invoke("GXBRIEL_ZAPRET", ProfileVerificationStatus);
        }
        catch (Exception ex)
        {
            ProfileVerificationStatus = Loc.T("Полный скан не завершён: ") + ex.Message;
            ShowErrorModal(
                Loc.T("Полный скан не завершён"),
                Loc.T("Приложение не смогло проверить и разложить стратегии. Уже существующие профили не удалялись."),
                ex.ToString());
        }
        finally
        {
            IsProfileVerifying = false;
        }
    }

    private static async Task<int> RunEngineModeAsync(string mode, Action<string> onLine)
    {
        string scriptPath = System.IO.Path.Combine(AppPaths.ProjectRoot, "GX_ENGINE.ps1");
        if (!AppPaths.HasDevelopmentProjectRoot || !System.IO.File.Exists(scriptPath))
        {
            onLine("Legacy GX_ENGINE.ps1 maintenance is available only with explicit GXBRIEL_ZAPRET_ROOT in a development checkout.");
            return -1;
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppPaths.ProjectRoot,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-Mode");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("-AssumeYes");

        using var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    // ---- ipset (IP-based bypass) ------------------------------------------
    private CancellationTokenSource? _listUpdateCts;

    private bool _isBuildingIpset;
    public bool IsBuildingIpset
    {
        get => _isBuildingIpset;
        internal set
        {
            if (SetField(ref _isBuildingIpset, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(UpdateListsButtonText));
            }
        }
    }

    private double _ipsetProgressValue;
    public double IpsetProgressValue
    {
        get => _ipsetProgressValue;
        set => SetField(ref _ipsetProgressValue, value);
    }

    private string _ipsetProgressStage = Loc.T("Готов к работе");
    public string IpsetProgressStage
    {
        get => _ipsetProgressStage;
        set => SetField(ref _ipsetProgressStage, value);
    }

    public string UpdateListsButtonText => IsBuildingIpset
        ? Loc.T("Парсинг…")
        : Loc.T("Парсить 60 сек и обновить базы");

    public string StopListsButtonText => Loc.T("Остановить");

    public string ParserScopeStatus => Loc.T("Будут отслеживаться: {0}. Окно можно свернуть — парсинг продолжится.",
        CurrentCaptureScope.Label);

    private LiveCaptureService.CaptureScope CurrentCaptureScope => new(
        Settings.ParseDiscordApps,
        Settings.ParseRiotApps,
        Settings.ParseRockstarApps,
        Settings.ParseOtherApps,
        _discovery.Targets);

    private string _ipsetStatus = Loc.T("Соберите список IP Discord, чтобы включить обход по IP (для жёстких блоков).");
    public string IpsetStatus { get => _ipsetStatus; set => SetField(ref _ipsetStatus, value); }

    // Static help texts that contain the literal token {IPSET}; the brace can't be wrapped in the XAML
    // {loc:Loc} markup extension, so they route through Loc.T here (which never reformats — the token
    // survives verbatim).
    public string IpsetHelpDiag => Loc.T(
        "Если Discord режут по IP-адресам, обход по доменам не помогает. Соберите актуальные подсети "
        + "Discord (резолв доменов) — список сохранится в ipset-discord.txt, его можно подключить в своём "
        + "пресете через токен {IPSET}.");

    public string IpsetHelpTelegram => Loc.T(
        "• Обход по IP — если ресурс режут по адресам, а не доменам: соберите подсети Discord "
        + "(резолв доменов), список подключается через токен {IPSET}.");

    private async Task BuildIpsetAsync()
    {
        if (IsBuildingIpset) return;
        IsBuildingIpset = true;
        _listUpdateCts?.Dispose();
        _listUpdateCts = new CancellationTokenSource();
        try
        {
            var ct = _listUpdateCts.Token;
            IpsetProgressValue = 10;
            IpsetProgressStage = Loc.T("DNS Резолв");
            IpsetStatus = Loc.T("Определяю IP-подсети Discord…");
            var domains = _hostlists.Exists("discord")
                ? _hostlists.ReadDomains("discord")
                : new List<string> { "discord.com", "gateway.discord.gg", "cdn.discordapp.com", "discord.media", "discordapp.net" };
            var res = await _ipset.BuildDiscordIpsetAsync(domains, ct);
            IpsetProgressValue = 100;
            IpsetProgressStage = Loc.T("Готово");
            IpsetStatus = Loc.T("Готово: {0} подсетей. Подключите список через {{IPSET}} в своей стратегии.", res.Subnets);
            Notify?.Invoke(Loc.T("GXBRIEL_ZAPRET — IP-подсети"), IpsetStatus);
            OnPropertyChanged(nameof(CommandPreview));
        }
        catch (OperationCanceledException)
        {
            IpsetProgressStage = Loc.T("Остановлено");
            IpsetStatus = Loc.T("Сбор IP-списка остановлен. Уже сохранённые файлы не удалялись.");
            Notify?.Invoke(Loc.T("GXBRIEL_ZAPRET — IP-подсети"), IpsetStatus);
        }
        catch (Exception ex)
        {
            IpsetProgressStage = Loc.T("Ошибка");
            IpsetStatus = Loc.T("Не удалось собрать IP-список: ") + ex.Message;
            ShowErrorModal(
                Loc.T("Не удалось собрать IP-список"),
                Loc.T("Приложение не смогло обновить IP-подсети Discord. Можно повторить позже или проверить доступ к DNS/интернету."),
                ex.ToString());
        }
        finally
        {
            IsBuildingIpset = false;
            _listUpdateCts?.Dispose();
            _listUpdateCts = null;
        }
    }

    private async Task UpdateAllListsAsync()
    {
        if (IsBuildingIpset) return;
        IsBuildingIpset = true;
        _listUpdateCts?.Dispose();
        _listUpdateCts = new CancellationTokenSource();
        try
        {
            var ct = _listUpdateCts.Token;
            IpsetProgressValue = 5;
            IpsetProgressStage = Loc.T("Живой захват 60 сек");
            IpsetStatus = Loc.T("Слушаю выбранные приложения 60 секунд. Можно свернуть окно — процесс продолжится.");

            var captureResult = await Task.Run(() => _liveCapture.RunLiveCaptureAsync(
                60,
                status => OnUi(() =>
                {
                    IpsetStatus = status;
                    IpsetProgressStage = Loc.T("Живой захват 60 сек");
                    IpsetProgressValue = LiveCaptureProgressFromStatus(status, 60);
                }),
                ct,
                CurrentCaptureScope,
                _discovery));

            IpsetProgressValue = 45;
            IpsetProgressStage = Loc.T("Парсинг и обновление баз");
            IpsetStatus = Loc.T("Живой захват завершён ({0} эндпоинтов). Объединяю с базами...", captureResult.DiscoveredEndpointsCount);

            var summary = await Task.Run(() => _hostlists.UpdateAndParseOnlineDatabasesAsync(
                _ipset,
                status => OnUi(() =>
                {
                    IpsetStatus = status;
                    if (status.StartsWith("[1/5]"))
                    {
                        IpsetProgressValue = 55;
                        IpsetProgressStage = Loc.T("Этап 1/5: BlitzFix & Трассировки");
                    }
                    else if (status.StartsWith("[2/5]"))
                    {
                        IpsetProgressValue = 65;
                        IpsetProgressStage = Loc.T("Этап 2/5: Загрузка с зеркал");
                    }
                    else if (status.StartsWith("[3/5]"))
                    {
                        IpsetProgressValue = 75;
                        IpsetProgressStage = Loc.T("Этап 3/5: Объединение доменов");
                    }
                    else if (status.StartsWith("[4/5]"))
                    {
                        IpsetProgressValue = 88;
                        IpsetProgressStage = Loc.T("Этап 4/5: DNS резолв и IP");
                    }
                    else if (status.StartsWith("[5/5]"))
                    {
                        IpsetProgressValue = 95;
                        IpsetProgressStage = Loc.T("Этап 5/5: Сохранение файлов");
                    }
                }),
                ct));

            ReloadHostlists();
            if (string.IsNullOrEmpty(SelectedHostlist) || !Hostlists.Contains(SelectedHostlist))
                SelectedHostlist = Hostlists.FirstOrDefault(h => h == "discord") ?? Hostlists.FirstOrDefault() ?? "";
            else
                HostlistContent = _hostlists.Read(SelectedHostlist);

            IpsetProgressValue = 100;
            IpsetProgressStage = Loc.T("Готово");
            IpsetStatus = summary.StatusMessage;
            Notify?.Invoke(Loc.T("GXBRIEL_ZAPRET — Парсинг завершён"), summary.StatusMessage);
            OnPropertyChanged(nameof(CommandPreview));
        }
        catch (OperationCanceledException)
        {
            IpsetProgressStage = Loc.T("Остановлено");
            IpsetStatus = Loc.T("Обновление баз остановлено. Уже сохранённые списки оставлены как есть.");
            Notify?.Invoke(Loc.T("GXBRIEL_ZAPRET — Базы"), IpsetStatus);
        }
        catch (Exception ex)
        {
            IpsetProgressStage = Loc.T("Ошибка");
            IpsetStatus = Loc.T("Не удалось обновить базы: ") + ex.Message;
            ShowErrorModal(
                Loc.T("Не удалось обновить базы"),
                Loc.T("Парсинг или загрузка списков завершились ошибкой. Существующие списки не удалялись; можно повторить обновление позже."),
                ex.ToString());
        }
        finally
        {
            IsBuildingIpset = false;
            _listUpdateCts?.Dispose();
            _listUpdateCts = null;
        }
    }

    private async Task RunLiveTraceAsync(int durationSeconds = 30)
    {
        await UpdateAllListsAsync();
    }

    private static double LiveCaptureProgressFromStatus(string status, int totalSeconds)
    {
        const double start = 5;
        const double end = 45;
        if (totalSeconds <= 0) return start;

        var match = System.Text.RegularExpressions.Regex.Match(status, @"осталось\s+(\d+)\s+сек", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int remaining))
            return start;

        remaining = Math.Clamp(remaining, 0, totalSeconds);
        double elapsedRatio = (totalSeconds - remaining) / (double)totalSeconds;
        return start + (end - start) * elapsedRatio;
    }

    // ---- Defender / firewall exclusions -----------------------------------

    private bool _isApplyingExclusions;
    public bool IsApplyingExclusions
    {
        get => _isApplyingExclusions;
        private set { if (SetField(ref _isApplyingExclusions, value)) RaiseCommandStates(); }
    }

    private string _exclusionsStatus =
        "Добавит приложение и движок в исключения Защитника Windows и правила брандмауэра, чтобы их не блокировали.";
    public string ExclusionsStatus { get => _exclusionsStatus; private set => SetField(ref _exclusionsStatus, value); }

    private async Task ApplyExclusionsAsync()
    {
        if (IsApplyingExclusions) return;
        IsApplyingExclusions = true;
        try
        {
            ExclusionsStatus = Loc.T("Добавление исключений…");
            var res = await _exclusions.ApplyAsync();
            if (res.AllOk)
            {
                Settings.MaintainFirewallExclusions = true;
                _settingsSvc.Save();
            }
            ExclusionsStatus = (res.AllOk
                ? Loc.T("Готово — правила брандмауэра добавлены и будут проверяться при запуске:\n")
                : Loc.T("Готово частично (что-то не удалось — нужны права администратора / сторонний антивирус):\n")) + res.Summary;
            Notify?.Invoke("GXBRIEL_ZAPRET", res.AllOk
                ? Loc.T("Исключения брандмауэра успешно добавлены.")
                : Loc.T("Исключения брандмауэра применены частично."));
        }
        catch (Exception ex)
        {
            ExclusionsStatus = Loc.T("Не удалось добавить исключения: ") + ex.Message;
        }
        finally
        {
            IsApplyingExclusions = false;
        }
    }

    private async Task EnsureMaintainedExclusionsAsync()
    {
        if (!Settings.MaintainFirewallExclusions || IsApplyingExclusions) return;

        try
        {
            var check = await _exclusions.VerifyAsync();
            if (check.AllOk)
            {
                ExclusionsStatus = Loc.T("Исключения брандмауэра активны:\n") + check.Summary;
                return;
            }

            var apply = await _exclusions.ApplyAsync();
            ExclusionsStatus = (apply.AllOk
                ? Loc.T("Исключения брандмауэра были восстановлены:\n")
                : Loc.T("Не удалось полностью восстановить исключения брандмауэра:\n")) + apply.Summary;
        }
        catch (Exception ex)
        {
            ExclusionsStatus = Loc.T("Не удалось проверить исключения: ") + ex.Message;
        }
    }

    // ---- factory reset ----------------------------------------------------

    /// <summary>Return the app to first-launch state: stop runtime services, undo system proxy/autostart,
    /// delete all mutable app data and restart so bundled defaults are unpacked again.</summary>
    private async Task FactoryResetAsync()
    {
        bool ok = ConfirmDialog.Show(
            Loc.T("Вернуть к заводскому состоянию?"),
            Loc.T("Будут остановлены обход и Telegram-прокси, выключен автозапуск, восстановлен системный прокси "
                + "и удалены все локальные настройки, стратегии, списки, логи, кэш и распакованный движок. "
                + "После сброса приложение перезапустится и заново развернёт встроенные файлы."),
            confirmText: Loc.T("Сбросить"),
            danger: true);
        if (!ok) return;

        try
        {
            AppendLog(Loc.T("Выполняю заводской сброс…"));
            _autoCts?.Cancel();
            _diagCts?.Cancel();
            _dpiCts?.Cancel();
            _listUpdateCts?.Cancel();
            _monitor.Stop();
            if (IsRunning) _engine.Stop();
            if (_tgProxy.IsRunning) _tgProxy.Stop();
            RestoreStaleSystemProxy();
            _autostart.Disable();
            await new GxbrielZapret.Services.Platform.BypassCleanupService()
                .StopAndVerifyCleanAsync(CancellationToken.None);
            await Task.Delay(500);

            var result = await Task.Run(() => new FactoryResetService().Reset());
            if (!result.Success && !result.CriticalDataWiped)
            {
                ShowErrorModal(
                    Loc.T("Заводской сброс выполнен частично"),
                    Loc.T("Часть файлов не удалось удалить. Закройте процессы, которые могут держать файлы, и повторите сброс."),
                    string.Join(Environment.NewLine, result.Errors));
                return;
            }

            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Заводской сброс выполнен. Перезапускаю приложение…"));
            App.RelaunchSelf();
        }
        catch (Exception ex)
        {
            ShowErrorModal(
                Loc.T("Заводской сброс не завершён"),
                Loc.T("Приложение не смогло полностью очистить локальное состояние."),
                ex.ToString());
        }
    }

    // ---- backup / restore -------------------------------------------------

    private string _backupStatus = Loc.T("Сохраните настройки и стратегии в файл — на случай переустановки или переноса на другой компьютер.");
    public string BackupStatus { get => _backupStatus; private set => SetField(ref _backupStatus, value); }

    /// <summary>Save the current configuration (settings, strategies, host lists) to a .z2bak file.</summary>
    private void ExportBackup()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _backup.DefaultFileName,
            Filter = Loc.T("Резервная копия GxbrielZapret") + " (*.z2bak)|*.z2bak",
            DefaultExt = ".z2bak",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _backup.Export(dlg.FileName);
            BackupStatus = Loc.T("Сохранено: {0}", System.IO.Path.GetFileName(dlg.FileName));
            Notify?.Invoke("GxbrielZapret", Loc.T("Резервная копия сохранена."));
        }
        catch (Exception ex)
        {
            BackupStatus = Loc.T("Не удалось сохранить копию: ") + ex.Message;
        }
    }

    /// <summary>Restore a .z2bak: validate it, confirm, then overwrite the config and relaunch so the
    /// restored files are what loads (see <see cref="BackupService.Restore"/> for why the restart matters).</summary>
    private void ImportBackup()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("Резервная копия GxbrielZapret") + " (*.z2bak)|*.z2bak|" + Loc.T("Все файлы") + " (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        if (!_backup.IsBackup(dlg.FileName))
        {
            BackupStatus = Loc.T("Это не резервная копия GxbrielZapret.");
            return;
        }
        bool ok = ConfirmDialog.Show(
            Loc.T("Восстановить из копии?"),
            Loc.T("Текущие настройки, стратегии и хостлисты будут заменены содержимым файла. Приложение "
                + "перезапустится; запущенный обход или прокси остановятся."),
            confirmText: Loc.T("Восстановить"),
            danger: true);
        if (!ok) return;
        try
        {
            if (IsRunning) _engine.Stop();
            if (_tgProxy.IsRunning) _tgProxy.Stop();
            _backup.Restore(dlg.FileName);
            App.RelaunchSelf();
        }
        catch (Exception ex)
        {
            BackupStatus = Loc.T("Не удалось восстановить: ") + ex.Message;
        }
    }

    // ---- log files --------------------------------------------------------

    private string _logFilesStatus = Loc.T("Журнал каждого запуска пишется в отдельный файл. Старые удаляются сами — остаются последние 20. Можно очистить вручную.");
    public string LogFilesStatus { get => _logFilesStatus; private set => SetField(ref _logFilesStatus, value); }

    /// <summary>Delete the accumulated engine-*.log files (the live session's own log, if any, is locked
    /// and skipped). The in-memory Журнал is left as is — this only touches files on disk.</summary>
    private void ClearLogFiles()
    {
        int n = LogMaintenance.ClearSessionLogs();
        LogFilesStatus = n > 0
            ? Loc.T("Удалено файлов логов: {0}.", n)
            : Loc.T("Старых логов нет — чистить нечего.");
        Notify?.Invoke("GXBRIEL_ZAPRET", LogFilesStatus);
    }

    // ---- support bundle ---------------------------------------------------

    private string _supportBundleStatus = Loc.T("Формирует ZIP-архив с логами, результатами диагностики и сетевыми параметрами (без паролей и личных данных) для отправки в поддержку.");
    public string SupportBundleStatus { get => _supportBundleStatus; private set => SetField(ref _supportBundleStatus, value); }

    public RelayCommand ExportSupportBundleCommand => new RelayCommand(async _ => await ExportSupportBundleAsync());

    private async Task ExportSupportBundleAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"GXBRIEL_SUPPORT_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            Filter = Loc.T("Архив поддержки GxbrielZapret") + " (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            SupportBundleStatus = Loc.T("Формирую архив поддержки…");
            var service = new SupportBundleService(_settingsSvc);
            await service.CreateBundleAsync(dlg.FileName, CancellationToken.None);
            SupportBundleStatus = Loc.T("Отчёт сохранён: {0}", System.IO.Path.GetFileName(dlg.FileName));
            Notify?.Invoke("GxbrielZapret", Loc.T("Отчёт для поддержки успешно сохранён."));
        }
        catch (Exception ex)
        {
            SupportBundleStatus = Loc.T("Не удалось создать отчёт: ") + ex.Message;
            CrashLog.Write(ex);
        }
    }

    // ---- DNS and browser socket cache flush --------------------------------

    private string _flushDnsStatus = Loc.T("Очистка системного кэша DNS и сброс зависших пулов сокетов браузера после смены стратегии.");
    public string FlushDnsStatus { get => _flushDnsStatus; private set => SetField(ref _flushDnsStatus, value); }

    public RelayCommand FlushDnsCommand => new RelayCommand(async _ => await FlushDnsAsync());
    public RelayCommand OpenBrowserSocketsHelpCommand => new RelayCommand(_ => OpenBrowserSocketsHelp());

    private async Task FlushDnsAsync()
    {
        FlushDnsStatus = Loc.T("Сбрасываю кэш DNS Windows…");
        AppendLog("[Сеть] Выполняю ipconfig /flushdns…");
        var (ok, msg) = await NetworkFlushService.FlushDnsAsync();
        FlushDnsStatus = msg;
        AppendLog($"[Сеть] {msg}");
        if (ok)
        {
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Кэш DNS успешно очищен."));
        }
    }

    private void OpenBrowserSocketsHelp()
    {
        try
        {
            System.Windows.Clipboard.SetText("chrome://net-internals/#sockets");
        }
        catch { }

        ShowErrorModal(
            Loc.T("Сброс пула сокетов браузера"),
            Loc.T("Браузеры (Chrome, Edge, Yandex) держат открытые постоянные соединения со старой стратегией. Чтобы применить новую немедленно:\n\n"
                + "1. Адрес chrome://net-internals/#sockets скопирован в буфер обмена — вставьте его в адресную строку браузера и нажмите Enter.\n"
                + "2. Нажмите кнопки «Close idle sockets» и «Flush socket pools».\n"
                + "3. Либо полностью перезапустите браузер (закрыть все окна).\n\n"
                + "Для Firefox: about:networking#sockets → «Close idle sockets»."),
            actionText: Loc.T("Понятно"));
    }
}

