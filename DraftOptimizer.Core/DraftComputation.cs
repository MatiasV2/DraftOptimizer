namespace DraftOptimizer.Core;

public sealed record DraftComputationResult(List<Suggestion> Suggestions, DraftComposition? Composition, DraftAnalysisResult? Analysis);

public static class DraftComputation
{
    // Capture leases before scheduling: replacing the catalogue cannot dispose a worker's JSON.
    public static async Task<DraftComputationResult> CalculateAsync(Dataset data, Dataset? context, Role role,
        DraftSession? snapshot, HashSet<int>? pickable, bool includeLocalSelection, bool summary, CancellationToken ct, bool calculateSuggestions = true)
    {
        using var dataLease = data.Retain();
        using var contextLease = context?.Retain();
        return await Task.Run(() =>
        {
            List<Suggestion> suggestions = calculateSuggestions ? data.Suggest(role, snapshot, context, pickable, includeLocalSelection, ct) : [];
            ct.ThrowIfCancellationRequested();
            var composition = summary && snapshot != null ? DraftRoleInference.Infer(data, context, snapshot) : null;
            var analysis = composition != null && context != null && snapshot!.HasCompositionPicks
                ? new DraftAnalyzer(data, context).Analyze(composition.Allies, composition.Enemies) : null;
            ct.ThrowIfCancellationRequested();
            return new DraftComputationResult(suggestions, composition, analysis);
        }, ct).ConfigureAwait(false);
    }
}

