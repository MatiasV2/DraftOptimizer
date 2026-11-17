using System.Windows.Threading;
using DraftOptimizer.Core;
using System.Windows;
using System.Windows.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DraftOptimizer;
public sealed partial class MainWindow
{
    private readonly DraftSimulation _simulation = new();
    private readonly ObservableCollection<ChampionListItem> _simulationRows = [];
    private readonly Dictionary<int, ChampionListItem> _simulationRowCache = [];
    private bool _simulationUpdating, _simulationReady, _simulationActive;
    private CancellationTokenSource? _simulationCancellation;
    private long _simulationVersion;
    private readonly DispatcherTimer _simulationSearchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private void InitializeSimulation()
    {
        SimulationCandidates.ItemsSource = _simulationRows;
        _simulationSearchDelay.Tick += (_, _) => { _simulationSearchDelay.Stop(); RefreshSimulation(); };
        Closed += (_, _) => _simulationSearchDelay.Stop();
        _simulationReady = true;
    }
    private void MainTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_simulationReady || sender is not TabControl tabs || !ReferenceEquals(e.Source, tabs)) return;
        bool active = ReferenceEquals(tabs.SelectedItem, SimulationTab);
        if (active == _simulationActive) return;
        _simulationActive = active;
        if (active) Dispatcher.InvokeAsync(RefreshSimulation);
        else { _simulationSearchDelay.Stop(); _simulationCancellation?.Cancel(); ++_simulationVersion; }
    }
    private void SetChampionPresentation(ListBox view, bool portraits)
    {
        var template = (DataTemplate)Root.Resources[portraits ? "ChampionPortraitTemplate" : "ChampionRowTemplate"];
        var panel = (ItemsPanelTemplate)Root.Resources[portraits ? "ChampionPortraitsPanel" : "ChampionRowsPanel"];
        if (ReferenceEquals(view.ItemTemplate, template)) return;
        var selected = view.SelectedItem;
        // A mode change is explicit; regular metric updates retain the same view and containers.
        view.ItemTemplate = template;
        view.ItemsPanel = panel;
        view.SelectedItem = selected;
    }
    private void PortraitToggled(object sender, RoutedEventArgs e)
    {
        if (SuggestionsList != null && PortraitToggle != null) SetChampionPresentation(SuggestionsList, PortraitToggle.IsChecked == true);
    }
    private void SimulationPortraitToggled(object sender, RoutedEventArgs e)
    {
        if (SimulationCandidates != null && SimulationPortraitToggle != null) SetChampionPresentation(SimulationCandidates, SimulationPortraitToggle.IsChecked == true);
    }
    private void SimulationRoleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_simulationUpdating) RefreshSimulation();
    }
    private void SimulationSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_simulationReady || !_simulationActive) return;
        _simulationCancellation?.Cancel(); ++_simulationVersion;
        _simulationSearchDelay.Stop(); _simulationSearchDelay.Start();
    }
    private void SimulationPickClick(object sender, RoutedEventArgs e)
    {
        if (SimulationCandidates.SelectedItem is not ChampionListItem selected || SimulationRoleBox.SelectedItem is not Role role) return;
        try { _simulation.Pick(selected.Champion.Id, role); RefreshSimulation(); }
        catch (InvalidOperationException ex) { Show("No se pudo seleccionar", ex.Message, InfoBarSeverity.Warning); }
    }
    private void SimulationUndoClick(object sender, RoutedEventArgs e) { _simulation.Undo(); RefreshSimulation(); }
    private void SimulationResetClick(object sender, RoutedEventArgs e) { _simulation.Reset(); RefreshSimulation(); }
    private void RefreshSimulation() => _ = RefreshSimulationAsync();
    private async Task RefreshSimulationAsync()
    {
        if (!_simulationReady || !_simulationActive || _simulationUpdating || _closed.IsCancellationRequested) return;
        _simulationCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _simulationCancellation = cancellation;
        var version = ++_simulationVersion;
        _simulationUpdating = true;
        try
        {
            var prior = SimulationRoleBox.SelectedItem;
            var available = Enum.GetValues<Role>().Where(_simulation.CanUseRole).ToArray();
            if (SimulationRoleBox.ItemsSource is not Role[] currentRoles || !currentRoles.SequenceEqual(available)) SimulationRoleBox.ItemsSource = available;
            object? nextRole = prior is Role previous && available.Contains(previous) ? previous : available.Length > 0 ? available[0] : null;
            if (!Equals(SimulationRoleBox.SelectedItem, nextRole)) SimulationRoleBox.SelectedItem = nextRole;
            SimulationTurnText.Text = _simulation.Complete ? "Draft completo · deshaz un pick para probar otra variante." : $"Pick {_simulation.Picks.Count + 1}/10 · equipo {(_simulation.NextSide == 0 ? "azul" : "rojo")} · orden 1–2–2–2–2–1";
            string Team(int side) => (_data == null ? "" : string.Join(" · ", _simulation.Picks.Where(p => p.Side == side).Select(p => $"{p.Role}: {_data.NameOf(p.ChampionId)}"))) is { Length: > 0 } names ? names : "Sin selecciones";
            SimulationBlueText.Text = "AZUL\n" + Team(0); SimulationRedText.Text = "ROJO\n" + Team(1);
            SimulationPickButton.IsEnabled = false;
            _simulationUpdating = false;
            var data = _data; var context = _context;
            var search = SimulationSearchBox.Text.Trim();
            var side = _simulation.NextSide;
            var session = _simulation.Session(side < 0 ? 0 : side);
            var blue = _simulation.Session(0);
            var used = _simulation.Picks.Select(p => p.ChampionId).ToHashSet();
            DraftComputationResult? work = null;
            DraftComputationResult? blueWork = null;
            var selectedRole = SimulationRoleBox.SelectedItem;
            if (data != null)
            {
                if (selectedRole is Role requestedRole)
                    work = await DraftComputation.CalculateAsync(data, context, requestedRole, session, null, true, false, cancellation.Token);
                if (cancellation.IsCancellationRequested || version != _simulationVersion || !ReferenceEquals(data, _data) || !ReferenceEquals(context, _context)) return;
                blueWork = await DraftComputation.CalculateAsync(data, context, Role.Middle, blue, null, true, true, cancellation.Token, calculateSuggestions: false);
            }
            if (cancellation.IsCancellationRequested || version != _simulationVersion || !_simulationActive || !ReferenceEquals(data, _data) || !ReferenceEquals(context, _context)) return;
            var rows = new List<ChampionListItem>();
            if (_data != null && SimulationRoleBox.SelectedItem is Role role)
            {
                var recommendations = work!.Suggestions.ToDictionary(s => s.Champion.Id);
                foreach (var champion in _data.Champions.Where(c => !used.Contains(c.Id) && c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!_simulationRowCache.TryGetValue(champion.Id, out var row)) _simulationRowCache[champion.Id] = row = new(champion);
                    row.SetPortraitPatch(_data.Patch);
                    row.SetGlobalConfidence(work.GlobalConfidence.TryGetValue(champion.Id, out var placement) ? placement : null);
                    row.Update(champion, role, false, true, recommendations.GetValueOrDefault(champion.Id), false);
                    rows.Add(row);
                }
            }
            SimulationPickButton.IsEnabled = data != null && !_simulation.Complete;
            IncrementalCollection.Synchronize(_simulationRows, rows.OrderByDescending(r => r.Recommendation?.Score ?? -1).ThenByDescending(r => r.Recommendation?.Games ?? 0).ThenBy(r => r.Champion.Name).ToList());
            if (_data == null) SimulationMetricText.Text = "Cargando catálogo…";
            else if (_simulation.Picks.Count == 0) SimulationMetricText.Text = "Sin picks: confianza del WR y partidas del rol seleccionado. Selecciona un candidato y confirma.";
            else if (_context == null) SimulationMetricText.Text = "Falta contexto de 30 días para calcular las métricas del draft. Puedes seguir seleccionando.";
            else
            {
                var result = blueWork!.Analysis!;
                SimulationMetricText.Text = $"WR azul {result.Winrate:P1} · rojo {1 - result.Winrate:P1} · rango azul {result.ConfidenceLower:P1}–{result.ConfidenceUpper:P1} · {result.MatchupPairs} cruces / {result.SynergyPairs} sinergias. Candidatos evaluados desde el equipo del próximo pick.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { Show("Simulación no disponible", ex.Message, InfoBarSeverity.Warning); }
        finally { _simulationUpdating = false; if (ReferenceEquals(_simulationCancellation, cancellation)) _simulationCancellation = null; cancellation.Dispose(); }
    }
}



