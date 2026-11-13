using System.Windows.Threading;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.ViewModels;

public sealed partial class MainViewModel
{
    private readonly AntigravityUpdateMonitor _patcherUpdateMonitor = new();
    private readonly CancellationTokenSource _patcherMonitorCts = new();
    private DispatcherTimer? _patcherMonitorTimer;
    private bool _isPatcherMonitorChecking;
    private bool _patcherForceMonitorRefresh;
    private string _patcherUpdateMessage = "";
    public string PatcherUpdateMessage
    {
        get => _patcherUpdateMessage;
        private set
        {
            if (SetField(ref _patcherUpdateMessage, value))
            {
                OnPropertyChanged(nameof(HasPatcherUpdateMessage));
                OnPropertyChanged(nameof(ShowPatcherBadge));
                OnPropertyChanged(nameof(PatcherBadgeColor));
                OnPropertyChanged(nameof(PatcherBadgeToolTip));
            }
        }
    }
    public bool HasPatcherUpdateMessage => !string.IsNullOrWhiteSpace(PatcherUpdateMessage);
    public bool PatcherAutoPatchEnabled
    {
        get => Settings.AntigravityAutoPatchEnabled;
        set
        {
            if (value == Settings.AntigravityAutoPatchEnabled) return;
            Settings.AntigravityAutoPatchEnabled = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            _patcherForceMonitorRefresh = true;
        }
    }
    public bool PatcherMonitorEnabled
    {
        get => Settings.AntigravityMonitorEnabled;
        set
        {
            if (value == Settings.AntigravityMonitorEnabled) return;
            Settings.AntigravityMonitorEnabled = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            if (value)
            {
                _patcherForceMonitorRefresh = true;
                if (_patcherMonitorTimer is not null) _ = CheckPatcherMonitorAsync();
            }
        }
    }

    private void StartPatcherMonitoring()
    {
        if (_patcherMonitorTimer is not null || _isShuttingDown) return;
        _patcherMonitorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _patcherMonitorTimer.Tick += async (_, _) => await CheckPatcherMonitorAsync();
        _patcherMonitorTimer.Start();
    }

    private async Task CheckPatcherMonitorAsync()
    {
        if (!PatcherMonitorEnabled || !CanRunPatcher) return;
        _isPatcherMonitorChecking = true;
        RefreshPatcherCommandStates();
        string? root = string.IsNullOrWhiteSpace(PatcherInstallRoot) ? null : PatcherInstallRoot;
        try
        {
            var result = await _patcherUpdateMonitor.CheckAsync(root, _patcherMonitorCts.Token, force: _patcherForceMonitorRefresh,
                autoPatch: PatcherAutoPatchEnabled);
            if (!_isShuttingDown && PatcherMonitorEnabled && result is not null)
            {
                _patcherForceMonitorRefresh = false;
                ShowPatcherMonitorResult(result);
            }
        }
        catch (OperationCanceledException) when (_patcherMonitorCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_isShuttingDown)
            {
                ApplyPatcherResult(UncheckedPatcherStatus());
                string message = "Наблюдение Antigravity: " + ex.Message + " Проверка будет повторена.";
                if (PatcherUpdateMessage != message) AppendPatcherLog(message);
                PatcherUpdateMessage = message;
            }
        }
        finally { _isPatcherMonitorChecking = false; RefreshPatcherCommandStates(); }
    }

    internal void ShowPatcherMonitorResult(AntigravityMonitorResult result)
    {
        if (_isShuttingDown) return;
        if (result.Status is not null) ApplyPatcherResult(result.Status);
        else if (result.Pending)
        {
            ApplyPatcherResult(UncheckedPatcherStatus());
            PatcherStatusMessage = result.Message;
        }
        if (result.Message != PatcherUpdateMessage && result.Message.Length > 0) AppendPatcherLog(result.Message);
        PatcherUpdateMessage = result.Message;
        if (result.Notify)
        {
            AppendLog("[Antigravity] " + result.Message);
            Notify?.Invoke("GXBRIEL_ZAPRET · Antigravity", result.Message);
        }
    }

    private void RefreshPatcherCommandStates()
    {
        OnPropertyChanged(nameof(CanRunPatcher));
        OnPropertyChanged(nameof(PatcherLaunchReason));
        OnPropertyChanged(nameof(PatcherPatchAllReason));
        OnPropertyChanged(nameof(PatcherRestoreAllReason));
        foreach (var command in PatcherCommands()) command?.RaiseCanExecuteChanged();
    }
}
