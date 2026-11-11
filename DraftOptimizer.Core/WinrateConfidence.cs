namespace DraftOptimizer.Core;

public readonly record struct WinrateInterval(double Lower, double Upper);

public static class WinrateConfidence
{
    private const double Z95 = 1.959963984540054;

    // Two-sided Wilson 95% interval. Fractional wins are supported for normalized datasets.
    public static WinrateInterval Wilson95(double wins, int games)
    {
        if (games < 0 || !double.IsFinite(wins) || wins < 0 || wins > games)
            throw new ArgumentOutOfRangeException(nameof(wins), "Victorias y partidas inválidas.");
        if (games == 0) return new(0, 1);
        double p = wins / games, z2 = Z95 * Z95;
        double denominator = 1 + z2 / games;
        double center = (p + z2 / (2 * games)) / denominator;
        double radius = Z95 * Math.Sqrt(p * (1 - p) / games + z2 / (4d * games * games)) / denominator;
        return new(Math.Clamp(center - radius, 0, 1), Math.Clamp(center + radius, 0, 1));
    }

    // Only retain the part of the contextual effect outside both uncertainty intervals.
    // This is a ranking heuristic, not a calibrated interval for a difference or a match.
    public static double ContextEffect(double pairWins, int pairGames, double baseWins, int baseGames)
    {
        if (pairGames <= 0 || baseGames <= 0) return 0;
        var pair = Wilson95(pairWins, pairGames);
        var baseline = Wilson95(baseWins, baseGames);
        double positive = pair.Lower - baseline.Upper;
        double negative = pair.Upper - baseline.Lower;
        double effect = positive > 0 ? positive : negative < 0 ? negative : 0;
        return effect * pairGames / (pairGames + 1000d);
    }
}
