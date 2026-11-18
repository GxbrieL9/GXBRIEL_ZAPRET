using System.Collections.ObjectModel;
using GxbrielZapret.Mvvm;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.ViewModels;

public sealed partial class MainViewModel
{
    private string _patcherProxyInput = "";
    public string PatcherProxyInput { get => _patcherProxyInput; set => SetField(ref _patcherProxyInput, value); }
    private string _patcherProxyStatus = "AG_LS_PROXY направляет Language Server через ваш HTTP/HTTPS или SOCKS5 прокси. Нужен работающий прокси с выходом не из РФ/РБ для обхода блокировки региона Google (User location is not supported).";
    public string PatcherProxyStatus { get => _patcherProxyStatus; private set => SetField(ref _patcherProxyStatus, value); }
    public ObservableCollection<LocalProxyCandidate> DetectedLocalProxies { get; } = [];
    public bool HasDetectedLocalProxies => DetectedLocalProxies.Count > 0;

    public RelayCommand SavePatcherProxyCommand { get; private set; } = null!;
    public RelayCommand TestPatcherProxyCommand { get; private set; } = null!;
    public RelayCommand ScanLocalProxiesCommand { get; private set; } = null!;
    public RelayCommand ApplyLocalProxyCommand { get; private set; } = null!;
    public RelayCommand ClearPatcherProxyCommand { get; private set; } = null!;

    private void InitializePatcherProxyCommands()
    {
        string? existing = AntigravityProxy.Read();
        if (!string.IsNullOrWhiteSpace(existing)) _patcherProxyInput = existing;

        SavePatcherProxyCommand = new RelayCommand(_ =>
        {
            try
            {
                string value = PatcherProxyInput.Trim();
                if (value.Length == 0) { PatcherProxyStatus = "Введите адрес прокси. Существующие настройки не изменены."; return; }
                AntigravityProxy.Parse(value);
                Environment.SetEnvironmentVariable("AG_LS_PROXY", value, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("AG_LS_PROXY", value);
                Settings.AntigravityProxy = value;
                Settings.AntigravityUseProxy = true;
                _settingsSvc.Save();
                PatcherDirectConnection = false;
                PatcherProxyInput = "";
                PatcherProxyStatus = AntigravityProxyDiagnostic.Read() + " Сохранено для запуска из GX. Перезапустите Antigravity кнопкой GX для применения.";
            }
            catch (ArgumentException ex) { PatcherProxyStatus = ex.Message; }
            catch { PatcherProxyStatus = "Не удалось сохранить AG_LS_PROXY в окружении пользователя Windows."; }
        }, _ => CanEditPatcherProxy);

        ClearPatcherProxyCommand = new RelayCommand(_ =>
        {
            try
            {
                Environment.SetEnvironmentVariable("AG_LS_PROXY", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("AG_LS_PROXY", null);
                Settings.AntigravityProxy = "";
                Settings.AntigravityUseProxy = false;
                _settingsSvc.Save();
                PatcherProxyInput = "";
                PatcherProxyStatus = AntigravityProxyDiagnostic.Read() + " Пользовательское значение удалено; системные настройки и VPN могут оставаться активными.";
            }
            catch { PatcherProxyStatus = "Не удалось удалить AG_LS_PROXY из системы."; }
        }, _ => CanEditPatcherProxy);

        ScanLocalProxiesCommand = new RelayCommand(async _ =>
        {
            PatcherProxyStatus = "Поиск запущенных локальных прокси (Xray, Clash, NekoBox, Shadowsocks)…";
            var detected = await Task.Run(AntigravityProxyManager.DetectLocalProxies);
            DetectedLocalProxies.Clear();
            foreach (var p in detected) DetectedLocalProxies.Add(p);
            OnPropertyChanged(nameof(HasDetectedLocalProxies));
            PatcherProxyStatus = detected.Count > 0
                ? $"Найдено запущенных прокси: {detected.Count}. Нажмите на нужный для подстановки."
                : "Запущенных локальных прокси на стандартных портах не обнаружено.";
        }, _ => CanEditPatcherProxy);

        ApplyLocalProxyCommand = new RelayCommand(p =>
        {
            if (p is string url)
            {
                PatcherProxyInput = url;
                PatcherProxyStatus = $"Выбран прокси {url}. Нажмите «Проверить связь» или «Сохранить прокси».";
            }
        });

        TestPatcherProxyCommand = new RelayCommand(async _ =>
        {
            IsPatcherBusy = true;
            _patcherCts = new();
            try
            {
                string? value = string.IsNullOrWhiteSpace(PatcherProxyInput) ? AntigravityProxy.Read() : PatcherProxyInput.Trim();
                if (string.IsNullOrWhiteSpace(value)) { PatcherProxyStatus = "Сначала укажите адрес прокси."; return; }
                PatcherProxyStatus = "Проверяю HTTPS к Google через указанный прокси…";
                var (success, message) = await AntigravityProxyManager.TestProxyAsync(value, _patcherCts.Token);
                PatcherProxyStatus = message;
                if (PatcherDirectConnection && success) PatcherProxyStatus += " (Внимание: сейчас включена галочка запуска без AG_LS_PROXY. Снимите её перед запуском).";
            }
            catch (ArgumentException ex) { PatcherProxyStatus = ex.Message; }
            catch (OperationCanceledException) { PatcherProxyStatus = "Проверка прокси отменена."; }
            catch { PatcherProxyStatus = "Проверка прокси не завершена. Настройки не изменены."; }
            finally { _patcherCts.Dispose(); _patcherCts = null; IsPatcherBusy = false; }
        }, _ => CanEditPatcherProxy);

        // Auto-scan on startup in background
        _ = Task.Run(() =>
        {
            try
            {
                var detected = AntigravityProxyManager.DetectLocalProxies();
                if (detected.Count > 0)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        DetectedLocalProxies.Clear();
                        foreach (var p in detected) DetectedLocalProxies.Add(p);
                        OnPropertyChanged(nameof(HasDetectedLocalProxies));
                    });
                }
            }
            catch { }
        });
    }
}
