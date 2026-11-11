using DraftOptimizer.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

static class DraftAnalysisTests
{
    public static void Run(Action<bool, string> check)
    {
        JsonObject Stats(int games = 10000, double wr = .5) => new() { ["games"] = games, ["wins"] = games * wr, ["matchup"] = new JsonObject(), ["synergy"] = new JsonObject() };
        JsonObject Champion(string key, string name, Role role, double wr = .5) => new()
        { ["id"] = key, ["name"] = name, ["statsByRole"] = new JsonObject { [((int)role).ToString()] = Stats(wr: wr) } };
        var root = new JsonObject
        {
            ["version"] = "16.22.1", ["date"] = "2026-11-09",
            ["championData"] = new JsonObject
            {
                ["103"] = Champion("Ahri", "Ahri", Role.Middle), ["1"] = Champion("Annie", "Annie", Role.Middle),
                ["238"] = Champion("Zed", "Zed", Role.Middle), ["99"] = Champion("Lux", "Lux", Role.Support)
            }
        };
        void Pair(int id, Role role, int other, Role otherRole, string kind, double wr, int games = 1000)
        {
            var table = root["championData"]![id.ToString()]!["statsByRole"]![((int)role).ToString()]![kind]!.AsObject();
            var key = ((int)otherRole).ToString(); table[key] ??= new JsonObject();
            table[key]![other.ToString()] = new JsonObject { ["games"] = games, ["wins"] = games * wr };
        }
        Pair(103, Role.Middle, 1, Role.Middle, "matchup", .6);
        Pair(1, Role.Middle, 103, Role.Middle, "matchup", .4);
        Pair(103, Role.Middle, 238, Role.Middle, "matchup", .4);
        Pair(238, Role.Middle, 103, Role.Middle, "matchup", .6);
        Pair(103, Role.Middle, 99, Role.Support, "synergy", .6);
        Pair(99, Role.Support, 103, Role.Middle, "synergy", .6);
        using var data = new Dataset(root.ToJsonString());
        TeamComposition Team(params (Role Role, int Id)[] picks) => new(picks.ToDictionary(p => p.Role, p => p.Id), []);
        var analyzer = new DraftAnalyzer(data, data);
        check(analyzer.Analyze(Team(), Team()).Winrate == .5, "Draft vacío produce estimación neutral");
        var favorable = analyzer.Analyze(Team((Role.Middle, 103)), Team((Role.Middle, 1)));
        check(Math.Abs(favorable.Winrate - .55) < 1e-12 && favorable.MatchupPairs == 1, "Cruce 60% con 1000 partidas y prior neutral reproduce 55% de DraftGap");
        var inverse = analyzer.Analyze(Team((Role.Middle, 1)), Team((Role.Middle, 103)));
        check(Math.Abs(inverse.Winrate + favorable.Winrate - 1) < 1e-12, "Intercambiar equipos invierte la estimación del matchup");
        var synergy = analyzer.Analyze(Team((Role.Middle, 103), (Role.Support, 99)), Team());
        check(Math.Abs(synergy.Winrate - .55) < 1e-12 && synergy.SynergyPairs == 1, "Sinergia aliada modifica el winrate de la composición");
        var enemySynergy = analyzer.Analyze(Team(), Team((Role.Middle, 103), (Role.Support, 99)));
        check(Math.Abs(enemySynergy.Winrate - .45) < 1e-12, "Sinergia rival reduce la estimación aliada");
        var draft = new DraftSession
        {
            LocalPlayerCellId = 2, MyTeam = [new() { CellId = 2, AssignedPosition = "middle" }],
            TheirTeam = [new() { CellId = 5, ChampionId = 1 }]
        };
        var first = data.Suggest(Role.Middle, draft, data, [103], false).Single();
        check(Math.Abs(first.DraftWinrate!.Value - .55) < 1e-12 && first.DraftAnalysis!.InferredRoles == 1, "Rival sin posición en LCU entra al cálculo mediante inferencia de roles");
        draft.TheirTeam[0].ChampionId = 238;
        var second = data.Suggest(Role.Middle, draft, data, [103], false).Single();
        check(Math.Abs(second.DraftWinrate!.Value - .45) < 1e-12 && second.BaseWinrate == first.BaseWinrate, "Nuevo pick cambia WR draft mientras WR base del parche permanece fijo");
        draft.TheirTeam.Clear();
        var without = data.Suggest(Role.Middle, draft, data, [103], false).Single();
        check(without.UsesGlobalConfidence && without.DraftWinrate == null && without.BaseWinrate == .5, "Retirar el último pick restaura confianza global sin arrastrar caché del draft");
        draft.MyTeam.Add(new() { CellId = 3, ChampionId = 99, AssignedPosition = "utility" });
        var withAlly = data.Suggest(Role.Middle, draft, data, [103], false).Single();
        check(Math.Abs(withAlly.DraftWinrate!.Value - .55) < 1e-12, "Nuevo aliado actualiza WR draft por sinergia");
        draft.MyTeam[0].ChampionId = 238;
        check(data.Suggest(Role.Middle, draft, data, [103], false).Single().DraftAnalysis!.AllyPicks == 2, "Candidato reemplaza preselección local sin duplicar su contribución");
        check(data.Suggest(Role.Middle, draft, null, [103], false).Single().DraftWinrate == null, "Sin dataset de contexto no se inventa un winrate contextual");
        check(data.Suggest(Role.Middle, null, data, [103]).Single().DraftWinrate == null, "Fuera de selección no se presenta una composición ficticia");
        var comp = DraftRoleInference.Infer(data, data, draft, Role.Middle);
        check(!comp.Allies.Picks.ContainsKey(Role.Middle) && comp.Allies.Picks[Role.Support] == 99, "Inferencia reserva la posición del candidato y conserva la del aliado");
        check(Math.Abs(DraftAnalyzer.ToWinrate(DraftAnalyzer.ToRating(.63)) - .63) < 1e-12, "Conversión rating/winrate coincide con transformación de DraftGap");

        var oneSide = root.DeepClone();
        oneSide["championData"]!["1"]!["statsByRole"]!["2"]!["matchup"] = new JsonObject();
        using var oneSideData = new Dataset(oneSide.ToJsonString());
        var oneSideWr = new DraftAnalyzer(oneSideData, oneSideData).Analyze(Team((Role.Middle, 103)), Team((Role.Middle, 1))).Winrate;
        check(Math.Abs(oneSideWr - 8d / 15) < 1e-12, "Datos direccionales incompletos mantienen el promedio y prior del modelo original");

        var weak = root.DeepClone();
        weak["championData"]!["103"]!["statsByRole"]!["2"]!["matchup"]!["2"]!["1"] = new JsonObject { ["games"] = 100, ["wins"] = 52 };
        weak["championData"]!["1"]!["statsByRole"]!["2"]!["matchup"]!["2"]!["103"] = new JsonObject { ["games"] = 100, ["wins"] = 48 };
        using var weakData = new Dataset(weak.ToJsonString());
        draft.MyTeam = [new() { CellId = 2, AssignedPosition = "middle" }]; draft.TheirTeam = [new() { CellId = 5, ChampionId = 1 }];
        var weakSuggestion = weakData.Suggest(Role.Middle, draft, weakData, [103], false).Single();
        check(weakSuggestion.DraftWinrate > .5 && weakSuggestion.Score < weakSuggestion.DraftWinrate * 100, "WR draft y confianza responden al contexto con margen de incertidumbre");

        // Expected results were generated by executing the original TypeScript functions
        // against real DraftGap datasets, then trimming only unrelated champion/pair data.
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DraftGapParity.json")));
        using var referenceData = new Dataset(fixture.RootElement.GetProperty("data").GetRawText());
        using var referenceContext = new Dataset(fixture.RootElement.GetProperty("context").GetRawText());
        TeamComposition ReadTeam(JsonElement element) => new(element.EnumerateObject().ToDictionary(p => (Role)int.Parse(p.Name), p => p.Value.GetInt32()), []);
        var referenceAnalyzer = new DraftAnalyzer(referenceData, referenceContext);
        foreach (var test in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var result = referenceAnalyzer.Analyze(ReadTeam(test.GetProperty("allies")), ReadTeam(test.GetProperty("enemies")));
            check(Math.Abs(result.Winrate - test.GetProperty("expectedWinrate").GetDouble()) < 1e-10, "Paridad con TypeScript original y datos reales · " + test.GetProperty("name").GetString());
        }
        var inference = fixture.RootElement.GetProperty("inference");
        var inferredDraft = new DraftSession { MyTeam = inference.GetProperty("championIds").EnumerateArray().Select((id, i) => new Player { CellId = i, ChampionId = id.GetInt32() }).ToList() };
        var inferred = DraftRoleInference.Infer(referenceData, referenceContext, inferredDraft).Allies;
        var expectedRoles = inference.GetProperty("expectedRoles").EnumerateObject().ToDictionary(p => (Role)int.Parse(p.Name), p => int.Parse(p.Value.GetString()!));
        check(inferred.Picks.Count == 5 && inferred.Picks.All(p => expectedRoles[p.Key] == p.Value), "Inferencia conjunta de cinco roles coincide con DraftGap original");
        check(inferred.InferredRoles.Count == 5 && inferred.Picks.Keys.Distinct().Count() == 5, "Predicción evita asignar dos campeones a la misma posición");
    }
}
