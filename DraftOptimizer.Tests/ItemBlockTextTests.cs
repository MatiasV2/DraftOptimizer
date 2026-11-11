using DraftOptimizer.Core;
using System.Text.Json.Nodes;

static class ItemBlockTextTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var ending in new[] { "\n", "\r\n", "\r" })
        {
            var blocks = ItemBlockText.Parse("Inicio: 1056, 2003, 2003" + ending + "Núcleo: 6655, 3020, 3089");
            check(blocks.Count == 2 && blocks[0].Items.SequenceEqual([1056, 2003, 2003]) && blocks[1].Items.SequenceEqual([6655, 3020, 3089]),
                "Editor separa bloques con " + (ending == "\r" ? "CR nativo" : ending == "\n" ? "LF" : "CRLF"));
        }
        var continued = ItemBlockText.Parse("Inicio:\r1056,\r2003\r2003\r\rNúcleo: 6655,\n3020, 3089,\r\n");
        check(continued.Count == 2 && continued[0].Items.SequenceEqual([1056, 2003, 2003]) && continued[1].Items.SequenceEqual([6655, 3020, 3089]), "Acepta IDs en líneas de continuación, líneas vacías y coma final");
        check(ItemBlockText.Parse("1056\r2003\r2003").Single() is { Name: "Objetos", Items.Length: 3 }, "Lista de IDs sin cabecera crea bloque Objetos");
        check(ItemBlockText.Parse("Inicio: 1056 2003\t2003").Single().Items.SequenceEqual([1056, 2003, 2003]), "Acepta espacios y tabulaciones entre IDs");
        var original = new List<ItemBlock>
        {
            new() { Name = "Core\r\nSituacional: AP", Items = [6697, 6610], HideIfSummonerSpell = "11" },
            new() { Name = "Nota \\\n\"Botas\"", Items = [3111], ShowIfSummonerSpell = "4" }
        };
        var formatted = ItemBlockText.Format(original);
        var parsed = ItemBlockText.Parse(formatted, original);
        check(parsed.Select(x => x.Name).SequenceEqual(original.Select(x => x.Name)) && parsed[0].HideIfSummonerSpell == "11" && parsed[1].ShowIfSummonerSpell == "4", "Nombres con saltos, dos puntos, comillas y barras conservan contenido y condiciones");
        var nativeText = formatted.Replace("\r\n", "\n").Replace("\n", "\r");
        check(ItemBlockText.Parse(nativeText, original).Select(x => x.Name).SequenceEqual(original.Select(x => x.Name)), "Formato cargado sobrevive conversión del editor a CR");
        var duplicateNames = new[] { new ItemBlock { Name = "Core", Items = [1056], HideIfSummonerSpell = "11" }, new ItemBlock { Name = "Core", Items = [2003], HideIfSummonerSpell = "4" } };
        var duplicates = ItemBlockText.Parse(ItemBlockText.Format(duplicateNames), duplicateNames);
        check(duplicates[0].HideIfSummonerSpell == "11" && duplicates[1].HideIfSummonerSpell == "4", "Bloques homónimos conservan sus condiciones individuales");
        check(ItemBlockText.Parse(ItemBlockText.AppendItem("Core:\r1056\r", 2003)).Single().Items.SequenceEqual([1056, 2003]), "Añadir objeto después de salto mantiene la línea de continuación");
        check(ItemBlockText.Parse(ItemBlockText.AppendItem("Core:\r", 1056)).Single().Items.SequenceEqual([1056]), "Añadir objeto en cabecera vacía genera bloque válido");
        foreach (var invalid in new[] { "", "Core:\rBotas: 3111", "Core: 1056\rno-es-un-id" })
        {
            try { ItemBlockText.Parse(invalid); throw new Exception("Texto de objetos inválido aceptado"); }
            catch (InvalidDataException) { check(true, "Detecta texto de objetos incompleto o inválido sin descartar líneas"); }
        }
        using var data = new Dataset(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "current-patch.json")));
        var export = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PantheonSR.json")))!;
        export[0]!["blocks"]![0]!["type"] = "Core\r\nSituacional: AP";
        var pretty = export.ToJsonString(Json.Options).Replace("\n", "\r\n");
        var imported = BuildImporter.Parse(pretty, data, Role.Top).Single();
        var roundtrip = ItemBlockText.Parse(ItemBlockText.Format(imported.Blocks), imported.Blocks);
        imported.Blocks = roundtrip; data.Validate(imported, requireRunes: false);
        check(imported.Blocks[0].Name == "Core\r\nSituacional: AP" && imported.Blocks.Count == 4, "JSON de Pantheon con nombre multilinea se importa, edita y valida sin pérdidas");
        var unknown = new BuildProfile { ChampionId = 80, Blocks = ItemBlockText.Parse("Core:\r999999"), Perks = [] };
        try { data.Validate(unknown, requireRunes: false); throw new Exception("Objeto desconocido aceptado"); }
        catch (InvalidDataException) { check(true, "Soportar saltos no elimina la validación de objetos del catálogo"); }
    }
}
