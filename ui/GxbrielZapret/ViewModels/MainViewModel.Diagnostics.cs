using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Views;

namespace GxbrielZapret.ViewModels;

/// <summary>
/// Availability matrix, the DPI check and the user's own target hosts.
/// </summary>
public sealed partial class MainViewModel
{
    // ---- diagnostics (endpoint matrix) ------------------------------------

    private bool _isDiagnosing;
    public bool IsDiagnosing
    {
        get => _isDiagnosing;
        private set
        {
            if (SetField(ref _isDiagnosing, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(IsAnyCheckRunning));
                RefreshHomeServiceReachability();
            }
        }
    }

    public bool IsAnyCheckRunning => IsDiagnosing || IsDpiChecking;

    private double _diagProgress;
    public double DiagProgress { get => _diagProgress; set => SetField(ref _diagProgress, value); }

    private string _diagProgressText = "";
    public string DiagProgressText { get => _diagProgressText; set => SetField(ref _diagProgressText, value); }

    private DiagStatus _discordReachStatus = DiagStatus.Pending;
    public DiagStatus DiscordReachabilityStatus { get => _discordReachStatus; private set => SetField(ref _discordReachStatus, value); }

    private string _discordReachText = Loc.T("Не проверено");
    public string DiscordReachabilityText { get => _discordReachText; private set => SetField(ref _discordReachText, value); }

    private DiagStatus _youtubeReachStatus = DiagStatus.Pending;
    public DiagStatus YouTubeReachabilityStatus { get => _youtubeReachStatus; private set => SetField(ref _youtubeReachStatus, value); }

    private string _youtubeReachText = Loc.T("Не проверено");
    public string YouTubeReachabilityText { get => _youtubeReachText; private set => SetField(ref _youtubeReachText, value); }

    private string _discordProbeDetails = Loc.T("Голос Discord не проверен.");
    public string DiscordProbeDetails { get => _discordProbeDetails; private set => SetField(ref _discordProbeDetails, value); }
    private string _youTubeProbeDetails = Loc.T("Видео YouTube не проверено.");
    public string YouTubeProbeDetails { get => _youTubeProbeDetails; private set => SetField(ref _youTubeProbeDetails, value); }
    private string _lastApplicationFlowSummary = "Видео: не проверено · Gateway: не проверен · Голос: не проверен";
    public string LastApplicationFlowSummary
    {
        get => _lastApplicationFlowSummary;
        private set => SetField(ref _lastApplicationFlowSummary, value);
    }
    private DateTime _lastHealthCheck;
    public string ServiceProbeHint => _lastHealthCheck == default
        ? Loc.T("Сервисы не проверены · Голос не проверен")
        : Loc.T("Проверка: {0} · Голос не проверен", _lastHealthCheck.ToLocalTime().ToString("HH:mm:ss"));

    private DiagStatus _netReachStatus = DiagStatus.Pending;
    public DiagStatus NetworkReachabilityStatus { get => _netReachStatus; private set => SetField(ref _netReachStatus, value); }

    private string _netReachText = Loc.T("Не проверено");
    public string NetworkReachabilityText { get => _netReachText; private set => SetField(ref _netReachText, value); }

    public string ActiveNetworkName
    {
        get
        {
            try
            {
                var nic = PrimaryNetworkSelector.Find();
                if (nic is not null && !string.IsNullOrWhiteSpace(nic.Name)) return nic.Name;
            }
            catch { }
            return Loc.T("Основная сеть");
        }
    }

    public string NetworkHealthSummaryText => NetworkReachabilityStatus switch
    {
        DiagStatus.Ok => Loc.T("Проверены интерфейс, фрагмент видео и Gateway"),
        DiagStatus.Timeout => Loc.T("Частичные задержки на некоторых маршрутах"),
        DiagStatus.Fail => Loc.T("Не все проверки подтвердили доступность сервисов"),
        DiagStatus.Running => Loc.T("Проверяю интерфейс, видео и Gateway…"),
        DiagStatus.Skip => Loc.T("Не все сценарии удалось проверить; подробности в результатах"),
        _ => Loc.T("Доступность сервисов ещё не проверена")
    };

    private void OnHealthSnapshotReceived(ServiceHealthSnapshot snap)
    {
        if (snap.Revision < _healthMonitor.GetSnapshot().Revision) return;
        DiscordProbeDetails = snap.DiscordDetails;
        YouTubeProbeDetails = snap.YouTubeDetails;
        _lastHealthCheck = snap.Timestamp;
        OnPropertyChanged(nameof(ServiceProbeHint));
        DiscordReachabilityStatus = snap.Discord switch
        {
            ServiceHealthState.Working => DiagStatus.Ok,
            ServiceHealthState.Failed => DiagStatus.Fail,
            ServiceHealthState.Checking => DiagStatus.Running,
            ServiceHealthState.Inconclusive => DiagStatus.Skip,
            _ => DiagStatus.Pending
        };
        DiscordReachabilityText = snap.Discord switch
        {
            ServiceHealthState.Working => Loc.T("API + Gateway"),
            ServiceHealthState.Failed => Loc.T("Не подтверждено"),
            ServiceHealthState.Inconclusive => Loc.T("Недостаточно данных"),
            ServiceHealthState.Checking => Loc.T("Проверка…"),
            _ => Loc.T("Не проверено")
        };

        YouTubeReachabilityStatus = snap.YouTube switch
        {
            ServiceHealthState.Working => DiagStatus.Ok,
            ServiceHealthState.Failed => DiagStatus.Fail,
            ServiceHealthState.Checking => DiagStatus.Running,
            ServiceHealthState.Inconclusive => DiagStatus.Skip,
            _ => DiagStatus.Pending
        };
        YouTubeReachabilityText = snap.YouTube switch
        {
            ServiceHealthState.Working => Loc.T("HTML + JS + видео"),
            ServiceHealthState.Failed => Loc.T("Не подтверждено"),
            ServiceHealthState.Inconclusive => Loc.T("Видео не подтверждено"),
            ServiceHealthState.Checking => Loc.T("Проверка…"),
            _ => Loc.T("Не проверено")
        };

        if (snap.IsFullyHealthy)
        {
            NetworkReachabilityStatus = DiagStatus.Ok;
            NetworkReachabilityText = Loc.T("Доступно (онлайн)");
        }
        else if (snap.Discord == ServiceHealthState.Failed || snap.YouTube == ServiceHealthState.Failed)
        {
            NetworkReachabilityStatus = DiagStatus.Fail;
            NetworkReachabilityText = Loc.T("Обнаружен сбой");
        }
        else if (snap.Discord == ServiceHealthState.Checking || snap.YouTube == ServiceHealthState.Checking)
        {
            NetworkReachabilityStatus = DiagStatus.Running;
            NetworkReachabilityText = Loc.T("Проверка…");
        }
        else if (snap.Discord == ServiceHealthState.Inconclusive || snap.YouTube == ServiceHealthState.Inconclusive)
        {
            NetworkReachabilityStatus = DiagStatus.Skip;
            NetworkReachabilityText = Loc.T("Недостаточно данных");
        }
        else
        {
            NetworkReachabilityStatus = DiagStatus.Pending;
            NetworkReachabilityText = Loc.T("Не проверено");
        }
        OnPropertyChanged(nameof(NetworkHealthSummaryText));
    }

    private void OnWatchdogDegradedTriggered()
    {
        if (!Settings.AutoHeal || State != EngineState.Running || IsAutoRunning || IsTransitioning ||
            IsTestingSelectedPreset || IsProfileVerifying || IsAutoSelecting) return;

        AppendLog(Loc.T("[Watchdog] Обнаружено падение связности целевых сервисов. WORK-пул не изменяю; пробую переключиться на другую рабочую стратегию…"));
        Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Watchdog: связь просела, пробую другую рабочую стратегию без изменения WORK-пула…"));
        _ = HandleActiveStrategyFailureAsync();
    }

    private void OnMonitorTick(bool ok)
    {
        if (State != EngineState.Running) return;
        if (!ok)
        {
            NetworkReachabilityStatus = DiagStatus.Timeout;
            NetworkReachabilityText = Loc.T("Нестабильно");
            OnPropertyChanged(nameof(NetworkHealthSummaryText));
        }
    }

    public void RefreshHomeServiceReachability()
    {
        // Endpoint reachability is not application proof. Keep the home indicators owned by the health monitor.
        OnHealthSnapshotReceived(_healthMonitor.GetSnapshot());

        int total = DiagRows.Count;
        int done = DiagRows.Count(r =>
            r.PingOnly
                ? r.Ping is not (DiagStatus.Pending or DiagStatus.Running or DiagStatus.Canceled)
                : r.Http is not (DiagStatus.Pending or DiagStatus.Running or DiagStatus.Canceled));
        DiagProgress = total > 0 ? (double)done * 100.0 / total : 0;
        DiagProgressText = total > 0 ? Loc.T("Проверено {0} из {1}", done, total) : "";
    }

    private string _diagStatusText = Loc.T("Запустите диагностику, чтобы увидеть, что открывается, а что режется.");
    public string DiagStatusText { get => _diagStatusText; private set => SetField(ref _diagStatusText, value); }

    private string _diagSummary = "";
    public string DiagSummary { get => _diagSummary; private set => SetField(ref _diagSummary, value); }

    /// <summary>Reminds the user that the matrix reflects whatever is on the wire right now.</summary>
    public string DiagEngineNote => IsRunning
        ? Loc.T("Тест идёт с активным обходом: «{0}». Сравните с выключенным.", Loc.T(RunningPreset?.Name ?? "Неизвестная стратегия"))
        : Loc.T("Обход выключен — это базовая проверка. Включите обход и перезапустите, чтобы увидеть разницу.");

    private async Task RunDiagnosticsAsync()
    {
        if (IsDiagnosing) return;
        IsDiagnosing = true;
        DiagSummary = "";
        DiagProgress = 0;
        DiagProgressText = Loc.T("Подготовка проверки…");
        _diagCts = new CancellationTokenSource();
        try
        {
            await _diag.RunAsync(DiagRows.ToList(), _diagCts.Token);
            ComputeDiagSummary();
            await _healthMonitor.ProbeAllAsync(_diagCts.Token);
            RefreshHomeServiceReachability();
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Проверка доступности завершена: {0}", DiagSummary));
        }
        catch (OperationCanceledException)
        {
            DiagStatusText = Loc.T("Диагностика остановлена.");
            RefreshHomeServiceReachability();
        }
        catch (Exception ex)
        {
            DiagStatusText = Loc.T("Ошибка диагностики: ") + ex.Message;
            RefreshHomeServiceReachability();
        }
        finally
        {
            IsDiagnosing = false;
            _diagCts?.Dispose();
            _diagCts = null;
        }
    }

    private void ComputeDiagSummary()
    {
        int ok = 0, bad = 0, to = 0;
        foreach (var r in DiagRows)
        {
            if (r.PingOnly) continue;
            foreach (var s in new[] { r.Http, r.Tls12, r.Tls13 })
            {
                if (s == DiagStatus.Ok) ok++;
                else if (s == DiagStatus.Fail) bad++;
                else if (s == DiagStatus.Timeout) to++;
            }
        }
        DiagSummary = Loc.T("OK: {0}   ·   Ошибки: {1}   ·   Таймауты: {2}", ok, bad, to);
    }

    // ---- DPI check (does the provider actively block by SNI?) -------------
    // Distinct from the availability matrix: for each censored host it TCP-connects (proving the server
    // is up), then sends a TLS ClientHello with the real SNI — a reset (RST) or a freeze on THAT packet
    // is a middlebox injecting, not "the site is down". Most telling with the bypass OFF.

    private static readonly string[] DpiHosts =
    {
        "discord.com", "gateway.discord.gg", "cdn.discordapp.com", "www.youtube.com",
    };

    private bool _isDpiChecking;
    public bool IsDpiChecking
    {
        get => _isDpiChecking;
        private set { if (SetField(ref _isDpiChecking, value)) RaiseCommandStates(); }
    }

    private string _dpiVerdictText =
        Loc.T("«Проверка DPI» определяет, режет ли провайдер по имени сайта (обрыв/заморозка) и душит ли соединение по объёму/пакетам (TCP 16-20) — а не просто «сайт недоступен».");
    public string DpiVerdictText { get => _dpiVerdictText; private set => SetField(ref _dpiVerdictText, value); }

    private string _dpiVerdictDetail = "";
    public string DpiVerdictDetail { get => _dpiVerdictDetail; private set => SetField(ref _dpiVerdictDetail, value); }

    private DiagStatus _dpiVerdictStatus = DiagStatus.Pending;
    public DiagStatus DpiVerdictStatus { get => _dpiVerdictStatus; private set => SetField(ref _dpiVerdictStatus, value); }

    private async Task RunDpiCheckAsync()
    {
        if (IsDpiChecking) return;
        IsDpiChecking = true;
        DpiVerdictStatus = DiagStatus.Running;
        DpiVerdictText = Loc.T("Проверяем DPI: имя сайта и лимит по объёму/пакетам (TCP 16-20)…");
        DpiVerdictDetail = "";
        _dpiCts = new CancellationTokenSource();
        try
        {
            var ct = _dpiCts.Token;
            var volTask = NetProbe.VolumeProbeAsync(ct);   // TCP 16-20 volume/packet limit — runs in parallel
            using var gate = new SemaphoreSlim(4);
            var results = await Task.WhenAll(DpiHosts.Select(async host =>
            {
                await gate.WaitAsync(ct);
                try { return (host, verdict: await NetProbe.DpiProbeAsync(host, ct)); }
                finally { gate.Release(); }
            }));
            var volume = await volTask;

            int reset = results.Count(r => r.verdict == DpiVerdict.Reset);
            int freeze = results.Count(r => r.verdict == DpiVerdict.Freeze);
            int clean = results.Count(r => r.verdict == DpiVerdict.Clean);

            DpiVerdictDetail = string.Join("\n",
                results.Select(r => $"{DpiGlyph(r.verdict)}  {r.host} — {DpiText(r.verdict)}")
                       .Append(VolumeLine(volume)));

            bool sniHit = reset + freeze > 0;
            bool volHit = volume == VolumeVerdict.Throttled;

            if (sniHit || volHit)
            {
                DpiVerdictStatus = DiagStatus.Fail;
                DpiVerdictText = BuildDpiVerdict(reset, freeze, volHit);
            }
            else if (clean > 0)
            {
                DpiVerdictStatus = DiagStatus.Ok;
                DpiVerdictText = IsRunning
                    ? Loc.T("Признаков DPI-блокировки нет — ни по имени сайта, ни по объёму/пакетам. Либо провайдер не режет, либо обход уже её снимает.")
                    : Loc.T("Признаков DPI-блокировки нет — ни по имени сайта, ни по объёму/пакетам (TCP 16-20).");
            }
            else
            {
                DpiVerdictStatus = DiagStatus.Timeout;
                DpiVerdictText = Loc.T("Нет соединения с хостами — похоже на проблему сети или блокировку по IP, а не DPI по имени.");
            }
        }
        catch (OperationCanceledException) { DpiVerdictText = Loc.T("Проверка DPI остановлена."); DpiVerdictStatus = DiagStatus.Pending; }
        catch (Exception ex) { DpiVerdictText = Loc.T("Ошибка проверки DPI: ") + ex.Message; DpiVerdictStatus = DiagStatus.Pending; }
        finally
        {
            IsDpiChecking = false;
            _dpiCts?.Dispose();
            _dpiCts = null;
        }
    }

    private static string DpiGlyph(DpiVerdict v) => v switch
    {
        DpiVerdict.Clean => "✓",
        DpiVerdict.Reset or DpiVerdict.Freeze => "✗",
        _ => "•",
    };

    private static string DpiText(DpiVerdict v) => v switch
    {
        DpiVerdict.Clean => Loc.T("чисто"),
        DpiVerdict.Reset => Loc.T("обрыв DPI (RST)"),
        DpiVerdict.Freeze => Loc.T("заморозка DPI"),
        _ => Loc.T("нет соединения"),
    };

    private static string VolumeLine(VolumeVerdict v) => v switch
    {
        VolumeVerdict.Throttled => Loc.T("✗  объём/пакеты (TCP 16-20) — душат после первых десятков КБ"),
        VolumeVerdict.Ok        => Loc.T("✓  объём/пакеты (TCP 16-20) — лимита не видно"),
        _                       => Loc.T("•  объём/пакеты (TCP 16-20) — проверить не удалось"),
    };

    /// <summary>Combined verdict from the two independent axes: name-based DPI (RST/freeze on the
    /// SNI) and volume/packet limiting (TCP 16-20). At least one axis is a hit when this is called.</summary>
    private string BuildDpiVerdict(int reset, int freeze, bool volHit)
    {
        string sniHow = reset > 0 && freeze > 0 ? Loc.T("обрыв (RST) и заморозка")
                      : reset > 0 ? Loc.T("обрыв соединения (RST-инъекция)")
                      : freeze > 0 ? Loc.T("заморозка (пакеты дропаются)")
                      : "";
        bool sniHit = sniHow.Length > 0;

        if (sniHit && volHit)
            return IsRunning
                ? Loc.T("Провайдер режет и по имени сайта ({0}), и по объёму/пакетам (TCP 16-20). Обход включён, но не снимает — смените стратегию.", sniHow)
                : Loc.T("Провайдер режет и по имени сайта ({0}), и по объёму/пакетам (TCP 16-20) — отсюда «работает, но тормозит и отваливается». Включите обход; учтите, что лимит по объёму TLS-обход снимает не всегда.", sniHow);
        if (sniHit)
            return IsRunning
                ? Loc.T("Провайдер режет по DPI: {0}. Обход включён, но эти хосты всё равно режутся — смените стратегию.", sniHow)
                : Loc.T("Провайдер режет по DPI: {0}. Это снимается обходом — включите его.", sniHow);
        // volume-limit only (SNI clean)
        return Loc.T("По имени сайта провайдер не режет, но душит соединение по объёму/пакетам (TCP 16-20) — это про «открывается, но тормозит, буферит и отваливается на больших объёмах». Обход по TLS/SNI такое лечит не всегда; помогает смена стратегии или сети.");
    }

    // ---- custom targets (Диагностика tab) ---------------------------------

    private bool _showTargetPopup;
    public bool ShowTargetPopup { get => _showTargetPopup; set => SetField(ref _showTargetPopup, value); }

    private bool _targetIsNew = true;
    public bool TargetIsNew { get => _targetIsNew; private set => SetField(ref _targetIsNew, value); }

    private string _targetRootInput = "";
    public string TargetRootInput { get => _targetRootInput; set => SetField(ref _targetRootInput, value); }

    private string _targetDomainsText = "";
    public string TargetDomainsText { get => _targetDomainsText; set => SetField(ref _targetDomainsText, value); }

    private string _targetStatus = "";
    public string TargetStatus { get => _targetStatus; private set => SetField(ref _targetStatus, value); }

    private bool _isExpandingTarget;
    public bool IsExpandingTarget
    {
        get => _isExpandingTarget;
        private set
        {
            if (!SetField(ref _isExpandingTarget, value)) return;
            ExpandTargetCommand.RaiseCanExecuteChanged();
            SaveTargetCommand.RaiseCanExecuteChanged();
        }
    }

    private string? _editingTargetName;

    private void ReloadTargets()
    {
        CustomTargets.Clear();
        foreach (var t in _targets.GetTargets()) CustomTargets.Add(t);
    }

    /// <summary>Diagnostics rows = the built-in service matrix + a "Свои цели" group (capped).</summary>
    internal void RebuildDiagRows()
    {
        DiagRows.Clear();
        foreach (var r in DiagnosticsService.BuildRows())
        {
            r.PropertyChanged += (_, _) => RefreshHomeServiceReachability();
            DiagRows.Add(r);
        }
        foreach (var d in _targets.AllDomains().Take(30))
        {
            var customRow = new DiagRow { Group = Loc.T("Свои цели"), Name = d, Host = d };
            customRow.PropertyChanged += (_, _) => RefreshHomeServiceReachability();
            DiagRows.Add(customRow);
        }
        RefreshHomeServiceReachability();
    }

    private void OpenTargetPopup(CustomTarget? existing)
    {
        if (existing is null)
        {
            _editingTargetName = null;
            TargetIsNew = true;
            TargetRootInput = "";
            TargetDomainsText = "";
            TargetStatus = Loc.T("Введите домен (например yandex.ru) и нажмите «Найти домены».");
        }
        else
        {
            _editingTargetName = existing.Name;
            TargetIsNew = false;
            TargetRootInput = existing.Name;
            TargetDomainsText = string.Join('\n', _targets.ReadDomains(existing.Name));
            TargetStatus = Loc.T("{0} домен(ов). Можно отредактировать список или найти ещё.", existing.DomainCount);
        }
        ShowTargetPopup = true;
    }

    private async Task ExpandTargetAsync()
    {
        string root = TargetService.Normalize(TargetRootInput);
        if (root.Length == 0) { TargetStatus = Loc.T("Сначала укажите корневой домен."); return; }
        IsExpandingTarget = true;
        TargetStatus = Loc.T("Ищу домены для «{0}»…", root);
        try
        {
            // Progress<T> marshals back to this (UI) thread, so live status updates are safe to bind.
            var progress = new Progress<string>(msg => TargetStatus = msg);
            var found = await _targets.ExpandAsync(root, 40, progress, CancellationToken.None);
            var have = TargetDomainsText.Split('\n').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0);
            var merged = have.Concat(found).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d.Length).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            TargetDomainsText = string.Join('\n', merged);
            TargetStatus = Loc.T("Найдено: {0}. Всего в списке: {1}. Проверьте и сохраните.", found.Count, merged.Count);
        }
        catch (Exception ex) { TargetStatus = Loc.T("Не удалось получить домены: ") + ex.Message; }
        finally { IsExpandingTarget = false; }
    }

    private void SaveTarget()
    {
        string root = TargetService.Normalize(TargetRootInput);
        if (root.Length == 0) { TargetStatus = Loc.T("Укажите корневой домен."); return; }
        var domains = TargetDomainsText.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (domains.Count == 0) domains.Add(root);

        // root renamed → drop the previous file so it isn't orphaned
        if (_editingTargetName is not null &&
            !string.Equals(_editingTargetName, root, StringComparison.OrdinalIgnoreCase))
            _targets.Delete(_editingTargetName);

        _targets.Save(root, domains);
        _editingTargetName = root;
        TargetIsNew = false;
        ReloadTargets();
        RebuildDiagRows();
        ShowTargetPopup = false;
        Notify?.Invoke(Loc.T("Цель сохранена"),
            Loc.T("«{0}»: {1} домен(ов). Учитывается в диагностике, подборе и обходе.", root, domains.Count));
        if (IsRunning) _ = ApplyEngineOptionsAsync(); // relaunch so the bypass covers the new domains
    }

    private void DeleteTarget(CustomTarget? t)
    {
        if (t is null) return;
        _targets.Delete(t.Name);
        ReloadTargets();
        RebuildDiagRows();
        if (IsRunning) _ = ApplyEngineOptionsAsync();
    }

    /// <summary>Flatten an exception chain into one line. HttpRequestException hides the real cause
    /// behind "see inner exception", but a TLS reset from DPI ("forcibly closed") reads completely
    /// differently from an antivirus MITM ("remote certificate is invalid") or a skewed clock — we
    /// want that distinction in the Journal instead of the dead-end outer message.</summary>
    private static string DescribeError(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            string msg = e is System.Net.Sockets.SocketException se
                ? $"{e.Message} [{se.SocketErrorCode}]" : e.Message;
            if (parts.Count == 0 || !string.Equals(parts[^1], msg, StringComparison.Ordinal))
                parts.Add(msg);
        }
        return string.Join(" ← ", parts);
    }

    /// <summary>Whether a failure looks like a TLS/connection break (reset, cert error, socket error)
    /// rather than a plain HTTP/logic error — so the engine-download catch can add the non-obvious
    /// context (the zip host differs from the release page) and a workaround.</summary>
    private static bool LooksLikeTlsFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException
                or System.Net.Sockets.SocketException) return true;
            if (e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("reset", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public async Task CheckAndUpdateAsync(bool silent)
    {
        if (IsUpdating) return;
        IsUpdating = true;

        // The engine binary disappeared while its version stamp stayed behind — nothing here deletes it,
        // so an antivirus quarantined it. First repair from the embedded runtime inside this exe; only
        // fall back to GitHub if local repair is blocked or the user is doing a real online update.
        bool vanished = _updater.EngineBinaryVanished;
        if (vanished)
        {
            AppendLog(Loc.T("Файл движка winws2.exe был заблокирован антивирусом. Восстанавливаю встроенный движок из exe..."));
            Notify?.Invoke(Loc.T("Антивирус заблокировал движок"),
                           Loc.T("Восстанавливаю из встроенных ресурсов exe без интернета."));
        }

        try
        {
            if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete)
            {
                UpdateStatus = Loc.T("Восстановление встроенного движка…");
                AppendLog(Loc.T("Распаковываю встроенный движок из exe…"));
                if (!AppPaths.TryRestoreBundledEngine(out string? restoreError))
                {
                    await _exclusions.ApplyAsync();
                    AppPaths.TryRestoreBundledEngine(out restoreError);
                }

                if (_updater.IsEngineInstalled && _updater.IsEngineComplete)
                {
                    EngineVersion = _updater.InstalledVersion ?? EngineVersion;
                    UpdateStatus = Loc.T("Встроенный движок готов (офлайн).");
                    AppendLog(Loc.T("Встроенный движок успешно распакован из exe без использования сети."));
                    OnPropertyChanged(nameof(CanStart));
                    RaiseCommandStates();

                    if (silent)
                        return;
                }
                else
                {
                    string detail = string.IsNullOrWhiteSpace(restoreError) ? Loc.T("блокировка файла") : restoreError;
                    UpdateStatus = Loc.T("Антивирус блокирует распаковку движка.");
                    AppendLog(Loc.T("Не удалось распаковать движок: {0}. Добавьте программу в исключения (Настройки → Добавить в исключения).", detail));
                    return;
                }
            }

            UpdateStatus = Loc.T("Проверка обновлений…");
            ReleaseInfo latest;
            try
            {
                latest = await _updater.FetchLatestAsync();
            }
            catch (Exception ex)
            {
                string detail = DescribeError(ex);
                UpdateStatus = _updater.IsEngineInstalled
                    ? Loc.T("Не удалось проверить обновления ({0}). Работаем на установленной версии.", detail)
                    : Loc.T("Нет связи с GitHub: {0}", detail);
                if (!silent)
                    Notify?.Invoke("GXBRIEL_ZAPRET", UpdateStatus);
                return;
            }

            if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete || _updater.IsUpdateAvailable(latest))
            {
                bool wasRunning = IsRunning;
                if (wasRunning)
                {
                    AppendLog(Loc.T("Остановка движка для обновления…"));
                    _engine.Stop();
                    // Wait for winws2 to FULLY exit before overwriting engine files (a File.Copy over a
                    // running winws2.exe throws) and before CanStart lets us relaunch — poll instead of a
                    // fixed delay, like ApplyStrategyAsync does.
                    for (int i = 0; i < 60 && State != EngineState.Stopped; i++)
                        await Task.Delay(50);
                    await Task.Delay(200);
                }

                var progress = new Progress<UpdateProgress>(p =>
                {
                    UpdateProgress = p.Fraction;
                    UpdateStatus = p.Message;
                });
                try
                {
                    UpdateProgress = 0;            // never flash the previous run's fill
                    IsDownloadingEngine = true;    // only NOW does the progress bar appear
                    await _updater.InstallAsync(latest, progress);
                }
                catch (Exception ex)
                {
                    string detail = DescribeError(ex);
                    UpdateStatus = Loc.T("Не удалось установить движок: {0}", detail);
                    AppendLog(Loc.T("Ошибка загрузки движка: ") + detail);
                    if (LooksLikeTlsFailure(ex))
                        AppendLog(Loc.T("TLS-соединение оборвалось. Zip движка отдаётся с githubusercontent.com ")
                                + Loc.T("(не с github.com — поэтому страница релиза открывается, а загрузка нет). "
                                + "Частые причины: DPI-блокировка этого хоста провайдером или антивирус с "
                                + "проверкой HTTPS. Движка ещё нет, поэтому обход не поможет — скачайте его "
                                + "вручную или через VPN (см. инструкцию)."));
                    return;
                }
                EngineVersion = _updater.InstalledVersion ?? "—";
                UpdateStatus = Loc.T("Движок обновлён: {0}", latest.Tag);
                if (!silent)
                    Notify?.Invoke("GXBRIEL_ZAPRET", UpdateStatus);
                OnPropertyChanged(nameof(CanStart));
                RaiseCommandStates();

                if (wasRunning && CanStart) Start();
            }
            else
            {
                UpdateStatus = Loc.T("Актуальная версия: {0}", latest.Tag);
                if (!silent)
                    Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Установлена актуальная версия движка ({0}).", latest.Tag));
            }
        }
        finally
        {
            IsUpdating = false;
            IsDownloadingEngine = false;
        }
    }

    private void NewHostlist()
    {
        string name = "list";
        int i = 1;
        while (_hostlists.Exists(name)) name = $"list{++i}";
        _hostlists.Create(name);
        ReloadHostlists();
        SelectedHostlist = name;
    }

    private void DeleteHostlist()
    {
        if (SelectedHostlist is null) return;
        if (!ConfirmDialog.Show(Loc.T("Удалить список?"),
                Loc.T("Список доменов «{0}» будет удалён без возможности восстановления.", SelectedHostlist)))
            return;
        _hostlists.Delete(SelectedHostlist);
        ReloadHostlists();
        SelectedHostlist = Hostlists.FirstOrDefault();
    }

    private void SaveHostlist()
    {
        if (SelectedHostlist is null) return;
        _hostlists.Write(SelectedHostlist, HostlistContent);
        string msg = Loc.T("Список «{0}» сохранён.", SelectedHostlist);
        AppendLog(msg);
        Notify?.Invoke("GXBRIEL_ZAPRET", msg);
    }

    private void AddDomain()
    {
        if (SelectedHostlist is null || string.IsNullOrWhiteSpace(NewDomain)) return;
        string domain = NewDomain.Trim();
        _hostlists.AddDomain(SelectedHostlist, domain);
        HostlistContent = _hostlists.Read(SelectedHostlist);
        NewDomain = "";
        Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Домен «{0}» добавлен в список «{1}».", domain, SelectedHostlist));
    }

    private void DuplicatePreset()
    {
        if (SelectedPreset is null) return;
        var copy = SelectedPreset.Clone();
        copy.Name = Loc.T(SelectedPreset.Name) + Loc.T(" (моя копия)");
        _presets.AddUser(copy);
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == copy.Name);
        Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Создана копия стратегии «{0}».", copy.Name));
    }

    private void DeletePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } p) return;
        if (!ConfirmDialog.Show(Loc.T("Удалить пресет?"),
                Loc.T("Пресет «{0}» будет удалён без возможности восстановления.", Loc.T(p.Name))))
            return;
        _presets.DeleteUser(p);
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault();
    }

    private void SavePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } p) return;
        _presets.UpdateUser(p);
        OnPropertyChanged(nameof(CommandPreview));
        string msg = Loc.T("Пресет «{0}» сохранён.", Loc.T(p.Name));
        AppendLog(msg);
        Notify?.Invoke("GXBRIEL_ZAPRET", msg);
    }

    public void StopEngine() => _engine.Stop();

    /// <summary>Stop everything and release resources on application exit.</summary>
    public void Shutdown()
    {
        _isShuttingDown = true;
        _unexpectedRecovery.Dispose();
        try { _blockcheck2.Dispose(); } catch { }
        try { _diagCts?.Cancel(); } catch { }
        try { _patcherCts?.Cancel(); } catch { }
        _patcherMonitorTimer?.Stop();
        _patcherMonitorCts?.Cancel();
        try { _dpiCts?.Cancel(); } catch { }
        try { _autoCts?.Cancel(); } catch { }
        try { _monitor.Stop(); } catch { }
        try { _healthMonitor.Dispose(); } catch { }
        _uptimeTimer?.Stop();
        try { _autoSelect.Dispose(); } catch { }
        try { _tgProxy.Stop(); } catch { }
        // The WARP switch promises the tunnel goes down with the program. A tunnel left standing would
        // keep routing the whole machine through Cloudflare with nothing on screen to explain it.
        // A proxy left behind by a crash is a listening socket, not a broken network — but it would
        // still hold the port the next run wants.
        // The system proxy has to come down BEFORE the proxy it points at, or there is a window in
        // which Windows is aimed at a socket that has already gone.
        try { RestoreStaleSystemProxy(); } catch { }
        try { _engine.Dispose(); } catch { }
    }

}
