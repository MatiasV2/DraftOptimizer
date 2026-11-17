using DraftOptimizer.Core;
using System.Text.Json.Nodes;

static class BuildQualityTests
{
    public static async Task RunAsync(Action<bool, string> check, Dataset data)
    {
        var ranked = BuildItemConfidence.Rank("item1", [(1,.54,10000),(2,.58,200),(3,.99,20),(4,.535,10000),(5,.40,10000)]);
        check(ranked[0].ItemId == 1 && ranked.Any(x => x.ItemId == 4) && ranked.All(x => x.ItemId is not (3 or 5)), "Objetos se ordenan por Wilson 95%, con al menos 3 alternativas elegibles y sin muestras <200");
        check(ranked.Zip(ranked.Skip(1)).All(x => x.First.ConfidenceLower >= x.Second.ConfidenceLower), "Alternativas mantienen orden conservador descendente");
        check(BuildItemConfidence.Rank("item2", [(7,double.NaN,1000),(8,1.1,1000),(9,.5,199)]).Count == 0, "Estadísticas inválidas no generan recomendaciones de compra");
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","LolalyticsBuildD2.json")))!;
        var profile = LolalyticsProvider.ParseSummary(fixture["summary"]!,103,Role.Middle,"16.22",fixture["details"]);
        data.Validate(profile);
        check(profile.EarlySkills.SequenceEqual(["W","Q","E"]) && profile.SkillPriority.SequenceEqual(["Q","W","E"]), "Datos D2+ reales distinguen habilidades de nivel 1–3 de prioridad de maxeo");
        check(profile.Blocks[0].Name.Contains("lvl1 W / lvl2 Q / lvl3 E") && profile.Blocks[0].Items.Count(x => x == 2003) == 2, "Título inicial contiene habilidades y conserva cantidades de consumibles");
        check(Enumerable.Range(1,3).All(i => profile.Blocks.Any(b => b.Name.StartsWith($"Objeto {i} · maxear {profile.SkillPriority[i-1]}"))), "Primeros tres objetos muestran prioridad de maxeo correspondiente");
        check(profile.ItemEvidence.Where(x=>x.Slot is not "start").GroupBy(x=>x.Slot).All(g=>g.Count() >= 3 && g.All(x=>x.Games>=200) && g.Zip(g.Skip(1)).All(pair=>pair.First.ConfidenceLower >= pair.Second.ConfidenceLower)), "Cada posición ofrece al menos 3 alternativas elegibles ordenadas por Wilson, con mínimo 200 partidas");
        check(profile.Blocks.Where(b=>b.Name.StartsWith("Inicio")).Count() <= 2, "La build muestra como máximo 2 opciones de objetos iniciales");
        var clone = Json.Read<BuildProfile>(Json.Write(profile));
        check(profile.Spell1Id == 4 && profile.Spell2Id == 14 && clone.Spell1Id == 4 && clone.Spell2Id == 14,
            "Lolalytics y el perfil nativo conservan los hechizos D y F en orden");
        var legacy = Json.Read<BuildProfile>("{}");
        data.ValidateSummonerSpells(legacy);
        check(legacy.Spell1Id == 0 && legacy.Spell2Id == 0, "Perfiles anteriores conservan los hechizos actuales de League");
        clone.Spell2Id = clone.Spell1Id;
        try { data.ValidateSummonerSpells(clone); check(false, "Rechaza dos hechizos iguales"); }
        catch (InvalidDataException) { check(true, "Rechaza dos hechizos iguales"); }
        clone.Spell2Id = 32;
        try { data.ValidateSummonerSpells(clone); check(false, "Marca es exclusiva de ARAM"); }
        catch (InvalidDataException) { check(true, "Marca es exclusiva de ARAM"); }
        clone.IsAram = true;
        data.ValidateSummonerSpells(clone);
        check(clone.EarlySkills.SequenceEqual(profile.EarlySkills) && clone.ItemEvidence.SequenceEqual(profile.ItemEvidence), "Perfil nativo conserva habilidades y evidencia al guardar y recargar");
        using var champions = new Dataset("""{"version":"test","date":"test","championData":{"103":{"id":"Ahri","name":"Ahri","statsByRole":{"2":{"games":1000,"wins":600}}}}}""");
        var controller = new AutoDraftController(); AutomaticPickRequest? request=null;
        var draft = new DraftSession { GameId=42,LocalPlayerCellId=1,MyTeam=[new(){CellId=1,AssignedPosition="middle"}],Actions=[[new(){Id=9,ActorCellId=1,Type="pick",IsInProgress=true}]] };
        await controller.TickAsync(champions,null,draft,[103],new FavoriteSettings(),Role.Middle,DateTimeOffset.UtcNow,r=>{request=r;return Task.FromResult(true);});
        check(request is {ChampionId:103,Complete:false}, "Sin favoritos se preselecciona la mejor opción del rol sin confirmar a ciegas");
        var handler = new FakeLeague {Completed=true,TimerPhase="BAN_PICK",InProgress=false,ChampionId=103};
        using var lcu = new LcuClient(new HttpClient(handler){BaseAddress=new Uri("https://127.0.0.1:29999/")});
        var target = new FinalLoadoutTarget(700,103,Role.Middle,RequireFinalization:false);
        await lcu.ImportSummonerSpellsAsync(profile, data, target: target);
        var spellWrite = handler.Writes.Single();
        check(spellWrite.Method == "PATCH" && spellWrite.Path.EndsWith("/session/my-selection") &&
            spellWrite.Body["spell1Id"]!.GetValue<int>() == 4 && spellWrite.Body["spell2Id"]!.GetValue<int>() == 14 &&
            spellWrite.Body.AsObject().Count == 2, "LCU aplica D y F sin cambiar la skin");
        await lcu.ImportRunesAsync(profile,data,target:target); await lcu.ImportItemsAsync(profile,data,target:target);
        var imported = handler.Writes.Last().Body["itemSets"]!.AsArray().Last()!;
        check(imported["title"]!.GetValue<string>() == "Ahri - Middle" && imported["blocks"]!.AsArray().All(b => !b!["type"]!.GetValue<string>().Contains("confianza 95%")), "Título enviado solo contiene campeón y línea, sin sufijos en bloques");
        check(handler.Writes.Count==3,"Cliente permite hechizos, runas y objetos de la misma partida justo después del lock-in");
        handler.Completed=false; int writes=handler.Writes.Count;
        try {await lcu.ImportRunesAsync(profile,data,target:target);} catch(InvalidOperationException){}
        try { await lcu.ImportSummonerSpellsAsync(profile, data, target: target); } catch (InvalidOperationException) { }
        check(handler.Writes.Count==writes,"Una preselección no confirmada no dispara importación temprana");
        handler.Completed = true;
        handler.GameId++;
        try { await lcu.ImportSummonerSpellsAsync(profile, data, target: target); } catch (InvalidOperationException) { }
        check(handler.Writes.Count == writes, "El cambio de partida impide aplicar hechizos automáticamente");
    }
}
