using System.Text.Json;

namespace DraftOptimizer.Core;

public sealed class Dataset : IDisposable
{
    private readonly JsonDocument _data;
    private readonly object _lifetime = new(), _suggestionLock = new();
    private int _references = 1;
    private bool _ownerReleased;
    public IDisposable Retain()
    {
        lock (_lifetime) { ObjectDisposedException.ThrowIf(_ownerReleased, this); _references++; }
        return new Lease(this);
    }
    private sealed class Lease(Dataset owner) : IDisposable
    {
        private Dataset? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
    private void Release() { lock (_lifetime) { if (--_references == 0) _data.Dispose(); } }

    private string? _suggestionCacheKey;
    private Dataset? _suggestionCacheContext;
    private List<Suggestion>? _suggestionCache;
    private readonly Dictionary<Role, IReadOnlyDictionary<int, GlobalConfidencePlacement>> _globalConfidenceByRole = [];
    private IReadOnlyDictionary<(string Table, int Id), string> _localizedNames = new Dictionary<(string, int), string>();
    public Dataset(string text) : this(JsonDocument.Parse(text)) { }
    private Dataset(JsonDocument document)
    {
        _data = document;
        Champions = Root.GetProperty("championData").EnumerateObject().Select(p =>
            new Champion(int.Parse(p.Name), p.Value.GetProperty("id").GetString()!, p.Value.GetProperty("name").GetString()!)).OrderBy(x => x.Name).ToList();
    }
    public static Task<Dataset> LoadFileAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonDocument.ParseAsync(input, cancellationToken: ct).ConfigureAwait(false);
        try { ct.ThrowIfCancellationRequested(); return new Dataset(document); }
        catch { document.Dispose(); throw; }
    }, ct);
    public JsonElement Root => _data.RootElement;
    public List<Champion> Champions { get; }
    public string Patch => Root.GetProperty("version").GetString()!;
    public string Date => Root.GetProperty("date").GetString()!;
    public string NameOf(int id) => Champions.FirstOrDefault(c => c.Id == id)?.Name ?? (id == 0 ? "—" : id.ToString());
    public string DisplayName(string table, int id)
    {
        var translations = Volatile.Read(ref _localizedNames);
        if (translations.TryGetValue((table, id), out var translated)) return translated;
        if (Root.TryGetProperty(table, out var values) && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(id.ToString(), out var value) && value.TryGetProperty("name", out var name)) return name.GetString() ?? id.ToString();
        return id.ToString();
    }
    public void ApplyLocalizedNames(IReadOnlyDictionary<(string Table, int Id), string> names) => Interlocked.Exchange(ref _localizedNames, names);
    public List<Choice> Choices(string key) => Root.GetProperty(key).EnumerateObject()
        .Select(p =>
        {
            var id = int.Parse(p.Name);
            string? icon = key == "itemData" ? $"https://ddragon.leagueoflegends.com/cdn/{Patch}/img/item/{id}.png" :
                p.Value.TryGetProperty("icon", out var iconNode) && iconNode.GetString() is { Length: > 0 } path
                    ? $"https://ddragon.leagueoflegends.com/cdn/img/{path}" : null;
            return new Choice(id, DisplayName(key, id), icon);
        }).OrderBy(c => c.Name).ToList();
    public List<Choice> Runes(int path, int? slot = null) => Root.GetProperty("runeData").EnumerateObject()
        .Where(p => p.Value.GetProperty("pathId").GetInt32() == path && (slot == null || p.Value.GetProperty("slot").GetInt32() == slot))
        .Select(p => new Choice(int.Parse(p.Name), DisplayName("runeData", int.Parse(p.Name)),
            p.Value.TryGetProperty("icon", out var icon) && icon.GetString() is { Length: > 0 } pathName ? $"https://ddragon.leagueoflegends.com/cdn/img/{pathName}" : null)).ToList();
    public int RuneSlot(int id) => Root.GetProperty("runeData").GetProperty(id.ToString()).GetProperty("slot").GetInt32();
    public List<Choice> Shards(int slot) => Root.GetProperty("statShardData").EnumerateObject()
        .Where(p => p.Value.GetProperty("positions").EnumerateArray().Any(s => s.GetProperty("slot").GetInt32() == slot))
        .Select(p => new Choice(int.Parse(p.Name), DisplayName("statShardData", int.Parse(p.Name)))).ToList();

    internal static bool SupportsSummonerSpell(int id, bool isAram) =>
        id is 1 or 3 or 4 or 6 or 7 or 14 or 21 || (isAram ? id is 13 or 32 : id is 11 or 12);

    public IReadOnlyList<Choice> SummonerSpells(bool isAram) =>
        Root.TryGetProperty("summonerSpellData", out var spells)
            ? spells.EnumerateObject()
                .Select(entry => int.Parse(entry.Name))
                .Where(id => SupportsSummonerSpell(id, isAram))
                .Select(id => new Choice(id, SummonerSpellName(id)))
                .OrderBy(spell => spell.Name).ToList()
            : [];

    public string SummonerSpellName(int id) => id switch
    {
        1 => "Purificación", 3 => "Extenuación", 4 => "Destello", 6 => "Fantasma",
        7 => "Curación", 11 => "Castigo", 12 => "Teleportación", 13 => "Claridad",
        14 => "Ignición", 21 => "Barrera", 32 => "Marca",
        _ => DisplayName("summonerSpellData", id)
    };

    public void ValidateSummonerSpells(BuildProfile profile)
    {
        if (profile.Spell1Id == 0 && profile.Spell2Id == 0) return;
        if (profile.Spell1Id == 0 || profile.Spell2Id == 0 || profile.Spell1Id == profile.Spell2Id)
            throw new InvalidDataException("Elige dos hechizos de invocador distintos o deja ambos sin configurar.");
        if (!Root.TryGetProperty("summonerSpellData", out var spells) ||
            !SupportsSummonerSpell(profile.Spell1Id, profile.IsAram) || !SupportsSummonerSpell(profile.Spell2Id, profile.IsAram) ||
            !spells.TryGetProperty(profile.Spell1Id.ToString(), out _) || !spells.TryGetProperty(profile.Spell2Id.ToString(), out _))
            throw new InvalidDataException("Uno de los hechizos de invocador no está disponible para este modo.");
    }

    public void Validate(BuildProfile profile, bool requireRunes = true)
    {
        if (!Enum.IsDefined(profile.Role)) throw new InvalidDataException("Posición inválida.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80) throw new InvalidDataException("El nombre debe tener entre 1 y 80 caracteres.");
        if (!Champions.Any(c => c.Id == profile.ChampionId)) throw new InvalidDataException("Selecciona un campeón válido.");
        if (profile.AssociatedMaps == null || profile.AssociatedMaps.Any(x => x <= 0)) throw new InvalidDataException("Mapas inválidos.");
        if (profile.Blocks == null || profile.Blocks.Count == 0 || profile.Blocks.Any(b => b == null || string.IsNullOrWhiteSpace(b.Name) || b.Items == null || b.Items.Length == 0 || b.Items.Any(id => !Root.GetProperty("itemData").TryGetProperty(id.ToString(), out _))))
            throw new InvalidDataException("Incluye al menos un bloque con objetos válidos del catálogo.");
        ValidateSummonerSpells(profile);
        if (profile.Perks == null) throw new InvalidDataException("Lista de runas inválida.");
        if (profile.EarlySkills == null || profile.SkillPriority == null || profile.ItemEvidence == null ||
            profile.EarlySkills.Any(s => s is not ("Q" or "W" or "E" or "R")) ||
            profile.SkillPriority.Any(s => s is not ("Q" or "W" or "E")) ||
            profile.ItemEvidence.Any(e => e == null || e.ItemId <= 0 || e.Games < 200 || !double.IsFinite(e.Winrate) || e.Winrate is < 0 or > 1 ||
                !double.IsFinite(e.ConfidenceLower) || !double.IsFinite(e.ConfidenceUpper) || e.ConfidenceLower < 0 || e.ConfidenceUpper > 1 || e.ConfidenceLower > e.ConfidenceUpper))
            throw new InvalidDataException("Habilidades o evidencia de objetos inválidas.");
        if (!requireRunes && profile.Perks.Length == 0) return;
        if (profile.Perks.Length == 0) throw new InvalidDataException("Esta build solo contiene objetos. Configura las runas antes de aplicarlas a League.");
        if (profile.PrimaryStyleId == profile.SubStyleId) throw new InvalidDataException("Las ramas de runas deben ser diferentes.");
        if (profile.Perks.Length != 9) throw new InvalidDataException("Selecciona cuatro runas primarias, dos secundarias y tres fragmentos.");
        for (int i = 0; i < 4; i++)
            if (!Runes(profile.PrimaryStyleId, i).Any(x => x.Id == profile.Perks[i])) throw new InvalidDataException($"Runa primaria {i + 1} inválida.");
        var secondary = Runes(profile.SubStyleId).Where(x => profile.Perks.Skip(4).Take(2).Contains(x.Id)).ToList();
        var runeData = Root.GetProperty("runeData");
        if (secondary.Count != 2 || secondary.Any(x => runeData.GetProperty(x.Id.ToString()).GetProperty("slot").GetInt32() == 0) ||
            secondary.Select(x => runeData.GetProperty(x.Id.ToString()).GetProperty("slot").GetInt32()).Distinct().Count() != 2)
            throw new InvalidDataException("Selecciona dos runas secundarias de filas diferentes, sin piedra angular.");
        for (int i = 0; i < 3; i++)
            if (!Shards(i).Any(x => x.Id == profile.Perks[i + 6])) throw new InvalidDataException($"Fragmento {i + 1} inválido.");
    }
    public List<Suggestion> Suggest(Role role, DraftSession? draft, Dataset? context, HashSet<int>? pickable = null, bool includeLocalSelection = true, CancellationToken ct = default)
    {
        lock (_suggestionLock) { ct.ThrowIfCancellationRequested(); return SuggestCore(role, draft, context, pickable, includeLocalSelection, ct); }
    }
    internal IReadOnlyDictionary<int, GlobalConfidencePlacement> GlobalConfidenceByRole(Role role, CancellationToken ct)
    {
        lock (_suggestionLock)
        {
            ct.ThrowIfCancellationRequested();
            if (!_globalConfidenceByRole.TryGetValue(role, out var placements))
            {
                placements = GlobalConfidencePlacement.RankSuggestions(SuggestCore(role, null, null, null, true, ct));
                _globalConfidenceByRole.Add(role, placements);
            }
            return placements;
        }
    }

    private List<Suggestion> SuggestCore(Role role, DraftSession? draft, Dataset? context, HashSet<int>? pickable, bool includeLocalSelection, CancellationToken ct)
    {
        // Timer ticks do not alter the composition. Picks, intents, roles, bans/actions
        // and availability all participate in the key, including in-place mutations.
        var draftKey = draft == null ? "" : Json.Write(new { draft.GameId, draft.LocalPlayerCellId, draft.MyTeam, draft.TheirTeam, draft.Actions, Phase = draft.Timer?.Phase });
        var cacheKey = $"{role}|{includeLocalSelection}|{draftKey}|{(pickable == null ? "all" : string.Join(',', pickable.OrderBy(x => x)))}";
        if (_suggestionCacheKey == cacheKey && ReferenceEquals(context, _suggestionCacheContext) && _suggestionCache != null) return [.. _suggestionCache];
        var excluded = draft?.Unavailable(includeLocalSelection) ?? [];
        var result = new List<Suggestion>();
        bool global = draft == null || !draft.HasCompositionPicks;
        var composition = global ? null : DraftRoleInference.Infer(this, context, draft!, role);
        var analyzer = global || context == null ? null : new DraftAnalyzer(this, context);
        foreach (var c in Champions)
        {
            ct.ThrowIfCancellationRequested();
            if (excluded.Contains(c.Id) || (pickable != null && !pickable.Contains(c.Id))) continue;
            double wins; int games;
            {
                if (!Stats(c.Id, role, out var stats)) continue;
                var observation = StatObservation.Read(stats);
                if (observation.Games > int.MaxValue) continue;
                games = (int)observation.Games; wins = observation.Wins;
            }
            if (games < 200) continue;
            if (!double.IsFinite(wins) || wins < 0 || wins > games) continue;
            var wr = wins / games;
            var analysis = analyzer?.Analyze(composition!.Allies.WithCandidate(role, c.Id), composition.Enemies);
            WinrateInterval? initial = global ? WinrateConfidence.Wilson95(wins, games) : null;
            double score = initial.HasValue ? initial.Value.Lower * 100 : analysis?.HasConfidence == true ? analysis.ConfidenceLower * 100 : 0;
            int pairs = analysis == null ? 0 : analysis.MatchupPairs + analysis.SynergyPairs;
            result.Add(new(c, wr, games, score, pairs) { DraftAnalysis = analysis, GlobalConfidenceInterval = initial });
        }
        _suggestionCache = result.OrderByDescending(x => x.Score).ThenByDescending(x => x.Games).ThenBy(x => x.Champion.Id).ToList();
        _suggestionCacheKey = cacheKey; _suggestionCacheContext = context;
        return [.. _suggestionCache];
    }
    internal bool Stats(int id, Role role, out JsonElement stats)
    {
        stats = default;
        return Root.GetProperty("championData").TryGetProperty(id.ToString(), out var c) &&
            c.ValueKind == JsonValueKind.Object && c.TryGetProperty("statsByRole", out var roles) &&
            roles.ValueKind == JsonValueKind.Object && roles.TryGetProperty(((int)role).ToString(), out stats) &&
            stats.ValueKind == JsonValueKind.Object;
    }
    public void Dispose()
    {
        lock (_lifetime) { if (_ownerReleased) return; _ownerReleased = true; }
        Release();
    }
}

public sealed class DatasetProvider(HttpClient http, string directory)
{
    public async Task<(Dataset Data, bool Cached)> LoadAsync(string name, CancellationToken ct = default)
    {
        if (name is not ("current-patch" or "30-days")) throw new ArgumentException("Dataset desconocido.");
        Directory.CreateDirectory(directory);
        var cache = Path.Combine(directory, name + ".json");
        var temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = await http.GetStreamAsync($"https://bucket.draftgap.com/datasets/v5/{name}.json", ct).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            var data = await Dataset.LoadFileAsync(temporary, ct).ConfigureAwait(false);
            try { File.Move(temporary, cache, true); return (data, false); }
            catch { data.Dispose(); throw; }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is (HttpRequestException or TaskCanceledException or JsonException))
        {
            var path = File.Exists(cache) ? cache : Path.Combine(AppContext.BaseDirectory, "Data", name + ".json");
            if (!File.Exists(path)) throw new IOException("Sin estadísticas disponibles; revisa Internet.", ex);
            return (await Dataset.LoadFileAsync(path, ct).ConfigureAwait(false), true);
        }
        finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } } }
    }
}


