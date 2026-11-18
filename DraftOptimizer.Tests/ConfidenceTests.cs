using DraftOptimizer.Core;

static class ConfidenceTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var benchmark = WinrateConfidence.Wilson99(50, 100);
        check(Math.Abs(benchmark.Lower - .3752796250448398) < 1e-10 && Math.Abs(benchmark.Upper - .6247203749551602) < 1e-10, "Wilson 99% coincide con referencia numérica independiente");
        var none = WinrateConfidence.Wilson99(0, 100);
        var all = WinrateConfidence.Wilson99(100, 100);
        check(none.Lower < 1e-14 && Math.Abs(all.Upper - 1) < 1e-14 && none.Upper < .063 && all.Lower > .937, "Wilson conserva límites válidos con cero o todas las victorias");
        check(WinrateConfidence.Wilson99(0, 0) == new WinrateInterval(0, 1), "Sin partidas el intervalo expresa incertidumbre completa");
        var small = WinrateConfidence.Wilson99(116, 200);
        var large = WinrateConfidence.Wilson99(5400, 10000);
        check(large.Lower > small.Lower, "54% en 10000 partidas supera 58% en 200 por evidencia más sólida");
        var equalRate = WinrateConfidence.Wilson99(58, 100);
        var sameRateLarge = WinrateConfidence.Wilson99(5800, 10000);
        check(sameRateLarge.Lower > equalRate.Lower && sameRateLarge.Upper - sameRateLarge.Lower < equalRate.Upper - equalRate.Lower, "Mismo winrate con más partidas reduce incertidumbre y mejora puntaje");
        check(WinrateConfidence.Wilson99(60, 100).Lower > equalRate.Lower, "Con igual muestra un mejor winrate aumenta puntaje");
        check(WinrateConfidence.ContextEffect(52, 100, 5000, 10000) == 0, "Diferencia contextual cubierta por incertidumbre no aumenta puntaje");
        check(WinrateConfidence.ContextEffect(3000, 5000, 5000, 10000) > 0, "Matchup favorable con evidencia suficiente aporta ajuste positivo");
        check(WinrateConfidence.ContextEffect(2000, 5000, 5000, 10000) < 0, "Matchup desfavorable con evidencia suficiente aporta ajuste negativo");
        check(WinrateConfidence.ContextEffect(60, 100, 5000, 10000) < WinrateConfidence.ContextEffect(6000, 10000, 5000, 10000), "Más muestra contextual respalda un efecto mayor con igual winrate");
        try { WinrateConfidence.Wilson99(101, 100); throw new Exception("Aceptó victorias imposibles"); }
        catch (ArgumentOutOfRangeException) { check(true, "Rechaza estadísticas imposibles en cálculo de confianza"); }
        using var dataset = new Dataset("""
        {"version":"16.22.1","date":"2026-11-09","championData":{
          "103":{"id":"Ahri","name":"Ahri","statsByRole":{"2":{"games":200,"wins":116,"matchup":{},"synergy":{}}}},
          "238":{"id":"Zed","name":"Zed","statsByRole":{"2":{"games":10000,"wins":5400,"matchup":{},"synergy":{}}}}
        }}
        """);
        var ranked = dataset.Suggest(Role.Middle, null, null);
        check(ranked[0].Champion.Id == 238 && ranked[0].BaseWinrate < ranked[1].BaseWinrate, "Ranking real prioriza confianza frente al winrate bruto");
        check(ranked.All(s => s.UsesGlobalConfidence && !s.HasDraftConfidence) && Math.Abs(ranked[0].Score - 100 * large.Lower) < 1e-9, "Sin picks se usa confianza global Wilson claramente separada del draft");
        var draft = new DraftSession { GameId = 8, LocalPlayerCellId = 1, MyTeam = [new() { CellId = 1, AssignedPosition = "middle" }], Actions = [[new() { Id = 4, ActorCellId = 1, IsInProgress = true, Type = "pick" }]] };
        AutomaticPickRequest? requested = null;
        var controller = new AutoDraftController();
        var favorites = new FavoriteSettings { Favorites = new() { [Role.Middle] = [103, 238] } };
        await controller.TickAsync(dataset, null, draft, [103, 238], favorites, Role.Middle, DateTimeOffset.UtcNow, request => { requested = request; return Task.FromResult(true); });
        check(requested?.ChampionId == 238, "Primer pick usa confianza global aunque el contexto todavía no esté disponible");
        await controller.TickAsync(dataset, dataset, draft, [103, 238], favorites, Role.Middle, DateTimeOffset.UtcNow, request => { requested = request; return Task.FromResult(true); });
        check(requested?.ChampionId == 238 && !requested.Complete, "Preselección usa la confianza del draft y penaliza muestra pequeña");
        draft.TheirTeam = [new() { CellId = 5, ChampionId = 103, AssignedPosition = "middle" }]; controller.Reset(); requested = null;
        await controller.TickAsync(dataset, null, draft, [103, 238], favorites, Role.Middle, DateTimeOffset.UtcNow, request => { requested = request; return Task.FromResult(true); });
        check(requested == null, "Con picks visibles y sin contexto no se sustituye confianza del draft por confianza global");
        using var weakContext = new Dataset("""
        {"version":"16.22.1","date":"2026-11-09","championData":{"103":{"id":"Ahri","name":"Ahri","statsByRole":{"2":{"games":10000,"wins":5000,"matchup":{"2":{"1":{"games":100,"wins":52}}},"synergy":{}}}}}}
        """);
        draft.TheirTeam = [new() { CellId = 5, ChampionId = 1, AssignedPosition = "middle" }];
        var weak = weakContext.Suggest(Role.Middle, draft, weakContext).Single();
        check(weak.ContextSamples == 1 && weak.HasDraftConfidence && weak.Score < weak.DraftWinrate * 100, "Confianza del draft incorpora incertidumbre del matchup débil");
        check(ranked.All(s => double.IsFinite(s.Score) && s.Score is >= 0 and <= 100), "Puntajes se mantienen finitos y entre cero y cien");
        Console.WriteLine($"Ejemplo: 58% / 200 -> {small.Lower * 100:F2}; 54% / 10000 -> {large.Lower * 100:F2}.");
    }
}
