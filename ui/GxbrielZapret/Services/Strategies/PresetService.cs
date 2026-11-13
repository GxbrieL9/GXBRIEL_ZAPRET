using System.IO;
using System.Text.Json;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;

namespace GxbrielZapret.Services.Strategies;

/// <summary>
/// Provides unified strategy management according to AGENTS.md:
/// - The production 1.0 pool is the immutable 39-strategy reference catalog only.
/// - WORK/BROKEN/UNTESTED/TESTING are logical states persisted per network fingerprint.
/// - Generated/adaptive/user imports are kept out of the normal 1.0 flow.
/// </summary>
public sealed class PresetService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly List<Preset> _all = new();
    private readonly object _lock = new();

    public List<Preset> UserPresets
    {
        get
        {
            lock (_lock)
            {
                return _all.Where(p => !p.IsBuiltIn && !p.IsGenerated && !p.IsAutoLeaderboard).ToList();
            }
        }
    }

    public PresetService() => Load();

    /// <summary>The immutable 39 reference baseline strategies.</summary>
    public IReadOnlyList<ReferenceStrategy> ReferenceStrategies => ReferenceStrategyCatalog.All;

    /// <summary>The 39 presets in the current pool corresponding to the reference baseline catalog.</summary>
    public IReadOnlyList<Preset> ReferencePresets
    {
        get
        {
            lock (_lock)
            {
                var canonicalIds = ReferenceStrategyCatalog.All
                    .Select(r => r.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return _all
                    .Where(p => p.ReferenceId is not null && canonicalIds.Contains(p.ReferenceId))
                    .GroupBy(p => p.ReferenceId!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            }
        }
    }

    /// <summary>All loaded presets deduplicated, with WORK precedence.</summary>
    public IReadOnlyList<Preset> All
    {
        get
        {
            lock (_lock)
            {
                return _all.ToList();
            }
        }
    }

    public Preset? FindByName(string name)
    {
        lock (_lock)
        {
            return _all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        }
    }

    public Preset? FindByFingerprint(string fp)
    {
        if (string.IsNullOrWhiteSpace(fp)) return null;
        lock (_lock)
        {
            return _all.FirstOrDefault(p => string.Equals(p.Fingerprint, fp, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void AddUser(Preset p)
    {
        lock (_lock)
        {
            if (p.Provenance == "UPSTREAM_BLOCKCHECK2")
                throw new InvalidOperationException("Профили blockcheck2 сохраняются только после полной проверки для конкретной сети.");
            if (string.IsNullOrEmpty(p.Fingerprint))
                p.Fingerprint = CanonicalProfile.ComputeFingerprint(p.Args);

            p.IsBuiltIn = false;
            var canonical = CanonicalProfile.FromPreset(p, p.Status, p.Reason, "user");
            canonical.SaveToDirectory(AppPaths.StrategiesUserDir);
            if (!_all.Any(x => x.Fingerprint == p.Fingerprint))
                _all.Add(p);
        }
    }

    public void UpdateUser(Preset p)
    {
        lock (_lock)
        {
            if (p.BoundNetworkFingerprint is not null) throw new InvalidOperationException("Проверенный сетевой профиль изменяется только подбором.");
            // If this is a reference strategy, restore all immutable fields back to canonical values
            // and refuse the update. We actively rollback because the caller may hold the same object
            // reference and may have already mutated fields on it.
            var refByFingerprint = ReferenceStrategyCatalog.FindByFingerprint(p.Fingerprint ?? "");
            var refById = !string.IsNullOrEmpty(p.ReferenceId)
                ? ReferenceStrategyCatalog.All.FirstOrDefault(r => r.Id == p.ReferenceId)
                : null;
            var refDef = refByFingerprint ?? refById;

            if (p.IsBuiltIn || refDef != null || ReferenceStrategyCatalog.IsReferenceStrategy(p.Fingerprint ?? ""))
            {
                // Rollback any in-place mutations the caller may have made to the object
                if (refDef != null)
                {
                    p.Name = refDef.Name;
                    p.Description = refDef.Description;
                    p.Tagline = refDef.Tagline;
                    p.Args = new List<string>(refDef.Args);
                    p.Provenance = refDef.Provenance;
                    p.TargetServices = refDef.TargetServices;
                    p.ReferenceId = refDef.Id;    // IsReferenceStrategy derives from this
                    p.IsBuiltIn = true;
                    p.Fingerprint = refDef.Fingerprint;
                    p.CompatibleLists = new List<string>(refDef.CompatibleLists);
                }
                return;
            }

            if (string.IsNullOrEmpty(p.Fingerprint))
                p.Fingerprint = CanonicalProfile.ComputeFingerprint(p.Args);

            string targetDir = p.IsWork ? AppPaths.ProfilesWorkDir : AppPaths.ProfilesBrokenDir;
            var canonical = CanonicalProfile.FromPreset(p, p.Status, p.Reason, "user");

            if (!string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
            {
                string oldDir = Path.GetDirectoryName(p.FilePath) ?? "";
                if (!string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(p.FilePath); } catch { }
                }
            }

            p.FilePath = canonical.SaveToDirectory(targetDir);
            try { canonical.SaveToDirectory(AppPaths.StrategiesUserDir); } catch { }
            SaveLegacy();
        }
    }

    public Preset MoveToStatus(Preset p, string status, string reason, string source = "verification")
    {
        if (p.BoundNetworkFingerprint is not null)
            throw new InvalidOperationException("Профиль сети обновляется только полной проверкой blockcheck2.");
        lock (_lock)
        {
            string normalizedStatus = string.Equals(status, "WORK", StringComparison.OrdinalIgnoreCase)
                ? "WORK"
                : string.Equals(status, "INCONCLUSIVE", StringComparison.OrdinalIgnoreCase) ? "INCONCLUSIVE" : "BROKEN";

            bool isRef = !string.IsNullOrEmpty(p.ReferenceId) || ReferenceStrategyCatalog.IsReferenceStrategy(p.Fingerprint);
            p.Status = normalizedStatus;
            p.Reason = string.IsNullOrWhiteSpace(reason) ? "unproven" : reason;
            p.IsBuiltIn = isRef;

            if (string.IsNullOrEmpty(p.Fingerprint))
                p.Fingerprint = CanonicalProfile.ComputeFingerprint(p.Args);

            if (isRef)
            {
                var refDef = ReferenceStrategyCatalog.FindByFingerprint(p.Fingerprint);
                if (refDef != null)
                {
                    p.ReferenceId = refDef.Id;
                    p.Provenance = refDef.Provenance;
                    p.TargetServices = refDef.TargetServices;
                    p.CompatibleLists = new List<string>(refDef.CompatibleLists);
                }

                if (!_all.Contains(p))
                    _all.Add(p);

                SaveLegacy();
                return p;
            }

            if (!p.IsWork)
            {
                // Discard failed non-reference probe/discovery candidates without writing to disk or polluting _all
                if (p.IsGenerated || p.Name.StartsWith("Discovery — ", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = _all.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(_all[i].Fingerprint, p.Fingerprint, StringComparison.OrdinalIgnoreCase) && _all[i].IsGenerated)
                            _all.RemoveAt(i);
                    }
                    return p;
                }

                // If it's an existing user preset that failed, update its metadata in place without dumping into ProfilesBrokenDir
                if (!string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
                {
                    try
                    {
                        var updatedCanonical = CanonicalProfile.FromPreset(p, p.Status, p.Reason, source);
                        updatedCanonical.LastTest = DateTime.UtcNow.ToString("O");
                        File.WriteAllText(p.FilePath, JsonSerializer.Serialize(updatedCanonical, JsonOpts));
                    }
                    catch { }
                }

                SaveLegacy();
                return p;
            }

            string targetDir = AppPaths.ProfilesWorkDir;
            string otherDir = AppPaths.ProfilesBrokenDir;
            var canonical = CanonicalProfile.FromPreset(p, p.Status, p.Reason, source);
            canonical.LastTest = DateTime.UtcNow.ToString("O");

            DeleteProfileFilesByFingerprint(otherDir, p.Fingerprint);
            DeleteProfileFilesByFingerprint(AppPaths.StrategiesUserDir, p.Fingerprint);
            DeleteProfileFilesByFingerprint(AppPaths.StrategiesGeneratedDir, p.Fingerprint);
            if (!string.IsNullOrEmpty(p.FilePath)
                && File.Exists(p.FilePath)
                && !string.Equals(
                    Path.GetFullPath(Path.GetDirectoryName(p.FilePath) ?? ""),
                    Path.GetFullPath(targetDir),
                    StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(p.FilePath); } catch { }
            }

            p.FilePath = canonical.SaveToDirectory(targetDir);

            for (int i = _all.Count - 1; i >= 0; i--)
            {
                var existing = _all[i];
                if (!ReferenceEquals(existing, p)
                    && string.Equals(existing.Fingerprint, p.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    _all.RemoveAt(i);
            }

            if (!_all.Contains(p))
                _all.Add(p);

            SaveLegacy();
            return p;
        }
    }

    private static void DeleteProfileFilesByFingerprint(string directory, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || !Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.gxprofile.json"))
        {
            var profile = CanonicalProfile.LoadFromFile(file);
            if (profile is null) continue;
            if (!string.Equals(profile.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch { }
        }
    }

    public void DeleteUser(Preset p)
    {
        lock (_lock)
        {
            if (p.BoundNetworkFingerprint is not null) return;
            if (p.IsBuiltIn || !string.IsNullOrEmpty(p.ReferenceId) || ReferenceStrategyCatalog.IsReferenceStrategy(p.Fingerprint)) return;

            if (!string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
            {
                try { File.Delete(p.FilePath); } catch { }
            }
            DeleteProfileFilesByFingerprint(AppPaths.StrategiesUserDir, p.Fingerprint);
            DeleteProfileFilesByFingerprint(AppPaths.StrategiesGeneratedDir, p.Fingerprint);

            _all.Remove(p);
            SaveLegacy();
        }
    }

    /// <summary>Replace the auto-saved "top-3 of the last generation" leaderboard presets with a fresh
    /// set, so the saved trio and their scores stay current after each generation run. The incoming
    /// presets are flagged as leaderboard entries; the previous trio is dropped first.</summary>
    public void ReplaceAutoLeaderboard(IEnumerable<Preset> top)
    {
        lock (_lock)
        {
            _all.RemoveAll(x => x.IsAutoLeaderboard);
            try
            {
                if (Directory.Exists(AppPaths.StrategiesGeneratedDir))
                {
                    foreach (var f in Directory.EnumerateFiles(AppPaths.StrategiesGeneratedDir, "*.gxprofile.json"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }

            foreach (var p in top)
            {
                if (string.IsNullOrEmpty(p.Fingerprint))
                    p.Fingerprint = CanonicalProfile.ComputeFingerprint(p.Args);

                p.IsBuiltIn = false;
                p.IsAutoLeaderboard = true;
                var canonical = CanonicalProfile.FromPreset(p, p.Status, p.Reason, "generated");
                try { canonical.SaveToDirectory(AppPaths.StrategiesGeneratedDir); } catch { }
                if (!_all.Any(x => x.Fingerprint == p.Fingerprint))
                    _all.Add(p);
            }
        }
    }

    public void Save() => SaveLegacy();

    private void SaveLegacy()
    {
        try
        {
            AppPaths.EnsureCreated();
            RotateBackups(AppPaths.PresetsFile);
            string tmp = AppPaths.PresetsFile + ".tmp";
            var userList = new List<Preset>();
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, userList, JsonOpts);
                stream.Flush(true);
            }
            File.Move(tmp, AppPaths.PresetsFile, overwrite: true);
        }
        catch { /* non-fatal */ }
    }

    private static void RotateBackups(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(AppPaths.BackupsDir);
            for (int i = 5; i >= 2; i--)
            {
                string newer = Path.Combine(AppPaths.BackupsDir, $"{Path.GetFileName(path)}.bak{i - 1}");
                string older = Path.Combine(AppPaths.BackupsDir, $"{Path.GetFileName(path)}.bak{i}");
                if (File.Exists(newer)) File.Copy(newer, older, overwrite: true);
            }
            File.Copy(path, Path.Combine(AppPaths.BackupsDir, $"{Path.GetFileName(path)}.bak1"), overwrite: true);
        }
        catch { }
    }

    private string _currentNetworkFp = "default";
    private string? _runtimeOptionsKey;
    private string? _referenceOptionsKey;
    public string CurrentNetworkFingerprint => _currentNetworkFp;

    public void LoadForNetwork(string? networkFp, string? runtimeOptionsKey = null, string? referenceOptionsKey = null)
    {
        _currentNetworkFp = string.IsNullOrWhiteSpace(networkFp) ? "default" : networkFp;
        _runtimeOptionsKey = runtimeOptionsKey;
        _referenceOptionsKey = referenceOptionsKey;
        Load();
    }

    public Preset UpdateStrategyProbeState(
        Preset p,
        string status,
        string reason,
        Dictionary<string, bool> probes,
        int passCount,
        int totalCount,
        long latencyMs,
        string networkFingerprint,
        ApplicationFlowResult? flows = null,
        string? optionsKey = null)
    {
        lock (_lock)
        {
            if (p.BoundNetworkFingerprint is not null)
                throw new InvalidOperationException("Для сетевого профиля требуется полная проверка сервисов.");
            if (string.Equals(status, "WORK", StringComparison.OrdinalIgnoreCase) &&
                (passCount != totalCount || totalCount <= 0 || probes.Count != totalCount || probes.Values.Any(ok => !ok) ||
                 Canonical5ProbeService.ScopeForTargets(probes.Keys) is not { } scope || flows?.Passes(scope) != true))
                throw new InvalidOperationException("WORK требует подтверждённых видеоданных YouTube и/или Gateway Discord для выбранных целей.");
            string id = p.ReferenceId ?? p.Fingerprint;
            var state = new StrategyProbeState
            {
                Id = id,
                ValidationVersion = StrategyProbeState.CurrentValidationVersion,
                Status = status,
                Reason = reason,
                LastTestedAt = DateTime.UtcNow,
                Probes = new Dictionary<string, bool>(probes, StringComparer.OrdinalIgnoreCase),
                PassCount = passCount,
                TotalCount = totalCount,
                LatencyMs = latencyMs,
                NetworkFingerprint = networkFingerprint,
                Flows = flows,
                OptionsKey = optionsKey
            };

            if (!StrategyStateStore.SaveSingleState(networkFingerprint, state))
                throw new IOException(Loc.T("Результат проверки не удалось сохранить. Проверьте доступ к папке данных."));
            p.Status = status;
            p.Reason = reason;
            p.LastTestedAt = state.LastTestedAt;
            p.ProbeResults = state.Probes;
            p.PassCount = passCount;
            p.TotalCount = totalCount;
            p.LatencyMs = latencyMs;
            p.VerifiedFlowSummary = flows?.Summary ?? "";
            p.VerifiedVideoMbps = flows?.YouTubeVideo.MegabitsPerSecond;
            p.NotifyProbeResultsChanged();
            MoveToStatus(p, status, reason, "5probe_test");
            return p;
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            AppPaths.EnsureCreated();
            _all.Clear();

            var storedNetworkStates = StrategyStateStore.GetForNetwork(_currentNetworkFp);
            foreach (var r in ReferenceStrategyCatalog.All)
            {
                var p = r.ToPreset();
                string fp = r.Fingerprint;

                if (storedNetworkStates.TryGetValue(r.Id, out var s) || storedNetworkStates.TryGetValue(fp, out s))
                {
                    p.Status = NormalizeStrategyStatus(s.Status);
                    p.Reason = string.IsNullOrWhiteSpace(s.Reason) ? "unproven" : s.Reason;
                    p.LastTestedAt = s.LastTestedAt;
                    p.ProbeResults = s.Probes ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    p.PassCount = s.PassCount;
                    p.TotalCount = s.TotalCount <= 0 ? 5 : s.TotalCount;
                    p.LatencyMs = s.LatencyMs;
                    p.VerifiedFlowSummary = s.Flows?.Summary ?? "";
                    p.VerifiedVideoMbps = s.Flows?.YouTubeVideo.MegabitsPerSecond;
                    if (p.Status == "BROKEN" && s.PassCount == s.TotalCount && s.TotalCount > 0 && s.Flows is not null &&
                        Canonical5ProbeService.ScopeForTargets(p.ProbeResults.Keys) is { } oldScope && !s.Flows.IsConclusive(oldScope))
                    {
                        p.Status = "INCONCLUSIVE";
                        p.Reason = "application_verification_inconclusive";
                    }

                    if (p.IsWork && (!IsStrictCanonicalWorkState(s) ||
                        (_referenceOptionsKey is not null && _referenceOptionsKey != s.OptionsKey)))
                    {
                        // Invalidate the badge, preserving historical evidence and the running strategy.
                        p.Status = "UNTESTED";
                        p.Reason = "requires_application_flow_recheck";
                        p.PassCount = 0;
                        p.ProbeResults = new(StringComparer.OrdinalIgnoreCase);
                        p.VerifiedFlowSummary = "";
                        p.VerifiedVideoMbps = null;
                    }
                }
                else
                {
                    p.Status = "UNTESTED";
                    p.Reason = "unproven";
                    p.PassCount = 0;
                    p.TotalCount = 5;
                }

                _all.Add(p);
            }

            void ScanProfilesDir(string dir, bool isUser, bool isLeaderboard = false)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.EnumerateFiles(dir, "*.gxprofile.json"))
                {
                    var profile = CanonicalProfile.LoadFromFile(file);
                    if (profile is null) continue;
                    var preset = profile.ToPreset();
                    if (preset.Provenance == "UPSTREAM_BLOCKCHECK2") continue;
                    preset.IsBuiltIn = false;
                    preset.IsAutoLeaderboard = isLeaderboard;
                    // File placement is historical; verification belongs to the current network.
                    if (storedNetworkStates.TryGetValue(preset.Fingerprint, out var saved))
                    {
                        preset.ProbeResults = new Dictionary<string, bool>(saved.Probes ?? new(), StringComparer.OrdinalIgnoreCase);
                        preset.PassCount = saved.PassCount;
                        preset.TotalCount = saved.TotalCount;
                        preset.LastTestedAt = saved.LastTestedAt;
                        preset.LatencyMs = saved.LatencyMs;
                        preset.Reason = saved.Reason;
                        preset.Status = string.Equals(saved.Status, "WORK", StringComparison.OrdinalIgnoreCase)
                            && (!IsStrictCanonicalWorkState(saved) || (_referenceOptionsKey is not null && _referenceOptionsKey != saved.OptionsKey))
                            ? "UNTESTED" : NormalizeStrategyStatus(saved.Status);
                        preset.VerifiedFlowSummary = saved.Flows?.Summary ?? "";
                        preset.VerifiedVideoMbps = saved.Flows?.YouTubeVideo.MegabitsPerSecond;
                    }
                    else
                    {
                        preset.Status = "UNTESTED";
                        preset.Reason = "unproven";
                        preset.ProbeResults.Clear();
                        preset.PassCount = 0;
                        preset.LastTestedAt = null;
                    }
                    if (!string.IsNullOrEmpty(preset.Fingerprint) && !_all.Any(x => string.Equals(x.Fingerprint, preset.Fingerprint, StringComparison.OrdinalIgnoreCase)))
                        _all.Add(preset);
                }
            }

            ScanProfilesDir(AppPaths.StrategiesUserDir, isUser: true);
            ScanProfilesDir(AppPaths.StrategiesGeneratedDir, isUser: false, isLeaderboard: true);
            ScanProfilesDir(AppPaths.ProfilesWorkDir, isUser: false);
            _all.AddRange(BlockcheckProfileStore.Load(_currentNetworkFp, _runtimeOptionsKey));
        }
    }

    private static string NormalizeStrategyStatus(string? status)
    {
        if (string.Equals(status, "WORK", StringComparison.OrdinalIgnoreCase)) return "WORK";
        if (string.Equals(status, "BROKEN", StringComparison.OrdinalIgnoreCase)) return "BROKEN";
        if (string.Equals(status, "TESTING", StringComparison.OrdinalIgnoreCase)) return "TESTING";
        if (string.Equals(status, "INCONCLUSIVE", StringComparison.OrdinalIgnoreCase)) return "INCONCLUSIVE";
        return "UNTESTED";
    }

    private static bool IsStrictCanonicalWorkState(StrategyProbeState state)
    {
        if (RequiresApplicationRecheck(state)) return false;
        if (!string.Equals(state.Status, "WORK", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsTrustedRuntimeScopeReason(state.Reason)) return false;
        if (state.TotalCount <= 0 || state.PassCount != state.TotalCount) return false;
        if (Canonical5ProbeService.ScopeForTargets(state.Probes.Keys) is not { } scope || state.Flows?.Passes(scope) != true) return false;

        return state.Probes != null && state.Probes.Count == state.TotalCount &&
            state.Probes.All(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value);
    }

    private static bool RequiresApplicationRecheck(StrategyProbeState state) =>
        state.ValidationVersion < StrategyProbeState.CurrentValidationVersion;

    private static bool IsTrustedRuntimeScopeReason(string? reason) =>
        reason != null && (
            reason.StartsWith("verified_5_of_5", StringComparison.OrdinalIgnoreCase) ||
            reason.StartsWith("verified_Discord + YouTube_", StringComparison.OrdinalIgnoreCase) ||
            reason.StartsWith("verified_Discord_", StringComparison.OrdinalIgnoreCase) ||
            reason.StartsWith("verified_YouTube_", StringComparison.OrdinalIgnoreCase) ||
            reason.StartsWith("manual_Discord + YouTube_", StringComparison.OrdinalIgnoreCase) ||
            reason.StartsWith("manual_Discord_", StringComparison.OrdinalIgnoreCase) ||
            reason.StartsWith("manual_YouTube_", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "manual_5_of_5_canonical_https", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "restored_after_watchdog_false_demote", StringComparison.OrdinalIgnoreCase));

    private static void CleanupProfilePoolDuplicates()
    {
        try
        {
            var work = IndexProfileFiles(AppPaths.ProfilesWorkDir);
            var broken = IndexProfileFiles(AppPaths.ProfilesBrokenDir);
            var user = IndexProfileFiles(AppPaths.StrategiesUserDir);
            var generated = IndexProfileFiles(AppPaths.StrategiesGeneratedDir);

            // Remove intra-directory duplicates (keep newest file per fingerprint)
            DeleteDuplicateFilesWithinIndex(work);
            DeleteDuplicateFilesWithinIndex(broken);
            DeleteDuplicateFilesWithinIndex(user);
            DeleteDuplicateFilesWithinIndex(generated);

            // WORK takes precedence over BROKEN: remove BROKEN copies
            foreach (var fp in work.Keys)
            {
                if (!broken.TryGetValue(fp, out var brokenFiles)) continue;
                foreach (var file in brokenFiles)
                    TryDeleteFile(file);
            }

            // WORK/BROKEN take precedence over user/generated: remove stale copies there
            foreach (var fp in work.Keys.Concat(broken.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (user.TryGetValue(fp, out var userFiles))
                    foreach (var file in userFiles) TryDeleteFile(file);
                if (generated.TryGetValue(fp, out var genFiles))
                    foreach (var file in genFiles) TryDeleteFile(file);
            }
        }
        catch { /* best-effort cleanup; loading must continue */ }
    }

    private static Dictionary<string, List<string>> IndexProfileFiles(string directory)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return index;

        foreach (var file in Directory.EnumerateFiles(directory, "*.*"))
        {
            string? fp = null;
            if (file.EndsWith(".gxprofile.json", StringComparison.OrdinalIgnoreCase))
            {
                var profile = CanonicalProfile.LoadFromFile(file);
                if (profile is not null && !string.IsNullOrWhiteSpace(profile.Fingerprint))
                    fp = profile.Fingerprint;
            }
            else if (file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                var preset = PresetImporter.ParseFile(file);
                if (preset is not null && preset.Args.Count > 0)
                    fp = CanonicalProfile.ComputeFingerprint(preset.Args);
            }

            if (!string.IsNullOrWhiteSpace(fp))
            {
                if (!index.TryGetValue(fp, out var files))
                {
                    files = new List<string>();
                    index[fp] = files;
                }
                files.Add(file);
            }
        }

        return index;
    }

    private static void DeleteDuplicateFilesWithinIndex(Dictionary<string, List<string>> index)
    {
        foreach (var files in index.Values)
        {
            if (files.Count <= 1) continue;
            foreach (var file in files
                         .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                         .Skip(1))
                TryDeleteFile(file);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    // ---- built-in strategies ----------------------------------------------

    // All built-ins are SNI-routed "combo" strategies: they send Discord SNIs,
    // YouTube/Google SNIs and everything else through different TLS desyncs
    // (via the discord/youtube hostlists), and always carry the Discord voice
    // profile (discord+stun faked with a QUIC blob, the way Flowseal alt10 does it —
    // the server discards the QUIC garbage so there is no SSRC poison / NO_ROUTE, and
    // no fragile ttl cutting is needed). They differ only in the per-service
    // TLS desync "bundle". The generic bol-van presets were dropped — what works
    // is the *combination*, routed per service (see the nfqws1→nfqws2 migration
    // + Flowseal general.bat). DPI bypass is provider-specific; these are proven
    // starting points, not a guarantee for every ISP.

    public static List<Preset> BuiltIns() =>
        ReferenceStrategyCatalog.BuiltIns.Select(r => r.ToPreset()).ToList();

    /// <summary>Build the shared combo argument list (per-service TLS bundles + QUIC + Discord voice).
    /// Reused by the strategy generator to assemble a personal preset from generated TLS bundles.</summary>
    public static List<string> BuildComboArgs(
        string[] discordTls, string[] youtubeTls, string[] fallbackTls,
        string discordFilter = "{HOSTLIST:discord}", string[]? voiceDesync = null)
    {
        var a = new List<string>
        {
            "{WF_TCP}",
            "{WF_UDP}",
            "--ctrack-disable=0",
            "--ipcache-lifetime=8400",
            "--ipcache-hostname=1",
            "--lua-init=fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')",
            "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
            "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
            // Отечественные фейк-блобы (ClientHello vk.com / sberbank.ru) — ТСПУ вайтлистит домашний
            // трафик, поэтому фейк «под vk/сбер» выживает там, где google-фейк режется (тренд 2026:
            // sonicdpi → ozon.ru, Flowseal → dbankcloud_ru). Входят в стандартную поставку движка.
            "--blob=tls_vk:@{FILES}\\fake\\tls_clienthello_vk_com.bin",
            "--blob=tls_sber:@{FILES}\\fake\\tls_clienthello_sberbank_ru.bin",
            "--blob=tls_gos:@{FILES}\\fake\\tls_clienthello_gosuslugi_ru.bin",
            // Отечественный QUIC-блоб для голоса Discord (аналог dbankcloud_ru из Flowseal ALT10/11).
            "--blob=quic_vk:@{FILES}\\fake\\quic_initial_vk_com.bin",
            "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
            "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
            // 1) Discord web/login/media (по SNI).
            "--filter-tcp=443-65535", "--filter-l7=tls", discordFilter, "--out-range=-d10", "--payload=tls_client_hello",
        };
        a.AddRange(discordTls);
        // 2a) Dedicated GGC Video CDN rule (rr*.googlevideo.com)
        // TSPU splits YouTube: edge web (www.youtube.com, i.ytimg.com) requires fake+multidisorder,
        // while GGC video media CDN (googlevideo.com) drops fake+multidisorder but streams at 65-85 Mbit/s
        // via hostfakesplit md5x6. This dedicated rule gives perfect video streaming without destabilizing web JS.
        a.Add("--new");
        a.AddRange(new[]
        {
            "--filter-tcp=443-65535", "--filter-l7=tls",
            "--hostlist-domains=googlevideo.com", "--hostlist-exclude-domains=redirector.googlevideo.com",
            "--out-range=-d10", "--payload=tls_client_hello",
            "--lua-desync=hostfakesplit:host=www.google.com:tcp_md5:repeats=6"
        });
        // 2b) YouTube / Google web (по SNI).
        a.Add("--new");
        a.AddRange(new[] { "--filter-tcp=443-65535", "--filter-l7=tls", "{HOSTLIST:youtube}", "--out-range=-d10", "--payload=tls_client_hello" });
        a.AddRange(youtubeTls);
        // NB: Telegram is intentionally NOT handled by the combo — the built-in tg-ws-proxy
        // (TelegramProxyService) carries Telegram over WebSocket instead of a DPI desync profile.
        // 3) Остальной TLS (вкл. hCaptcha и пр.). Catch-all — поэтому исключаем чувствительные
        //    домены (банки/госуслуги/VK/Яндекс/Steam/…) через {EXCLUDE:exclude}, чтобы их не сломать.
        a.Add("--new");
        a.AddRange(new[] { "--filter-tcp=443-65535", "--filter-l7=tls", "{EXCLUDE:exclude}", "--out-range=-d10", "--payload=tls_client_hello" });
        a.AddRange(fallbackTls);
        // 4) QUIC YouTube (по SNI) → google-фейк.
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=443-65535", "--filter-l7=quic", "{HOSTLIST:youtube}", "--payload=quic_initial",
                           "--lua-desync=fake:blob=quic_google:repeats=11" });
        // 4b) QUIC Discord (по SNI) → google-фейк. cdn/media.discordapp по HTTP/3: без отдельного
        //     профиля медиа/вложения не грузятся в режиме «только списки» (там catch-all QUIC отключён).
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=443-65535", "--filter-l7=quic", "{HOSTLIST:discord}", "--payload=quic_initial",
                           "--lua-desync=fake:blob=quic_google:repeats=11" });
        // 5) QUIC остальное → дефолтный фейк. Тоже catch-all → исключаем чувствительные домены.
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=443-65535", "--filter-l7=quic", "{EXCLUDE:exclude}", "--payload=quic_initial",
                           "--lua-desync=fake:blob=fake_default_quic:repeats=6" });
        // 6) Голос Discord (STUN + IP-discovery). Порты войса — ВЕСЬ высокий диапазон 50000-65535
        //    (узкий 50000-50100 пропускал половину войс-серверов → вечный 5000 пинг). Фейк QUIC-блобом
        //    google (мусор для войс-потока → SSRC не портится) + ip_autottl: фейк умирает на DPI
        //    провайдера, не доходя до сервера (анти-дроп), поэтому реальный RTP идёт без троттлинга.
        //    repeats=2 — лёгкий, как в актуальном фиксе 5к-пинга (Flowseal #12614 «Anti-Drop»).
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=19294-19344,50000-65535", "--filter-l7=discord,stun" });
        // Default voice: google QUIC-blob junk + ip_autottl anti-drop (Flowseal #12614). ALT10/11 pass a
        // domestic-blob voice (quic_vk) instead — that's what breaks 5k-ping/«не слышно» on some nets.
        a.AddRange(voiceDesync ?? new[]
            { "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=2" });

        return a;
    }

}
