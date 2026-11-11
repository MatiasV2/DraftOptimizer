using System.Collections.ObjectModel;

namespace DraftOptimizer.Core;

public sealed record SimulatedPick(int Side, Role Role, int ChampionId);

public sealed class DraftSimulation
{
    private static readonly int[] Order = [0, 1, 1, 0, 0, 1, 1, 0, 0, 1];
    private readonly List<SimulatedPick> _picks = [];
    public ReadOnlyCollection<SimulatedPick> Picks => _picks.AsReadOnly();
    public bool Complete => _picks.Count == Order.Length;
    public int NextSide => Complete ? -1 : Order[_picks.Count];
    public bool CanUseRole(Role role) => Enum.IsDefined(role) && !Complete && !_picks.Any(p => p.Side == NextSide && p.Role == role);
    public void Pick(int championId, Role role)
    {
        if (championId <= 0 || _picks.Any(p => p.ChampionId == championId)) throw new InvalidOperationException("Campeón inválido o ya seleccionado.");
        if (!CanUseRole(role)) throw new InvalidOperationException("Posición ocupada o draft completo.");
        _picks.Add(new(NextSide, role, championId));
    }
    public void Undo() { if (_picks.Count > 0) _picks.RemoveAt(_picks.Count - 1); }
    public void Reset() => _picks.Clear();
    public DraftSession Session(int side)
    {
        if (side is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(side));
        return new DraftSession
        {
            GameId = -1, LocalPlayerCellId = -1,
            MyTeam = _picks.Where(p => p.Side == side).Select((p, i) => new Player { CellId = i, ChampionId = p.ChampionId, AssignedPosition = p.Role.ToString() }).ToList(),
            TheirTeam = _picks.Where(p => p.Side != side).Select((p, i) => new Player { CellId = i + 5, ChampionId = p.ChampionId, AssignedPosition = p.Role.ToString() }).ToList()
        };
    }
}
