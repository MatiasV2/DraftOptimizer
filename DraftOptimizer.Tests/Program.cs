using DraftOptimizer.Core;
using System.Text.Json.Nodes;

int passed = 0, failed = 0;
void Check(bool condition, string name) { if (!condition) { Console.WriteLine("FAIL: " + name); failed++; return; } Console.WriteLine("PASS: " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
using var data = new Dataset(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Data", "current-patch.json")));
Check(data.Champions.Count > 160, "Catálogo de campeones real");
var profile = new BuildProfile { ChampionId = 103, Perks = [8112, 8126, 8138, 8106, 8226, 8210, 5008, 5008, 5001], Blocks = [new() { Name = "Inicio", Items = [1056, 2003, 2003] }] };
// Select primary runes from the fixture to remain independent of perk removals.
profile.Perks = Enumerable.Range(0, 4).Select(i => data.Runes(8100, i).First().Id).Concat(new[] { 8226, 8210, 5008, 5008, 5001 }).ToArray();
data.Validate(profile); Check(true, "Perfil válido con tres fragmentos y cantidades repetidas");
var clone = Json.Read<BuildProfile>(Json.Write(profile)); clone.SubStyleId = clone.PrimaryStyleId;
Reject(() => data.Validate(clone), "Rechaza dos ramas iguales");
clone = Json.Read<BuildProfile>(Json.Write(profile)); clone.Perks[5] = clone.Perks[4];
Reject(() => data.Validate(clone), "Rechaza runas secundarias repetidas");
clone = Json.Read<BuildProfile>(Json.Write(profile)); clone.Perks[6] = 5013;
Reject(() => data.Validate(clone), "Rechaza fragmentos en posición incorrecta");
clone = Json.Read<BuildProfile>(Json.Write(profile)); clone.Blocks[0].Items = [int.MaxValue];
Reject(() => data.Validate(clone), "Rechaza objetos inexistentes");
var auth = LcuCredentials.ParseLockfile("LeagueClient:1234:54321:test-password:https");
Check(auth.Port == 54321 && auth.Password == "test-password" && !auth.ToString().Contains("test-password"), "Lockfile y credenciales ocultas en representación");
Reject(() => LcuCredentials.ParseLockfile("LeagueClient:1234:0:secret:https"), "Rechaza puertos inválidos");
Reject(() => LcuCredentials.ParseLockfile("LeagueClient:1234:12345:secret:http"), "Rechaza protocolo sin TLS");
var draft = Json.Read<DraftSession>("""
{"localPlayerCellId":2,"myTeam":[{"cellId":2,"championId":103}],"theirTeam":[{"cellId":5,"championId":1}],"actions":[[{"id":9,"actorCellId":2,"type":"pick","completed":false,"isInProgress":true},{"id":10,"type":"ban","championId":238}]]}
""");
Check(draft.CurrentPick?.Id == 9 && draft.Unavailable().SetEquals([103, 1, 238]), "Selecciona acción local y excluye picks/bans");
Check(data.Suggest(Role.Middle, draft, null).All(s => !draft.Unavailable().Contains(s.Champion.Id)), "Sugerencias excluyen campeones ocupados");
Check(data.Suggest(Role.Middle, null, null, [103]) is { Count: 1 } available && available[0].Champion.Id == 103, "Sugerencias respetan disponibilidad de la cuenta");
draft.Actions[0][0].Completed = true; Check(draft.CurrentPick == null, "No reutiliza una selección completada");
var summary = JsonNode.Parse("""
{"pick":{"runes":{"page":{"pri":8100,"sec":8200},"set":{"pri":[8112,8126,8138,8106],"sec":[8226,8210],"mod":[5008,5008,5001]}},"items":{"start":{"set":[1056,2003,2003]},"core":{"set":[6655,3020,3089]},"item4":[{"id":3157}],"item5":[],"item6":[]}}}
""")!;
var built = LolalyticsProvider.ParseSummary(summary, 103, Role.Middle, "16.22");
Check(built.Perks.Length == 9 && built.Blocks[0].Items.Count(x => x == 2003) == 2 && built.Blocks[1].Items.SequenceEqual([6655, 3020, 3089]), "Parser conserva orden de compra y cantidades");
Reject(() => LolalyticsProvider.ParsePage("<html>challenge</html>", 103, Role.Middle, "16.22"), "No inventa builds ante bloqueo del proveedor");
Check(Roles.TryParse("utility", out var support) && support == Role.Support, "Convierte utility de LCU a Support");
using var contextData = new Dataset("""
{"version":"16.22.1","date":"2026-11-08","championData":{"103":{"id":"Ahri","name":"Ahri","statsByRole":{"2":{"games":10000,"wins":5000,"matchup":{"2":{"1":{"games":5000,"wins":3000}}},"synergy":{}}}}}}
""");
var contextDraft = new DraftSession { TheirTeam = [new() { ChampionId = 1, AssignedPosition = "middle" }] };
var baseline = contextData.Suggest(Role.Middle, null, null).Single();
var adjusted = contextData.Suggest(Role.Middle, contextDraft, contextData).Single();
Check(baseline.UsesGlobalConfidence && adjusted.HasDraftConfidence && adjusted.DraftWinrate > baseline.BaseWinrate && adjusted.ContextSamples == 1, "Primer rival cambia confianza global por análisis contextual con matchup favorable");
// Minimal Qwik graph exercises reference decoding, index-style mapping and identity checks.
var qwikObjects = new JsonArray();
string Base36(int n) { const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz"; return n < 36 ? alphabet[n].ToString() : Base36(n / 36) + alphabet[n % 36]; }
string Encode(JsonNode? node)
{
    var id = qwikObjects.Count; qwikObjects.Add((JsonNode?)null);
    if (node is JsonObject obj) { var target = new JsonObject(); foreach (var pair in obj) target[pair.Key] = Encode(pair.Value); qwikObjects[id] = target; }
    else if (node is JsonArray arr) { var target = new JsonArray(); foreach (var child in arr) target.Add(Encode(child)); qwikObjects[id] = target; }
    else qwikObjects[id] = node?.DeepClone();
    return Base36(id);
}
summary["pick"]!["runes"]!["page"]!["pri"] = 1; summary["pick"]!["runes"]!["page"]!["sec"] = 2;
summary["pick"]!["runes"]!["wr"] = 51.5;
summary["pick"]!["runes"]!["n"] = 1200;
summary["win"] = summary["pick"]!.DeepClone();
summary["win"]!["runes"]!["wr"] = 54.0;
summary["win"]!["runes"]!["n"] = 400;
Encode(new JsonObject { ["analysed"] = 10000, ["enemy"] = new JsonObject(), ["summary"] = summary.DeepClone(), ["header"] = new JsonObject { ["cid"] = 103, ["lane"] = "middle", ["patch"] = "16.22" } });
var fixture = "<script type=\"qwik/json\">" + new JsonObject { ["objs"] = qwikObjects }.ToJsonString() + "</script>";
var decoded = LolalyticsProvider.ParsePage(fixture, 103, Role.Middle, "16.22.1");
Check(decoded.PrimaryStyleId == 8100 && decoded.SubStyleId == 8200 && decoded.Perks.Length == 9, "Decodifica Qwik y convierte ramas indexadas a IDs de League");
Reject(() => LolalyticsProvider.ParsePage(fixture, 1, Role.Middle, "16.22"), "Rechaza build con campeón diferente");
Reject(() => LolalyticsProvider.ParsePage(fixture, 103, Role.Top, "16.22"), "Rechaza build con posición diferente");
Reject(() => LolalyticsProvider.ParsePage(fixture, 103, Role.Middle, "16.23"), "Rechaza build con parche diferente");
var handler = new FakeLeague();
using var lcu = new LcuClient(new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:29999/") });
await lcu.PickAsync(103, false);
Check(handler.Writes.Last().Path.EndsWith("/actions/9") && handler.Writes.Last().Body["completed"]!.GetValue<bool>() == false, "Preselección usa la acción local sin confirmar");
await lcu.PickAsync(103, true);
Check(handler.Writes.Last().Body["completed"]!.GetValue<bool>(), "Confirmación envía completed=true");
var writeCount = handler.Writes.Count;
try { await lcu.PickAsync(238, true); throw new Exception("Campeón no disponible aceptado"); } catch (InvalidOperationException) { }
Check(handler.Writes.Count == writeCount, "Campeón no disponible no genera escritura");
handler.InProgress = false;
try { await lcu.PickAsync(103, true); throw new Exception("Turno ajeno aceptado"); } catch (InvalidOperationException) { }
Check(handler.Writes.Count == writeCount, "Fuera de turno no genera escritura");
handler.InProgress = true;
await lcu.ImportRunesAsync(profile, data);
Check(handler.Writes.Last().Method == "PUT" && handler.Writes.Last().Path.EndsWith("/pages/42") && handler.Writes.Last().Body["selectedPerkIds"]!.AsArray().Count == 9, "Actualiza exclusivamente la página DraftOptimizer");
await lcu.ImportItemsAsync(profile, data);
var itemDocument = handler.Writes.Last().Body;
Check(itemDocument["itemSets"]!.AsArray().Count == 2 && itemDocument["itemSets"]![0]!["uid"]!.GetValue<string>() == "other-app" && itemDocument["timestamp"]!.GetValue<int>() == 123, "Importación conserva otros conjuntos y metadatos");
Check(itemDocument["itemSets"]![1]!["blocks"]![0]!["items"]![1]!["count"]!.GetValue<int>() == 2, "Conjunto de League agrupa las dos pociones");
await lcu.ImportItemsAsync(profile, data);
Check(handler.Writes.Last().Body["itemSets"]!.AsArray().Count == 2, "Reimportar el perfil no duplica conjuntos");
handler.ChampionId = 1;
writeCount = handler.Writes.Count;
try { await lcu.ImportRunesAsync(profile, data); throw new Exception("Perfil de otro campeón aceptado"); } catch (InvalidOperationException) { }
Check(handler.Writes.Count == writeCount, "Bloquea perfil de otro campeón sin escribir");
var pantheonText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PantheonSR.json"));
var pantheon = BuildImporter.Parse(pantheonText, data, Role.Top).Single();
Check(pantheon.Name == "Pantheon" && pantheon.ChampionId == 80 && pantheon.Role == Role.Top && pantheon.AssociatedMaps.SequenceEqual([11]), "Importa archivo real PantheonSR con campeón, mapa y posición elegida");
Check(pantheon.Blocks.Select(x => x.Name).SequenceEqual(["Core", "Finales", "Situacional", "Botas"]) && pantheon.Blocks[0].Items.SequenceEqual([6697, 6610, 3742, 6699]), "Importación conserva los cuatro bloques y orden de objetos");
Check(pantheon.Perks.Length == 0, "Build de objetos no inventa runas");
data.Validate(pantheon, requireRunes: false);
Reject(() => data.Validate(pantheon), "Solo objetos se guarda pero no permite aplicar runas ausentes");
var exported = JsonNode.Parse(pantheonText)!.AsArray()[0]!.DeepClone();
exported["blocks"]![0]!["items"]![0]!["count"] = 3;
exported["blocks"]![0]!["hideIfSummonerSpell"] = "11";
var counted = BuildImporter.Parse(exported.ToJsonString(), data, Role.Top).Single();
Check(counted.Blocks[0].Items.Take(3).All(x => x == 6697) && counted.Blocks[0].Items.Length == 6 && counted.Blocks[0].HideIfSummonerSpell == "11", "Importa objeto raíz, cantidades y condiciones de bloque");
var collection = new JsonObject { ["itemSets"] = new JsonArray(exported.DeepClone(), exported.DeepClone()) };
var batch = BuildImporter.Parse(collection.ToJsonString(), data, Role.Jungle);
Check(batch.Count == 2 && batch[0].Id != batch[1].Id, "Importa contenedor itemSets con múltiples builds e identidades independientes");
exported["associatedChampions"] = new JsonArray(80, 103);
Check(BuildImporter.Parse(exported.ToJsonString(), data, Role.Top).Select(p => p.ChampionId).SequenceEqual([80, 103]), "Build compartida crea perfil para cada campeón asociado");
exported["associatedChampions"] = new JsonArray();
Check(BuildImporter.Parse(exported.ToJsonString(), data, Role.Top, 80).Single().ChampionId == 80, "Build genérica usa campeón elegido");
Reject(() => BuildImporter.Parse(exported.ToJsonString(), data, Role.Top), "Build genérica exige campeón de destino");
exported["associatedChampions"] = new JsonArray(80);
exported["blocks"]![0]!["items"]![0]!["count"] = 0;
Reject(() => BuildImporter.Parse(exported.ToJsonString(), data, Role.Top), "Rechaza cantidades nulas");
exported["blocks"]![0]!["items"]![0]!["count"] = 1000000;
Reject(() => BuildImporter.Parse(exported.ToJsonString(), data, Role.Top), "Rechaza cantidades excesivas antes de expandir");
exported["blocks"]![0]!["items"]![0]!["count"] = 1;
exported["blocks"]![0]!["items"]![0]!["id"] = "999999";
Reject(() => BuildImporter.Parse(exported.ToJsonString(), data, Role.Top), "Rechaza objetos ajenos al catálogo");
Reject(() => BuildImporter.Parse("[]", data, Role.Top), "Rechaza lista vacía");
Reject(() => BuildImporter.Parse("{\"title\":\"Falta bloque\"}", data, Role.Top), "Rechaza estructura de build incompleta");
var nativeImport = BuildImporter.Parse(Json.Write(profile), data, Role.Middle).Single();
Check(nativeImport.Id != profile.Id && nativeImport.Perks.SequenceEqual(profile.Perks), "Mantiene compatibilidad con perfiles nativos y genera nueva identidad");
handler.ChampionId = 80;
await lcu.ImportItemsAsync(pantheon, data);
var importedSet = handler.Writes.Last().Body["itemSets"]!.AsArray().Last()!;
Check(importedSet["associatedChampions"]![0]!.GetValue<int>() == 80 && importedSet["associatedMaps"]![0]!.GetValue<int>() == 11 && importedSet["blocks"]!.AsArray().Count == 4, "Envía PantheonSR a LCU sin requerir runas y conserva mapa y bloques");
writeCount = handler.Writes.Count;
try { await lcu.ImportRunesAsync(pantheon, data); throw new Exception("Se aceptaron runas ausentes"); } catch (InvalidDataException) { }
Check(handler.Writes.Count == writeCount, "Aplicar runas de build de objetos no escribe en League");
var temp = Path.Combine(Environment.CurrentDirectory, "work", "draftoptimizer-test-" + Guid.NewGuid());
var store = new ProfileStore(temp);
await store.SaveAsync([profile]); var stored = (await store.LoadAsync()).Single();
Check(stored.Id == profile.Id && stored.Perks.SequenceEqual(profile.Perks), "Persistencia conserva identidad y runas");
await store.SaveAsync([profile]); Check(File.Exists(store.PathName + ".bak"), "Guarda copia de seguridad");
await store.SaveAsync([pantheon]);
Check((await store.LoadAsync()).Single().Perks.Length == 0 && (await store.LoadAsync()).Single().Blocks.Count == 4, "Persistencia conserva build importada sin runas");
await File.WriteAllTextAsync(store.PathName, "corrupto");
try { await store.LoadAsync(); throw new Exception("FAIL: perfiles corruptos"); } catch (System.Text.Json.JsonException) { Check(await File.ReadAllTextAsync(store.PathName) == "corrupto", "Perfiles corruptos no se sobrescriben"); }
if (args.Length > 0)
{
    var html = await File.ReadAllTextAsync(args[0]);
    var tier = args.Length > 1 && args[1] is "Diamond" or "D2" ? LolalyticsProvider.DefaultTier : null;
    var real = LolalyticsProvider.ParsePage(html, 103, Role.Middle, data.Patch, tier);
    Console.WriteLine(Json.Write(real));
    data.Validate(real); Check(real.Perks.Length == 9 && real.Blocks.Count >= 2, "Extracción de página REAL de Lolalytics y validación completa");
    if (tier != null) Reject(() => LolalyticsProvider.ParsePage(html.Replace("diamond_plus", "emerald_plus"), 103, Role.Middle, data.Patch, tier), "Rechaza una respuesta con otro rango en los filtros reales de Lolalytics");
}
await DiamondStatisticsTests.RunAsync(Check);
await SharedDatasetTests.RunAsync(Check);
await AutomaticRetryTests.RunAsync(Check);
await ErrorLogTests.RunAsync(Check);
await AutoDraftTests.RunAsync(Check);
await ConfidenceTests.RunAsync(Check);
DraftAnalysisTests.Run(Check);
ItemBlockTextTests.Run(Check);
await WorkflowTests.RunAsync(Check, data, profile);
InitialConfidenceTests.Run(Check);
NullStatisticsTests.Run(Check);
SimulationTests.Run(Check);
await BuildQualityTests.RunAsync(Check, data);
await AsyncWorkTests.RunAsync(Check);
Console.WriteLine($"{passed} comprobaciones correctas.");
if (failed > 0) { Console.WriteLine($"{failed} comprobaciones fallidas."); Environment.ExitCode = 1; }

sealed class FakeLeague : HttpMessageHandler
{
    public bool InProgress { get; set; } = true;
    public int ChampionId { get; set; } = 103;
    public long GameId { get; set; } = 700;
    public int ActionId { get; set; } = 9;
    public string ActionType { get; set; } = "pick";
    public int AllyIntent { get; set; }
    public int HoveredChampion { get; set; }
    public Action? OnBanSelection { get; set; }
    public string Role { get; set; } = "middle";
    public double? RemainingMs { get; set; }
    public bool InfiniteTimer { get; set; }
    public string TimerPhase { get; set; } = "BAN_PICK";
    public string Phase { get; set; } = "ChampSelect";
    public bool Completed { get; set; }
    public Action? OnPages { get; set; }
    public HashSet<int> Available { get; set; } = [103];
    public Action? OnPickable { get; set; }
    public List<(string Method, string Path, JsonNode Body)> Writes { get; } = [];
    private string _sets = """{"timestamp":123,"itemSets":[{"uid":"other-app","title":"Mi build","blocks":[]}]}""";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method != HttpMethod.Get)
        {
            var body = request.Content == null ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!;
            Writes.Add((request.Method.Method, path, body));
            if (path.EndsWith("/sets")) _sets = body.ToJsonString();
            if (ActionType == "ban" && request.Method == HttpMethod.Patch && path.EndsWith($"/actions/{ActionId}"))
            {
                HoveredChampion = body["championId"]!.GetValue<int>();
                OnBanSelection?.Invoke();
            }
            return new(System.Net.HttpStatusCode.NoContent);
        }
        string result = path switch
        {
            "/lol-gameflow/v1/gameflow-phase" => Json.Write(Phase),
            "/lol-champ-select/v1/session" => Json.Write(new DraftSession { GameId = GameId, LocalPlayerCellId = 2, Timer = RemainingMs.HasValue ? new DraftTimer { AdjustedTimeLeftInPhase = RemainingMs, InternalNowInEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Phase = TimerPhase, IsInfinite = InfiniteTimer } : null, MyTeam = [new() { CellId = 2, ChampionId = ChampionId, AssignedPosition = Role }, new() { CellId = 3, ChampionPickIntent = AllyIntent }], Actions = [[new() { Id = ActionId, ActorCellId = 2, Type = ActionType, IsInProgress = InProgress, Completed = Completed, ChampionId = Completed ? ChampionId : HoveredChampion }]] }),
            "/lol-champ-select/v1/pickable-champion-ids" or "/lol-champ-select/v1/bannable-champion-ids" => ReadAvailability(),
            "/lol-champ-select/v1/all-grid-champions" => """[{"id":103,"positionsFavorited":["MIDDLE","UTILITY","MIDDLE"]},{"id":238,"positionsFavorited":["middle","unknown"]},{"id":99,"positionsFavorited":[]}]""",
            "/lol-perks/v1/pages" => ReadPages(),
            "/lol-summoner/v1/current-summoner" => """{"summonerId":7}""",
            "/lol-item-sets/v1/item-sets/7/sets" => _sets,
            _ => throw new Exception("Endpoint inesperado: " + path)
        };
        return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(result, System.Text.Encoding.UTF8, "application/json") };
    }
    private string ReadAvailability() { OnPickable?.Invoke(); return Json.Write(Available); }
    private string ReadPages() { OnPages?.Invoke(); return """[{"id":99,"name":"No tocar","isEditable":true},{"id":42,"name":"DraftOptimizer","isEditable":true}]"""; }
}




