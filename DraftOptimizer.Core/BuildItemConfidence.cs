namespace DraftOptimizer.Core;

public sealed record BuildItemEvidence(string Slot, int ItemId, int Games, double Winrate, double ConfidenceLower, double ConfidenceUpper);

public static class BuildItemConfidence
{
    public const int MinimumGames = 200;
    public const double MaximumGap = .02;
    public static List<BuildItemEvidence> Rank(string slot, IEnumerable<(int Id, double Winrate, int Games)> observations, int minimumCount = 3)
    {
        var ranked = observations.Where(x => x.Id > 0 && x.Games >= MinimumGames && double.IsFinite(x.Winrate) && x.Winrate is >= 0 and <= 1)
            .GroupBy(x => x.Id).Select(g => g.OrderByDescending(x => x.Games).First())
            .Select(x => { var interval = WinrateConfidence.Wilson95(x.Winrate * x.Games, x.Games); return new BuildItemEvidence(slot, x.Id, x.Games, x.Winrate, interval.Lower, interval.Upper); })
            .OrderByDescending(x => x.ConfidenceLower).ThenByDescending(x => x.Games).ThenBy(x => x.ItemId).ToList();
        if (ranked.Count == 0) return [];
        var withinConfidenceGap = ranked.TakeWhile(x => ranked[0].ConfidenceLower - x.ConfidenceLower <= MaximumGap + 1e-12).Count();
        return ranked.Take(Math.Min(ranked.Count, Math.Max(withinConfidenceGap, Math.Max(1, minimumCount)))).ToList();
    }
}
