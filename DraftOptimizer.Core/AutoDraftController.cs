namespace DraftOptimizer.Core;

public sealed record AutomaticPickRequest(long GameId, int ActionId, int CellId, int ChampionId, int ExpectedVisibleChampion, Role Role, bool Complete, int LockInSeconds = 1);

/// <summary>One serial tick per observed client state. Never schedules a detached lock-in.</summary>
public sealed class AutoDraftController
{
    private (long GameId, int ActionId, int CellId)? _turn;
    private int? _ownedChampion;
    private bool _observedOwned, _paused, _locked;
    private DateTimeOffset _hoverSentAt;
    public string Status { get; private set; } = "Añade favoritos para tu posición.";
    public void Reset()
    {
        _turn = null; _ownedChampion = null; _observedOwned = _paused = _locked = false;
        Status = "Esperando tu turno de selección.";
    }
    public void PauseCurrentTurn(DraftSession? draft = null)
    {
        if (draft?.CurrentPick is { } action) _turn = (draft.GameId, action.Id, draft.LocalPlayerCellId);
        _paused = true;
        Status = "Automatización pausada para este turno; selección manual disponible.";
    }
    public async Task TickAsync(Dataset data, Dataset? context, DraftSession? draft, HashSet<int>? pickable,
        FavoriteSettings settings, Role role, DateTimeOffset now, Func<AutomaticPickRequest, Task<bool>> send, IReadOnlyList<Suggestion>? recommendations = null)
    {
        if (draft?.CurrentPick is not { } action) { Reset(); return; }
        var turn = (draft.GameId, action.Id, draft.LocalPlayerCellId);
        if (_turn != turn)
        {
            Reset(); _turn = turn;
        }
        if (!settings.AutoHoverEnabled) { Status = "Preselección automática desactivada."; return; }
        if (context == null && draft.HasCompositionPicks) { Status = "Esperando estadísticas de contexto para evaluar confianza del draft."; return; }
        if (_paused) { Status = "Automatización pausada para este turno; selección manual disponible."; return; }
        if (_locked) return;
        var favorites = settings.ForRole(role);
        if (pickable == null) { Status = "Esperando disponibilidad de campeones."; return; }
        var current = draft.LocalVisibleChampion;
        if (_ownedChampion.HasValue)
        {
            if (current == _ownedChampion) _observedOwned = true;
            else if (_observedOwned || now - _hoverSentAt > TimeSpan.FromSeconds(1)) { PauseCurrentTurn(); return; }
            else { Status = "Esperando que League refleje la preselección…"; return; }
        }
        var best = (recommendations ?? data.Suggest(role, draft, context, pickable, includeLocalSelection: false))
            .Where(s => (favorites.Count == 0 || favorites.Contains(s.Champion.Id)) && s.HasConfidence).OrderByDescending(s => s.Score).ThenByDescending(s => s.Games).ThenBy(s => s.Champion.Id).FirstOrDefault();
        if (best == null) { Status = "No hay candidatos disponibles con estadísticas suficientes para esta posición."; return; }
        var remaining = draft.Timer?.RemainingMilliseconds(now);
        if (remaining == 0) { Status = "El temporizador expiró; no se enviará una selección."; return; }
        int lockInSeconds = Math.Clamp(settings.AutoLockSeconds, 1, 30);
        bool lastSecond = settings.AutoLockEnabled && remaining.HasValue && remaining.Value > 0 && remaining.Value <= lockInSeconds * 1000d;
        // The LCU sender revalidates the turn, hovered champion, availability and timer.
        bool complete = lastSecond;
        if (current == best.Champion.Id && !complete)
        {
            _ownedChampion ??= current; _observedOwned = true;
            Status = Describe(best, context, remaining, settings.AutoLockEnabled, lockInSeconds);
            return;
        }
        if (current != best.Champion.Id || complete)
        {
            var request = new AutomaticPickRequest(draft.GameId, action.Id, draft.LocalPlayerCellId, best.Champion.Id, current, role, complete, lockInSeconds);
            try
            {
                if (!await send(request)) { PauseCurrentTurn(); return; }
            }
            catch { PauseCurrentTurn(); throw; }
            _ownedChampion = best.Champion.Id; _observedOwned = current == best.Champion.Id; _hoverSentAt = now;
            _locked = complete;
            Status = complete ? $"Confirmado automáticamente: {best.Champion.Name}." : Describe(best, context, remaining, settings.AutoLockEnabled, lockInSeconds);
        }
    }
    private static string Describe(Suggestion best, Dataset? context, double? remaining, bool autoLock, int lockInSeconds) =>
        $"Mejor opción: {best.Champion.Name} · confianza {(best.UsesGlobalConfidence ? "global" : "del draft")} {best.Score:F1} · evidencia {(best.UsesGlobalConfidence ? best.Games : best.DraftAnalysis?.EvidenceGames):N0} partidas · pares {best.ContextSamples}. " +
        (best.DraftWinrate.HasValue ? $"WR estimado del draft: {best.DraftWinrate.Value:P1}. " : "") +
        (best.UsesGlobalConfidence ? "Orden inicial por WR y partidas del rol seleccionado. " : "") +
        (autoLock ? remaining.HasValue ? $"Confirmación al quedar {lockInSeconds} s · quedan {Math.Ceiling(remaining.Value / 1000):F0} s." : "Sin temporizador válido: no se confirmará automáticamente." : "Confirmación automática desactivada.");
}
