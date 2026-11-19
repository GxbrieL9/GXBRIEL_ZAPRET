using System.Diagnostics;
using System.Windows;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Telegram;

namespace GxbrielZapret.ViewModels;

/// <summary>
/// Built-in Telegram MTProto proxy (native tg-ws-proxy).
/// </summary>
public sealed partial class MainViewModel
{
    // ---- built-in Telegram proxy (native tg-ws-proxy) ----------------------

    /// <summary>True while the local MTProto→WebSocket proxy is listening.</summary>
    public bool IsTelegramProxyRunning => _tgProxy.IsRunning;

    public bool IsTelegramProxyEnabled
    {
        get => _tgProxy.IsRunning;
        set
        {
            if (value == _tgProxy.IsRunning) return;
            if (value) StartTelegramProxy();
            else StopTelegramProxy();
        }
    }

    public string TelegramProxyEndpoint => $"{_tgProxy.Host}:{_tgProxy.Port}";
    public string TelegramProxySecret => _tgProxy.SecretHex;
    public string TelegramProxyLink => _tgProxy.ProxyLink;

    private string _telegramProxyStatus = Loc.T("Выключено. Нажмите «Включить», затем «Открыть в Telegram».");
    public string TelegramProxyStatus { get => _telegramProxyStatus; private set => SetField(ref _telegramProxyStatus, value); }

    public string TelegramProxyButtonText => _tgProxy.IsRunning ? Loc.T("Остановить") : Loc.T("Включить");

    public void StartTelegramProxy(bool autoOpenClient = true)
    {
        Settings.DesiredTelegramEnabled = true;
        _tgProxy.Configure(Settings.TgProxyPort, Settings.TgProxySecret);
        if (_tgProxy.Start())
        {
            Settings.TelegramActual = "RUNNING";
            if (string.IsNullOrEmpty(Settings.TgProxySecret))
            {
                Settings.TgProxySecret = _tgProxy.SecretHex;
            }
            _settingsSvc.Save();

            string link = _tgProxy.ProxyLink;
            if (autoOpenClient && !string.Equals(Settings.TelegramLastOpenedLink, link, StringComparison.Ordinal))
            {
                try
                {
                    OpenUrl(link);
                    Settings.TelegramLastOpenedLink = link;
                    _settingsSvc.Save();
                }
                catch { }
            }
        }
        else
        {
            Settings.TelegramActual = "ERROR";
            _settingsSvc.Save();
        }
        RefreshTelegramProxyStatus();
    }

    public void StopTelegramProxy()
    {
        Settings.DesiredTelegramEnabled = false;
        Settings.TelegramActual = "STOPPED";
        _settingsSvc.Save();
        _tgProxy.Stop();
        RefreshTelegramProxyStatus();
    }

    public void ToggleTelegramProxy()
    {
        if (_tgProxy.IsRunning)
        {
            StopTelegramProxy();
        }
        else
        {
            StartTelegramProxy();
        }
    }

    public int TelegramProxyPort
    {
        get => Settings.TgProxyPort;
        set
        {
            if (value is < 1 or > 65535 || Settings.TgProxyPort == value) return;
            Settings.TgProxyPort = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            _tgProxy.Configure(value, Settings.TgProxySecret);
            OnPropertyChanged(nameof(TelegramProxyEndpoint));
            OnPropertyChanged(nameof(TelegramProxyLink));
        }
    }

    /// <summary>Whether to auto-start the built-in Telegram proxy on app launch.</summary>
    public bool TelegramProxyAutostart
    {
        get => Settings.TgProxyAutostart;
        set
        {
            if (Settings.TgProxyAutostart == value) return;
            Settings.TgProxyAutostart = value;
            _settingsSvc.Save();
            OnPropertyChanged();
        }
    }

    public bool IsTelegramProxyReconfigureRequired => _tgProxy.IsReconfigureRequired;

    public string TelegramProxyActionText => _tgProxy.IsReconfigureRequired
        ? Loc.T("Обновить Telegram")
        : Loc.T("Открыть в Telegram");

    public void RefreshTelegramProxyStatus()
    {
        if (!_tgProxy.IsRunning)
        {
            TelegramProxyStatus = _tgProxy.StartError ?? Loc.T("Выключено. Нажмите «Включить», затем «Открыть в Telegram».");
        }
        else if (_tgProxy.IsReconfigureRequired)
        {
            TelegramProxyStatus = Loc.T("Порт прокси изменён (предыдущий занят). Нажмите «Обновить Telegram», чтобы применить настройки.");
        }
        else
        {
            var routes = _tgProxy.Routes.GetAllRoutes();
            bool hasSuccess = routes.Any(r => r.ConsecutiveSuccesses > 0 && r.State == RouteState.Healthy);
            bool allDead = routes.Count > 0 && routes.All(r => r.State == RouteState.Dead || r.IsCooling);

            if (allDead)
            {
                TelegramProxyStatus = Loc.T("Ошибка: нет доступных upstream-маршрутов Telegram.");
            }
            else if (hasSuccess)
            {
                TelegramProxyStatus = Loc.T("Прокси Telegram активен (upstream подтверждён).");
            }
            else
            {
                TelegramProxyStatus = Loc.T("Прокси запущен локально. Ожидание подключения Telegram…");
            }
        }

        OnPropertyChanged(nameof(IsTelegramProxyRunning));
        OnPropertyChanged(nameof(IsTelegramProxyEnabled));
        OnPropertyChanged(nameof(TelegramProxyButtonText));
        OnPropertyChanged(nameof(IsTelegramProxyReconfigureRequired));
        OnPropertyChanged(nameof(TelegramProxyActionText));
        OnPropertyChanged(nameof(TelegramProxyEndpoint));
        OnPropertyChanged(nameof(TelegramProxyPort));
        OnPropertyChanged(nameof(TelegramProxySecret));
        OnPropertyChanged(nameof(TelegramProxyLink));
        OnPropertyChanged(nameof(TelegramProxyLatencyText));
        OnPropertyChanged(nameof(TelegramProxyLaneSummary));
    }

    public string TelegramProxyLatencyText
    {
        get
        {
            if (!_tgProxy.IsRunning) return "—";
            var routes = _tgProxy.Routes.GetAllRoutes();
            var successfulRoutes = routes.Where(r => r.ConsecutiveSuccesses > 0 && r.LatencyMs > 0).ToList();
            if (successfulRoutes.Count == 0) return "—";
            var minLat = successfulRoutes.Select(r => r.LatencyMs).Min();
            return $"{minLat} ms";
        }
    }

    public string TelegramProxyLaneSummary => _tgProxy.IsRunning
        ? Loc.T("Сообщения: Прямой / WSS · Медиа: WSS")
        : Loc.T("MTProto-over-WSS туннель готов к запуску");

    private void OnTelegramProxyStateChanged()
    {
        // Persist the auto-generated secret the first time so the tg:// link stays stable across runs.
        if (string.IsNullOrEmpty(Settings.TgProxySecret))
        {
            Settings.TgProxySecret = _tgProxy.SecretHex;
            _settingsSvc.Save();
        }

        // Sticky port persistence: if proxy bound to a different fallback port, save it so subsequent launches use this port
        if (_tgProxy.IsRunning && Settings.TgProxyPort != _tgProxy.Port)
        {
            Settings.TgProxyPort = _tgProxy.Port;
            _settingsSvc.Save();
        }

        if (!_tgProxy.IsRunning)
        {
            if (!string.IsNullOrEmpty(_tgProxy.StartError))
            {
                Settings.TelegramActual = "ERROR";
            }
            else if (Settings.TelegramActual == "RUNNING")
            {
                Settings.TelegramActual = "STOPPED";
            }
            _settingsSvc.Save();
        }

        RefreshTelegramProxyStatus();
        OnPropertyChanged(nameof(IsTelegramProxyRunning));
        SimpleStatus = _tgProxy.IsRunning ? Loc.T("Прокси Telegram запущен.") : Loc.T("Прокси Telegram остановлен.");
    }

    public IReadOnlyCollection<RouteItem> TelegramRoutes => _tgProxy.Routes.GetAllRoutes();
    public int TelegramRouteSwitchCount => _tgProxy.Routes.RouteSwitchCount;

    private bool _isCheckingTgProxy;

    /// <summary>True while the Telegram-proxy self-test runs (disables its button, swaps its label).</summary>
    public bool IsCheckingTelegramProxy
    {
        get => _isCheckingTgProxy;
        private set
        {
            if (!SetField(ref _isCheckingTgProxy, value)) return;
            OnPropertyChanged(nameof(CheckTelegramProxyButtonText));
            CheckTelegramProxyCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Label for the Telegram self-test button.</summary>
    public string CheckTelegramProxyButtonText => _isCheckingTgProxy ? Loc.T("Проверяю…") : Loc.T("Проверить соединение");

    private string? _lastTelegramDiagnostics;
    public string? LastTelegramDiagnostics
    {
        get => _lastTelegramDiagnostics;
        private set
        {
            if (SetField(ref _lastTelegramDiagnostics, value))
            {
                OnPropertyChanged(nameof(HasTelegramDiagnostics));
                CopyTelegramDiagnosticsCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasTelegramDiagnostics => !string.IsNullOrWhiteSpace(_lastTelegramDiagnostics);

    public void CopyTelegramDiagnostics()
    {
        if (string.IsNullOrWhiteSpace(LastTelegramDiagnostics)) return;
        CopyToClipboard(LastTelegramDiagnostics);
        SimpleStatus = Loc.T("Диагностический отчёт Telegram скопирован.");
    }

    /// <summary>Run the built-in proxy's upstream self-test and show the verdict on the card; the
    /// step-by-step details go to the journal. Independent of the winws2 engine and needs no admin.</summary>
    private async Task CheckTelegramProxyAsync()
    {
        IsCheckingTelegramProxy = true;
        TelegramProxyStatus = Loc.T("Проверяю соединение с Telegram…");
        try
        {
            var report = await _tgProxy.DetailedDiagnosticCheckAsync();
            LastTelegramDiagnostics = report.ToSafeDiagnosticText();
            TelegramProxyStatus = report.Summary;
            _tgProxy.RaiseLogLine(Loc.T("[tg-proxy] Результаты проверки:\n{0}", LastTelegramDiagnostics));
        }
        catch (Exception ex)
        {
            TelegramProxyStatus = Loc.T("Проверка не удалась: ") + ex.Message;
        }
        finally
        {
            IsCheckingTelegramProxy = false;
        }
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            SimpleStatus = Loc.T("Ссылка на прокси скопирована.");
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Ссылка на Telegram-прокси скопирована в буфер обмена."));
        }
        catch { /* clipboard busy — ignore */ }
    }
}
