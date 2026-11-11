using System.Text.Json;

namespace DraftOptimizer.Core;

internal static class StatObservation
{
    // Missing observations are unavailable evidence, never zero wins in played games.
    internal static (double Wins, double Games) Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("games", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out var games) ||
            !value.TryGetProperty("wins", out var w) || w.ValueKind != JsonValueKind.Number || !w.TryGetDouble(out var wins) ||
            !double.IsFinite(games) || games <= 0 || games != Math.Truncate(games) ||
            !double.IsFinite(wins) || wins < 0 || wins > games) return (0, 0);
        return (wins, games);
    }
}
