using GxbrielZapret.Localization;

namespace GxbrielZapret.Models;

/// <summary>
/// A self-contained winws2 strategy. <see cref="Args"/> holds the command-line
/// arguments after the mandatory lua-init of the bundled libraries.
///
/// Tokens expanded by <c>EngineService</c> at launch time:
///   {FILES}    -> absolute path to the engine "files" folder (fake blobs)
///   {WF}       -> absolute path to the engine "windivert.filter" folder (raw-part filters)
///   {HOSTLIST} -> "--hostlist=&lt;path&gt;" for the active list, or removed if none
/// </summary>
public sealed class Preset : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
    }

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Short, human-friendly one-liner shown as the PRIMARY text of the Strategies list row,
    /// so the row reads friendly instead of leading with a cryptic technical name (e.g. "Если ALT10 не
    /// помог"). Built-ins set it; empty for user/generated presets (they front with their own name).</summary>
    public string Tagline { get; set; } = "";

    /// <summary>winws2 arguments (capture filters, --filter-*, --lua-desync=, --new, …).</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>True if this preset honours the selected hostlist via the {HOSTLIST} token.</summary>
    public bool UsesHostlist { get; set; }

    /// <summary>Built-in presets cannot be deleted (only duplicated/edited into a copy).</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>The single preset the Simple mode applies with its one-click button.</summary>
    public bool IsRecommended { get; set; }

    private bool _isFavorite;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFavorite
    {
        get => _isFavorite;
        internal set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True if assembled by the strategy generator (personalised). Marked ✨ in the list.</summary>
    public bool IsGenerated { get; set; }

    /// <summary>True for the auto-saved "top-3 of the last generation" presets — replaced (actualised)
    /// on every generation run so their scores stay current. Marked ★ and grouped separately.</summary>
    public bool IsAutoLeaderboard { get; set; }

    private string _status = "UNTESTED";
    /// <summary>Canonical classification: WORK, BROKEN, UNTESTED, TESTING.</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(IsWork));
                OnPropertyChanged(nameof(IsUntested));
                OnPropertyChanged(nameof(IsInconclusive));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsTesting));
                OnPropertyChanged(nameof(TestStateLabel));
                OnPropertyChanged(nameof(PoolStatusShort));
                OnPropertyChanged(nameof(PoolStatusHint));
                OnPropertyChanged(nameof(IsVerifiedForDisplayScope));
                OnPropertyChanged(nameof(ScopeStatusLabel));
            }
        }
    }

    private string _reason = "unproven";
    /// <summary>Reason for current status (e.g. unproven, verified_5_of_5, etc.).</summary>
    public string Reason
    {
        get => _reason;
        set
        {
            if (_reason != value)
            {
                _reason = value;
                OnPropertyChanged(nameof(Reason));
                OnPropertyChanged(nameof(IsWork));
                OnPropertyChanged(nameof(IsUntested));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsTesting));
                OnPropertyChanged(nameof(TestStateLabel));
                OnPropertyChanged(nameof(PoolStatusShort));
                OnPropertyChanged(nameof(PoolStatusHint));
                OnPropertyChanged(nameof(IsVerifiedForDisplayScope));
                OnPropertyChanged(nameof(ScopeStatusLabel));
            }
        }
    }

    /// <summary>Importer compatibility verdict: native_zapret2, legacy_maybe, unsupported_tokens, or empty.</summary>
    public string Compatibility { get; set; } = "";

    /// <summary>Human-readable compatibility notes from the importer.</summary>
    public string CompatibilityNotes { get; set; } = "";

    /// <summary>SHA-256 fingerprint of normalized arguments for deduplication.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>Stable Reference Strategy ID (e.g. GX-B01..GX-B09, GX-C01..GX-C30) if part of the 39 reference set.</summary>
    public string? ReferenceId { get; set; }

    /// <summary>Provenance taxonomy tag (e.g. ZAPRET2UI_ADAPTED, GX_CUSTOM, FLOWSEAL_ADAPTED, UPSTREAM_ZAPRET2_ADAPTED, YOUTUBEDISCORD_ADAPTED).</summary>
    public string? Provenance { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BoundNetworkFingerprint { get; internal set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public AutoScope? BoundScope { get; internal set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string VerifiedFlowSummary { get; internal set; } = "";

    /// <summary>Target services description (e.g. Discord web/login/media/voice, YouTube, fallback TLS).</summary>
    public string? TargetServices { get; set; }

    /// <summary>Compatible hostlist names (e.g. discord, youtube, exclude).</summary>
    public List<string> CompatibleLists { get; set; } = new();

    /// <summary>True if this preset belongs to the immutable 39 reference strategies catalog.</summary>
    public bool IsReferenceStrategy => !string.IsNullOrEmpty(ReferenceId);

    /// <summary>Path to the profile manifest file on disk.</summary>
    public string? FilePath { get; set; }

    public DateTime? LastTestedAt { get; set; }
    public Dictionary<string, bool> ProbeResults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int PassCount { get; set; }
    public int TotalCount { get; set; } = 5;
    public long LatencyMs { get; set; }
    public double? VerifiedVideoMbps { get; internal set; }

    public string ProbeScore => TotalCount > 0 ? $"{PassCount}/{TotalCount}" : "—";
    public string LastTestedDisplay => LastTestedAt.HasValue ? LastTestedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : "—";

    internal void NotifyProbeResultsChanged()
    {
        OnPropertyChanged(nameof(ProbeResults));
        OnPropertyChanged(nameof(ProbeScore));
        OnPropertyChanged(nameof(LastTestedDisplay));
        OnPropertyChanged(nameof(TestedTargetsDisplay));
        OnPropertyChanged(nameof(IsVerifiedForDisplayScope));
        OnPropertyChanged(nameof(ScopeStatusLabel));
        OnPropertyChanged(nameof(PoolStatusHint));
    }

    public bool IsWork => string.Equals(Status, "WORK", StringComparison.OrdinalIgnoreCase);
    private string[] _displayTargets = Array.Empty<string>();
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsVerifiedForDisplayScope => _displayTargets.Length == 0 ? IsWork : IsWorkFor(_displayTargets);
    [System.Text.Json.Serialization.JsonIgnore]
    public string ScopeStatusLabel => IsWork && !IsVerifiedForDisplayScope ? Loc.T("Другие цели") : TestStateLabel;

    internal void SetDisplayTargets(IEnumerable<string> targets)
    {
        var next = targets.ToArray();
        if (_displayTargets.SequenceEqual(next, StringComparer.OrdinalIgnoreCase)) return;
        _displayTargets = next;
        OnPropertyChanged(nameof(IsVerifiedForDisplayScope));
        OnPropertyChanged(nameof(ScopeStatusLabel));
        OnPropertyChanged(nameof(PoolStatusHint));
    }
    public bool IsWorkFor(IEnumerable<string> targets) => IsWork && targets.Any() &&
        targets.All(target => ProbeResults.Any(p =>
            string.Equals(p.Key, target, StringComparison.OrdinalIgnoreCase) && p.Value));
    public bool IsTesting => string.Equals(Status, "TESTING", StringComparison.OrdinalIgnoreCase);
    public bool IsInconclusive => string.Equals(Status, "INCONCLUSIVE", StringComparison.OrdinalIgnoreCase);
    public bool IsUntested => IsInconclusive || string.Equals(Status, "UNTESTED", StringComparison.OrdinalIgnoreCase) ||
                              (string.Equals(Status, "BROKEN", StringComparison.OrdinalIgnoreCase) && string.Equals(Reason, "unproven", StringComparison.OrdinalIgnoreCase));
    public bool IsFailed => !IsWork && !IsUntested && !IsTesting;

    public string TestStateLabel => IsWork ? Loc.T("Работает") : (IsTesting ? Loc.T("Проверка…") : IsInconclusive ? Loc.T("Не подтверждено") : (IsUntested ? Loc.T("Не проверен") : Loc.T("Сбой")));

    public string PoolStatusShort => TestStateLabel;

    public string PoolStatusHint => IsWork && !IsVerifiedForDisplayScope
        ? Loc.T("HTTPS проверен для других целей. Для выбранных целей нужен новый тест.")
        : IsWork && !string.IsNullOrEmpty(VerifiedFlowSummary) ? VerifiedFlowSummary
        : IsWork
        ? Loc.T("Проверено: {0}/{1} целей доступны по HTTPS. Видео и голос отдельно не проверялись.", PassCount, TotalCount)
        : IsInconclusive ? VerifiedFlowSummary
        : (IsTesting ? Loc.T("Выполняется проверка выбранных целей…") : (IsUntested ? Loc.T("Не проверен: выполните полный скан") : Loc.T("Не все цели прошли последнюю проверку")));

    public string ProvenanceDisplay => Provenance ?? (IsBuiltIn ? "REFERENCE (Immutable)" : IsGenerated ? "GENERATED (Discovery)" : "USER CUSTOM");
    public string FingerprintShort => !string.IsNullOrEmpty(Fingerprint) && Fingerprint.Length >= 12 ? Fingerprint[..12] : (!string.IsNullOrEmpty(Fingerprint) ? Fingerprint : "—");
    public string TestedTargetsDisplay => ProbeResults.Count == 0 ? Loc.T("Цели ещё не проверены")
        : string.Join(Environment.NewLine, ProbeResults.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{(p.Value ? "OK" : Loc.T("Сбой"))}  {p.Key}"));

    // Display text is localized here, in the COMPUTED getters only — never in Name/Description/Tagline
    // themselves. Name is the identity key (persisted in settings.json, matched by Ordinal equality),
    // so it must read the same in every language; these read-only projections translate it for the eye
    // via Loc.T while the stored value stays Russian.

    /// <summary>Section title for grouping in the presets list.</summary>
    public string GroupTitle => Loc.T(Provenance == "UPSTREAM_BLOCKCHECK2" ? "Найдены blockcheck2" : !IsBuiltIn
        ? (IsAutoLeaderboard ? "★ Лучшие из последней генерации"
           : IsGenerated ? "✨ Сгенерировано автоподбором"
           : "Личные (созданные)")
        : "Основные (Discord / YouTube)");

    /// <summary>True when a friendly tagline is set (built-ins) — gates the accent subtitle/label.</summary>
    public bool HasTagline => !string.IsNullOrEmpty(Tagline);

    /// <summary>Strategies-list primary text: the friendly tagline when present, else the name.</summary>
    public string PrimaryLabel => Loc.T(HasTagline ? Tagline : Name);

    /// <summary>Strategies-list secondary text: the technical name when a tagline fronts the row,
    /// otherwise the description.</summary>
    public string SecondaryLabel => Loc.T(HasTagline ? Name : Description);

    /// <summary>Name for display (identity <see cref="Name"/> stays Russian; this translates it).</summary>
    public string NameDisplay => Loc.T(Name);

    /// <summary>Description for display, translated.</summary>
    public string DescriptionDisplay => Loc.T(Description);

    /// <summary>Tagline for display, translated.</summary>
    public string TaglineDisplay => Loc.T(Tagline);

    public Preset Clone() => new()
    {
        Name = Name,
        Description = Description,
        Tagline = Tagline,
        Args = new List<string>(Args),
        UsesHostlist = UsesHostlist,
        IsGenerated = IsGenerated,
        IsBuiltIn = false,
        ReferenceId = null,
        Provenance = Provenance,
        TargetServices = TargetServices,
        CompatibleLists = new List<string>(CompatibleLists),
        Compatibility = Compatibility,
        CompatibilityNotes = CompatibilityNotes
    };
}
