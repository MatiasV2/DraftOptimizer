namespace DraftOptimizer.Core;

/// <summary>A champion's relative position among candidates with global confidence data.</summary>
public readonly record struct GlobalConfidencePlacement(int Rank, int Total, double Score, double ColorPosition)
{
    public static IReadOnlyDictionary<int, GlobalConfidencePlacement> RankSuggestions(IEnumerable<Suggestion> suggestions)
    {
        var groups = suggestions.Where(suggestion => suggestion.UsesGlobalConfidence && double.IsFinite(suggestion.Score))
            .GroupBy(suggestion => suggestion.Score)
            .OrderByDescending(group => group.Key)
            .ToArray();
        int total = groups.Sum(group => group.Count());
        var placements = new Dictionary<int, GlobalConfidencePlacement>(total);
        int rank = 1;
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            double position = groups.Length == 1 ? 0 : (double)i / (groups.Length - 1);
            foreach (var suggestion in group)
                placements.Add(suggestion.Champion.Id, new(rank, total, group.Key, position));
            rank += group.Count();
        }
        return placements;
    }
}
