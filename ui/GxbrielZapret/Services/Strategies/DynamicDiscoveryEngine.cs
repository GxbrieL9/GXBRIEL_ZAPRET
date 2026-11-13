using GxbrielZapret.Localization;
using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Strategies;

/// <summary>A bounded search over native winws2 Lua recipes, not the upstream blockcheck script.</summary>
public sealed class DynamicDiscoveryEngine
{
    public event Action<string>? Status;
    public event Action<string>? CandidateStarted;
    public event Action<AutoScore>? ScoreReady;

    public static readonly string[] MandatoryGoalHosts = Canonical5ProbeService.CanonicalTargets.ToArray();

    public static IReadOnlyList<Preset> CreateCandidates()
    {
        var known = ReferenceStrategyCatalog.All.Select(p => p.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<Preset>();
        foreach (var bundle in StrategyGeneratorService.Candidates)
        {
            var args = PresetService.BuildComboArgs(bundle.Tls, bundle.Tls, bundle.Tls);
            var fingerprint = CanonicalProfile.ComputeFingerprint(args);
            if (!known.Add(fingerprint)) continue;
            result.Add(new Preset
            {
                Name = "Discovery — " + bundle.Name,
                Args = args,
                Fingerprint = fingerprint,
                IsGenerated = true,
                Status = "UNTESTED",
                Reason = "unproven"
            });
        }
        return result;
    }

    public async Task<(ComboStrategy strategy, AutoScore score)?> DiscoverWorkingRecipeAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        using var probe = new Canonical5ProbeService();
        foreach (var preset in CreateCandidates())
        {
            ct.ThrowIfCancellationRequested();
            CandidateStarted?.Invoke(preset.Name);
            var message = Loc.T("Проверка дополнительных параметров: {0}", preset.Name);
            Status?.Invoke(message);
            progress?.Report(message);
            var result = await probe.EvaluateCandidateAsync(preset, null, ct).ConfigureAwait(false);
            var strategy = new ComboStrategy(preset.Name, preset.Args) { BypassAll = false };
            var score = new AutoScore(preset.Name, result.PassCount, result.TotalCount - result.PassCount,
                result.TotalCount, strategy, result.Probes.Select(p => new AutoHostResult(p.Key,
                    DiagStatus.Skip, DiagStatus.Skip, p.Value ? DiagStatus.Ok : DiagStatus.Fail)).ToList());
            ScoreReady?.Invoke(score);
            if (result.IsWork && IsFullyWork(score)) return (strategy, score);
        }
        return null;
    }

    public static bool IsFullyWork(AutoScore score) => score.Hosts is not null &&
        MandatoryGoalHosts.All(host => score.Hosts.Any(h =>
            string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase) && h.Https == DiagStatus.Ok));

    /// <summary>Accepts native Lua options only; legacy parameters cannot be executed by winws2.</summary>
    public static List<string> AssembleCombinedStrategyArgs(string tcpDesyncPart)
    {
        var options = tcpDesyncPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (options.Length == 0 || options.Any(a => !a.StartsWith("--lua-desync=", StringComparison.Ordinal)))
            throw new ArgumentException("Use native winws2 --lua-desync options.", nameof(tcpDesyncPart));
        return PresetService.BuildComboArgs(options, options, options);
    }
}
