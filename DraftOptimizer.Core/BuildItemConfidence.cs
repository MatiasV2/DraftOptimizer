namespace DraftOptimizer.Core;

public sealed record BuildItemEvidence(string Slot, int ItemId, int Games, double Winrate, double ConfidenceLower, double ConfidenceUpper);

public static class BuildItemConfidence
{
    public const int MinimumGames = 200;
    public const double MaximumGap = .02;
    private const int MejaisSoulstealerId = 3041;
    public static List<BuildItemEvidence> Rank(string slot, IEnumerable<(int Id, double Winrate, int Games)> observations, int minimumCount = 4, bool fillFromSmallSamples = false)
    {
        var ranked = observations.Where(x => x.Id > 0 && x.Games >= (fillFromSmallSamples ? 1 : MinimumGames) && double.IsFinite(x.Winrate) && x.Winrate is >= 0 and <= 1)
            .GroupBy(x => x.Id).Select(g => g.OrderByDescending(x => x.Games).First())
            .Select(x => { var interval = WinrateConfidence.Wilson(x.Winrate * x.Games, x.Games); return new BuildItemEvidence(slot, x.Id, x.Games, x.Winrate, interval.Lower, interval.Upper); })
            .OrderByDescending(x => x.Games >= MinimumGames).ThenByDescending(x => x.ConfidenceLower).ThenByDescending(x => x.Games).ThenBy(x => x.ItemId).ToList();
        if (ranked.Count == 0) return [];
        var withinConfidenceGap = ranked.TakeWhile(x => x.Games >= MinimumGames && ranked[0].ConfidenceLower - x.ConfidenceLower <= MaximumGap + 1e-12).Count();
        var count = Math.Max(withinConfidenceGap, Math.Max(1, minimumCount));
        if (slot.StartsWith("item", StringComparison.Ordinal) && ranked.Take(count).Any(x => x.ItemId == MejaisSoulstealerId))
            count++;
        return ranked.Take(count).ToList();
    }
}
