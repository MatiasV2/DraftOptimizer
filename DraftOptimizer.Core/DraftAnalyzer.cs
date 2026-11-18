using System.Text.Json;

namespace DraftOptimizer.Core;

public sealed record DraftAnalysisResult(double Winrate, int MatchupPairs, int SynergyPairs, int InferredRoles, int AllyPicks, int EnemyPicks)
{
    public double ConfidenceLower { get; init; }
    public double ConfidenceUpper { get; init; } = 1;
    public bool HasConfidence { get; init; }
    public double EvidenceGames { get; init; }
    public int ConfidenceSources { get; init; }
}

/// <summary>
/// Port of DraftGap's rating-based draft analysis, with medium risk (1000 prior games).
/// Conservative uncertainty is propagated over the complete draft rating.
/// </summary>
public sealed class DraftAnalyzer(Dataset data, Dataset context)
{
    private const double PriorGames = 1000;
    private readonly Dictionary<(int, Role), double> _championRatings = [];
    private readonly Dictionary<(int, Role), double> _contextRatings = [];
    private readonly Dictionary<(int, Role), double> _championVariance = [];
    private readonly Dictionary<(int, Role), double> _contextVariance = [];
    private readonly Dictionary<(int, Role, int, Role, bool), (double Rating, bool Evidence, double Variance, double Games)> _pairRatings = [];
    private static double ProportionVariance(double wins, double games) => (wins + .5) * (games - wins + .5) / (Math.Pow(games + 1, 2) * (games + 2));
    private static double RatingDerivative(double p) { p = Math.Clamp(p, 1e-9, 1 - 1e-9); return 400 / (Math.Log(10) * p * (1 - p)); }
    public static double ToRating(double winrate) => 400 * Math.Log10(Math.Clamp(winrate, 1e-9, 1 - 1e-9) / (1 - Math.Clamp(winrate, 1e-9, 1 - 1e-9)));
    public static double ToWinrate(double rating) => 1 / (1 + Math.Pow(10, -rating / 400));

    public DraftAnalysisResult Analyze(TeamComposition allies, TeamComposition enemies)
    {
        double rating = 0, variance = 0; int matchups = 0, duos = 0;
        var evidence = new List<double>();
        void AddChampion(int id, Role role, int sign)
        {
            rating += sign * ChampionRating(id, role); variance += _championVariance[(id, role)];
            var current = Stats(data, id, role); var previous = Stats(context, id, role);
            var n = current.Games > 0 ? current.Games : previous.Games;
            if (n > 0) evidence.Add(n);
        }
        foreach (var (role, id) in allies.Picks) AddChampion(id, role, 1);
        foreach (var (role, id) in enemies.Picks) AddChampion(id, role, -1);
        void AddDuos(TeamComposition team, int sign)
        {
            var entries = team.Picks.OrderBy(p => p.Key).ToArray();
            for (int i = 0; i < entries.Length; i++)
            for (int j = i + 1; j < entries.Length; j++)
            {
                var pair = PairRating(entries[i].Value, entries[i].Key, entries[j].Value, entries[j].Key, false);
                rating += sign * pair.Rating;
                variance += pair.Variance;
                if (pair.Evidence) { duos++; evidence.Add(pair.Games); }
            }
        }
        AddDuos(allies, 1); AddDuos(enemies, -1);
        foreach (var (allyRole, allyId) in allies.Picks)
        foreach (var (enemyRole, enemyId) in enemies.Picks)
        {
            var pair = PairRating(allyId, allyRole, enemyId, enemyRole, true);
            rating += pair.Rating;
            variance += pair.Variance;
            if (pair.Evidence) { matchups++; evidence.Add(pair.Games); }
        }
        double margin = WinrateConfidence.Z99 * Math.Sqrt(variance);
        return new(ToWinrate(rating), matchups, duos, allies.InferredRoles.Count + enemies.InferredRoles.Count, allies.Picks.Count, enemies.Picks.Count)
        {
            HasConfidence = evidence.Count > 0,
            ConfidenceLower = evidence.Count > 0 ? ToWinrate(rating - margin) : 0,
            ConfidenceUpper = evidence.Count > 0 ? ToWinrate(rating + margin) : 1,
            EvidenceGames = evidence.Count > 0 ? evidence.Min() : 0,
            ConfidenceSources = evidence.Count
        };
    }

    private double ChampionRating(int id, Role role)
    {
        if (_championRatings.TryGetValue((id, role), out var rating)) return rating;
        var stats = Stats(data, id, role);
        double priorWinrate = ToWinrate(ContextRating(id, role));
        rating = ToRating((stats.Wins + PriorGames * priorWinrate) / (stats.Games + PriorGames));
        var previous = Stats(context, id, role);
        double weight = stats.Games / (stats.Games + PriorGames);
        // Patch and 30-day samples overlap: do not add their games as independent evidence.
        double sd = weight * Math.Sqrt(ProportionVariance(stats.Wins, stats.Games)) + (1 - weight) * Math.Sqrt(ProportionVariance(previous.Wins, previous.Games));
        _championVariance[(id, role)] = Math.Pow(RatingDerivative(ToWinrate(rating)) * sd, 2);
        _championRatings[(id, role)] = rating;
        return rating;
    }
    private double ContextRating(int id, Role role)
    {
        if (_contextRatings.TryGetValue((id, role), out var rating)) return rating;
        var stats = Stats(context, id, role);
        rating = stats.Games > 0 ? ToRating(stats.Wins / stats.Games) : 0;
        double stabilized = (stats.Wins + .5) / (stats.Games + 1);
        _contextVariance[(id, role)] = Math.Pow(RatingDerivative(stabilized), 2) * ProportionVariance(stats.Wins, stats.Games);
        _contextRatings[(id, role)] = rating;
        return rating;
    }
    private (double Rating, bool Evidence, double Variance, double Games) PairRating(int a, Role roleA, int b, Role roleB, bool matchup)
    {
        var key = (a, roleA, b, roleB, matchup);
        if (_pairRatings.TryGetValue(key, out var cached)) return cached;
        double expectedRating = ContextRating(a, roleA) + (matchup ? -1 : 1) * ContextRating(b, roleB);
        var forward = Pair(a, roleA, b, roleB, matchup);
        var reverse = Pair(b, roleB, a, roleA, matchup);
        double games = (forward.Games + reverse.Games) / 2;
        double wins = (forward.Wins + (matchup ? reverse.Games - reverse.Wins : reverse.Wins)) / 2;
        if (games == 0)
        {
            var unknownVariance = Math.Pow(RatingDerivative(ToWinrate(expectedRating)), 2) * ProportionVariance(0, 0);
            _pairRatings[key] = (0, false, unknownVariance, 0); return _pairRatings[key];
        }
        double expected = ToWinrate(expectedRating);
        double adjusted = (wins + PriorGames * expected) / (games + PriorGames);
        double derivative = RatingDerivative(adjusted);
        // Priors regularize the point estimate but never count as played games for uncertainty.
        double observedSd = derivative * Math.Sqrt(ProportionVariance(wins, games));
        double baselineSd = Math.Sqrt(_contextVariance[(a, roleA)]) + Math.Sqrt(_contextVariance[(b, roleB)]);
        double sd = observedSd + baselineSd;
        var result = (ToRating(adjusted) - expectedRating, true, sd * sd, games);
        _pairRatings[key] = result;
        return result;
    }
    private (double Wins, double Games) Pair(int a, Role roleA, int b, Role roleB, bool matchup)
    {
        if (!context.Stats(a, roleA, out var stats) || !stats.TryGetProperty(matchup ? "matchup" : "synergy", out var table) ||
            table.ValueKind != JsonValueKind.Object || !table.TryGetProperty(((int)roleB).ToString(), out var lane) || lane.ValueKind != JsonValueKind.Object ||
            !lane.TryGetProperty(b.ToString(), out var pair)) return (0, 0);
        return ReadStats(pair);
    }
    private static (double Wins, double Games) Stats(Dataset source, int id, Role role) => source.Stats(id, role, out var stats) ? ReadStats(stats) : (0, 0);
    private static (double Wins, double Games) ReadStats(JsonElement value)
    {
        return StatObservation.Read(value);
    }
}
