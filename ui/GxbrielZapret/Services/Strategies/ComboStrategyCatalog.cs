using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Strategies;

/// <summary>A full, ready-to-run winws2 strategy (multi-profile) the auto-selector tries.</summary>
public sealed record ComboStrategy(string Name, List<string> Args)
{
    /// <summary>How the auto-selector must probe this candidate. The ~30 global catalog entries below
    /// are catch-alls → probed with bypassAll=true so the goal hosts are actually desynced during the
    /// test (the default). An already SNI-scoped preset candidate is probed with bypassAll=false —
    /// exactly as it really runs, its own discord/youtube hostlists covering the goal hosts.</summary>
    public bool BypassAll { get; init; } = true;

    /// <summary>Non-null when this candidate IS a ready-made preset (the strategy saved for this
    /// network, or a built-in): the winner is then that preset itself, not a global TLS bundle to
    /// re-route into a scoped combo.</summary>
    public Preset? SourcePreset { get; init; }
}

/// <summary>
/// The candidate "configs" the auto-selector cycles through — each a complete
/// multi-profile winws2 strategy that handles HTTP + TLS + QUIC (general) AND
/// Discord voice (STUN) in one go, differing only in the TLS/QUIC
/// desync flavour.
///
/// Projected directly from the 30 catalog candidates (GX-C01 .. GX-C30) in ReferenceStrategyCatalog.
/// </summary>
public static class ComboStrategyCatalog
{
    public static IReadOnlyList<ComboStrategy> All { get; } =
        ReferenceStrategyCatalog.CatalogCandidates
            .Select(r => new ComboStrategy(r.Name, new List<string>(r.Args)))
            .ToList();
}
