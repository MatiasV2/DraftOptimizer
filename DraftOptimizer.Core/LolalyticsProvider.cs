using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DraftOptimizer.Core;

public sealed class LolalyticsProvider(HttpClient http)
{
    public const string DefaultTier = "d2_plus";
    public const string AramTier = "emerald_plus";
    public static string PageUrl(Champion champion, Role role, string patch) =>
        $"https://lolalytics.com/lol/{champion.Slug}/build/?lane={role.ToString().ToLowerInvariant()}&tier={DefaultTier}&region=all&patch={Uri.EscapeDataString(string.Join('.', patch.Split('.').Take(2)))}";
    public async Task<BuildProfile> GetAsync(Champion champion, Role role, string patch, CancellationToken ct = default)
    {
        var html = await http.GetStringAsync(PageUrl(champion, role, patch), ct).ConfigureAwait(false);
        var profile = await Task.Run(() => ParsePage(html, champion.Id, role, patch, expectedTier: DefaultTier), ct).ConfigureAwait(false);
        profile.Name = $"{champion.Name} - {role}";
        return profile;
    }
    public async Task<BuildRecommendationOptions> GetOptionsAsync(Champion champion, Role role, string patch, CancellationToken ct = default)
    {
        var html = await http.GetStringAsync(PageUrl(champion, role, patch), ct).ConfigureAwait(false);
        var options = await Task.Run(() => ParsePageOptions(html, champion.Id, role, patch, expectedTier: DefaultTier), ct).ConfigureAwait(false);
        options.Popular.Profile.Name = $"{champion.Name} - {role} - más popular";
        options.HighestWinrate.Profile.Name = $"{champion.Name} - {role} - mejor WR";
        return options;
    }
    public static string AramPageUrl(Champion champion, string patch) =>
        $"https://lolalytics.com/lol/{champion.Slug}/aram/build/?tier={AramTier}&region=all&patch={Uri.EscapeDataString(string.Join('.', patch.Split('.').Take(2)))}";
    public async Task<BuildProfile> GetAramAsync(Champion champion, string patch, CancellationToken ct = default)
    {
        var html = await http.GetStringAsync(AramPageUrl(champion, patch), ct).ConfigureAwait(false);
        var profile = await Task.Run(() => ParsePage(html, champion.Id, Role.Middle, patch, expectedTier: AramTier, isAram: true), ct).ConfigureAwait(false);
        profile.Name = $"{champion.Name} - ARAM";
        return profile;
    }
    public async Task<Suggestion> GetAramStatsAsync(Champion champion, string patch, CancellationToken ct = default)
    {
        var html = await http.GetStringAsync(AramPageUrl(champion, patch), ct).ConfigureAwait(false);
        var visible = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        var winrate = Regex.Match(visible, @"has\s+(?:a\s+)?([\d.]+)%\s+win rate\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(3));
        var games = 0;
        if (winrate.Success)
        {
            var afterHeadline = visible[(winrate.Index + winrate.Length)..];
            var window = afterHeadline[..Math.Min(afterHeadline.Length, 5000)];
            var gamesMatch = Regex.Match(window, @"([\d,]+)\s+Games?\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(3));
            if (gamesMatch.Success) int.TryParse(gamesMatch.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out games);
        }
        if (!winrate.Success || !double.TryParse(winrate.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wr) || games <= 0)
            throw new InvalidDataException($"Lolalytics no entregó estadísticas ARAM legibles para {champion.Name}.");
        var rate = wr / 100d;
        var interval = WinrateConfidence.Wilson95(rate * games, games);
        return new(champion, rate, games, interval.Lower * 100, 0) { GlobalConfidenceInterval = interval, IsAram = true };
    }
    public static BuildProfile ParsePage(string html, int championId, Role role, string patch, string? expectedTier = null, bool isAram = false)
        => ParsePageOptions(html, championId, role, patch, expectedTier, isAram).Recommended.Profile;

    public static BuildRecommendationOptions ParsePageOptions(string html, int championId, Role role, string patch, string? expectedTier = null, bool isAram = false)
    {
        var script = Regex.Match(html, "<script\\s+type=[\"']qwik/json[\"'][^>]*>([\\s\\S]*?)</script>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(3));
        if (!script.Success) throw new InvalidDataException("Lolalytics no entregó datos legibles. Abre la página o usa un perfil personalizado.");
        using var doc = JsonDocument.Parse(script.Groups[1].Value);
        var objects = doc.RootElement.GetProperty("objs");
        int dataIndex = -1;
        for (int i = 0; i < objects.GetArrayLength(); i++)
        {
            var obj = objects[i];
            // Lolalytics omits lane-enemy and analysis fields on ARAM pages; summary is the
            // stable payload on both modes. Header is optional in newer page variants.
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("summary", out _)) { dataIndex = i; break; }
        }
        if (dataIndex < 0) throw new InvalidDataException("Cambió el formato de Lolalytics; usa la configuración personalizada.");
        JsonNode? Expand(JsonElement value, int depth)
        {
            if (depth > 40) throw new InvalidDataException("Estructura Lolalytics no compatible.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var node = new JsonObject();
                foreach (var prop in value.EnumerateObject()) node[prop.Name] = Reference(prop.Value, depth + 1);
                return node;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                var node = new JsonArray();
                foreach (var element in value.EnumerateArray()) node.Add(Reference(element, depth + 1));
                return node;
            }
            return JsonNode.Parse(value.GetRawText());
        }
        JsonNode? Reference(JsonElement value, int depth)
        {
            if (value.ValueKind != JsonValueKind.String) return Expand(value, depth + 1);
            var str = value.GetString()!;
            int index = 0;
            foreach (var ch in str)
            {
                int digit = ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'z' ? ch - 'a' + 10 : -1;
                if (digit < 0) throw new InvalidDataException("Referencia Lolalytics no compatible.");
                index = checked(index * 36 + digit);
            }
            if (index >= objects.GetArrayLength()) throw new InvalidDataException("Referencia fuera de rango.");
            return Expand(objects[index], depth);
        }
        // Expand only build fields; unrelated framework state may contain cyclic references.
        var raw = objects[dataIndex];
        var summary = Reference(raw.GetProperty("summary"), 0)!;
        var header = raw.TryGetProperty("header", out var headerValue) ? Reference(headerValue, 0) : null;
        var actualPatch = header?["patch"]?.ToString() ?? patch;
        var expectedPatch = string.Join('.', patch.Split('.').Take(2));
        var expectedLane = isAram ? "aram" : role.ToString().ToLowerInvariant();
        var headerChampion = int.TryParse(header?["cid"]?.ToString(), out var parsedChampion) ? parsedChampion : championId;
        var headerLane = header?["lane"]?.ToString();
        if (headerChampion != championId || (!isAram && headerLane != expectedLane) || (isAram && headerLane is not null && headerLane is not ("aram" or "all")) || string.Join('.', actualPatch.Split('.').Take(2)) != expectedPatch)
            throw new InvalidDataException("Lolalytics respondió con otro campeón, rol o parche.");
        if (expectedTier != null && !isAram)
        {
            bool matched = objects.EnumerateArray().Any(obj => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("tier", out var t) &&
                obj.TryGetProperty("region", out var r) && obj.TryGetProperty("patch", out var p) && obj.TryGetProperty("lane", out var l) &&
                Reference(t, 0)?.ToString() == expectedTier && Reference(r, 0)?.ToString() == "all" &&
                Reference(p, 0)?.ToString() == expectedPatch && Reference(l, 0)?.ToString() == expectedLane);
            if (!matched) throw new InvalidDataException("Lolalytics no confirmó los filtros D2+, región, posición y parche solicitados.");
        }
        if (expectedTier != null && !isAram && !raw.TryGetProperty("item1", out _)) throw new InvalidDataException("Lolalytics no proporcionó muestras por posición de compra para calcular la confianza.");
        var details = new JsonObject();
        foreach (var field in new[] { "boots", "item1", "item2", "item3", "item4", "item5", "startSet" })
            if (raw.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String) details[field] = Reference(value, 0);
        return ParseBuildOptions(summary, championId, role, actualPatch, details, isAram);
    }
    public static BuildRecommendationOptions ParseBuildOptions(JsonNode summary, int championId, Role role, string patch, JsonNode? details = null, bool isAram = false)
    {
        BuildOption ParseOption(string variant, string label)
        {
            var data = summary[variant] ?? (variant == "pick" ? summary["pick"] : null)
                ?? throw new InvalidDataException($"Lolalytics no entregó la opción {label}.");
            var profile = ParseSummary(summary, championId, role, patch, details, isAram, variant);
            profile.Name = $"{profile.Name} · {label}";
            static (double Rate, int Games)? Stats(JsonNode? node)
            {
                if (node == null || !double.TryParse(node["wr"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var wr) ||
                    !int.TryParse(node["n"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var games) || games <= 0 || wr is < 0 or > 100) return null;
                return (wr / 100d, games);
            }
            var runeStats = Stats(data["runes"]);
            var coreStats = Stats(data["items"]?["core"]);
            if (runeStats == null && coreStats == null) throw new InvalidDataException($"Lolalytics no entregó winrate y partidas para {label}.");
            WinrateInterval? RuneInterval((double Rate, int Games)? stats) => stats is { } value ? WinrateConfidence.Wilson95(value.Rate * value.Games, value.Games) : null;
            var runeConfidence = RuneInterval(runeStats);
            var buildConfidence = RuneInterval(coreStats);
            var score = new[] { runeConfidence?.Lower, buildConfidence?.Lower }.Where(x => x.HasValue).Min()!.Value;
            return new(profile, runeStats?.Rate, runeStats?.Games, runeConfidence, coreStats?.Rate, coreStats?.Games, buildConfidence, score);
        }
        return new(ParseOption("pick", "más popular"), ParseOption("win", "mejor winrate"));
    }

    public static BuildProfile ParseSummary(JsonNode summary, int championId, Role role, string patch, JsonNode? details = null, bool isAram = false, string variant = "pick")
    {
        var pick = summary[variant] ?? throw new InvalidDataException(variant == "pick" ? "Sin build popular de Lolalytics." : "Sin build con mejor winrate de Lolalytics.");
        var runes = pick["runes"]!;
        int[] Values(JsonNode? n) => n?.AsArray().Select(x => x!.GetValue<int>()).ToArray() ?? [];
        var set = runes["set"]!;
        var items = pick["items"]!;
        int StyleId(JsonNode node)
        {
            var id = node.GetValue<int>();
            int[] styles = [8000, 8100, 8200, 8400, 8300];
            return id is >= 0 and < 5 ? styles[id] : id;
        }
        var early = (pick["skillorder"]?["id"]?.ToString() ?? "").Take(3).Select(c => c is >= '1' and <= '4' ? new[] { "Q", "W", "E", "R" }[c - '1'] : "").ToArray();
        if (early.Length != 3 || early.Any(string.IsNullOrEmpty)) early = [];
        var priority = (pick["skillpriority"]?["id"]?.ToString() ?? "").Where(c => c is 'Q' or 'W' or 'E').Distinct().Select(c => c.ToString()).ToArray();
        string earlyTitle = early.Length == 3 ? $"Inicio · lvl1 {early[0]} / lvl2 {early[1]} / lvl3 {early[2]}" : "Inicio · niveles 1–3 sin datos";
        var blocks = new List<ItemBlock> { new() { Name = earlyTitle, Items = Values(items["start"]?["set"]) } };
        var evidence = new List<BuildItemEvidence>();
        static IEnumerable<(int Id, double Winrate, int Games)> Observations(JsonNode? node)
        {
            if (node is not JsonArray rows) yield break;
            foreach (var row in rows)
            {
                JsonNode? id = null, rate = null, count = null;
                if (row is JsonArray a && a.Count >= 4) { id = a[0]; rate = a[1]; count = a[3]; }
                else if (row is JsonObject o) { id = o["id"]; rate = o["wr"]; count = o["n"]; }
                if (int.TryParse(id?.ToString(), out var itemId) && double.TryParse(rate?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var wr) && int.TryParse(count?.ToString(), out var n))
                    yield return (itemId, wr / 100, n);
            }
        }
        void AddRanked(string slot, string title, JsonNode? candidates)
        {
            var ranked = BuildItemConfidence.Rank(slot, Observations(candidates));
            if (ranked.Count == 0) return;
            evidence.AddRange(ranked);
            blocks.Add(new() { Name = title, Items = ranked.Select(x => x.ItemId).ToArray() });
        }
        bool hasPositions = details?["item1"] is JsonArray;
        if (hasPositions)
        {
            if (details!["startSet"] is JsonArray starts)
            {
                var configurations = new Dictionary<int, int[]>();
                var observations = new List<(int Id, double Winrate, int Games)>();
                for (int i = 0; i < starts.Count; i++)
                {
                    if (starts[i] is not JsonArray a || a.Count < 4) continue;
                    var ids = (a[0]?.ToString() ?? "").Split('_').Select(x => int.TryParse(x, out var id) ? id : 0).ToArray();
                    if (ids.Length == 0 || ids.Any(x => x <= 0) || !double.TryParse(a[1]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var wr) || !int.TryParse(a[3]?.ToString(), out var n)) continue;
                    configurations[i + 1] = ids; observations.Add((i + 1, wr / 100, n));
                }
                var rankedStarts = BuildItemConfidence.Rank("start", observations);
                if (rankedStarts.Count > 0)
                {
                    blocks.Clear();
                    foreach (var start in rankedStarts.Take(2))
                    {
                        var ids = configurations[start.ItemId];
                        blocks.Add(new() { Name = earlyTitle, Items = ids });
                        evidence.AddRange(ids.Distinct().Select(id => start with { ItemId = id }));
                    }
                }
            }
            var coreItems = Values(items["core"]?["set"]);
            if (coreItems.Length > 0)
                blocks.Add(new() { Name = variant == "pick" ? "Núcleo · más popular" : "Núcleo · mejor winrate", Items = coreItems });
            AddRanked("boots", "Botas", details!["boots"]);
            for (int position = 1; position <= 5; position++)
            {
                var max = position <= 3 && priority.Length >= position ? $" · maxear {priority[position - 1]}" : "";
                AddRanked($"item{position}", $"Objeto {position}{max}", details![$"item{position}"]);
            }
            if (!evidence.Any(x => x.Slot == "item1")) throw new InvalidDataException("Lolalytics no ofrece objetos con al menos 200 partidas para la primera compra completa.");
        }
        else
        {
            // Older exports may only contain the popular sequence: keep it explicitly unranked.
            blocks.Add(new() { Name = "Núcleo popular · sin datos para confianza", Items = Values(items["core"]?["set"]) });
            foreach (var key in new[] { "item4", "item5", "item6" }) AddRanked(key, $"Alternativas · {key}", items[key]);
        }
        int spell1Id = 0, spell2Id = 0;
        if (pick["sums"] is JsonObject summoners && summoners["ids"] is JsonArray { Count: 2 } spells &&
            spells[0] is JsonValue first && first.TryGetValue<int>(out var firstId) &&
            spells[1] is JsonValue second && second.TryGetValue<int>(out var secondId) &&
            firstId != secondId && Dataset.SupportsSummonerSpell(firstId, isAram) && Dataset.SupportsSummonerSpell(secondId, isAram))
        {
            spell1Id = firstId;
            spell2Id = secondId;
        }
        return new BuildProfile
        {
            Name = isAram ? "Lolalytics · ARAM" : $"Lolalytics · {role}", ChampionId = championId, Role = role, IsAram = isAram,
            PrimaryStyleId = StyleId(runes["page"]!["pri"]!), SubStyleId = StyleId(runes["page"]!["sec"]!),
            Perks = Values(set["pri"]).Concat(Values(set["sec"])).Concat(Values(set["mod"])).ToArray(),
            Spell1Id = spell1Id, Spell2Id = spell2Id,
            EarlySkills = early, SkillPriority = priority, ItemEvidence = evidence,
            Blocks = blocks.Where(b => b.Items.Length > 0).ToList(), AssociatedMaps = isAram ? [12] : [11],
            Source = hasPositions ? (isAram ? "Lolalytics · ARAM · Emerald+ · Global · Wilson 95% · ≥200 partidas" : "Lolalytics · D2+ · Global · Solo/Duo · Wilson 95% · ≥200 partidas · mínimo 3 alternativas por posición y hasta 2 inicios") : "Lolalytics · núcleo popular · sin muestras por posición", Patch = patch
        };
    }
}

