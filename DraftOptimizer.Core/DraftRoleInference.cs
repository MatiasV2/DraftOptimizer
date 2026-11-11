namespace DraftOptimizer.Core;

public sealed record TeamComposition(Dictionary<Role, int> Picks, HashSet<Role> InferredRoles)
{
    public TeamComposition WithCandidate(Role role, int championId)
    {
        var picks = new Dictionary<Role, int>(Picks) { [role] = championId };
        var inferred = InferredRoles.ToHashSet(); inferred.Remove(role);
        return new(picks, inferred);
    }
}
public sealed record DraftComposition(TeamComposition Allies, TeamComposition Enemies);

/// <summary>Most likely non-overlapping roles, following DraftGap's role-combination model.</summary>
public static class DraftRoleInference
{
    public static DraftComposition Infer(Dataset data, Dataset? context, DraftSession session, Role? candidateRole = null)
    {
        var allies = candidateRole.HasValue ? session.MyTeam.Where(p => p.CellId != session.LocalPlayerCellId) : session.MyTeam;
        return new(InferTeam(data, context, allies, candidateRole), InferTeam(data, context, session.TheirTeam));
    }

    private static TeamComposition InferTeam(Dataset data, Dataset? context, IEnumerable<Player> players, Role? reservedRole = null)
    {
        var picks = new Dictionary<Role, int>();
        var unknown = new List<Player>();
        foreach (var player in players.Where(p => p.VisibleChampion > 0).Take(5))
        {
            if (Roles.TryParse(player.AssignedPosition, out var role))
            {
                if (role == reservedRole) continue; // This is the hypothetical slot being replaced.
                if (picks.TryAdd(role, player.VisibleChampion)) continue;
            }
            unknown.Add(player);
        }
        var roles = Enum.GetValues<Role>().Where(r => r != reservedRole && !picks.ContainsKey(r)).ToArray();
        unknown = unknown.Take(roles.Length).ToList();
        Dictionary<Role, int>? best = null;
        HashSet<Role> bestInferred = [];
        double bestWeight = double.NegativeInfinity;
        void Search(int index, double weight, HashSet<Role> inferred)
        {
            if (index == unknown.Count)
            {
                if (weight > bestWeight) { bestWeight = weight; best = new(picks); bestInferred = inferred.ToHashSet(); }
                return;
            }
            int id = unknown[index].VisibleChampion;
            var games = Enum.GetValues<Role>().ToDictionary(r => r, r => Games(data, id, r));
            if (games.Values.Sum() == 0 && context != null) games = Enum.GetValues<Role>().ToDictionary(r => r, r => Games(context, id, r));
            double total = games.Values.Sum();
            foreach (var role in roles.Where(r => !picks.ContainsKey(r)))
            {
                double probability = total == 0 ? .2 : Math.Max(1e-12, games[role] / total);
                picks[role] = id; inferred.Add(role);
                Search(index + 1, weight + Math.Log(probability), inferred);
                picks.Remove(role); inferred.Remove(role);
            }
        }
        Search(0, 0, []);
        return new(best ?? picks, bestInferred);
    }

    private static double Games(Dataset data, int id, Role role) => data.Stats(id, role, out var stats) ? StatObservation.Read(stats).Games : 0;
}
