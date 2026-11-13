using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Mvvm;
using GxbrielZapret.Services.Strategies;

namespace GxbrielZapret.ViewModels;

/// <summary>
/// Where the user is: simple or advanced, which tab, and the first-run walkthrough.
/// </summary>
public sealed partial class MainViewModel
{
    // ---- simple / advanced mode -------------------------------------------

    public bool IsSimpleMode
    {
        get => Settings.SimpleMode;
        set
        {
            if (Settings.SimpleMode == value) return;
            Settings.SimpleMode = value;
            _settingsSvc.Save();
            if (value && (SelectedTabIndex == 2 || SelectedTabIndex == 4))
                SelectedTabIndex = 0;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAdvancedMode));
            HomeToggleCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAdvancedMode => !IsSimpleMode;

    // ---- top-tab navigation (redesign) ------------------------------------
    // Bound to the TabControl; lets the Home gear jump to Настройки and lets
    // Simple mode lock the view to Главная (the tab strip is hidden there).
    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (!SetField(ref _selectedTabIndex, value)) return;

        }
    }

    // Tab order: Главная (0), Стратегии (1), Хостлисты (2), Диагностика (3), Журнал (4), Патчер (5), Настройки (6).
    // These indexes and the TabItem order in MainWindow.xaml must agree: inserting a tab means bumping
    // every index after it, or the Home shortcuts land on the wrong page.

    /// <summary>Index of the Патчер tab.</summary>
    public const int PatcherTabIndex = 5;

    /// <summary>Index of the Настройки tab.</summary>
    internal const int SettingsTabIndex = 6;

    /// <summary>Index of the Стратегии tab.</summary>
    internal const int StrategiesTabIndex = 1;

    /// <summary>The preset the Simple-mode one-click button applies (combined Discord+YouTube).</summary>
    public Preset? RecommendedPreset =>
        Presets.FirstOrDefault(p => p.IsWorkFor(ActiveProbeTargets) && p.IsRecommended)
        ?? Presets.FirstOrDefault(p => p.IsWorkFor(ActiveProbeTargets))
        ?? Presets.FirstOrDefault(p => p.IsRecommended)
        ?? Presets.FirstOrDefault();

    private string _simpleStatus = "";
    public string SimpleStatus
    {
        get => _simpleStatus;
        private set
        {
            if (SetField(ref _simpleStatus, value))
                OnPropertyChanged(nameof(HeroSubtitleText));
        }
    }

    private void SimpleToggle()
    {
        if (IsRunning) { _ = StopEngineSmoothAsync(); SimpleStatus = ""; return; }

        var preset = RecommendedPreset;
        if (preset is null) { SimpleStatus = Loc.T("Движок ещё не установлен — дождитесь загрузки."); return; }
        SelectedPreset = preset;
        SimpleStatus = Loc.T("Стратегия: «{0}»", Loc.T(preset.Name));
        Start();
    }

    // ---- actionable error modal -------------------------------------------

    private bool _showErrorDialog;
    public bool ShowErrorDialog { get => _showErrorDialog; set => SetField(ref _showErrorDialog, value); }

    private string _errorDialogTitle = "";
    public string ErrorDialogTitle { get => _errorDialogTitle; set => SetField(ref _errorDialogTitle, value); }

    private string _errorDialogMessage = "";
    public string ErrorDialogMessage { get => _errorDialogMessage; set => SetField(ref _errorDialogMessage, value); }

    private string _errorDialogDetails = "";
    public string ErrorDialogDetails
    {
        get => _errorDialogDetails;
        set
        {
            if (SetField(ref _errorDialogDetails, value))
                OnPropertyChanged(nameof(HasErrorDialogDetails));
        }
    }

    public bool HasErrorDialogDetails => !string.IsNullOrWhiteSpace(_errorDialogDetails);

    private bool _showErrorDetails;
    public bool ShowErrorDetails { get => _showErrorDetails; set => SetField(ref _showErrorDetails, value); }

    private string _errorDialogActionText = "";
    public string ErrorDialogActionText
    {
        get => _errorDialogActionText;
        set
        {
            if (SetField(ref _errorDialogActionText, value))
                OnPropertyChanged(nameof(HasErrorDialogAction));
        }
    }

    public bool HasErrorDialogAction => !string.IsNullOrWhiteSpace(_errorDialogActionText);

    private Action? _errorDialogAction;
    public Action? ErrorDialogAction { get => _errorDialogAction; set => _errorDialogAction = value; }

    public RelayCommand CloseErrorDialogCommand => CloseErrorModalCommand;
    public RelayCommand ErrorDialogActionCommand => ErrorModalActionCommand;
    public RelayCommand ToggleErrorDetailsCommand => new(_ => ShowErrorDetails = !ShowErrorDetails);

    public void ShowErrorModal(string title, string message, string? details = null, string? actionText = null, Action? action = null)
    {
        OnUi(() =>
        {
            ErrorDialogTitle = title;
            ErrorDialogMessage = message;
            ErrorDialogDetails = details ?? "";
            ShowErrorDetails = false;
            ErrorDialogActionText = actionText ?? "";
            if (action is not null) _errorDialogAction = action;
            ShowErrorDialog = true;
        });
    }

    private void ExecuteErrorModalAction()
    {
        ShowErrorDialog = false;
        _errorDialogAction?.Invoke();
    }

    // ---- first-run walkthrough --------------------------------------------

    private bool _showHowItWorks;
    /// <summary>Whether the "how it works / app tour" instruction modal is shown.</summary>
    public bool ShowHowItWorks { get => _showHowItWorks; set => SetField(ref _showHowItWorks, value); }

    /// <summary>How long the confirm button is held on the very first launch, so the steps get read
    /// instead of the modal being dismissed reflexively.</summary>
    private const int WelcomeDelaySeconds = 6;

    private bool _showWelcome;
    /// <summary>Whether the first-run walkthrough is shown. Opened once on a fresh install (see
    /// <see cref="MarkWelcomeSeen"/>) and on demand from Настройки → «Показать вводную».</summary>
    public bool ShowWelcome { get => _showWelcome; set => SetField(ref _showWelcome, value); }

    private int _welcomeCountdown;
    /// <summary>Seconds left before the walkthrough can be dismissed; 0 = dismissible right away.</summary>
    public int WelcomeCountdown
    {
        get => _welcomeCountdown;
        private set
        {
            if (!SetField(ref _welcomeCountdown, value)) return;
            OnPropertyChanged(nameof(WelcomeButtonText));
            CloseWelcomeCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Confirm-button caption, carrying the countdown while it runs.</summary>
    public string WelcomeButtonText =>
        WelcomeCountdown > 0 ? Loc.T("Понятно, начать ({0})", WelcomeCountdown) : Loc.T("Понятно, начать");

    /// <summary>First-run walkthrough is available via Help/Settings, never blocks startup.</summary>
    public bool NeedsWelcome => false;

    /// <summary>
    /// Show the walkthrough. <paramref name="withCountdown"/> only on the genuine first launch —
    /// someone reopening it from Настройки has already been through it and shouldn't be made to wait.
    /// </summary>
    public void OpenWelcome(bool withCountdown)
    {
        ShowWelcome = true;
        WelcomeCountdown = 0;
        if (withCountdown) _ = RunWelcomeCountdownAsync();
    }

    /// <summary>Tick the confirm button's countdown down to zero (UI thread; stops if closed early).</summary>
    private async Task RunWelcomeCountdownAsync()
    {
        WelcomeCountdown = WelcomeDelaySeconds;
        while (WelcomeCountdown > 0)
        {
            await Task.Delay(1000);
            if (!ShowWelcome) { WelcomeCountdown = 0; return; }
            WelcomeCountdown--;
        }
    }

    /// <summary>Remember that the walkthrough was seen, so it doesn't reopen on every launch.</summary>
    private void MarkWelcomeSeen()
    {
        ShowWelcome = false;
        WelcomeCountdown = 0;
        if (Settings.WelcomeShown) return;
        Settings.WelcomeShown = true;
        _settingsSvc.Save();
    }

    /// <summary>Select a specific tried candidate from the pool.</summary>
    private void ApplyScore(AutoScore? score)
    {
        if (score is null) return;
        var p = Presets.FirstOrDefault(x => x.Name == score.Name) ?? _presets.FindByName(score.Name);
        if (p is not null)
        {
            SelectedPreset = p;
            SetAutoStatus(Loc.T("Выбрана стратегия «{0}».", Loc.T(p.Name)));
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Выбрана стратегия «{0}».", Loc.T(p.Name)));
        }
    }

    /// <summary>Save a candidate as a preset AND start it (or restart with it if already running) —
    /// the one-click "use this one" action from the review popup.</summary>
    private async Task ApplyScoreAndStartAsync(AutoScore? score)
    {
        if (score is null) return;
        ApplyScore(score);
        if (IsRunning) await ApplyStrategyAsync();
        else if (CanStart) await StartAsync();
    }
}
