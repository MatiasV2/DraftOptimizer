using DraftOptimizer;
using DraftOptimizer.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Nodes;

static class WorkflowTests
{
    public static async Task RunAsync(Action<bool, string> check, Dataset data, BuildProfile valid)
    {
        var a = new ChampionListItem(new(103, "Ahri", "Ahri")); var b = new ChampionListItem(new(80, "Pantheon", "Pantheon"));
        var rows = new ObservableCollection<ChampionListItem> { a, b }; int resets = 0, replacements = 0, moves = 0;
        rows.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; if (e.Action == NotifyCollectionChangedAction.Replace) replacements++; if (e.Action == NotifyCollectionChangedAction.Move) moves++; };
        int changes = 0; a.PropertyChanged += (_, _) => changes++;
        a.Update(a.Champion, Role.Middle, false, true, null, true); int originalChanges = changes;
        a.Update(a.Champion, Role.Middle, false, true, null, true);
        check(changes == originalChanges, "Sondeo sin cambios no notifica ni redibuja filas");
        a.Update(a.Champion, Role.Middle, true, true, null, true);
        IncrementalCollection.Synchronize(rows, [b, a]);
        check(ReferenceEquals(rows[1], a) && resets == 0 && replacements == 0 && moves == 1, "Reordenación mueve filas existentes sin reemplazar ItemsSource ni emitir Reset");
        IncrementalCollection.Synchronize(rows, [a]); IncrementalCollection.Synchronize(rows, [b, a]);
        check(ReferenceEquals(rows[1], a) && resets == 0, "Filtrar y restaurar conserva la identidad de los campeones");

        DraftSession Draft(bool final = false, int champion = 103) => new()
        {
            GameId = 700, LocalPlayerCellId = 2, Timer = new() { Phase = final ? "FINALIZATION" : "BAN_PICK" },
            MyTeam = [new() { CellId = 2, ChampionId = champion, AssignedPosition = "middle" }],
            Actions = [[new() { Id = 9, ActorCellId = 2, Type = "pick", ChampionId = champion, Completed = true }]]
        };
        var personal = Json.Read<BuildProfile>(Json.Write(valid)); personal.Role = Role.Middle;
        int fetches = 0, applies = 0;
        Task<BuildProfile> Fetch(Champion c, Role r, string patch, bool isAram, CancellationToken ct) { fetches++; return Task.FromResult(Json.Read<BuildProfile>(Json.Write(personal))); }
        Task Apply(BuildProfile p, FinalLoadoutTarget t, CancellationToken ct) { applies++; return Task.CompletedTask; }
        using var loadout = new LoadoutCoordinator();
        var unconfirmed = Draft(); unconfirmed.Actions[0][0].Completed = false;
        loadout.Observe(new("ChampSelect", unconfirmed), data, Role.Middle, null, true, Fetch, Apply); await loadout.Pending;
        check(fetches == 0 && applies == 0, "Preselección sin confirmar no prepara ni aplica la configuración de otro campeón");
        loadout.Observe(new("ChampSelect", Draft()), data, Role.Middle, personal, true, Fetch, Apply); await loadout.Pending;
        check(fetches == 0 && applies == 1 && loadout.Prepared != null, "Perfil personal se aplica al confirmar sin esperar al cierre del draft");
        personal.UpdatedAt = DateTimeOffset.UtcNow;
        loadout.Observe(new("ChampSelect", Draft(true)), data, Role.Middle, personal, true, Fetch, Apply); await loadout.Pending;
        loadout.Observe(new("ChampSelect", Draft(true)), data, Role.Middle, personal, true, Fetch, Apply); await loadout.Pending;
        check(applies == 1 && fetches == 0, "Finalización aplica una sola vez aunque cambie UpdatedAt del editor");
        loadout.Reset(); applies = fetches = 0;
        loadout.Observe(new("ChampSelect", Draft()), data, Role.Middle, null, true, Fetch, Apply); await loadout.Pending;
        var automaticId = loadout.Prepared!.Id;
        check(fetches == 1 && applies == 1, "Sin perfil se prepara y aplica Diamante+ al campeón confirmado");
        loadout.Observe(new("ChampSelect", Draft(true)), data, Role.Middle, null, true, Fetch, Apply); await loadout.Pending;
        check(applies == 1 && loadout.Status.Contains("aplicados"), "Fallback preparado se aplica al finalizar el draft");
        loadout.Observe(new("ChampSelect", Draft(true)), data, Role.Middle, loadout.Prepared, true, Fetch, Apply); await loadout.Pending;
        check(applies == 1 && fetches == 1, "Ver el perfil preparado en el editor no provoca otra importación");
        loadout.Reset();
        loadout.Observe(new("ChampSelect", Draft()), data, Role.Middle, null, true, Fetch, Apply); await loadout.Pending;
        check(loadout.Prepared!.Id == automaticId, "Fallback tiene identidad estable para no duplicar conjuntos entre partidas");
        loadout.Reset(); var itemsOnly = Json.Read<BuildProfile>(Json.Write(personal)); itemsOnly.Perks = [];
        itemsOnly.Spell1Id = itemsOnly.Spell2Id = 0;
        personal.Spell1Id = 4; personal.Spell2Id = 14;
        var originalItems = itemsOnly.Blocks.SelectMany(x => x.Items).ToArray();
        loadout.Observe(new("ChampSelect", Draft()), data, Role.Middle, itemsOnly, true, Fetch, Apply); await loadout.Pending;
        check(loadout.Prepared!.Perks.Length == 9 && loadout.Prepared.Blocks.SelectMany(x => x.Items).SequenceEqual(originalItems), "Perfil solo de objetos conserva sus compras y completa runas desde Diamante+");
        check(loadout.Prepared!.SpellsFor(FlashKey.F) == (14, 4) && itemsOnly.Spell1Id == 0,
            "La búsqueda que completa las runas también trae hechizos para un perfil de objetos");
        loadout.Reset();
        itemsOnly.Spell1Id = 4; itemsOnly.Spell2Id = 12;
        loadout.Observe(new("ChampSelect", Draft()), data, Role.Middle, itemsOnly, true, Fetch, Apply); await loadout.Pending;
        check(loadout.Prepared!.SpellsFor(FlashKey.Profile) == (4, 12), "Los hechizos del perfil personal tienen prioridad sobre los de Lolalytics");
        loadout.Reset(); int before = applies;
        loadout.Observe(new("ChampSelect", Draft(true)), data, Role.Middle, personal, false, Fetch, Apply); await loadout.Pending;
        check(applies == before, "Desactivar aplicación automática evita escrituras al finalizar");
        var deferred = new TaskCompletionSource<BuildProfile>();
        loadout.Observe(new("ChampSelect", Draft()), data, Role.Middle, null, true, (_, _, _, _, _) => deferred.Task, Apply);
        loadout.Observe(new("GameStart", null), data, Role.Middle, null, true, Fetch, Apply);
        deferred.SetResult(personal); await Task.Yield();
        check(applies == before && loadout.Prepared == null, "Respuesta tardía del proveedor no aplica tras abandonar la selección");
        loadout.Reset();
        loadout.Observe(new("ChampSelect", Draft(true)), data, Role.Middle, null, true, (_, _, _, _, _) => throw new IOException("Proveedor no disponible"), Apply);
        await loadout.Pending;
        check(applies == before && loadout.Status.Contains("No se pudo") && loadout.Error == "Proveedor no disponible" && loadout.FailureId > 0, "Fallo de proveedor expone el motivo y un identificador para mostrar una sola alerta");
        loadout.Reset();
        var wrong = Json.Read<BuildProfile>(Json.Write(personal)); wrong.ChampionId = 80;
        loadout.Observe(new("ChampSelect", Draft(true)), data, Role.Middle, null, true, (_, _, _, _, _) => Task.FromResult(wrong), Apply); await loadout.Pending;
        check(applies == before && loadout.Prepared == null && loadout.Status.Contains("No se pudo"), "Fallback de otro campeón se rechaza antes de aplicar runas u objetos");
        var completed = Draft(); completed.TheirTeam.Add(new() { CellId = 5, ChampionId = 80 }); completed.Actions[0].Add(new() { ActorCellId = 5, Type = "pick", ChampionId = 80, Completed = true });
        check(completed.IsFinalization, "Todos los picks completados identifican el cierre aunque el temporizador aún no cambie");

        var lateHandler = new FakeLeague { Completed = true, InProgress = false, ChampionId = 103 };
        using var lateLcu = new LcuClient(new HttpClient(lateHandler) { BaseAddress = new Uri("https://127.0.0.1:29999/") });
        using var lateController = new LoadoutCoordinator();
        var lateBuild = Json.Read<BuildProfile>(Json.Write(personal));
        lateBuild.Spell1Id = 4; lateBuild.Spell2Id = 14;
        var lateFetch = new TaskCompletionSource<BuildProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ApplyLate(BuildProfile profile, FinalLoadoutTarget target, CancellationToken ct)
        {
            await lateLcu.ImportRunesAsync(profile, data, ct, target);
            await lateLcu.ImportItemsAsync(profile, data, ct, target);
            await lateLcu.ImportSummonerSpellsAsync(profile, data, ct, target, FlashKey.F);
        }
        lateController.Observe(new("ChampSelect", Draft()), data, Role.Middle, null, true,
            (_, _, _, _, _) => lateFetch.Task, ApplyLate);
        var pendingLateImport = lateController.Pending;
        lateFetch.SetResult(lateBuild);
        await pendingLateImport;
        check(lateHandler.Writes.Count == 3 &&
            lateHandler.Writes.Any(write => write.Path.EndsWith("/pages/42")) &&
            lateHandler.Writes.Any(write => write.Path.EndsWith("/sets")) &&
            lateHandler.Writes.Any(write => write.Path.EndsWith("/my-selection") && write.Body["spell2Id"]!.GetValue<int>() == 4),
            "Abrir después del lock-in aplica runas, objetos y hechizos al terminar la descarga sin esperar otro sondeo");
        lateController.Observe(new("ChampSelect", Draft()), data, Role.Middle, null, true,
            (_, _, _, _, _) => throw new Exception("Descarga duplicada"), ApplyLate);
        await lateController.Pending;
        check(lateHandler.Writes.Count == 3, "El sondeo posterior al arranque no repite la importación ya completada");
        lateController.Reset();
        lateController.Observe(new("ChampSelect", Draft(true)), data, Role.Middle, lateBuild, true,
            (_, _, _, _, _) => throw new Exception("Perfil personal ignorado"), ApplyLate);
        await lateController.Pending;
        check(lateHandler.Writes.Count == 6, "Abrir durante finalización vuelve a importar el perfil personal de la partida");

        var handler = new FakeLeague { RemainingMs = 3000, TimerPhase = "FINALIZATION", Completed = true };
        using var lcu = new LcuClient(new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:29999/") });
        var target = new FinalLoadoutTarget(700, 103, Role.Middle);
        await lcu.ImportRunesAsync(personal, data, target: target); await lcu.ImportItemsAsync(personal, data, target: target);
        check(handler.Writes.Count == 2, "LCU permite runas y objetos durante FINALIZATION del campeón correcto");
        int writes = handler.Writes.Count; handler.GameId = 701;
        try { await lcu.ImportRunesAsync(personal, data, target: target); } catch (InvalidOperationException) { }
        check(handler.Writes.Count == writes, "LCU cancela carga automática si cambió la partida");
        handler.GameId = 700; handler.TimerPhase = "BAN_PICK";
        try { await lcu.ImportItemsAsync(personal, data, target: target); } catch (InvalidOperationException) { }
        check(handler.Writes.Count == writes, "LCU no aplica anticipadamente antes de cerrar el draft");
        handler.TimerPhase = "FINALIZATION"; handler.OnPages = () => handler.Phase = "GameStart";
        try { await lcu.ImportRunesAsync(personal, data, target: target); } catch (InvalidOperationException) { }
        check(handler.Writes.Count == writes, "Revalidación justo antes de escribir cancela importación si comenzó la partida");
        check(LolalyticsProvider.PageUrl(new(103, "Ahri", "Ahri"), Role.Middle, data.Patch).Contains("tier=diamond_plus"), "Proveedor manual y automático solicitan Diamante+ explícitamente");

        JsonObject Stats(int n, double p) => new() { ["games"] = n, ["wins"] = n * p, ["matchup"] = new JsonObject(), ["synergy"] = new JsonObject() };
        Dataset Evidence(int pairs, double p) => new(new JsonObject { ["version"] = "16.22", ["date"] = "test", ["championData"] = new JsonObject
        {
            ["103"] = new JsonObject { ["id"] = "Ahri", ["name"] = "Ahri", ["statsByRole"] = new JsonObject { ["2"] = Stats(10000, .5) } },
            ["238"] = new JsonObject { ["id"] = "Zed", ["name"] = "Zed", ["statsByRole"] = new JsonObject { ["2"] = Stats(10000, .5) } }
        }}.ToJsonString());
        // Pair count and outcomes are set on independent datasets below.
        Dataset Context(int n, double winrate)
        {
            using var initial = Evidence(n, winrate); var root = JsonNode.Parse(initial.Root.GetRawText())!;
            root["championData"]!["103"]!["statsByRole"]!["2"]!["matchup"] = new JsonObject { ["2"] = new JsonObject { ["238"] = new JsonObject { ["games"] = n, ["wins"] = n * winrate } } };
            root["championData"]!["238"]!["statsByRole"]!["2"]!["matchup"] = new JsonObject { ["2"] = new JsonObject { ["103"] = new JsonObject { ["games"] = n, ["wins"] = n * (1 - winrate) } } };
            return new(root.ToJsonString());
        }
        using var weak = Context(200, .5); using var strong = Context(20000, .5); using var favorable = Context(20000, .7);
        var composition = new DraftSession { LocalPlayerCellId = 2, TheirTeam = [new() { CellId = 5, ChampionId = 238, AssignedPosition = "middle" }] };
        var weakPick = weak.Suggest(Role.Middle, composition, weak, [103], false).Single();
        var strongPick = strong.Suggest(Role.Middle, composition, strong, [103], false).Single();
        var favorablePick = favorable.Suggest(Role.Middle, composition, favorable, [103], false).Single();
        check(strongPick.Score > weakPick.Score && Math.Abs(strongPick.DraftWinrate!.Value - weakPick.DraftWinrate!.Value) < 1e-12, "Mismo WR del draft con más evidencia contextual obtiene más confianza");
        check(favorablePick.Score > strongPick.Score && favorablePick.BaseWinrate == strongPick.BaseWinrate, "Mismo WR base produce distinta confianza al cambiar el matchup del draft");
        check(favorablePick.Score == favorablePick.ConfidenceInterval.Lower * 100 && favorablePick.Score < favorablePick.DraftWinrate * 100, "Puntaje es el límite conservador del draft completo y no del winrate base");
    }
}
