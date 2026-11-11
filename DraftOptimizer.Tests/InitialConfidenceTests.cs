using DraftOptimizer;
using DraftOptimizer.Core;

static class InitialConfidenceTests
{
    public static void Run(Action<bool, string> check)
    {
        using var data = new Dataset("""
        {"version":"16.22.1","date":"test","championData":{
          "103":{"id":"Ahri","name":"Ahri","statsByRole":{"0":{"games":300,"wins":180,"matchup":{},"synergy":{}},"2":{"games":200,"wins":100,"matchup":{},"synergy":{}}}},
          "238":{"id":"Zed","name":"Zed","statsByRole":{"0":{"games":10000,"wins":5400,"matchup":{},"synergy":{}},"2":{"games":0,"wins":0,"matchup":{},"synergy":{}}}}
        }}
        """);
        var empty = new DraftSession { LocalPlayerCellId = 2, MyTeam = [new() { CellId = 2, AssignedPosition = "middle" }], Actions = [[new() { ActorCellId = 2, Id = 9, Type = "pick", IsInProgress = true }]] };
        var global = data.Suggest(Role.Middle, empty, data, [103, 238], false);
        var ahri = global.Single(s => s.Champion.Id == 103);
        check(ahri.Games == 200 && Math.Abs(ahri.BaseWinrate - .5) < 1e-12, "Confianza inicial usa solo victorias y partidas del rol seleccionado");
        check(Math.Abs(ahri.Score - 100 * WinrateConfidence.Wilson95(100, 200).Lower) < 1e-10, "Puntaje inicial usa Wilson con victorias y partidas totales, no promedio de winrates");
        check(global.Count == 1 && global[0].Champion.Id == 103, "Partidas de otro rol no habilitan un campeón sin muestra en el rol seleccionado");
        check(global.All(s => s.UsesGlobalConfidence && s.HasConfidence && s.DraftAnalysis == null), "Draft vacío usa modo global incluso con dataset de contexto cargado");
        var top = data.Suggest(Role.Top, empty, data, [103, 238], false);
        check(top.Single(s => s.Champion.Id == 103).Games == 300 && Math.Abs(top.Single(s => s.Champion.Id == 103).Score - 100 * WinrateConfidence.Wilson95(180, 300).Lower) < 1e-10 && top.Single(s => s.Champion.Id == 238).Games == 10000, "Cambiar de rol invalida la caché y usa exclusivamente la muestra de ese rol");
        var noClient = data.Suggest(Role.Middle, null, null);
        check(noClient.Select(s => s.Score).SequenceEqual(global.Select(s => s.Score)), "Sin cliente se muestra el mismo orden inicial de confianza global");
        var row = new ChampionListItem(ahri.Champion); row.Update(ahri.Champion, Role.Middle, false, true, ahri, true);
        check(row.Label.Contains("WR del rol") && row.Label.Contains("partidas del rol") && row.Label.Contains("confianza global"), "Fila identifica WR y partidas del rol seleccionado");
        empty.Actions[0][0].ChampionId = 103; empty.MyTeam[0].ChampionId = 103;
        check(data.Suggest(Role.Middle, empty, data, [103], false).Single().UsesGlobalConfidence, "La preselección local del primer turno no crea una composición ficticia");
        empty.TheirTeam.Add(new() { CellId = 5, ChampionId = 238, AssignedPosition = "top" });
        var contextual = data.Suggest(Role.Middle, empty, data, [103], false).Single();
        check(contextual.HasDraftConfidence && !contextual.UsesGlobalConfidence && contextual.Games == 200, "Primer rival activa confianza del draft usando estadísticas de la posición seleccionada");
        var missing = data.Suggest(Role.Middle, empty, null, [103], false).Single();
        check(!missing.HasConfidence && !missing.UsesGlobalConfidence, "Picks existentes sin contexto no reciben un puntaje global engañoso");
        empty.TheirTeam.Clear();
        check(data.Suggest(Role.Middle, empty, null, [103], false).Single().UsesGlobalConfidence, "Eliminar el último rival invalida la caché y restaura el criterio global");
        empty.MyTeam[0].ChampionId = 0; empty.MyTeam[0].ChampionPickIntent = 103;
        empty.TheirTeam.Add(new() { CellId = 5, ChampionPickIntent = 238 });
        check(!empty.HasCompositionPicks && data.Suggest(Role.Middle, empty, data, [103], false).Single().UsesGlobalConfidence, "Intenciones de preselección sin picks no desactivan la confianza global inicial");
        empty.Actions[0][0].Completed = true; empty.Actions[0][0].IsInProgress = false;
        check(empty.HasCompositionPicks, "Un pick local confirmado sí cuenta como campeón seleccionado");
    }
}

