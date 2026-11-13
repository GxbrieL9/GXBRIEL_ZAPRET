using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Strategies;

public sealed record BlockcheckSelection(Preset? Profile, Canonical5ProbeResult? Verification, int Attempted, int CandidateCount);

public static class BlockcheckSelectionService
{
    public static async Task<BlockcheckSelection> SelectAsync(string report, AutoScope scope, string network,
        string optionsKey, bool disableQuic, Action checkpoint,
        Func<Preset, CancellationToken, Task<Canonical5ProbeResult>> verify,
        Action<Preset, Canonical5ProbeResult?, int, int>? progress, CancellationToken ct)
    {
        var candidates = BlockcheckPlanBuilder.Build(report, scope, disableQuic);
        int attempted = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            checkpoint();
            progress?.Invoke(candidate, null, attempted, candidates.Count);
            var result = await verify(candidate, ct);
            attempted++;
            checkpoint();
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(candidate, result, attempted, candidates.Count);
            if (!result.IsWork || result.Flows?.Passes(scope) != true) continue;
            var saved = BlockcheckProfileStore.SaveVerified(network, scope, optionsKey, candidate, result, checkpoint);
            return new(saved, result with { StrategyName = saved.Name }, attempted, candidates.Count);
        }
        return new(null, null, attempted, candidates.Count);
    }
}
