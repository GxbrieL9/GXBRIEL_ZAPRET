using System.Windows.Threading;
using System.IO;
using GxbrielZapret.Mvvm;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.ViewModels;

public sealed partial class MainViewModel
{
    private readonly AntigravityRelayService _antigravityRelay = new();
    private DispatcherTimer? _antigravityRelayTimer;
    private AntigravityRouteFeedback? _antigravityRouteFeedback;
    private bool _builtinFeedbackBusy;
    public bool BuiltinAntigravityRoutingEnabled => Settings.AntigravityBuiltinRouting;
    public bool CanEditPatcherProxy => CanRunPatcher && !BuiltinAntigravityRoutingEnabled;
    public string BuiltinAntigravityRoutingLabel => BuiltinAntigravityRoutingEnabled ? "Выключить встроенный маршрут" : "Включить встроенный маршрут";
    private string _builtinAntigravityStatus = "Маршрут выключен. Нажмите «Включить встроенный маршрут». Доступность зависит от публичных сервисов.";
    public string BuiltinAntigravityStatus { get => _builtinAntigravityStatus; private set => SetField(ref _builtinAntigravityStatus, value); }
    public RelayCommand ToggleBuiltinAntigravityCommand { get; private set; } = null!;
    public RelayCommand CheckBuiltinAntigravityCommand { get; private set; } = null!;
    public RelayCommand NextBuiltinAntigravityRouteCommand { get; private set; } = null!;

    private void InitializeBuiltinAntigravityCommands()
    {
        ToggleBuiltinAntigravityCommand = new RelayCommand(async _ =>
        {
            if (Settings.AntigravityBuiltinRouting)
            {
                IsPatcherBusy = true;
                try
                {
                    Settings.AntigravityBuiltinRouting = false;
                    if (!_settingsSvc.Save())
                    {
                        Settings.AntigravityBuiltinRouting = true;
                        BuiltinAntigravityStatus = "Не удалось сохранить отключение. Встроенный маршрут оставлен включённым.";
                        return;
                    }
                    _antigravityRelayTimer?.Stop();
                    _antigravityRouteFeedback = null;
                    await _antigravityRelay.StopAsync();
                    BuiltinAntigravityStatus = "Встроенный маршрут выключен. Перезапустите Antigravity из GX, чтобы сменить окружение. Системный DNS и VPN не изменялись.";
                }
                finally { NotifyBuiltinRoutingChanged(); IsPatcherBusy = false; }
                return;
            }
            IsPatcherBusy = true;
            _patcherCts = new();
            bool previousDirect = Settings.AntigravityDirectConnection;
            try
            {
                _antigravityRelay.Start();
                ResetBuiltinRouteFeedback();
                Settings.AntigravityBuiltinRouting = true;
                Settings.AntigravityDirectConnection = false;
                if (!_settingsSvc.Save()) throw new InvalidOperationException("Настройки не сохранены.");
                NotifyBuiltinRoutingChanged();
                BuiltinAntigravityStatus = "Проверяю встроенные маршруты. Отдельный VPN не запускается…";
                string report = await _antigravityRelay.CheckAsync(_patcherCts.Token);
                BuiltinAntigravityStatus = report + "\nПерезапустите Antigravity кнопкой GX. Для встроенного маршрута GX должен оставаться запущенным, в том числе в трее.";
                StartBuiltinRoutingTimer();
            }
            catch (OperationCanceledException)
            {
                BuiltinAntigravityStatus = "Проверка отменена. Локальный маршрут включён; ответ модели не проверен.";
                StartBuiltinRoutingTimer();
            }
            catch
            {
                Settings.AntigravityBuiltinRouting = false;
                Settings.AntigravityDirectConnection = previousDirect;
                await _antigravityRelay.StopAsync();
                _settingsSvc.Save();
                BuiltinAntigravityStatus = "Не удалось включить встроенный маршрут. Прежние настройки запуска восстановлены.";
            }
            finally { NotifyBuiltinRoutingChanged(); _patcherCts.Dispose(); _patcherCts = null; IsPatcherBusy = false; }
        }, _ => CanRunPatcher);
        CheckBuiltinAntigravityCommand = new RelayCommand(async _ =>
        {
            IsPatcherBusy = true; _patcherCts = new();
            try { BuiltinAntigravityStatus = await _antigravityRelay.CheckAsync(_patcherCts.Token); }
            catch (OperationCanceledException) { BuiltinAntigravityStatus = "Проверка отменена. Ответ модели не проверен."; }
            catch { BuiltinAntigravityStatus = "Не удалось проверить встроенный маршрут."; }
            finally { _patcherCts.Dispose(); _patcherCts = null; IsPatcherBusy = false; }
        }, _ => CanRunPatcher && BuiltinAntigravityRoutingEnabled);
        NextBuiltinAntigravityRouteCommand = new RelayCommand(_ =>
        {
            _antigravityRelay.NextRoute();
            _antigravityRouteFeedback?.Reset();
            BuiltinAntigravityStatus = "Выбран следующий маршрут. Соединения с API переподключаются; текущий запрос может прерваться. Отправьте новое сообщение в Antigravity. " + _antigravityRelay.Status;
        }, _ => CanRunPatcher && BuiltinAntigravityRoutingEnabled);
    }

    private void NotifyBuiltinRoutingChanged()
    {
        OnPropertyChanged(nameof(BuiltinAntigravityRoutingEnabled));
        OnPropertyChanged(nameof(BuiltinAntigravityRoutingLabel));
        OnPropertyChanged(nameof(PatcherDirectConnection));
        OnPropertyChanged(nameof(CanEditPatcherProxy));
        RefreshPatcherCommandStates();
    }

    private void RestoreBuiltinAntigravityRouting()
    {
        if (!Settings.AntigravityBuiltinRouting) return;
        try
        {
            _antigravityRelay.Start();
            ResetBuiltinRouteFeedback();
            BuiltinAntigravityStatus = "Встроенный маршрут восстановлен. Запускайте Antigravity кнопкой GX. GX должен оставаться в трее. Ответ модели не проверен.";
            StartBuiltinRoutingTimer();
        }
        catch { BuiltinAntigravityStatus = "Не удалось восстановить встроенный маршрут. Выключите и включите его повторно."; }
    }

    private void StartBuiltinRoutingTimer()
    {
        _lastBuiltinRelayStatus = _antigravityRelay.Status;
        _antigravityRelayTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _antigravityRelayTimer.Stop();
        _antigravityRelayTimer.Tick -= BuiltinRoutingTick;
        _antigravityRelayTimer.Tick += BuiltinRoutingTick;
        _antigravityRelayTimer.Start();
    }

    private string _lastBuiltinRelayStatus = "";
    private void ResetBuiltinRouteFeedback() => _antigravityRouteFeedback = new AntigravityRouteFeedback(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity", "logs", "language_server.log"));

    private async void BuiltinRoutingTick(object? sender, EventArgs e)
    {
        if (IsPatcherBusy || _builtinFeedbackBusy || !BuiltinAntigravityRoutingEnabled) return;
        _builtinFeedbackBusy = true;
        try
        {
            var feedback = _antigravityRouteFeedback;
            var result = feedback is null ? AntigravityRouteFeedbackResult.None : await Task.Run(feedback.Poll);
            if (IsPatcherBusy || !BuiltinAntigravityRoutingEnabled || feedback != _antigravityRouteFeedback) return;
            if (result == AntigravityRouteFeedbackResult.SwitchRoute)
            {
                _antigravityRelay.NextRoute();
                _lastBuiltinRelayStatus = _antigravityRelay.Status;
                BuiltinAntigravityStatus = "В новой записи журнала App найден отказ по региону. GX переключил маршрут и переподключил API. Повторите сообщение в Antigravity. Ответ модели не проверен.";
                return;
            }
            if (result == AntigravityRouteFeedbackResult.Exhausted)
            {
                BuiltinAntigravityStatus = "После переключений App продолжает сообщать отказ по региону. Автопереключение временно остановлено. Попробуйте позже или укажите свой прокси; повторный патч не устранит серверный отказ.";
                return;
            }
            string status = _antigravityRelay.Status;
            if (status == _lastBuiltinRelayStatus) return;
            _lastBuiltinRelayStatus = status;
            BuiltinAntigravityStatus = status + " GX должен оставаться запущенным, в том числе в трее.";
        }
        catch { /* bounded log diagnostics must never interrupt the app */ }
        finally { _builtinFeedbackBusy = false; }
    }
}
