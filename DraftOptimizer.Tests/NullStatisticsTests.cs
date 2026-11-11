using DraftOptimizer.Core;
using System.Text.Json;

static class NullStatisticsTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var invalid in new[] { "null", "{}", "{\"games\":null,\"wins\":100}", "{\"games\":500,\"wins\":null}", "{\"games\":\"500\",\"wins\":100}", "{\"games\":500,\"wins\":501}" })
        {
            using var value = JsonDocument.Parse(invalid);
            check(StatObservation.Read(value.RootElement) == (0, 0), "Estadística incompleta no se interpreta como derrotas: " + invalid);
        }
        using var data = new Dataset("""
        {"version":"test","date":"test","championData":{
          "103":{"id":"Ahri","name":"Ahri","statsByRole":{"0":{"games":500,"wins":null},"1":null,"2":{"games":1000,"wins":550,"matchup":{"2":{"238":{"games":100,"wins":null}}},"synergy":{}}}},
          "238":{"id":"Zed","name":"Zed","statsByRole":{"0":{"games":null,"wins":null},"2":{"games":1000,"wins":500,"matchup":{"2":{"103":null}},"synergy":null}}}
        }}
        """);
        var global = data.Suggest(Role.Middle, null, data).Single(s => s.Champion.Id == 103);
        check(global.Games == 1000 && global.BaseWinrate == .55, "Roles nulos no descartan las estadísticas globales válidas ni añaden derrotas");
        var draft = new DraftSession { LocalPlayerCellId = 1, MyTeam = [new() { CellId = 1 }], TheirTeam = [new() { CellId = 2, ChampionId = 238 }] };
        var contextual = data.Suggest(Role.Middle, draft, data, [103]).Single();
        check(contextual.DraftAnalysis is { MatchupPairs: 0 } && double.IsFinite(contextual.Score), "Roles inferidos y matchups nulos conservan un resultado de draft finito sin evidencia ficticia");
        check(data.Suggest(Role.Top, draft, data, [103]).Count == 0, "Candidato sin victorias válidas no obtiene confianza contextual artificial");
    }
}
