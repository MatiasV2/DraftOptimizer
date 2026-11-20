using DraftOptimizer.Core;

static class AutoDraftTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        DraftTimer Timer(double remaining) => new() { AdjustedTimeLeftInPhase = remaining, InternalNowInEpochMs = now.ToUnixTimeMilliseconds(), Phase = "BAN_PICK" };
        check(Timer(2000).RemainingMilliseconds(now.AddMilliseconds(1200)) == 800, "Reloj descuenta tiempo transcurrido desde la marca del cliente");
        check(Timer(1000).RemainingMilliseconds(now.AddSeconds(2)) == 0, "Reloj expirado se mantiene en cero");
        check(new DraftTimer().RemainingMilliseconds(now) == null, "Temporizador ausente no arma confirmación");
        var infinite = Timer(500); infinite.IsInfinite = true;
        check(infinite.RemainingMilliseconds(now) == null, "Temporizador infinito no arma confirmación");
        var future = Timer(500); future.InternalNowInEpochMs += 10000;
        check(future.RemainingMilliseconds(now) == null, "Reloj incompatible no arma confirmación");

        using var data = new Dataset("""
        {"version":"16.22.1","date":"2026-11-09","championData":{
          "103":{"id":"Ahri","name":"Ahri","statsByRole":{"2":{"games":10000,"wins":5500,"matchup":{"2":{"1":{"games":5000,"wins":2250}}},"synergy":{}}}},
          "238":{"id":"Zed","name":"Zed","statsByRole":{"2":{"games":10000,"wins":5200,"matchup":{"2":{"1":{"games":5000,"wins":3500}}},"synergy":{}}}}
        }}
        """);
        DraftSession Draft(double? ms = 20000) => new()
        {
            GameId = 100, LocalPlayerCellId = 2, Timer = ms.HasValue ? Timer(ms.Value) : null,
            MyTeam = [new() { CellId = 2, AssignedPosition = "middle" }], TheirTeam = [new() { CellId = 5, ChampionId = 1, AssignedPosition = "middle" }],
            Actions = [[new() { Id = 9, ActorCellId = 2, Type = "pick", IsInProgress = true }]]
        };
        var settings = new FavoriteSettings { Favorites = new() { [Role.Middle] = [103, 238], [Role.Top] = [80] } };
        check(settings.AutoHoverEnabled && !settings.AutoLockEnabled, "Preselección disponible y confirmación automática inicialmente desactivada");
        var controller = new AutoDraftController(); var draft = Draft();
        var writes = new List<AutomaticPickRequest>();
        Task<bool> Send(AutomaticPickRequest request)
        {
            writes.Add(request); draft.Actions[0][0].ChampionId = request.ChampionId;
            return Task.FromResult(true);
        }
        Task Tick() => controller.TickAsync(data, data, draft, [103, 238], settings, Role.Middle, now, Send);
        await Tick();
        check(writes.Count == 1 && writes[0].ChampionId == 238 && !writes[0].Complete, "Mejor favorito por matchup supera favorito con mayor winrate base");
        await Tick(); await Tick();
        check(writes.Count == 1, "No repite preselecciones en cada sondeo");
        draft.TheirTeam.Clear(); await Tick();
        check(writes.Count == 2 && writes.Last().ChampionId == 103, "Recalcula favorito cuando cambia la composición");
        draft.MyTeam[0].ChampionId = 103; await Tick();
        check(writes.Count == 2, "La preselección local no excluye al propio favorito ni provoca oscilación");
        settings.AutoLockEnabled = true;
        draft.Actions[0][0].ChampionId = 238; draft.Timer = Timer(500); await Tick();
        check(writes.Count == 2 && controller.Status.Contains("pausada"), "Cambio manual pausa preselección y confirmación del turno");
        draft = Draft(); draft.GameId = 101; await Tick();
        check(writes.Count == 3, "Nueva partida reinicia el estado aunque reutilice ID de acción");
        await Tick(); draft.Timer = Timer(1001); await Tick();
        check(writes.Count == 3, "No confirma antes del umbral de un segundo");
        draft.Timer = Timer(1000); await Tick(); await Tick();
        check(writes.Count == 4 && writes.Last().Complete && writes.Last().ChampionId == 238, "Confirma una sola vez al entrar al último segundo");
        controller.Reset(); draft = Draft(500); writes.Clear(); await Tick();
        check(writes.Single().Complete, "Si inicia en el último segundo confirma el mejor favorito en una petición");
        controller.Reset(); draft = Draft(500); writes.Clear(); settings.AutoLockEnabled = false; await Tick(); await Tick();
        check(writes.Count == 1 && !writes[0].Complete, "Opción desactivada nunca confirma automáticamente");
        controller.Reset(); draft = Draft(0); writes.Clear(); settings.AutoLockEnabled = true; await Tick();
        check(writes.Count == 0, "No selecciona con temporizador expirado");
        controller.Reset(); draft = Draft(null); await Tick();
        check(writes.Count == 1 && !writes[0].Complete, "Sin reloj válido permite preselección pero no confirmación");
        controller.Reset(); draft = Draft(); writes.Clear(); settings.AutoHoverEnabled = false; await Tick();
        check(writes.Count == 0, "Desactivar automatización bloquea ambas acciones");
        settings.AutoHoverEnabled = true;
        await controller.TickAsync(data, data, draft, [], settings, Role.Middle, now, Send);
        check(writes.Count == 0, "Favoritos no disponibles no causan selección fuera del conjunto");
        await controller.TickAsync(data, data, draft, [103, 238], settings, Role.Support, now, Send);
        check(writes.Count == 0, "No usa favoritos de una posición diferente");
        await controller.TickAsync(data, data, draft, [80], settings, Role.Top, now, Send);
        check(writes.Count == 0, "Favorito sin estadísticas suficientes no se selecciona a ciegas");
        controller.PauseCurrentTurn(draft); await Tick();
        check(writes.Count == 0, "Botón manual pausa incluso antes de la primera acción automática");
        controller.Reset(); draft = Draft(); draft.Actions[0][0].IsInProgress = false; await Tick();
        check(writes.Count == 0, "No actúa fuera del turno local");
        controller.Reset(); draft = Draft();
        await controller.TickAsync(data, data, draft, [103, 238], settings, Role.Middle, now, _ => Task.FromResult(false));
        await Tick(); check(writes.Count == 0, "Validación final rechazada pausa el turno sin reintentos repetidos");
        controller.Reset();
        try { await controller.TickAsync(data, data, draft, [103, 238], settings, Role.Middle, now, _ => throw new IOException("Simulado")); } catch (IOException) { }
        await Tick(); check(writes.Count == 0, "Error de envío no provoca duplicación de acciones");
        controller.Reset(); draft = Draft(); draft.Actions[0].Add(new() { Type = "ban", ChampionId = 238 }); await Tick();
        check(writes.Single().ChampionId == 103, "Un favorito baneado se excluye aunque figure en disponibilidad antigua");
        controller.Reset(); writes.Clear(); draft.Actions[0][0].Completed = true; await Tick();
        check(writes.Count == 0, "Pick ya completado no genera selección automática");
        var allyJson = System.Text.Json.Nodes.JsonNode.Parse(data.Root.GetRawText())!;
        allyJson["championData"]!["238"]!["statsByRole"]!["2"]!["synergy"] = System.Text.Json.Nodes.JsonNode.Parse("""{"4":{"99":{"games":5000,"wins":4500}}}""");
        using var allyData = new Dataset(allyJson.ToJsonString());
        var allyDraft = Draft(); allyDraft.TheirTeam.Clear(); allyDraft.MyTeam.Add(new() { CellId = 3, ChampionId = 99, AssignedPosition = "utility" });
        var allyWrites = new List<AutomaticPickRequest>();
        await new AutoDraftController().TickAsync(allyData, allyData, allyDraft, [103, 238], settings, Role.Middle, now, request => { allyWrites.Add(request); return Task.FromResult(true); });
        check(allyWrites.Single().ChampionId == 238, "Sinergia con aliado utility modifica la prioridad entre favoritos");

        var banHandler = new FakeLeague { RemainingMs = 20000, ChampionId = 0, ActionType = "ban", Available = [103, 238], AllyIntent = 103 };
        using var banLcu = new LcuClient(new HttpClient(banHandler) { BaseAddress = new Uri("https://127.0.0.1:29999/") });
        var favoritesFromClient = await banLcu.ReadFavoritesAsync();
        check(favoritesFromClient.Count == 2 && favoritesFromClient[Role.Middle].SetEquals([103, 238]) && favoritesFromClient[Role.Support].SetEquals([103]),
            "Importa favoritos por posición, incluida Utility, sin duplicados");
        var unavailableBan = await banLcu.AutomaticBanAsync(700, 9, Role.Middle, [99], _ => true);
        check(unavailableBan == null && banHandler.Writes.Count == 0 && banLcu.AutomaticBanStatus.Contains("League no permite banearlo"),
            "El estado identifica cuándo League descarta todos los campeones prioritarios");
        var banned = await banLcu.AutomaticBanAsync(700, 9, Role.Middle, [99, 103, 238], _ => true);
        check(banned == 238 && banHandler.Writes.Count == 2 &&
            banHandler.Writes[0].Method == "PATCH" && banHandler.Writes[0].Body["championId"]!.GetValue<int>() == 238 &&
            banHandler.Writes[1] is { Method: "POST", Path: "/lol-champ-select/v1/session/actions/9/complete" },
            "Al comenzar el turno selecciona y confirma el ban, protegiendo al aliado y omitiendo candidatos no disponibles");
        banHandler.OnPickable = () => banHandler.HoveredChampion = 103;
        banned = await banLcu.AutomaticBanAsync(700, 9, Role.Middle, [238], _ => true);
        check(banned == null && banHandler.Writes.Count == 2, "Respeta una selección manual posterior a la consulta de disponibilidad");
        banHandler.OnPickable = null;
        banHandler.HoveredChampion = 0;
        banHandler.RemainingMs = 0;
        banned = await banLcu.AutomaticBanAsync(700, 9, Role.Middle, [238], _ => true);
        check(banned == null && banHandler.Writes.Count == 2, "No envía un ban con el tiempo vencido");

        banHandler.RemainingMs = 4170;
        banHandler.OnBanSelection = () => banHandler.AllyIntent = 238;
        banned = await banLcu.AutomaticBanAsync(700, 9, Role.Middle, [238], _ => true);
        check(banned == null && banHandler.Writes.Count == 3 && banHandler.Writes.Last().Method == "PATCH",
            "Si un aliado preselecciona al campeón entre PATCH y POST, el ban no se confirma");
        banHandler.OnBanSelection = () => banHandler.HoveredChampion = 103;
        banHandler.HoveredChampion = 0;
        banHandler.AllyIntent = 0;
        banned = await banLcu.AutomaticBanAsync(700, 9, Role.Middle, [238], _ => true);
        check(banned == null && banHandler.Writes.Count == 4 && banHandler.Writes.Last().Method == "PATCH",
            "Una selección manual entre PATCH y POST no se confirma automáticamente");

        var handler = new FakeLeague { RemainingMs = 500 };
        using var lcu = new LcuClient(new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:29999/") });
        var pick = new AutomaticPickRequest(700, 9, 2, 103, 103, Role.Middle, true);
        check(await lcu.AutomaticPickAsync(pick, () => true) && handler.Writes.Single().Body["completed"]!.GetValue<bool>(), "LCU confirma favorito después de verificar turno y reloj frescos");
        async Task NoWrite(AutomaticPickRequest request, string name, Func<bool>? enabled = null)
        {
            int count = handler.Writes.Count;
            bool result = await lcu.AutomaticPickAsync(request, enabled ?? (() => true));
            check(!result && handler.Writes.Count == count, name);
        }
        await NoWrite(pick, "LCU respeta opción desactivada inmediatamente", () => false);
        handler.RemainingMs = 2000; await NoWrite(pick, "LCU rechaza confirmar antes de un segundo");
        handler.RemainingMs = 0; await NoWrite(pick, "LCU rechaza confirmar tras expirar");
        handler.RemainingMs = null; await NoWrite(pick, "LCU rechaza confirmar con reloj ausente");
        handler.RemainingMs = 500; handler.InfiniteTimer = true; await NoWrite(pick, "LCU rechaza reloj infinito"); handler.InfiniteTimer = false;
        handler.TimerPhase = "PLANNING"; await NoWrite(pick, "LCU rechaza fase de temporizador distinta de BAN_PICK"); handler.TimerPhase = "BAN_PICK";
        await NoWrite(pick with { GameId = 701 }, "LCU rechaza partida diferente");
        await NoWrite(pick with { ActionId = 10 }, "LCU rechaza acción diferente");
        await NoWrite(pick with { Role = Role.Top }, "LCU rechaza posición diferente");
        await NoWrite(pick with { ExpectedVisibleChampion = 238 }, "LCU rechaza campeón cambiado manualmente");
        handler.Available = []; await NoWrite(pick, "LCU rechaza favorito que dejó de estar disponible"); handler.Available = [103];
        handler.OnPickable = () => handler.ChampionId = 1;
        await NoWrite(pick, "Relectura final detecta cambio manual ocurrido durante la petición"); handler.ChampionId = 103;
        handler.OnPickable = () => handler.ActionId = 10;
        await NoWrite(pick, "Relectura final detecta cambio de turno durante la petición"); handler.ActionId = 9;
        handler.OnPickable = () => handler.RemainingMs = 0;
        await NoWrite(pick, "Relectura final evita confirmación si el reloj expiró durante la petición"); handler.RemainingMs = 500;
        bool enabled = true; handler.OnPickable = () => enabled = false;
        await NoWrite(pick, "Desactivar opción durante la petición cancela la escritura", () => enabled);
        handler.OnPickable = null;

        var directory = Path.Combine(Environment.CurrentDirectory, "work", "favorites-test-" + Guid.NewGuid());
        var store = new FavoriteSettingsStore(directory);
        await store.SaveAsync(settings); var restored = await store.LoadAsync();
        check(restored.ForRole(Role.Middle).SetEquals([103, 238]) && restored.ForRole(Role.Top).SetEquals([80]) && restored.AutoLockEnabled, "Persiste favoritos por posición y opciones de automatización");
        await store.SaveAsync(settings);
        check(File.Exists(store.PathName + ".bak"), "Configuración de favoritos conserva respaldo");
        await File.WriteAllTextAsync(store.PathName, "invalid");
        try { await store.LoadAsync(); throw new Exception("Se aceptó configuración dañada"); }
        catch (System.Text.Json.JsonException) { check(await File.ReadAllTextAsync(store.PathName) == "invalid", "Favoritos corruptos no se sobrescriben silenciosamente"); }
    }
}
