using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Mvvm;
using GxbrielZapret.Services.Engine;

namespace GxbrielZapret.ViewModels;

public sealed partial class MainViewModel
{
    // =========================================================================
    // Discovery / Live Capture Catalog State
    // =========================================================================

    public ObservableCollection<DiscoveryTarget> DiscoveryTargets { get; } = new();

    private string _discoverySearchText = "";
    public string DiscoverySearchText
    {
        get => _discoverySearchText;
        set
        {
            if (SetField(ref _discoverySearchText, value))
            {
                RefreshDiscoveryViews();
            }
        }
    }

    private bool _showDiscoveryCatalog;
    public bool ShowDiscoveryCatalog
    {
        get => _showDiscoveryCatalog;
        set => SetField(ref _showDiscoveryCatalog, value);
    }

    private bool _showAddTargetChooser;
    public bool ShowAddTargetChooser
    {
        get => _showAddTargetChooser;
        set => SetField(ref _showAddTargetChooser, value);
    }

    private bool _showProcessPicker;
    public bool ShowProcessPicker
    {
        get => _showProcessPicker;
        set => SetField(ref _showProcessPicker, value);
    }

    private bool _showTargetCandidates;
    public bool ShowTargetCandidates
    {
        get => _showTargetCandidates;
        set => SetField(ref _showTargetCandidates, value);
    }

    private DiscoveryTarget? _selectedTargetForCandidates;
    public DiscoveryTarget? SelectedTargetForCandidates
    {
        get => _selectedTargetForCandidates;
        set => SetField(ref _selectedTargetForCandidates, value);
    }

    public ObservableCollection<DiscoveredItem> TargetCandidatesView { get; } = new();

    // Process Picker Dialog State
    public ObservableCollection<ProcessSnapshotItem> ActiveProcesses { get; } = new();

    private ProcessSnapshotItem? _selectedProcessItem;
    public ProcessSnapshotItem? SelectedProcessItem
    {
        get => _selectedProcessItem;
        set
        {
            if (SetField(ref _selectedProcessItem, value))
            {
                ConfirmAddProcessTargetCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private string _processSearchText = "";
    public string ProcessSearchText
    {
        get => _processSearchText;
        set
        {
            if (SetField(ref _processSearchText, value))
            {
                RefreshProcessView();
            }
        }
    }

    private bool _showSystemProcesses;
    public bool ShowSystemProcesses
    {
        get => _showSystemProcesses;
        set
        {
            if (SetField(ref _showSystemProcesses, value))
            {
                _ = LoadActiveProcessesAsync();
            }
        }
    }

    private bool _isLoadingProcesses;
    public bool IsLoadingProcesses
    {
        get => _isLoadingProcesses;
        set => SetField(ref _isLoadingProcesses, value);
    }

    // Filtered lists for UI catalog categories
    public ObservableCollection<DiscoveryTarget> DiscoveryGamesTargets { get; } = new();
    public ObservableCollection<DiscoveryTarget> DiscoveryProgramsTargets { get; } = new();
    public ObservableCollection<DiscoveryTarget> DiscoveryWebServicesTargets { get; } = new();
    public ObservableCollection<DiscoveryTarget> DiscoveryCustomTargets { get; } = new();

    // Add Domain Target Modal State
    private bool _showAddDomainTargetModal;
    public bool ShowAddDomainTargetModal
    {
        get => _showAddDomainTargetModal;
        set => SetField(ref _showAddDomainTargetModal, value);
    }

    private string _newDomainInputText = "";
    public string NewDomainInputText
    {
        get => _newDomainInputText;
        set
        {
            if (SetField(ref _newDomainInputText, value))
            {
                UpdateParsedDomainPreview();
            }
        }
    }

    private string _newDomainDisplayName = "";
    public string NewDomainDisplayName
    {
        get => _newDomainDisplayName;
        set => SetField(ref _newDomainDisplayName, value);
    }

    private string _parsedDomainPreview = "";
    public string ParsedDomainPreview
    {
        get => _parsedDomainPreview;
        set => SetField(ref _parsedDomainPreview, value);
    }

    private void UpdateParsedDomainPreview()
    {
        string raw = (_newDomainInputText ?? "").Trim();
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) raw = raw[8..];
        else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) raw = raw[7..];

        int slash = raw.IndexOfAny(new[] { '/', '?', '#', ':' });
        if (slash >= 0) raw = raw[..slash];

        string domain = HostlistService.CleanDomain(raw);
        ParsedDomainPreview = !string.IsNullOrEmpty(domain) ? domain : "";

        if (string.IsNullOrWhiteSpace(_newDomainDisplayName) && !string.IsNullOrEmpty(domain))
        {
            NewDomainDisplayName = char.ToUpperInvariant(domain[0]) + domain[1..];
        }
    }

    // Website Learning Session State
    private bool _showWebsiteLearningModal;
    public bool ShowWebsiteLearningModal
    {
        get => _showWebsiteLearningModal;
        set => SetField(ref _showWebsiteLearningModal, value);
    }

    private DiscoveryTarget? _learningTarget;
    public DiscoveryTarget? LearningTarget
    {
        get => _learningTarget;
        set => SetField(ref _learningTarget, value);
    }

    private bool _isLearningSessionActive;
    public bool IsLearningSessionActive
    {
        get => _isLearningSessionActive;
        set => SetField(ref _isLearningSessionActive, value);
    }

    private int _learningElapsedSeconds;
    public int LearningElapsedSeconds
    {
        get => _learningElapsedSeconds;
        set => SetField(ref _learningElapsedSeconds, value);
    }

    private int _learningDomainsCount;
    public int LearningDomainsCount
    {
        get => _learningDomainsCount;
        set => SetField(ref _learningDomainsCount, value);
    }

    private int _learningIpsCount;
    public int LearningIpsCount
    {
        get => _learningIpsCount;
        set => SetField(ref _learningIpsCount, value);
    }

    private int _learningActiveConnections;
    public int LearningActiveConnections
    {
        get => _learningActiveConnections;
        set => SetField(ref _learningActiveConnections, value);
    }

    private string _learningLastHost = "";
    public string LearningLastHost
    {
        get => _learningLastHost;
        set => SetField(ref _learningLastHost, value);
    }

    private string _learningStatusText = "";
    public string LearningStatusText
    {
        get => _learningStatusText;
        set => SetField(ref _learningStatusText, value);
    }

    // Learning Results Modal State
    private bool _showLearningResultsModal;
    public bool ShowLearningResultsModal
    {
        get => _showLearningResultsModal;
        set => SetField(ref _showLearningResultsModal, value);
    }

    private DiscoveryTarget? _learningResultTarget;
    public DiscoveryTarget? LearningResultTarget
    {
        get => _learningResultTarget;
        set => SetField(ref _learningResultTarget, value);
    }

    private string _learningResultSummary = "";
    public string LearningResultSummary
    {
        get => _learningResultSummary;
        set => SetField(ref _learningResultSummary, value);
    }

    public ObservableCollection<DiscoveredItem> LearningRecommendedCandidates { get; } = new();
    public ObservableCollection<DiscoveredItem> LearningPossibleCandidates { get; } = new();
    public ObservableCollection<DiscoveredItem> LearningExcludedCandidates { get; } = new();

    // =========================================================================
    // Summary & Preview Properties for Settings Page
    // =========================================================================

    public int EnabledTargetsCount => DiscoveryTargets.Count(t => t.Enabled);

    public string DiscoverySelectedSummary
    {
        get
        {
            int count = EnabledTargetsCount;
            string word = count switch
            {
                1 => Loc.T("приложение"),
                2 or 3 or 4 => Loc.T("приложения"),
                _ => Loc.T("приложений")
            };
            return Loc.T("Выбрано: {0} {1}", count, word);
        }
    }

    public List<DiscoveryTarget> ActiveDiscoveryTargetsPreview =>
        DiscoveryTargets.Where(t => t.Enabled).Take(4).ToList();

    public bool HasMoreActiveTargets =>
        EnabledTargetsCount > 4;

    public string MoreActiveTargetsText =>
        Loc.T("и ещё {0}", EnabledTargetsCount - 4);

    // =========================================================================
    // Commands
    // =========================================================================

    public RelayCommand OpenDiscoveryCatalogCommand { get; private set; } = null!;
    public RelayCommand CloseDiscoveryCatalogCommand { get; private set; } = null!;
    public RelayCommand OpenAddCustomAppCommand { get; private set; } = null!;
    public RelayCommand CloseAddTargetChooserCommand { get; private set; } = null!;
    public RelayCommand OpenProcessPickerCommand { get; private set; } = null!;
    public RelayCommand CloseProcessPickerCommand { get; private set; } = null!;
    public RelayCommand ChooseRunningProcessCommand { get; private set; } = null!;
    public RelayCommand ChooseExeFileCommand { get; private set; } = null!;
    public RelayCommand ChooseWebDomainCommand { get; private set; } = null!;
    public RelayCommand OpenAddDomainTargetCommand { get; private set; } = null!;
    public RelayCommand CloseAddDomainTargetCommand { get; private set; } = null!;
    public RelayCommand ConfirmAddDomainTargetCommand { get; private set; } = null!;
    public RelayCommand ConfirmAddProcessTargetCommand { get; private set; } = null!;
    public RelayCommand ToggleDiscoveryTargetCommand { get; private set; } = null!;
    public RelayCommand DeleteCustomTargetCommand { get; private set; } = null!;
    public RelayCommand RefreshRunningStatusCommand { get; private set; } = null!;
    public RelayCommand OpenTargetCandidatesCommand { get; private set; } = null!;
    public RelayCommand CloseTargetCandidatesCommand { get; private set; } = null!;
    public RelayCommand ExportTargetCandidatesCommand { get; private set; } = null!;
    public RelayCommand ClearTargetCandidatesCommand { get; private set; } = null!;
    public RelayCommand SelectAllCandidatesCommand { get; private set; } = null!;
    public RelayCommand DeselectAllCandidatesCommand { get; private set; } = null!;

    // Website Learning Commands
    public RelayCommand OpenWebsiteLearningCommand { get; private set; } = null!;
    public RelayCommand StartWebsiteLearningCommand { get; private set; } = null!;
    public RelayCommand StopWebsiteLearningCommand { get; private set; } = null!;
    public RelayCommand CloseWebsiteLearningModalCommand { get; private set; } = null!;
    public RelayCommand ApplyLearningRecommendedCommand { get; private set; } = null!;
    public RelayCommand ApplyAllLearningCandidatesCommand { get; private set; } = null!;
    public RelayCommand CloseLearningResultsModalCommand { get; private set; } = null!;

    // =========================================================================
    // Initialization & Logic
    // =========================================================================

    private void InitializeDiscovery()
    {
        OpenDiscoveryCatalogCommand = new RelayCommand(_ =>
        {
            RefreshDiscoveryTargetsRunningStatus();
            ShowDiscoveryCatalog = true;
        });

        CloseDiscoveryCatalogCommand = new RelayCommand(_ =>
        {
            ShowDiscoveryCatalog = false;
            _discovery.Save();
            SyncDiscoveryToSettings();
        });

        OpenAddCustomAppCommand = new RelayCommand(_ =>
        {
            ShowAddTargetChooser = true;
        });

        CloseAddTargetChooserCommand = new RelayCommand(_ =>
        {
            ShowAddTargetChooser = false;
        });

        OpenProcessPickerCommand = new RelayCommand(async _ =>
        {
            ShowAddTargetChooser = false;
            ShowProcessPicker = true;
            await LoadActiveProcessesAsync();
        });

        CloseProcessPickerCommand = new RelayCommand(_ =>
        {
            ShowProcessPicker = false;
        });

        ChooseRunningProcessCommand = OpenProcessPickerCommand;

        ChooseExeFileCommand = new RelayCommand(_ =>
        {
            ShowAddTargetChooser = false;
            PickCustomExe();
        });

        ChooseWebDomainCommand = new RelayCommand(_ =>
        {
            ShowAddTargetChooser = false;
            NewDomainInputText = "";
            NewDomainDisplayName = "";
            ParsedDomainPreview = "";
            ShowAddDomainTargetModal = true;
        });

        OpenAddDomainTargetCommand = ChooseWebDomainCommand;

        CloseAddDomainTargetCommand = new RelayCommand(_ =>
        {
            ShowAddDomainTargetModal = false;
        });

        ConfirmAddDomainTargetCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(ParsedDomainPreview) && string.IsNullOrWhiteSpace(NewDomainInputText))
                return;

            string domainInput = !string.IsNullOrWhiteSpace(ParsedDomainPreview) ? ParsedDomainPreview : NewDomainInputText;
            var target = _discovery.AddCustomDomainTarget(domainInput, NewDomainDisplayName);
            ReloadDiscoveryTargets();
            ShowAddDomainTargetModal = false;
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Добавлен веб-сервис: {0}", target.DisplayName));
        });

        ConfirmAddProcessTargetCommand = new RelayCommand(_ =>
        {
            if (SelectedProcessItem is null) return;
            var target = _discovery.AddCustomTargetFromProcess(SelectedProcessItem);
            ReloadDiscoveryTargets();
            ShowProcessPicker = false;
            Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Добавлено приложение: {0}", target.DisplayName));
        }, _ => SelectedProcessItem is not null);

        ToggleDiscoveryTargetCommand = new RelayCommand(p =>
        {
            if (p is DiscoveryTarget target)
            {
                target.Enabled = !target.Enabled;
                _discovery.UpdateTarget(target);
                OnPropertyChanged(nameof(EnabledTargetsCount));
                OnPropertyChanged(nameof(DiscoverySelectedSummary));
                OnPropertyChanged(nameof(ActiveDiscoveryTargetsPreview));
                OnPropertyChanged(nameof(HasMoreActiveTargets));
                OnPropertyChanged(nameof(MoreActiveTargetsText));
                OnPropertyChanged(nameof(ParserScopeStatus));
                SyncDiscoveryToSettings();
            }
        });

        DeleteCustomTargetCommand = new RelayCommand(p =>
        {
            if (p is DiscoveryTarget target && !target.IsBuiltIn)
            {
                _discovery.RemoveTarget(target.Id);
                ReloadDiscoveryTargets();
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Цель {0} удалена из захвата.", target.DisplayName));
            }
        });

        RefreshRunningStatusCommand = new RelayCommand(_ => RefreshDiscoveryTargetsRunningStatus());

        OpenTargetCandidatesCommand = new RelayCommand(p =>
        {
            if (p is DiscoveryTarget target)
            {
                SelectedTargetForCandidates = target;
                TargetCandidatesView.Clear();
                foreach (var item in target.DiscoveredCandidates)
                {
                    TargetCandidatesView.Add(item);
                }
                ShowTargetCandidates = true;
            }
        });

        CloseTargetCandidatesCommand = new RelayCommand(_ =>
        {
            ShowTargetCandidates = false;
            SelectedTargetForCandidates = null;
        });

        SelectAllCandidatesCommand = new RelayCommand(_ =>
        {
            foreach (var item in TargetCandidatesView) item.IsSelected = true;
        });

        DeselectAllCandidatesCommand = new RelayCommand(_ =>
        {
            foreach (var item in TargetCandidatesView) item.IsSelected = false;
        });

        ClearTargetCandidatesCommand = new RelayCommand(_ =>
        {
            if (SelectedTargetForCandidates is not null)
            {
                _discovery.ClearCandidates(SelectedTargetForCandidates.Id);
                TargetCandidatesView.Clear();
                ReloadDiscoveryTargets();
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Кандидаты очищены"));
            }
        });

        ExportTargetCandidatesCommand = new RelayCommand(_ =>
        {
            if (SelectedTargetForCandidates is not null)
            {
                _discovery.ExportCandidates(
                    SelectedTargetForCandidates.Id,
                    TargetCandidatesView,
                    out int addedDomains,
                    out int addedIps);

                if ((addedDomains > 0 || addedIps > 0) && _engine.IsRunning)
                {
                    _engine.ReapplyActiveStrategy($"Export {addedDomains} domains, {addedIps} IPs for {SelectedTargetForCandidates.DisplayName}");
                }

                ReloadDiscoveryTargets();
                ShowTargetCandidates = false;
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Экспортировано: {0} доменов, {1} IP", addedDomains, addedIps));
            }
        });

        // Website Learning Commands Implementation
        OpenWebsiteLearningCommand = new RelayCommand(p =>
        {
            if (p is DiscoveryTarget target)
            {
                LearningTarget = target;
                LearningElapsedSeconds = 0;
                LearningDomainsCount = target.TotalDomainsCount;
                LearningIpsCount = 0;
                LearningActiveConnections = 0;
                LearningLastHost = target.SeedDomains.FirstOrDefault() ?? target.DisplayName;
                LearningStatusText = Loc.T("Готов к обучению. Нажмите «Начать обучение».");
                IsLearningSessionActive = false;
                ShowWebsiteLearningModal = true;
            }
        });

        StartWebsiteLearningCommand = new RelayCommand(async _ =>
        {
            if (LearningTarget is null || IsLearningSessionActive) return;

            IsLearningSessionActive = true;
            LearningStatusText = Loc.T("Идёт обучение… Используйте сайт в браузере.");

            try
            {
                var result = await _webLearning.StartLearningSessionAsync(
                    LearningTarget,
                    prog => OnUi(() =>
                    {
                        LearningElapsedSeconds = prog.ElapsedSeconds;
                        LearningDomainsCount = prog.DiscoveredDomainsCount;
                        LearningIpsCount = prog.DiscoveredIpsCount;
                        LearningActiveConnections = prog.ActiveConnectionsCount;
                        LearningLastHost = prog.LastDiscoveredHost;
                        LearningStatusText = Loc.T("Сбор сетевой активности ({0} сек)… Доменов: {1}, IP: {2}",
                            prog.ElapsedSeconds, prog.DiscoveredDomainsCount, prog.DiscoveredIpsCount);
                    }));

                ShowWebsiteLearningModal = false;
                IsLearningSessionActive = false;

                // Populate Results Modal
                LearningResultTarget = result.Target;
                LearningResultSummary = result.SummaryMessage;

                LearningRecommendedCandidates.Clear();
                foreach (var item in result.RecommendedItems) LearningRecommendedCandidates.Add(item);

                LearningPossibleCandidates.Clear();
                foreach (var item in result.PossibleItems) LearningPossibleCandidates.Add(item);

                LearningExcludedCandidates.Clear();
                foreach (var item in result.ExcludedItems) LearningExcludedCandidates.Add(item);

                ShowLearningResultsModal = true;
            }
            catch (Exception ex)
            {
                IsLearningSessionActive = false;
                LearningStatusText = Loc.T("Ошибка обучения: {0}", ex.Message);
            }
        });

        StopWebsiteLearningCommand = new RelayCommand(_ =>
        {
            if (IsLearningSessionActive)
            {
                LearningStatusText = Loc.T("Завершаю и анализирую собранные данные…");
                _webLearning.StopSession();
            }
        });

        CloseWebsiteLearningModalCommand = new RelayCommand(_ =>
        {
            if (IsLearningSessionActive)
            {
                _webLearning.StopSession();
            }
            ShowWebsiteLearningModal = false;
            IsLearningSessionActive = false;
            LearningTarget = null;
        });

        ApplyLearningRecommendedCommand = new RelayCommand(_ =>
        {
            if (LearningResultTarget is not null)
            {
                var selected = LearningRecommendedCandidates.Where(c => c.IsSelected).ToList();
                _discovery.AddAcceptedCandidates(LearningResultTarget.Id, selected);
                _discovery.ExportCandidates(LearningResultTarget.Id, selected, out int doms, out int ips);

                if ((doms > 0 || ips > 0) && _engine.IsRunning)
                {
                    _engine.ReapplyActiveStrategy($"Learning applied for {LearningResultTarget.DisplayName}");
                }

                ReloadDiscoveryTargets();
                ShowLearningResultsModal = false;
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Обучение завершено: добавлено {0} доменов, {1} IP", doms, ips));
            }
        });

        ApplyAllLearningCandidatesCommand = new RelayCommand(_ =>
        {
            if (LearningResultTarget is not null)
            {
                var allSelected = LearningRecommendedCandidates.Concat(LearningPossibleCandidates)
                    .Concat(LearningExcludedCandidates)
                    .Where(c => c.IsSelected).ToList();

                _discovery.AddAcceptedCandidates(LearningResultTarget.Id, allSelected);
                _discovery.ExportCandidates(LearningResultTarget.Id, allSelected, out int doms, out int ips);

                if ((doms > 0 || ips > 0) && _engine.IsRunning)
                {
                    _engine.ReapplyActiveStrategy($"All learning candidates applied for {LearningResultTarget.DisplayName}");
                }

                ReloadDiscoveryTargets();
                ShowLearningResultsModal = false;
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Сохранено {0} доменов, {1} IP", doms, ips));
            }
        });

        CloseLearningResultsModalCommand = new RelayCommand(_ =>
        {
            ShowLearningResultsModal = false;
            LearningResultTarget = null;
        });

        ReloadDiscoveryTargets();
    }

    public void ReloadDiscoveryTargets()
    {
        DiscoveryTargets.Clear();
        foreach (var t in _discovery.Targets)
        {
            DiscoveryTargets.Add(t);
        }
        RefreshDiscoveryTargetsRunningStatus();
        RefreshDiscoveryViews();
        OnPropertyChanged(nameof(EnabledTargetsCount));
        OnPropertyChanged(nameof(DiscoverySelectedSummary));
        OnPropertyChanged(nameof(ActiveDiscoveryTargetsPreview));
        OnPropertyChanged(nameof(HasMoreActiveTargets));
        OnPropertyChanged(nameof(MoreActiveTargetsText));
        OnPropertyChanged(nameof(ParserScopeStatus));
    }

    public void RefreshDiscoveryTargetsRunningStatus()
    {
        _discovery.RefreshRunningStatus();
    }

    private void RefreshDiscoveryViews()
    {
        string filter = (DiscoverySearchText ?? "").Trim();

        DiscoveryGamesTargets.Clear();
        DiscoveryProgramsTargets.Clear();
        DiscoveryWebServicesTargets.Clear();
        DiscoveryCustomTargets.Clear();

        foreach (var t in DiscoveryTargets)
        {
            if (!string.IsNullOrEmpty(filter))
            {
                bool matches = t.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || t.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || t.SeedDomains.Any(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    || t.KnownDomains.Any(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase));
                if (!matches) continue;
            }

            switch (t.Category)
            {
                case TargetCategory.Games:
                    DiscoveryGamesTargets.Add(t);
                    break;
                case TargetCategory.Programs:
                    DiscoveryProgramsTargets.Add(t);
                    break;
                case TargetCategory.WebServices:
                    DiscoveryWebServicesTargets.Add(t);
                    break;
                case TargetCategory.Custom:
                default:
                    DiscoveryCustomTargets.Add(t);
                    break;
            }
        }
    }

    private void SyncDiscoveryToSettings()
    {
        var discord = DiscoveryTargets.FirstOrDefault(t => t.Id == "discord");
        if (discord is not null) Settings.ParseDiscordApps = discord.Enabled;

        var lol = DiscoveryTargets.FirstOrDefault(t => t.Id == "league_of_legends");
        var riot = DiscoveryTargets.FirstOrDefault(t => t.Id == "riot_client");
        var blitz = DiscoveryTargets.FirstOrDefault(t => t.Id == "blitz");
        if (lol is not null || riot is not null || blitz is not null)
        {
            Settings.ParseRiotApps = (lol?.Enabled ?? false) || (riot?.Enabled ?? false) || (blitz?.Enabled ?? false);
        }

        var rockstar = DiscoveryTargets.FirstOrDefault(t => t.Id == "rockstar_games");
        if (rockstar is not null) Settings.ParseRockstarApps = rockstar.Enabled;

        _settingsSvc.Save();
    }

    private void PickCustomExe()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("Выберите исполняемый файл приложения"),
                Filter = Loc.T("Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*"),
                CheckFileExists = true
            };

            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                var target = _discovery.AddCustomTargetFromExe(dlg.FileName);
                ReloadDiscoveryTargets();
                Notify?.Invoke("GXBRIEL_ZAPRET", Loc.T("Добавлено приложение: {0}", target.DisplayName));
            }
        }
        catch (Exception ex)
        {
            ShowErrorModal(
                Loc.T("Не удалось добавить приложение"),
                Loc.T("Произошла ошибка при выборе файла приложения."),
                ex.ToString());
        }
    }

    private List<ProcessSnapshotItem> _cachedRawProcesses = new();

    private async Task LoadActiveProcessesAsync()
    {
        IsLoadingProcesses = true;
        try
        {
            bool showSys = ShowSystemProcesses;
            var list = await Task.Run(() => DiscoveryService.GetActiveProcesses(includeSystem: showSys));
            _cachedRawProcesses = list;
            RefreshProcessView();
        }
        catch (Exception ex)
        {
            ShowErrorModal(
                Loc.T("Ошибка получения процессов"),
                Loc.T("Не удалось получить список запущенных процессов Windows."),
                ex.ToString());
        }
        finally
        {
            IsLoadingProcesses = false;
        }
    }

    private void RefreshProcessView()
    {
        string q = (ProcessSearchText ?? "").Trim();
        ActiveProcesses.Clear();

        foreach (var p in _cachedRawProcesses)
        {
            if (!string.IsNullOrEmpty(q))
            {
                bool matches = p.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || p.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || p.Pid.ToString().Contains(q);
                if (!matches) continue;
            }
            ActiveProcesses.Add(p);
        }

        SelectedProcessItem = ActiveProcesses.FirstOrDefault();
    }
}
