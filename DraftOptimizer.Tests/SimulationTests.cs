using DraftOptimizer.Core;

static class SimulationTests
{
    public static void Run(Action<bool, string> check)
    {
        var simulation = new DraftSimulation();
        int[] order = [0, 1, 1, 0, 0, 1, 1, 0, 0, 1];
        check(simulation.NextSide == 0 && simulation.Picks.Count == 0, "Simulación inicia vacía por el lado azul");
        foreach (var (side, index) in order.Select((side, index) => (side, index)))
        {
            check(simulation.NextSide == side, $"Orden de selección correcto en turno {index + 1}");
            var role = Enum.GetValues<Role>().First(simulation.CanUseRole);
            simulation.Pick(index + 1, role);
        }
        check(simulation.Complete && simulation.NextSide == -1 && !simulation.CanUseRole(Role.Middle), "Simulación cierra después de diez picks");
        var blue = simulation.Session(0); var red = simulation.Session(1);
        check(blue.MyTeam.Count == 5 && blue.TheirTeam.Count == 5 && blue.HasCompositionPicks, "Simulación genera composición completa para el analizador existente");
        check(blue.MyTeam.Select(p => p.ChampionId).SequenceEqual(red.TheirTeam.Select(p => p.ChampionId)), "Perspectiva roja invierte correctamente los equipos");
        bool blocked = false; try { simulation.Pick(20, Role.Top); } catch (InvalidOperationException) { blocked = true; }
        check(blocked, "No se aceptan picks después del cierre");
        simulation.Undo(); check(!simulation.Complete && simulation.NextSide == 1, "Deshacer restaura turno y equipo");
        blocked = false; try { simulation.Pick(1, Role.Support); } catch (InvalidOperationException) { blocked = true; }
        check(blocked && simulation.Picks.Count == 9, "No se permiten campeones duplicados entre equipos");
        simulation.Reset(); simulation.Pick(103, Role.Middle); simulation.Pick(238, Role.Middle);
        check(!simulation.CanUseRole(Role.Middle), "Una posición ocupada no puede repetirse dentro del equipo");
        simulation.Reset(); check(simulation.Picks.Count == 0 && simulation.NextSide == 0, "Reiniciar elimina todas las selecciones");
    }
}
