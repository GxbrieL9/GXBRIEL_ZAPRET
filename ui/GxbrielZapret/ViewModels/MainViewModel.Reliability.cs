using System.Windows;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;

namespace GxbrielZapret.ViewModels;

public sealed partial class MainViewModel
{
    private Preset? _crashedPreset;
    private string? _crashedHostlist;
    public bool HasUnsavedSettings => _settingsSvc.LastSaveError is not null;
    public string? SettingsSaveError => _settingsSvc.LastSaveError;
    public bool IsRecoveringEngine => _unexpectedRecovery.IsRecovering;

    private void OnUnexpectedRecoveryStateChanged()
    {
        OnPropertyChanged(nameof(IsRecoveringEngine));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(HeroButtonText));
        OnPropertyChanged(nameof(HeroSubtitleText));
        OnPropertyChanged(nameof(ActiveStrategySimpleSubtitle));
        OnPropertyChanged(nameof(StrategyCardBadgeText));
        RaiseCommandStates();
    }

    private void OnSettingsSaveStatusChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedSettings));
        OnPropertyChanged(nameof(SettingsSaveError));
        RetrySaveSettingsCommand.RaiseCanExecuteChanged();
    }

    private Task<bool> RecoverCrashedEngineOnUiAsync(CancellationToken ct)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(() => RecoverCrashedEngineAsync(ct)).Task.Unwrap();
        return RecoverCrashedEngineAsync(ct);
    }

    private async Task<bool> RecoverCrashedEngineAsync(CancellationToken ct)
    {
        await _engineTransitionGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_isShuttingDown || !Settings.AutoHeal || !Settings.DesiredBypassEnabled ||
                IsAutoSelecting || IsTestingSelectedPreset) return false;
            if (_engine.IsRunning) return true;
            var preset = _crashedPreset ?? Presets.FirstOrDefault(p => p.Name == Settings.ActivePresetId);
            if (preset is null) return false;

            AppendLog(Loc.T("Движок завершился неожиданно. Восстанавливаю стратегию «{0}»…", Loc.T(preset.Name)));
            State = EngineState.Starting;
            await Task.Run(() => _engine.Start(preset, _crashedHostlist), ct);
            // A manual stop received during a blocking native launch still takes precedence.
            if (_isShuttingDown || !Settings.DesiredBypassEnabled)
                await Task.Run(() => _engine.Stop());
            State = _engine.State;
            if (!_engine.IsRunning) return false;
            RunningPreset = preset;
            _ = _healthMonitor.ProbeAllAsync(CancellationToken.None);
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Движок восстановлен после сбоя."));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppendLog(Loc.T("Ошибка автоматического восстановления: {0}", ex.Message));
            return false;
        }
        finally
        {
            State = _engine.State;
            _engineTransitionGate.Release();
            RaiseCommandStates();
        }
    }
}
