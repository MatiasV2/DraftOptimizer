using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DraftOptimizer.Core;

public sealed class DiamondStatisticsProvider(HttpClient http, string directory)
{
    public async Task<(Dataset Data, bool Cached)> LoadAsync(Dataset catalog, string window, CancellationToken ct,
        IProgress<string>? progress = null, bool forceRefresh = false)
    {
        if (window is not ("current-patch" or "30-days" or "14-days" or "7-days")) throw new ArgumentException("Ventana desconocida.");
        var patch = window == "7-days" ? "7" : window == "14-days" ? "14" : window == "30-days" ? "30" : string.Join('.', catalog.Patch.Split('.').Take(2));
        var folder = Path.Combine(directory, "diamond-plus", patch);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "dataset.json");
        if (!forceRefresh && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(12))
            return (await Dataset.LoadFileAsync(path, ct), true);
        try
        {
            var root = JsonNode.Parse(catalog.Root.GetRawText())!.AsObject();
            var champions = root["championData"]!.AsObject();
            foreach (var champion in champions) champion.Value!["statsByRole"] = new JsonObject();
            using var limiter = new SemaphoreSlim(4);
            int completed = 0;
            int total = catalog.Champions.Count * Enum.GetValues<Role>().Length;
            var tasks = catalog.Champions.SelectMany(champion => Enum.GetValues<Role>().Select(async role =>
            {
                await limiter.WaitAsync(ct);
                try
                {
                    var file = Path.Combine(folder, $"{champion.Id}-{role}.json");
                    JsonObject stats;
                    if (!forceRefresh && File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(12))
                        stats = JsonNode.Parse(await File.ReadAllTextAsync(file, ct))!.AsObject();
                    else
                    {
                        string lane = role.ToString().ToLowerInvariant();
                        string url = $"https://lolalytics.com/lol/{champion.Slug}/build/?lane={lane}&tier={LolalyticsProvider.DefaultTier}&region=all&patch={patch}";
                        using var response = await http.GetAsync(url, ct);
                        if (response.StatusCode == HttpStatusCode.NotFound)
                            stats = EmptyStats();
                        else
                        {
                            response.EnsureSuccessStatusCode();
                            stats = ParsePage(await response.Content.ReadAsStringAsync(ct), champion.Id, role, patch);
                        }
                        if (stats["games"]!.GetValue<int>() >= 200)
                        {
                            var teamUrl = $"https://a1.lolalytics.com/mega/?ep=build-team&v=1&patch={patch}&c={champion.Slug}&lane={lane}&tier={LolalyticsProvider.DefaultTier}&queue=ranked&region=all";
                            var team = JsonNode.Parse(await http.GetStringAsync(teamUrl, ct))!;
                            if (team["response"]?["valid"]?.GetValue<bool>() != true)
                                throw new InvalidDataException("Lolalytics no entregó sinergias válidas.");
                            stats["synergy"] = ParsePairs(team["team"]!, role);
                        }
                        await WriteAsync(file, stats.ToJsonString(), ct);
                    }
                    lock (champions) champions[champion.Id.ToString()]!["statsByRole"]![((int)role).ToString()] = stats;
                    progress?.Report($"Estadísticas Diamante+ · {window} · {Interlocked.Increment(ref completed)}/{total}");
                }
                finally { limiter.Release(); }
            }));
            await Task.WhenAll(tasks);
            Normalize(root);
            root["date"] = DateTimeOffset.UtcNow.ToString("O");
            root["statisticsTier"] = LolalyticsProvider.DefaultTier;
            root["statisticsWindow"] = window;
            var json = root.ToJsonString();
            var data = new Dataset(json);
            try { await WriteAsync(path, json, ct); }
            catch { data.Dispose(); throw; }
            return (data, false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or IOException)
        {
            ErrorLog.Write("DiamondStatisticsProvider", ex);
            if (File.Exists(path)) return (await Dataset.LoadFileAsync(path, ct), true);
            throw new IOException("No se pudieron completar las estadísticas Diamante+ de Lolalytics. La descarga puede reanudarse al actualizar.", ex);
        }
    }

    private static JsonObject EmptyStats() => new()
    {
        ["games"] = 0, ["wins"] = 0d, ["matchup"] = new JsonObject(), ["synergy"] = new JsonObject()
    };

    private static async Task WriteAsync(string path, string text, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, ct);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static JsonObject ParsePage(string html, int championId, Role role, string patch)
    {
        try { return ParsePageCore(html, championId, role, patch); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or
            IndexOutOfRangeException or OverflowException or FormatException)
        {
            ErrorLog.Write("DiamondStatisticsProvider", ex);
            throw new InvalidDataException($"Lolalytics devolvió estadísticas incompletas para el campeón {championId}, {role}, parche {patch}.", ex);
        }
    }

    private static JsonObject ParsePageCore(string html, int championId, Role role, string patch)
    {
        var match = Regex.Match(html, "<script\\s+type=[\"']qwik/json[\"'][^>]*>([\\s\\S]*?)</script>",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(3));
        if (!match.Success) throw new InvalidDataException("Lolalytics no entregó estadísticas legibles.");
        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        var objects = doc.RootElement.GetProperty("objs");
        JsonNode? Expand(JsonElement value, int depth)
        {
            if (depth > 40) throw new InvalidDataException("Referencias de estadísticas incompatibles.");
            if (value.ValueKind == JsonValueKind.String)
            {
                int index = 0;
                foreach (char c in value.GetString()!)
                {
                    int digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : -1;
                    if (digit < 0) throw new InvalidDataException("Referencia inválida.");
                    index = checked(index * 36 + digit);
                }
                return objects[index].ValueKind == JsonValueKind.String ? JsonValue.Create(objects[index].GetString()) : Expand(objects[index], depth + 1);
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                var result = new JsonObject();
                foreach (var property in value.EnumerateObject()) result[property.Name] = Expand(property.Value, depth + 1);
                return result;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                var result = new JsonArray();
                foreach (var child in value.EnumerateArray()) result.Add(Expand(child, depth + 1));
                return result;
            }
            return JsonNode.Parse(value.GetRawText());
        }
        string lane = role.ToString().ToLowerInvariant();
        bool filtersMatch = objects.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("tier", out var tier) && value.TryGetProperty("region", out var region) &&
            value.TryGetProperty("patch", out var version) && value.TryGetProperty("lane", out var position) &&
            Expand(tier, 0)?.ToString() == LolalyticsProvider.DefaultTier && Expand(region, 0)?.ToString() == "all" &&
            Expand(version, 0)?.ToString() == patch && Expand(position, 0)?.ToString() == lane);
        if (!filtersMatch) throw new InvalidDataException("Las estadísticas no confirman Diamante+, posición, región y parche.");
        var raw = objects.EnumerateArray().FirstOrDefault(value => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("header", out _) && value.TryGetProperty("nav", out _));
        if (raw.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Lolalytics no entregó la cabecera de estadísticas del campeón.");
        var header = Expand(raw.GetProperty("header"), 0)!;
        if (header["cid"]!.GetValue<int>() != championId || header["lane"]!.ToString() != lane ||
            header["patch"]!.ToString() != patch) throw new InvalidDataException("Estadísticas de otro campeón, posición o parche.");
        int games = header["n"]!.GetValue<int>();
        if (games == 0) return EmptyStats();
        double winrate = header["wr"]!.GetValue<double>();
        if (games < 0 || !double.IsFinite(winrate) || winrate is < 0 or > 100)
            throw new InvalidDataException("Muestra de estadísticas inválida.");
        return new JsonObject
        {
            ["games"] = games, ["wins"] = games * winrate / 100,
            ["matchup"] = games >= 200 && raw.TryGetProperty("enemy", out var enemy) ? ParsePairs(Expand(enemy, 0)!) : new JsonObject(),
            ["synergy"] = new JsonObject(),
            ["damageProfile"] = header["damage"]?.DeepClone(),
            ["statsByTime"] = new JsonArray()
        };
    }

    private static JsonObject ParsePairs(JsonNode source, Role? exclude = null)
    {
        var result = new JsonObject();
        foreach (var role in Enum.GetValues<Role>().Where(role => role != exclude))
        {
            var entries = new JsonObject();
            if (source[role.ToString().ToLowerInvariant()] is not JsonArray rows)
                throw new InvalidDataException("Faltan estadísticas por posición.");
            foreach (var row in rows)
            {
                if (row is not JsonArray { Count: >= 6 } values ||
                    !int.TryParse(values[0]?.ToString(), out int id) ||
                    !double.TryParse(values[1]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ||
                    !int.TryParse(values[5]?.ToString(), out var games) || id <= 0 || games < 0 ||
                    !double.IsFinite(rate) || rate is < 0 or > 100) throw new InvalidDataException("Enfrentamiento inválido.");
                entries[id.ToString()] = new JsonObject { ["games"] = games, ["wins"] = games * rate / 100 };
            }
            result[((int)role).ToString()] = entries;
        }
        return result;
    }

    private static void Normalize(JsonObject root)
    {
        var roles = root["championData"]!.AsObject().SelectMany(champion => champion.Value!["statsByRole"]!.AsObject())
            .Select(role => role.Value!.AsObject()).ToArray();
        double games = roles.Sum(role => role["games"]!.GetValue<int>());
        if (games <= 0) throw new InvalidDataException("Sin partidas de Diamante+.");
        double baseline = DraftAnalyzer.ToRating(roles.Sum(role => role["wins"]!.GetValue<double>()) / games);
        void Adjust(JsonObject stats)
        {
            double n = stats["games"]!.GetValue<int>();
            if (n > 0) stats["wins"] = n * DraftAnalyzer.ToWinrate(DraftAnalyzer.ToRating(stats["wins"]!.GetValue<double>() / n) - baseline);
        }
        foreach (var role in roles)
        {
            Adjust(role);
            foreach (string field in new[] { "matchup", "synergy" })
                foreach (var position in role[field]!.AsObject())
                    foreach (var pair in position.Value!.AsObject()) Adjust(pair.Value!.AsObject());
        }
    }
}
