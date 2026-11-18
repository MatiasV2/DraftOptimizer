namespace DraftOptimizer.Core;

public sealed record FinalLoadoutTarget(long GameId, int ChampionId, Role Role, bool RequireFinalization = true);

/// <summary>Prepares and applies once after local lock-in, revalidating game and champion before writes.</summary>
public sealed class LoadoutCoordinator : IDisposable
{
    private string? _key;
    private CancellationTokenSource? _cancellation;
    private Task? _preparation, _application;
    private BuildProfile? _ready;
    private bool _failed;
    private string? _inputFingerprint;
    private static string Fingerprint(BuildProfile profile) => Json.Write(new { profile.Id, profile.Name, profile.ChampionId, profile.Role, profile.IsAram, profile.PrimaryStyleId, profile.SubStyleId, profile.Perks, profile.Spell1Id, profile.Spell2Id, profile.Blocks, profile.AssociatedMaps });
    public Task Pending => _application ?? _preparation ?? Task.CompletedTask;
    public string Status { get; private set; } = "Runas y objetos automáticos: esperando tu campeón confirmado.";
    public BuildProfile? Prepared => _ready;
    public void Reset()
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
        _key = null; _preparation = _application = null; _ready = null; _failed = false;
        _inputFingerprint = null;
    }
    public void Observe(ClientState state, Dataset data, Role fallbackRole, BuildProfile? selected, bool enabled,
        Func<Champion, Role, string, bool, CancellationToken, Task<BuildProfile>> fetch,
        Func<BuildProfile, FinalLoadoutTarget, CancellationToken, Task> apply)
    {
        if (!enabled) { Reset(); Status = "Aplicación automática de runas y objetos desactivada."; return; }
        if (state.Phase != "ChampSelect" || state.Draft == null)
        {
            if (_key != null) Reset();
            Status = "Runas y objetos: esperando selección de campeones."; return;
        }
        var draft = state.Draft;
        var champion = data.Champions.FirstOrDefault(c => c.Id == draft.LocalLockedChampion);
        if (champion == null) { Reset(); Status = "Runas y objetos: esperando tu campeón confirmado."; return; }
        var assigned = draft.MyTeam.FirstOrDefault(p => p.CellId == draft.LocalPlayerCellId)?.AssignedPosition ?? "";
        var role = Roles.TryParse(assigned, out var actualRole) ? actualRole : fallbackRole;
        if (selected?.ChampionId != champion.Id || selected.Role != role || selected.IsAram != draft.IsAram) selected = null;
        var fingerprint = selected == null ? "D2+" : Fingerprint(selected);
        if (_ready != null && selected != null && Fingerprint(_ready) == fingerprint) fingerprint = _inputFingerprint ?? fingerprint;
        var key = $"{draft.GameId}|{champion.Id}|{role}|{draft.IsAram}|{data.Patch}|{fingerprint}";
        if (_key != key)
        {
            Reset(); _key = key; _inputFingerprint = fingerprint; _cancellation = new();
            var token = _cancellation.Token;
            _preparation = PrepareAsync(key, champion, role, draft.IsAram, selected == null ? null : Json.Read<BuildProfile>(Json.Write(selected)), data, fetch, token);
        }
        if (_ready != null && !_failed && _application == null)
        {
            var token = _cancellation!.Token;
            _application = ApplyAsync(key, _ready, new(draft.GameId, champion.Id, role, RequireFinalization: false), apply, token);
        }
    }
    private async Task PrepareAsync(string key, Champion champion, Role role, bool isAram, BuildProfile? selected, Dataset data,
        Func<Champion, Role, string, bool, CancellationToken, Task<BuildProfile>> fetch, CancellationToken ct)
    {
        try
        {
            Status = selected == null || selected.Perks.Length == 0 ? $"Preparando Lolalytics {(isAram ? "ARAM Emerald+" : "D2+")} para {champion.Name}…" : $"Preparando perfil {selected.Name} · {champion.Name} · {role}…";
            var result = selected;
            if (result == null || result.Perks.Length == 0)
            {
                var automatic = await fetch(champion, role, data.Patch, isAram, ct);
                ct.ThrowIfCancellationRequested();
                data.Validate(automatic);
                if (automatic.ChampionId != champion.Id || automatic.Role != role) throw new InvalidDataException("Lolalytics devolvió otro campeón o posición.");
                if (result == null)
                {
                    result = automatic;
                    var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"DraftOptimizer:{(isAram ? "ARAM" : "D2+")}:{champion.Id}:{role}"));
                    result.Id = new Guid(hash.AsSpan(0, 16)); result.Name = isAram ? $"{champion.Name} - ARAM" : $"{champion.Name} - {role}";
                }
                else
                {
                    result.PrimaryStyleId = automatic.PrimaryStyleId;
                    result.SubStyleId = automatic.SubStyleId;
                    result.Perks = automatic.Perks.ToArray();
                    bool addSpells = result.Spell1Id == 0 && result.Spell2Id == 0 && automatic.Spell1Id != 0;
                    if (addSpells)
                    {
                        result.Spell1Id = automatic.Spell1Id;
                        result.Spell2Id = automatic.Spell2Id;
                    }
                    result.Source += $" · {(addSpells ? "runas y hechizos" : "runas")} Lolalytics {(isAram ? "ARAM" : "D2+")}";
                }
            }
            ct.ThrowIfCancellationRequested(); data.Validate(result);
            if (result.ChampionId != champion.Id || result.Role != role) throw new InvalidDataException("La configuración preparada corresponde a otro campeón o posición.");
            if (_key != key) return;
            _ready = result;
            Status = $"Configuración lista: {result.Name} · {champion.Name}{(isAram ? " · ARAM" : $" · {role}")}. Se aplicará al campeón confirmado.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (_key == key) { _failed = true; Status = "No se pudo preparar la configuración: " + ex.Message + " Usa Reintentar configuración."; } }
    }
    private async Task ApplyAsync(string key, BuildProfile profile, FinalLoadoutTarget target,
        Func<BuildProfile, FinalLoadoutTarget, CancellationToken, Task> apply, CancellationToken ct)
    {
        try
        {
            Status = profile.Spell1Id == 0 ? "Aplicando runas y objetos al campeón confirmado…" : "Aplicando hechizos, runas y objetos al campeón confirmado…";
            await apply(profile, target, ct);
            if (_key == key && !ct.IsCancellationRequested) Status = $"{(profile.Spell1Id == 0 ? "Runas y objetos" : "Hechizos, runas y objetos")} aplicados a League: {profile.Name}.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (_key == key) { _failed = true; Status = "No se completó la aplicación automática: " + ex.Message + " Usa Reintentar configuración."; } }
    }
    public void Dispose() => Reset();
}
