using System.Text.Json.Serialization;
using GxbrielZapret.Mvvm;

namespace GxbrielZapret.Models;

public enum TargetCategory
{
    Games,
    Programs,
    WebServices,
    Custom
}

public enum TargetType
{
    Process,
    Domain
}

public enum CandidateConfidence
{
    High,
    Medium,
    Low
}

public enum DomainSourceType
{
    Known,
    Discovered,
    User
}

/// <summary>
/// Represents an application, game, or web service target for live network discovery.
/// </summary>
public sealed class DiscoveryTarget : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    private TargetType _type = TargetType.Process;
    public TargetType Type
    {
        get => _type;
        set
        {
            if (SetField(ref _type, value))
            {
                OnPropertyChanged(nameof(IsDomainTarget));
                OnPropertyChanged(nameof(IsProcessTarget));
                OnPropertyChanged(nameof(TypeBadgeText));
                OnPropertyChanged(nameof(TypeDescriptionText));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    private TargetCategory _category = TargetCategory.Custom;
    public TargetCategory Category
    {
        get => _category;
        set
        {
            if (SetField(ref _category, value))
            {
                OnPropertyChanged(nameof(CategoryLabel));
            }
        }
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public List<string> Executables { get; set; } = new();
    public List<string> ProcessNamePatterns { get; set; } = new();

    private bool _trackChildProcesses = true;
    public bool TrackChildProcesses
    {
        get => _trackChildProcesses;
        set => SetField(ref _trackChildProcesses, value);
    }

    private string? _executablePath;
    public string? ExecutablePath
    {
        get => _executablePath;
        set
        {
            if (SetField(ref _executablePath, value))
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public List<string> SeedDomains { get; set; } = new();
    public List<string> KnownDomains { get; set; } = new();
    public List<string> DiscoveredDomains { get; set; } = new();
    public List<string> UserDomains { get; set; } = new();
    public List<string> KnownIpRanges { get; set; } = new();

    public bool IsBuiltIn { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ---- Runtime observable UI properties (not serialized) ----

    [JsonIgnore]
    public bool IsDomainTarget => Type == TargetType.Domain;

    [JsonIgnore]
    public bool IsProcessTarget => Type == TargetType.Process;

    [JsonIgnore]
    public string TypeBadgeText => Type == TargetType.Domain ? "Веб-сервис" : "Процесс";

    [JsonIgnore]
    public string TypeDescriptionText => Type == TargetType.Domain ? "Доменное отслеживание" : "Процессное приложение";

    private bool _isRunning;
    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(RunningStatusText));
            }
        }
    }

    private int _runningProcessCount;
    [JsonIgnore]
    public int RunningProcessCount
    {
        get => _runningProcessCount;
        set => SetField(ref _runningProcessCount, value);
    }

    [JsonIgnore]
    public string RunningStatusText
    {
        get
        {
            if (IsDomainTarget)
            {
                int count = TotalDomainsCount;
                string word = count switch
                {
                    1 => "домен",
                    2 or 3 or 4 => "домена",
                    _ => "доменов"
                };
                return $"{count} {word}";
            }

            return IsRunning
                ? (RunningProcessCount > 1 ? $"Запущен · {RunningProcessCount} процесса" : "Запущен")
                : "Не запущен";
        }
    }

    [JsonIgnore]
    public int TotalDomainsCount =>
        KnownDomains.Concat(SeedDomains).Concat(DiscoveredDomains).Concat(UserDomains)
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .Count();

    [JsonIgnore]
    public string SourceBadgeText => IsBuiltIn ? "Встроенный профиль" : "Пользовательский";

    [JsonIgnore]
    public string CategoryLabel => Category switch
    {
        TargetCategory.Games => "Игры",
        TargetCategory.Programs => "Программы",
        TargetCategory.WebServices => "Веб-сервисы",
        _ => "Пользовательские"
    };

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            if (IsDomainTarget)
            {
                var seeds = SeedDomains.Count > 0 ? SeedDomains : KnownDomains;
                if (seeds.Count > 0)
                    return string.Join(", ", seeds);
                return "Отслеживание по доменам";
            }

            if (!string.IsNullOrWhiteSpace(ExecutablePath))
                return ExecutablePath;
            if (Executables.Count > 0)
                return string.Join(", ", Executables);
            if (ProcessNamePatterns.Count > 0)
                return string.Join(", ", ProcessNamePatterns);
            return DisplayName;
        }
    }

    public List<DiscoveredItem> DiscoveredCandidates { get; set; } = new();

    [JsonIgnore]
    public int DiscoveredCount => DiscoveredCandidates.Count;

    [JsonIgnore]
    public bool HasCandidates => DiscoveredCandidates.Count > 0;

    [JsonIgnore]
    public string CandidatesBadgeText => $"+{DiscoveredCandidates.Count}";

    public void NotifyCandidatesChanged()
    {
        OnPropertyChanged(nameof(DiscoveredCount));
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(CandidatesBadgeText));
        OnPropertyChanged(nameof(RunningStatusText));
        OnPropertyChanged(nameof(TotalDomainsCount));
        OnPropertyChanged(nameof(Subtitle));
    }

    public DiscoveryTarget Clone()
    {
        return new DiscoveryTarget
        {
            Id = Id,
            DisplayName = DisplayName,
            Type = Type,
            Category = Category,
            Enabled = Enabled,
            Executables = new List<string>(Executables),
            ProcessNamePatterns = new List<string>(ProcessNamePatterns),
            TrackChildProcesses = TrackChildProcesses,
            ExecutablePath = ExecutablePath,
            SeedDomains = new List<string>(SeedDomains),
            KnownDomains = new List<string>(KnownDomains),
            DiscoveredDomains = new List<string>(DiscoveredDomains),
            UserDomains = new List<string>(UserDomains),
            KnownIpRanges = new List<string>(KnownIpRanges),
            DiscoveredCandidates = new List<DiscoveredItem>(DiscoveredCandidates),
            IsBuiltIn = IsBuiltIn,
            CreatedAt = CreatedAt
        };
    }
}

/// <summary>
/// A domain or IP address captured from a live target or website learning session,
/// ready for user review, confidence assessment, and export.
/// </summary>
public sealed class DiscoveredItem : ObservableObject
{
    public string Value { get; set; } = "";
    public bool IsIp { get; set; }
    public int RemotePort { get; set; }
    public string ProcessName { get; set; } = "";
    public int HitCount { get; set; } = 1;
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    private CandidateConfidence _confidence = CandidateConfidence.High;
    public CandidateConfidence Confidence
    {
        get => _confidence;
        set
        {
            if (SetField(ref _confidence, value))
            {
                OnPropertyChanged(nameof(ConfidenceLabel));
                OnPropertyChanged(nameof(ConfidenceBadgeText));
            }
        }
    }

    public string Reason { get; set; } = "";
    public DomainSourceType SourceType { get; set; } = DomainSourceType.Discovered;

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    [JsonIgnore]
    public string TypeBadge => IsIp ? "IP" : "HOST";

    [JsonIgnore]
    public string ConfidenceLabel => Confidence switch
    {
        CandidateConfidence.High => "Высокая",
        CandidateConfidence.Medium => "Средняя",
        _ => "Низкая"
    };

    [JsonIgnore]
    public string ConfidenceBadgeText => Confidence switch
    {
        CandidateConfidence.High => "РЕКОМЕНДУЕТСЯ",
        CandidateConfidence.Medium => "ВОЗМОЖНО",
        _ => "ИСКЛЮЧЕНО"
    };

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Reason))
                return $"{Reason} • обращений: {HitCount}";
            if (!string.IsNullOrWhiteSpace(ProcessName) && ProcessName != "web_discovery")
                return $"{ProcessName} • порт {RemotePort} • обращений: {HitCount}";
            return $"порт {RemotePort} • обращений: {HitCount}";
        }
    }
}

/// <summary>
/// Snapshot of an active OS process for the process picker dialog.
/// </summary>
public sealed class ProcessSnapshotItem
{
    public int Pid { get; init; }
    public required string ProcessName { get; init; }
    public string WindowTitle { get; init; } = "";
    public string Description { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public bool IsSystem { get; init; }

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Description)
        ? Description
        : (!string.IsNullOrWhiteSpace(WindowTitle) ? WindowTitle : ProcessName);

    public string Subtitle => $"PID {Pid} • {ProcessName}.exe";
}
