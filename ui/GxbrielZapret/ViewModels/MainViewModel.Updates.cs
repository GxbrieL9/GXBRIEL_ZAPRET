using GxbrielZapret.Localization;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Mvvm;
using System.Windows;

namespace GxbrielZapret.ViewModels;

/// <summary>
/// Engine updates and the app's own update notice — both ask GitHub what the newest release is,
/// and neither ever installs anything without being told to.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _isUpdating;
    public bool IsUpdating
    {
        get => _isUpdating;
        private set
        {
            if (SetField(ref _isUpdating, value))
            {
                OnPropertyChanged(nameof(CanStart));
                RaiseCommandStates();
            }
        }
    }

    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; private set => SetField(ref _updateProgress, value); }

    /// <summary>True only while the engine zip is actually being fetched/installed. The progress bar
    /// binds to THIS, not to <see cref="IsUpdating"/>: a routine "is there a newer engine?" check is
    /// also an update operation, and showing a progress bar (holding its previous value, no less) for
    /// a plain network check made it look like the engine re-downloads on every launch.</summary>
    private bool _isDownloadingEngine;
    public bool IsDownloadingEngine
    {
        get => _isDownloadingEngine;
        private set
        {
            if (SetField(ref _isDownloadingEngine, value))
            {
                OnPropertyChanged(nameof(CanStart));
                RaiseCommandStates();
            }
        }
    }

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => SetField(ref _updateStatus, value); }

    private string _engineVersion = "—";
    public string EngineVersion { get => _engineVersion; private set => SetField(ref _engineVersion, value); }

    // ---- app self-update notification -------------------------------------

    /// <summary>This app's version, shown in the caption bar (e.g. "v0.1.0").</summary>
    public string AppVersion => "v" + UpdaterService.AppVersion;

    private bool _appUpdateAvailable;
    public bool AppUpdateAvailable { get => _appUpdateAvailable; private set => SetField(ref _appUpdateAvailable, value); }

    private string _appUpdateText = "";
    public string AppUpdateText { get => _appUpdateText; private set => SetField(ref _appUpdateText, value); }

    private AppRelease? _appRelease;
    private bool _isAppUpdating;
    public bool IsAppUpdating { get => _isAppUpdating; private set { if (SetField(ref _isAppUpdating, value)) { CheckAppUpdatesCommand?.RaiseCanExecuteChanged(); OpenAppReleaseCommand?.RaiseCanExecuteChanged(); } } }
    private string _appUpdateStatus = "";
    public string AppUpdateStatus { get => _appUpdateStatus; private set => SetField(ref _appUpdateStatus, value); }
    public RelayCommand CheckAppUpdatesCommand { get; private set; } = null!;
    public RelayCommand OpenLicensesCommand { get; } = new(_ =>
    {
        ThirdPartyNotices.Ensure();
        var info = new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = false };
        info.ArgumentList.Add(ThirdPartyNotices.NoticePath);
        System.Diagnostics.Process.Start(info)?.Dispose();
    });

    private async Task CheckAppUpdateAsync()
    {
        if (IsAppUpdating) return;
        IsAppUpdating = true;
        AppUpdateStatus = "Проверяю обновления программы…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            _appRelease = await new AppUpdateService().FetchLatestAsync(timeout.Token);
            AppUpdateAvailable = _appRelease is not null;
            AppUpdateText = _appRelease is null ? "" : $"Обновить до {_appRelease.Manifest.Version}";
            AppUpdateStatus = _appRelease is null ? $"Установлена актуальная версия {UpdaterService.AppVersion}." : "Новая версия доступна. Подпись выпуска проверена.";
        }
        catch (Exception ex) { AppUpdateStatus = "Не удалось проверить обновления: " + ex.Message; }
        finally { IsAppUpdating = false; }
    }

    private async Task InstallAppUpdateAsync()
    {
        if (IsAppUpdating || _appRelease is null) return;
        if (MessageBox.Show($"Установить GXBRIEL_ZAPRET {_appRelease.Manifest.Version}?\n\nПрограмма перезапустится. Настройки сохранятся, предыдущий EXE останется для отката.",
            "Обновление программы", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsAppUpdating = true;
        try
        {
            AppUpdateStatus = "Загружаю проверенное обновление…";
            int lastPercent = -1;
            var progress = new Progress<double>(p => { int percent = (int)p; if (percent != lastPercent) { lastPercent = percent; AppUpdateStatus = $"Загрузка обновления: {percent}%"; } });
            string directory = await new AppUpdateService().PrepareAsync(_appRelease, progress);
            AppUpdateStatus = "Проверяю обновление и готовлю перезапуск…";
            await AppUpdateInstaller.BeginAsync(directory);
            (Application.Current.MainWindow as GxbrielZapret.Views.MainWindow)?.ExitApp();
        }
        catch (Exception ex) { AppUpdateStatus = "Обновление не установлено: " + ex.Message; MessageBox.Show(AppUpdateStatus, "Обновление программы", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { IsAppUpdating = false; }
    }
}
