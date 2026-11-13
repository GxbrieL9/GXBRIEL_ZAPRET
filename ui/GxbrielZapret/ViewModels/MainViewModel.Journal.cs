using GxbrielZapret.Localization;

namespace GxbrielZapret.ViewModels;

public sealed partial class MainViewModel
{
    private string _logSearchText = "";
    public string LogSearchText
    {
        get => _logSearchText;
        set
        {
            if (!SetField(ref _logSearchText, value ?? "")) return;
            LogView.Refresh();
            OnPropertyChanged(nameof(HasVisibleLogLines));
            OnPropertyChanged(nameof(EmptyJournalText));
        }
    }

    public bool FollowLog
    {
        get => Settings.FollowLog;
        set
        {
            if (Settings.FollowLog == value) return;
            Settings.FollowLog = value;
            _settingsSvc.Save();
            OnPropertyChanged();
        }
    }

    public bool HasVisibleLogLines => !LogView.IsEmpty;
    public string EmptyJournalText => string.IsNullOrWhiteSpace(LogSearchText)
        ? Loc.T("В журнале пока нет событий.") : Loc.T("В журнале нет строк по этому запросу.");

    private bool LogMatches(object item) => item is string line &&
        (string.IsNullOrWhiteSpace(LogSearchText) || line.Contains(LogSearchText.Trim(), StringComparison.OrdinalIgnoreCase));
}
