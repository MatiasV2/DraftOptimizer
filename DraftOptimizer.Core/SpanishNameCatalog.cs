using System.Text.Json;

namespace DraftOptimizer.Core;

/// <summary>Loads Latin American Spanish item and rune names from Data Dragon, with a patch-local disk cache.</summary>
public sealed class SpanishNameCatalog(HttpClient http, string cacheDirectory)
{
    public async Task<IReadOnlyDictionary<(string Table, int Id), string>> LoadAsync(string version, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = Path.Combine(cacheDirectory, $"names-es_MX-{version}.json");
        if (File.Exists(cachePath))
        {
            try { return Parse(await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
        }

        var baseUrl = $"https://ddragon.leagueoflegends.com/cdn/{Uri.EscapeDataString(version)}/data/es_MX";
        var itemTask = http.GetStringAsync($"{baseUrl}/item.json", ct);
        var runeTask = http.GetStringAsync($"{baseUrl}/runesReforged.json", ct);
        await Task.WhenAll(itemTask, runeTask).ConfigureAwait(false);
        var names = new Dictionary<(string Table, int Id), string>();
        using (var items = JsonDocument.Parse(await itemTask.ConfigureAwait(false)))
        foreach (var item in items.RootElement.GetProperty("data").EnumerateObject())
            if (int.TryParse(item.Name, out var id) && item.Value.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } translated)
                names[("itemData", id)] = translated;

        using (var runes = JsonDocument.Parse(await runeTask.ConfigureAwait(false)))
        foreach (var path in runes.RootElement.EnumerateArray())
        {
            foreach (var slot in path.GetProperty("slots").EnumerateArray())
            foreach (var rune in slot.GetProperty("runes").EnumerateArray())
                AddRune(rune);
            AddRune(path);
        }

        AddShardNames(names);
        var serialized = names.ToDictionary(p => $"{p.Key.Table}:{p.Key.Id}", p => p.Value);
        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(serialized), ct).ConfigureAwait(false);
            File.Move(temporary, cachePath, true);
        }
        finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } } }
        return names;

        void AddRune(JsonElement rune)
        {
            if (rune.TryGetProperty("id", out var idNode) && idNode.TryGetInt32(out var id) &&
                rune.TryGetProperty("name", out var nameNode) && nameNode.GetString() is { Length: > 0 } translated)
                names[("runeData", id)] = translated;
        }
    }

    private static IReadOnlyDictionary<(string Table, int Id), string> Parse(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new InvalidDataException("Catálogo español vacío.");
        var names = new Dictionary<(string Table, int Id), string>();
        foreach (var (key, name) in values)
        {
            var split = key.LastIndexOf(':');
            if (split <= 0 || !int.TryParse(key.AsSpan(split + 1), out var id) || string.IsNullOrWhiteSpace(name)) continue;
            names[(key[..split], id)] = name;
        }
        AddShardNames(names);
        if (names.Count < 100) throw new InvalidDataException("El catálogo español está incompleto.");
        return names;
    }

    private static void AddShardNames(Dictionary<(string Table, int Id), string> names)
    {
        names[("statShardData", 5001)] = "Vida progresiva";
        names[("statShardData", 5005)] = "Velocidad de ataque";
        names[("statShardData", 5007)] = "Aceleración de habilidad";
        names[("statShardData", 5008)] = "Fuerza adaptable";
        names[("statShardData", 5010)] = "Flexible";
        names[("statShardData", 5011)] = "Vida";
        names[("statShardData", 5013)] = "Tenacidad y resistencia a ralentizaciones";
    }
}
