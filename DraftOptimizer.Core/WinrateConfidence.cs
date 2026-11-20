namespace DraftOptimizer.Core;

public readonly record struct WinrateInterval(double Lower, double Upper);

public static class WinrateConfidence
{
    private sealed record Settings(double Percent, double Z);
    private static Settings _settings = new(99, 2.5758293035489004);
    public static double LevelPercent => Volatile.Read(ref _settings).Percent;
    public static string LevelLabel => LevelPercent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";
    internal static double Z => Volatile.Read(ref _settings).Z;

    public static bool Configure(double percent)
    {
        if (!double.IsFinite(percent) || percent < 50 || percent > 99.99)
            throw new ArgumentOutOfRangeException(nameof(percent), "La confianza debe estar entre 50 y 99.99.");
        if (LevelPercent == percent) return false;
        double low = 0, high = 5;
        double target = percent / 200;
        // Integrate the standard normal density with Simpson's rule.
        for (int iteration = 0; iteration < 45; iteration++)
        {
            double mid = (low + high) / 2, step = mid / 1024;
            double sum = 1 + Math.Exp(-mid * mid / 2);
            for (int i = 1; i < 1024; i++)
            {
                double x = i * step;
                sum += (i % 2 == 0 ? 2 : 4) * Math.Exp(-x * x / 2);
            }
            double area = sum * step / (3 * Math.Sqrt(2 * Math.PI));
            if (area < target) low = mid; else high = mid;
        }
        Interlocked.Exchange(ref _settings, new(percent, (low + high) / 2));
        return true;
    }

    public static WinrateInterval Wilson99(double wins, int games) => Wilson(wins, games);

    // Two-sided Wilson interval using the configured confidence level. Fractional wins are supported for normalized datasets.
    public static WinrateInterval Wilson(double wins, int games)
    {
        if (games < 0 || !double.IsFinite(wins) || wins < 0 || wins > games)
            throw new ArgumentOutOfRangeException(nameof(wins), "Victorias y partidas inválidas.");
        if (games == 0) return new(0, 1);
        double z = Z;
        double p = wins / games, z2 = z * z;
        double denominator = 1 + z2 / games;
        double center = (p + z2 / (2 * games)) / denominator;
        double radius = z * Math.Sqrt(p * (1 - p) / games + z2 / (4d * games * games)) / denominator;
        return new(Math.Clamp(center - radius, 0, 1), Math.Clamp(center + radius, 0, 1));
    }

    // Only retain the part of the contextual effect outside both uncertainty intervals.
    // This is a ranking heuristic, not a calibrated interval for a difference or a match.
    public static double ContextEffect(double pairWins, int pairGames, double baseWins, int baseGames)
    {
        if (pairGames <= 0 || baseGames <= 0) return 0;
        var pair = Wilson(pairWins, pairGames);
        var baseline = Wilson(baseWins, baseGames);
        double positive = pair.Lower - baseline.Upper;
        double negative = pair.Upper - baseline.Lower;
        double effect = positive > 0 ? positive : negative < 0 ? negative : 0;
        return effect * pairGames / (pairGames + 1000d);
    }
}
