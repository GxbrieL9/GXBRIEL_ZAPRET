using System.Windows;
using GxbrielZapret.Mvvm;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.ViewModels;

public sealed partial class MainViewModel
{
    private AntigravityComponentStatus _patcherLs = new() { Key = "LanguageServer", Name = "Antigravity · Language Server", State = "NotChecked" };
    public AntigravityComponentStatus PatcherLs { get => _patcherLs; private set => SetField(ref _patcherLs, value); }
    private AntigravityComponentStatus _patcherIde = new() { Key = "Ide", Name = "Antigravity IDE · main.js", State = "NotChecked" };
    public AntigravityComponentStatus PatcherIde { get => _patcherIde; private set => SetField(ref _patcherIde, value); }
    private AntigravityComponentStatus _patcherVsCode = new() { Key = "VsCode", Name = "Расширение VS Code", State = "NotChecked" };
    public AntigravityComponentStatus PatcherVsCode { get => _patcherVsCode; private set => SetField(ref _patcherVsCode, value); }
    private AntigravityComponentStatus _patcherAgy = new() { Key = "Agy", Name = "Antigravity CLI · agy", State = "NotChecked" };
    public AntigravityComponentStatus PatcherAgy { get => _patcherAgy; private set => SetField(ref _patcherAgy, value); }
    public IReadOnlyList<AntigravityComponentStatus> PatcherComponents => [PatcherLs, PatcherIde, PatcherAgy, PatcherVsCode];
    public bool HasPatcherActionNeeded => PatcherComponents.Any(c => c.CanPatch);
    public bool HasAnyPatchedComponent => PatcherComponents.Any(c => c.IsPatched);
    public bool ShowPatcherBadge => HasPatcherActionNeeded || HasAnyPatchedComponent || HasPatcherUpdateMessage;
    public string PatcherBadgeColor => HasPatcherActionNeeded || HasPatcherUpdateMessage ? "#C5B76B" : "#75BB9F";
    public string PatcherBadgeToolTip => HasPatcherUpdateMessage ? PatcherUpdateMessage : HasPatcherActionNeeded 
        ? Loc.T("Доступны обновления или новые патчи") 
        : Loc.T("Патч обнаружен. Ответ модели не проверен.");

    private CancellationTokenSource? _patcherCts;
    private bool _patcherSelectionRequired;
    private bool _isPatcherBusy;
    public bool IsPatcherBusy
    {
        get => _isPatcherBusy;
        private set
        {
            if (!SetField(ref _isPatcherBusy, value)) return;
            RefreshPatcherCommandStates();
        }
    }
    public bool CanRunPatcher => !IsPatcherBusy && !_isPatcherMonitorChecking && !IsInitializing && !_isShuttingDown;
    public bool IsPatcherAutoDiscovery => string.IsNullOrWhiteSpace(PatcherInstallRoot);
    public bool PatcherDirectConnection
    {
        get => Settings.AntigravityDirectConnection;
        set
        {
            if (!CanRunPatcher || value == Settings.AntigravityDirectConnection) return;
            Settings.AntigravityDirectConnection = value;
            _settingsSvc.Save();
            OnPropertyChanged();
        }
    }
    public string PatcherInstallRoot
    {
        get => Settings.AntigravityInstallRoot;
        set
        {
            if (IsPatcherBusy || _isPatcherMonitorChecking || value == Settings.AntigravityInstallRoot) return;
            Settings.AntigravityInstallRoot = value?.Trim() ?? "";
            _settingsSvc.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPatcherAutoDiscovery));
            ApplyPatcherResult(UncheckedPatcherStatus());
            PatcherUpdateMessage = "";
            _patcherForceMonitorRefresh = true;
            PatcherStatusMessage = "Каталог изменён. Файлы ещё не проверены.";
        }
    }
    private string _patcherStatusMessage = "Компоненты ещё не проверены. Ответ модели не проверен.";
    public string PatcherStatusMessage { get => _patcherStatusMessage; private set => SetField(ref _patcherStatusMessage, value); }
    private string _patcherLog = "";
    public string PatcherLog { get => _patcherLog; private set => SetField(ref _patcherLog, value); }

    public RelayCommand CheckPatcherStatusCommand { get; private set; } = null!;
    public RelayCommand PatchAllComponentsCommand { get; private set; } = null!;
    public RelayCommand RestoreAllComponentsCommand { get; private set; } = null!;
    public RelayCommand PatchComponentCommand { get; private set; } = null!;
    public RelayCommand RestoreComponentCommand { get; private set; } = null!;
    public RelayCommand ClearPatcherLogCommand { get; private set; } = null!;
    public RelayCommand BrowseAntigravityCommand { get; private set; } = null!;
    public RelayCommand LaunchAntigravityCommand { get; private set; } = null!;
    public RelayCommand CancelPatcherCommand { get; private set; } = null!;
    public RelayCommand ResetAntigravityRootCommand { get; private set; } = null!;
    public RelayCommand TerminateAntigravityProcessesCommand { get; private set; } = null!;
    public RelayCommand RestartAntigravityCommand { get; private set; } = null!;
    public RelayCommand ForceCloseAntigravityCommand { get; private set; } = null!;

    private IEnumerable<RelayCommand?> PatcherCommands() =>
        [CheckPatcherStatusCommand, PatchAllComponentsCommand, RestoreAllComponentsCommand, PatchComponentCommand,
         RestoreComponentCommand, BrowseAntigravityCommand, LaunchAntigravityCommand, CancelPatcherCommand, ResetAntigravityRootCommand,
         TerminateAntigravityProcessesCommand, RestartAntigravityCommand, ForceCloseAntigravityCommand];

    private void InitializePatcherCommands()
    {
        CheckPatcherStatusCommand = new RelayCommand(async _ => await RefreshPatcherStatusAsync(), _ => CanRunPatcher);
        PatchAllComponentsCommand = new RelayCommand(async _ => await PatchComponentAsync("All"), _ => CanRunPatcher && !_patcherSelectionRequired && PatcherComponents.Any(c => c.CanPatch));
        RestoreAllComponentsCommand = new RelayCommand(async _ => await RestoreComponentAsync("All"), _ => CanRunPatcher && !_patcherSelectionRequired && PatcherComponents.Any(c => c.CanRestore));
        PatchComponentCommand = new RelayCommand(async p => await PatchComponentAsync((string)p!), p => CanRunPatcher && PatcherComponents.Any(c => c.Key == p as string && c.CanPatch && (!_patcherSelectionRequired || c.IsStandaloneCli)));
        RestoreComponentCommand = new RelayCommand(async p => await RestoreComponentAsync((string)p!), p => CanRunPatcher && PatcherComponents.Any(c => c.Key == p as string && c.CanRestore && (!_patcherSelectionRequired || c.IsStandaloneCli)));
        ClearPatcherLogCommand = new RelayCommand(_ => PatcherLog = "");
        CancelPatcherCommand = new RelayCommand(_ => _patcherCts?.Cancel(), _ => IsPatcherBusy);
        ResetAntigravityRootCommand = new RelayCommand(async _ => { PatcherInstallRoot = ""; await RefreshPatcherStatusAsync(); }, _ => CanRunPatcher);
        BrowseAntigravityCommand = new RelayCommand(async _ =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Каталог Antigravity или компонента", Multiselect = false };
            if (dialog.ShowDialog() == true) { PatcherInstallRoot = dialog.FolderName; await RefreshPatcherStatusAsync(); }
        }, _ => CanRunPatcher);
        LaunchAntigravityCommand = new RelayCommand(async _ => await RunPatcherApplicationAsync(false), _ => CanRunPatcher && PatcherLauncherPath is not null);
        TerminateAntigravityProcessesCommand = new RelayCommand(async _ => await RunPatcherApplicationAsync(false, closeOnly: true), _ => CanRunPatcher && PatcherLauncherPath is not null);
        RestartAntigravityCommand = new RelayCommand(async _ => await RunPatcherApplicationAsync(true), _ => CanRunPatcher && PatcherLauncherPath is not null);
        ForceCloseAntigravityCommand = new RelayCommand(async _ => await ForceCloseAntigravityAsync(), _ => CanRunPatcher && PatcherLauncherPath is not null);
    }

    public Task RefreshPatcherStatusAsync() => RunPatcherAsync("Status", "All");
    public Task PatchComponentAsync(string target) => RunPatcherAsync("Patch", target);
    public Task RestoreComponentAsync(string target) => RunPatcherAsync("Restore", target);

    private async Task RunPatcherAsync(string action, string target)
    {
        if (!CanRunPatcher) return;
        string? root = string.IsNullOrWhiteSpace(PatcherInstallRoot) ? null : PatcherInstallRoot;
        string? operationRoot = target == "Agy" && PatcherAgy.IsStandaloneCli ? System.IO.Path.GetDirectoryName(PatcherAgy.Path!) : root;
        IsPatcherBusy = true;
        _patcherCts = new();
        try
        {
            if (action == "Status") ApplyPatcherResult(UncheckedPatcherStatus());
            if (action != "Status")
            {
                if (_patcherSelectionRequired && !(target == "Agy" && PatcherAgy.IsStandaloneCli))
                {
                    PatcherStatusMessage = "Выберите нужную установку Antigravity кнопкой папки. Файлы не изменены; процессы не закрывались.";
                    return;
                }
                var selected = PatcherComponents.Where(c => (target == "All" || c.Key == target) && (action == "Patch" ? c.CanPatch : c.CanRestore)).ToArray();
                if (selected.Length == 0) { PatcherStatusMessage = "Нет подходящих компонентов для этой операции."; return; }
                string text = string.Join(Environment.NewLine, selected.Select(c => c.Path)) +
                    "\n\nПеред изменением файлов будет отправлен запрос на обычное закрытие Antigravity. Сохраните работу; принудительное завершение не используется. Откат доступен только для проверенной резервной копии текущей версии.";
                if (MessageBox.Show(text, action == "Patch" ? "Применить патч Antigravity?" : "Восстановить исходные файлы?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                PatcherStatusMessage = "Ожидаю закрытия Antigravity…";
                var remaining = await AntigravityPatcherService.RequestCloseProcessesAsync(operationRoot, _patcherCts.Token);
                if (remaining.Count > 0)
                {
                    PatcherStatusMessage = "Antigravity не закрылся. Ожидаю решения о завершении процессов.";
                    if (MessageBox.Show("Обычное закрытие не завершило процессы:\n" + string.Join(", ", remaining) +
                        "\n\nЗавершить их принудительно и продолжить? Несохранённые изменения Antigravity будут потеряны.",
                        "Продолжить применение патча?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    {
                        PatcherStatusMessage = "Операция отменена: Antigravity ещё работает. Файлы не изменены.";
                        return;
                    }
                    _patcherCts.Token.ThrowIfCancellationRequested();
                    PatcherStatusMessage = "Завершаю процессы Antigravity перед изменением файлов…";
                    remaining = await AntigravityProcesses.TerminateAsync(operationRoot, _patcherCts.Token);
                    if (remaining.Count > 0)
                    {
                        PatcherStatusMessage = "Файлы не изменены. Не завершены процессы: " + string.Join(", ", remaining);
                        AppendPatcherLog(PatcherStatusMessage);
                        return;
                    }
                }
            }
            PatcherStatusMessage = action == "Status" ? "Проверяю файлы компонентов…" : "Проверяю сигнатуры, копии и записываю изменения…";
            if (action == "Status")
            {
                var check = await _patcherUpdateMonitor.CheckAsync(root, _patcherCts.Token, force: true);
                if (check is not null) ShowPatcherMonitorResult(check);
                return;
            }
            var result = action switch
            {
                "Patch" => await AntigravityPatcherService.PatchAsync(target, _patcherCts.Token, operationRoot),
                "Restore" => await AntigravityPatcherService.RestoreAsync(target, _patcherCts.Token, operationRoot),
                _ => await AntigravityPatcherService.GetStatusAsync(_patcherCts.Token, root)
            };
            ApplyPatcherResult(operationRoot != root ? await AntigravityPatcherService.GetStatusAsync(_patcherCts.Token, root) : result);
            PatcherStatusMessage = result.Summary;
            AppendPatcherLog(result.Summary);
            if (action != "Status") Notify?.Invoke("GXBRIEL_ZAPRET · Antigravity", result.Summary);
            if (result.Succeeded)
            {
                var check = await _patcherUpdateMonitor.CheckAsync(root, _patcherCts.Token, force: true, acknowledge: true);
                if (check is not null)
                {
                    ShowPatcherMonitorResult(check);
                    PatcherStatusMessage = result.Summary;
                }
            }
        }
        catch (OperationCanceledException)
        {
            PatcherStatusMessage = "Операция остановлена. Уже применённые изменения сохраняются и доступны для отката.";
            if (!_isShuttingDown)
                try { ApplyPatcherResult(await AntigravityPatcherService.GetStatusAsync(root: root)); }
                catch { ApplyPatcherResult(UncheckedPatcherStatus()); _patcherForceMonitorRefresh = true; }
        }
        catch (Exception ex)
        {
            ApplyPatcherResult(UncheckedPatcherStatus());
            _patcherForceMonitorRefresh = true;
            PatcherStatusMessage = "Ошибка: " + ex.Message;
            AppendPatcherLog(PatcherStatusMessage);
        }
        finally { _patcherCts.Dispose(); _patcherCts = null; IsPatcherBusy = false; }
    }

    internal void ApplyPatcherResult(AntigravityStatusResult result)
    {
        _patcherSelectionRequired = result.SelectionRequired;
        PatcherLs = result.LanguageServer; PatcherIde = result.Ide; PatcherVsCode = result.VsCode; PatcherAgy = result.Agy;
        OnPropertyChanged(nameof(PatcherComponents));
        PatcherLauncherPath = result.SelectionRequired ? null : ResolvePatcherLauncher(PatcherInstallRoot, PatcherComponents);
        PatcherCheckedAt = DateTimeOffset.TryParse(result.Timestamp, out var time) ? "Проверено: " + time.ToLocalTime().ToString("HH:mm:ss") : "";
        OnPropertyChanged(nameof(PatcherLauncherPath));
        OnPropertyChanged(nameof(PatcherLauncherDisplay));
        OnPropertyChanged(nameof(PatcherCheckedAt));
        OnPropertyChanged(nameof(PatcherActionSummary));
        OnPropertyChanged(nameof(HasPatcherActionNeeded));
        OnPropertyChanged(nameof(HasAnyPatchedComponent));
        OnPropertyChanged(nameof(ShowPatcherBadge));
        OnPropertyChanged(nameof(PatcherBadgeColor));
        OnPropertyChanged(nameof(PatcherBadgeToolTip));
        if (!string.IsNullOrWhiteSpace(result.Summary)) PatcherStatusMessage = result.Summary;
        RefreshPatcherCommandStates();
        foreach (var log in result.Logs) AppendPatcherLog(log);
    }
    private void AppendPatcherLog(string line)
    {
        string updated = PatcherLog + (PatcherLog.Length == 0 ? "" : Environment.NewLine) + $"[{DateTime.Now:HH:mm:ss}] {line}";
        PatcherLog = updated.Length <= 64000 ? updated : updated[^60000..];
    }
}
