using System.Text.Json;
using System.Text.Json.Serialization;

namespace DraftOptimizer.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("JSON vacío.");
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public enum Role { Top, Jungle, Middle, Bottom, Support }
public static class Roles
{
    public static bool TryParse(string value, out Role role)
    {
        if (value.Equals("utility", StringComparison.OrdinalIgnoreCase)) { role = Role.Support; return true; }
        return Enum.TryParse(value, true, out role) && Enum.IsDefined(role);
    }
}
public sealed record Champion(int Id, string Key, string Name)
{
    public override string ToString() => Name;
    public string Slug => Key == "MonkeyKing" ? "wukong" : Key.ToLowerInvariant();
}
public sealed record Choice(int Id, string Name, string? IconUrl = null)
{
    public override string ToString() => $"{Name} · {Id}";
}
public enum FlashKey { Profile, D, F }

public sealed class BuildProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Mi configuración";
    public int ChampionId { get; set; }
    public Role Role { get; set; } = Role.Middle;
    public bool IsAram { get; set; }
    public int PrimaryStyleId { get; set; } = 8100;
    public int SubStyleId { get; set; } = 8200;
    public int[] Perks { get; set; } = [];
    public int Spell1Id { get; set; }
    public int Spell2Id { get; set; }

    public (int D, int F) SpellsFor(FlashKey flashKey)
    {
        const int flashId = 4;
        bool swap = (flashKey == FlashKey.D && Spell2Id == flashId) ||
                    (flashKey == FlashKey.F && Spell1Id == flashId);
        return swap ? (Spell2Id, Spell1Id) : (Spell1Id, Spell2Id);
    }
    public List<ItemBlock> Blocks { get; set; } = [];
    public string[] EarlySkills { get; set; } = [];
    public string[] SkillPriority { get; set; } = [];
    public List<BuildItemEvidence> ItemEvidence { get; set; } = [];
    public int[] AssociatedMaps { get; set; } = [11];
    public string Source { get; set; } = "Personalizada";
    public string Patch { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public override string ToString() => Name;
}
public sealed record BuildOption(BuildProfile Profile, double? RuneWinrate, int? RuneGames, WinrateInterval? RuneConfidence,
    double? BuildWinrate, int? BuildGames, WinrateInterval? BuildConfidence, double ConfidenceScore);
public sealed record BuildRecommendationOptions(BuildOption Popular, BuildOption HighestWinrate)
{
    // The conservative lower bound decides the initial choice; ties favor the popular option.
    public BuildOption Recommended => HighestWinrate.ConfidenceScore > Popular.ConfidenceScore ? HighestWinrate : Popular;
}
public sealed class ItemBlock
{
    public string Name { get; set; } = "Objetos";
    public int[] Items { get; set; } = [];
    public string HideIfSummonerSpell { get; set; } = "";
    public string ShowIfSummonerSpell { get; set; } = "";
}
public sealed class Player
{
    public int CellId { get; set; }
    public int ChampionId { get; set; }
    public int ChampionPickIntent { get; set; }
    public string AssignedPosition { get; set; } = "";
    [JsonIgnore] public int VisibleChampion => ChampionId > 0 ? ChampionId : ChampionPickIntent;
}
public sealed class DraftAction
{
    public int Id { get; set; }
    public int ActorCellId { get; set; }
    public string Type { get; set; } = "";
    public bool Completed { get; set; }
    public bool IsInProgress { get; set; }
    public int ChampionId { get; set; }
}
public sealed class DraftSession
{
    public long GameId { get; set; }
    public int QueueId { get; set; }
    public bool BenchEnabled { get; set; }
    public bool AllowRerolling { get; set; }
    public int[] BenchChampionIds { get; set; } = [];
    public List<AramBenchChampion> BenchChampions { get; set; } = [];
    public DraftTimer? Timer { get; set; }
    public int LocalPlayerCellId { get; set; }
    public List<Player> MyTeam { get; set; } = [];
    public List<Player> TheirTeam { get; set; } = [];
    public List<List<DraftAction>> Actions { get; set; } = [];
    [JsonIgnore] public DraftAction? CurrentPick => Actions.SelectMany(x => x).FirstOrDefault(x => x.ActorCellId == LocalPlayerCellId && x.Type == "pick" && x.IsInProgress && !x.Completed);
    [JsonIgnore] public DraftAction? CurrentBan => Actions.SelectMany(x => x).FirstOrDefault(x => x.ActorCellId == LocalPlayerCellId && x.Type == "ban" && x.IsInProgress && !x.Completed);
    [JsonIgnore] public int LocalVisibleChampion => CurrentPick is { ChampionId: > 0 } action ? action.ChampionId : MyTeam.FirstOrDefault(p => p.CellId == LocalPlayerCellId)?.VisibleChampion ?? 0;
    [JsonIgnore] public int LocalLockedChampion => Actions.SelectMany(a => a).FirstOrDefault(a => a.ActorCellId == LocalPlayerCellId && a.Type == "pick" && a.Completed && a.ChampionId > 0)?.ChampionId ??
        (IsFinalization ? MyTeam.FirstOrDefault(p => p.CellId == LocalPlayerCellId)?.ChampionId ?? 0 : 0);
    [JsonIgnore] public bool IsFinalization => string.Equals(Timer?.Phase, "FINALIZATION", StringComparison.OrdinalIgnoreCase) ||
        (MyTeam.Count > 0 && TheirTeam.Count > 0 && MyTeam.Concat(TheirTeam).All(p => p.ChampionId > 0) &&
        Actions.SelectMany(a => a).Count(a => a.Type == "pick") >= MyTeam.Count + TheirTeam.Count && Actions.SelectMany(a => a).Where(a => a.Type == "pick").All(a => a.Completed));
    [JsonIgnore] public bool IsAram => QueueId == 450 || BenchEnabled || AllowRerolling || BenchChampionIds.Length > 0 || BenchChampions.Count > 0;
    [JsonIgnore] public HashSet<int> AramAvailableChampionIds => BenchChampionIds.Concat(BenchChampions.Where(x => !x.IsDisabled).Select(x => x.ChampionId)).Where(x => x > 0).ToHashSet();
    [JsonIgnore] public bool HasCompositionPicks => MyTeam.Where(p => p.CellId != LocalPlayerCellId).Concat(TheirTeam).Any(p => p.ChampionId > 0) ||
        Actions.SelectMany(a => a).Any(a => a.Type == "pick" && a.Completed && a.ChampionId > 0) ||
        (IsFinalization && MyTeam.Any(p => p.ChampionId > 0));
    public bool AllyHasPickIntent(int championId) => MyTeam.Any(p => p.CellId != LocalPlayerCellId && p.ChampionPickIntent == championId);
    public HashSet<int> Unavailable(bool includeLocalPlayer = true) => MyTeam.Where(p => includeLocalPlayer || p.CellId != LocalPlayerCellId).Concat(TheirTeam).Where(x => x.ChampionId > 0).Select(x => x.ChampionId)
        .Concat(Actions.SelectMany(x => x).Where(x => x.Type == "ban" && x.ChampionId > 0).Select(x => x.ChampionId)).ToHashSet();
}
public sealed class AramBenchChampion
{
    public int ChampionId { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsInTeam { get; set; }
    public bool IsOnBench { get; set; }
}
public sealed class DraftTimer
{
    public double? AdjustedTimeLeftInPhase { get; set; }
    public long InternalNowInEpochMs { get; set; }
    public bool IsInfinite { get; set; }
    public string Phase { get; set; } = "";
    public double? RemainingMilliseconds(DateTimeOffset now)
    {
        if (IsInfinite || !string.Equals(Phase, "BAN_PICK", StringComparison.OrdinalIgnoreCase) ||
            AdjustedTimeLeftInPhase is not { } remaining || !double.IsFinite(remaining) || InternalNowInEpochMs <= 0) return null;
        var elapsed = now.ToUnixTimeMilliseconds() - InternalNowInEpochMs;
        if (elapsed < -250) return null;
        return Math.Max(0, remaining - Math.Max(0, elapsed));
    }
}

public sealed record Suggestion(Champion Champion, double BaseWinrate, int Games, double Score, int ContextSamples)
{
    public bool IsAram { get; init; }
    public DraftAnalysisResult? DraftAnalysis { get; init; }
    public WinrateInterval? GlobalConfidenceInterval { get; init; }
    public bool UsesGlobalConfidence => GlobalConfidenceInterval.HasValue;
    public double? DraftWinrate => DraftAnalysis?.Winrate;
    public bool HasDraftConfidence => DraftAnalysis?.HasConfidence == true;
    public bool HasConfidence => UsesGlobalConfidence || HasDraftConfidence;
    public WinrateInterval ConfidenceInterval => GlobalConfidenceInterval ?? (DraftAnalysis is { HasConfidence: true } analysis ? new(analysis.ConfidenceLower, analysis.ConfidenceUpper) : new(0, 1));
    public double ConfidenceScore => HasConfidence ? Score : 0;
    public double ContextAdjustment => DraftWinrate.HasValue ? 100 * (DraftWinrate.Value - BaseWinrate) : 0;
    public string Label => UsesGlobalConfidence
        ? $"{Champion.Name} · WR del rol {BaseWinrate:P1} · {Games:N0} partidas del rol · confianza global {Score:F1}"
        : $"{Champion.Name} · WR draft {(DraftWinrate.HasValue ? DraftWinrate.Value.ToString("P1") : "—")} · WR base {BaseWinrate:P1} · {Games:N0} partidas · puntaje {Score:F1} · contexto {ContextSamples}";
}
public sealed record ClientState(string Phase, DraftSession? Draft);
