using System.Globalization;
using System.Text.Json;

namespace DraftOptimizer.Core;

/// <summary>Imports native profiles or League item-set exports, without inventing runes.</summary>
public static class BuildImporter
{
    public static List<BuildProfile> Parse(string text, Dataset dataset, Role role, int? fallbackChampionId = null)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("El JSON supera el límite de 4 MB.");
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("itemSets", out var sets)) root = sets;
        var entries = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
        if (entries.Count is 0 or > 100) throw new InvalidDataException("El archivo debe contener entre 1 y 100 builds.");
        var profiles = new List<BuildProfile>();
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Cada build debe ser un objeto JSON.");
            if (entry.TryGetProperty("championId", out _) && entry.TryGetProperty("name", out _))
            {
                var profile = Json.Read<BuildProfile>(entry.GetRawText());
                dataset.Validate(profile, requireRunes: false);
                profile.Id = Guid.NewGuid();
                profiles.Add(profile);
                continue;
            }
            if (!entry.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("blocks", out var rawBlocks) || rawBlocks.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Formato no reconocido: se esperan title, associatedChampions y blocks, o un perfil de DraftOptimizer.");
            var champions = ReadIds(entry, "associatedChampions");
            if (champions.Length == 0)
                champions = fallbackChampionId is > 0 ? [fallbackChampionId.Value] : throw new InvalidDataException("La build no indica campeón. Selecciona uno antes de importar.");
            var maps = ReadIds(entry, "associatedMaps");
            var blocks = new List<ItemBlock>();
            foreach (var block in rawBlocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                    !block.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Cada bloque debe contener type e items.");
                var expanded = new List<int>();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idValue)) throw new InvalidDataException("Objeto sin id.");
                    var id = PositiveInt(idValue, "id de objeto");
                    var count = item.TryGetProperty("count", out var countValue) ? PositiveInt(countValue, "cantidad") : 1;
                    if (count > 99 || expanded.Count + count > 500) throw new InvalidDataException("Máximo 99 unidades por objeto y 500 por bloque.");
                    expanded.AddRange(Enumerable.Repeat(id, count));
                }
                blocks.Add(new ItemBlock { Name = type.GetString()!, Items = expanded.ToArray(), HideIfSummonerSpell = OptionalString(block, "hideIfSummonerSpell"), ShowIfSummonerSpell = OptionalString(block, "showIfSummonerSpell") });
            }
            foreach (var championId in champions.Distinct())
            {
                var profile = new BuildProfile
                {
                    Name = title.GetString()!, ChampionId = championId, Role = role, AssociatedMaps = maps,
                    Perks = [], Blocks = blocks.Select(b => new ItemBlock { Name = b.Name, Items = b.Items.ToArray(), HideIfSummonerSpell = b.HideIfSummonerSpell, ShowIfSummonerSpell = b.ShowIfSummonerSpell }).ToList(),
                    Source = "Importación JSON · solo objetos", Patch = ""
                };
                dataset.Validate(profile, requireRunes: false);
                profiles.Add(profile);
            }
        }
        return profiles;
    }
    private static int[] ReadIds(JsonElement entry, string key)
    {
        if (!entry.TryGetProperty(key, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{key} debe ser una lista.");
        return value.EnumerateArray().Select(x => PositiveInt(x, key)).ToArray();
    }
    private static int PositiveInt(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0) return number;
        throw new InvalidDataException($"{name}: se espera un entero positivo.");
    }
    private static string OptionalString(JsonElement entry, string key)
    {
        if (!entry.TryGetProperty(key, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{key} debe ser texto.");
        return value.GetString()!;
    }
}
