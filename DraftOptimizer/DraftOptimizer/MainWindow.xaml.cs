using DraftOptimizer.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DraftOptimizer;

public sealed partial class MainWindow : Window
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftOptimizer");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly LcuClient _lcu = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Dataset? _data, _context;
    private DraftSession? _draft;
    private HashSet<int>? _pickable;
    private List<BuildProfile> _profiles = [];
    private BuildProfile? _editing;
    private bool _initializing = true, _polling, _busy, _loadingData, _profilesValid = true;
    private int _followedChampion;
    private DateTime _lastConnect;
    private Choice? _item;
    private string? _lastSuggestions;
    private FavoriteSettings _favorites = new();
    private readonly AutoDraftController _autoDraft = new();
    private readonly SemaphoreSlim _favoritesSave = new(1, 1);
    private bool _favoritesValid = true, _updatingAutomation;
    private readonly ObservableCollection<ChampionListItem> _championRows = [];
    private readonly Dictionary<int, ChampionListItem> _rowsById = [];
    private readonly Dictionary<(string Patch, int ChampionId), Suggestion> _aramStats = [];
    private readonly LoadoutCoordinator _loadout = new();
    private (long GameId, int ActionId)? _autoBanAttempted;
    private ProfileStore Store => new(_directory);
    private Champion? SelectedChampion => ChampionBox.SelectedItem as Champion;
    private Role SelectedRole => RoleBox.SelectedItem is Role r ? r : Role.Middle;
    private Role SelectedBanRole => BanRoleBox.SelectedItem is Role role ? role : SelectedRole;

    public MainWindow()
    {
        InitializeComponent();
        InitializeSimulation();
        InitializeBackgroundWork();
        SuggestionsList.ItemsSource = _championRows;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 850));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DraftOptimizer/0.1");
        _timer.Tick += async (_, _) => await PollAsync();
        Closed += (_, _) => { _timer.Stop(); _closed.Cancel(); _loadout.Dispose(); _lcu.Dispose(); _http.Dispose(); _data?.Dispose(); _context?.Dispose(); };
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ProfileRoleBox.ItemsSource = Enum.GetValues<Role>(); ProfileRoleBox.SelectedItem = Role.Middle;
        RoleBox.ItemsSource = Enum.GetValues<Role>(); RoleBox.SelectedItem = Role.Middle;
        BanRoleBox.ItemsSource = Enum.GetValues<Role>(); BanRoleBox.SelectedItem = Role.Middle;
        Directory.CreateDirectory(_directory);
        var settings = Path.Combine(_directory, "lockfile-path.txt");
        if (File.Exists(settings)) _lcu.LockfilePath = await File.ReadAllTextAsync(settings);
        try { _profiles = await Store.LoadAsync(); }
        catch (Exception ex) { _profilesValid = false; Show("No se pudieron leer tus perfiles", ex.Message, InfoBarSeverity.Error); }
        ProfilesBox.ItemsSource = _profiles;
        try { _favorites = await new FavoriteSettingsStore(_directory).LoadAsync(); }
        catch (Exception ex) { _favoritesValid = false; _favorites = new() { AutoHoverEnabled = false }; Show("No se pudieron leer los favoritos", ex.Message, InfoBarSeverity.Error); }
        UpdateAutomationControls();
        _initializing = false;
        await LoadDataAsync();
        _timer.Start(); await PollAsync();
    }
    private void Show(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    { Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
    private async Task RunAsync(Func<Task> action, string success, bool showSuccess = true)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); if (showSuccess) Show(success, "", InfoBarSeverity.Success); }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { Show("No se pudo completar", ex.Message, InfoBarSeverity.Error); }
        finally { _busy = false; }
    }
    private async Task LoadDataAsync()
    {
        if (_loadingData) return;
        _loadingData = true;
        _loadout.Reset();
        try
        {
            var provider = new DatasetProvider(_http, Path.Combine(_directory, "cache"));
            var (data, cached) = await provider.LoadAsync("current-patch", _closed.Token);
            BuildProfile? current = null;
            if (_data != null) { try { current = ReadProfile(); } catch { } }
            var old = _data; _data = data;
            _initializing = true;
            ChampionBox.ItemsSource = data.Champions; ProfileChampionBox.ItemsSource = data.Champions;
            BanChampionBox.ItemsSource = data.Champions; RefreshBanPriorities();
            ((ProfileChampionConverter)Root.Resources["ProfileChampionConverter"]).Catalogue = data;
            ProfilesBox.ItemsSource = null; ProfilesBox.ItemsSource = _profiles;
            PrimaryBox.ItemsSource = data.Choices("runePathData"); SecondaryBox.ItemsSource = data.Choices("runePathData");
            F0.ItemsSource = data.Shards(0); F1.ItemsSource = data.Shards(1); F2.ItemsSource = data.Shards(2);
            _initializing = false;
            if (current != null) LoadProfile(current); else NewProfile();
            UpdateFavoritesText();
            old?.Dispose(); RefreshSimulation();
            _ = ApplySpanishNamesAsync(data);
            DataText.Text = $"Parche {data.Patch} · {data.Date} · {(cached ? "CACHÉ LOCAL" : "datos en línea")} · DraftGap / Lolalytics";
            Show("Catálogo disponible", cached ? "Usando datos guardados; revisa la fecha antes de importar." : "Cargando estadísticas de enfrentamientos y sinergias…");
            RefreshSuggestions();
            try
            {
                var (context, contextCached) = await provider.LoadAsync("30-days", _closed.Token);
                _context?.Dispose(); _context = context;
                DataText.Text += $" · contexto 30 días {context.Date}{(contextCached ? " (caché)" : "")}";
                RefreshSuggestions(); RefreshSimulation();
                if (_profilesValid && _favoritesValid) Notice.IsOpen = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { Show("Catálogo disponible sin contexto", "El orden usa solo estadísticas base. " + ex.Message, InfoBarSeverity.Warning); }
        }
        catch (Exception ex) { Show("No se pudo cargar el catálogo", ex.Message, InfoBarSeverity.Error); }
        finally { _initializing = false; _loadingData = false; }
    }
    private async Task PollAsync()
    {
        if (_polling || _closed.IsCancellationRequested) return;
        _polling = true;
        try
        {
            if (!_lcu.Connected)
            {
                if ((DateTime.UtcNow - _lastConnect).TotalSeconds < 6) return;
                _lastConnect = DateTime.UtcNow;
                await _lcu.ConnectAsync(_closed.Token);
            }
            var state = await _lcu.StateAsync(_closed.Token);
            _draft = state.Draft;
            _pickable = _draft == null || _draft.IsFinalization || _draft.IsAram ? null : await _lcu.PickableAsync(_closed.Token);
            ConnectionText.Text = "● League conectado · " + state.Phase;
            _timer.Interval = _draft == null ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(_draft.CurrentPick == null ? 500 : 200);
            var remaining = _draft?.Timer?.RemainingMilliseconds(DateTimeOffset.UtcNow);
            DraftText.Text = _draft == null ? "Esperando la selección de campeones." : _draft.IsAram ? "ARAM · opciones disponibles del banco." : _draft.CurrentPick != null ? "Es tu turno de seleccionar." + (remaining.HasValue ? $" Quedan {Math.Ceiling(remaining.Value / 1000):F0} s." : "") : "Draft en curso · esperando tu turno.";
            if (_draft == null) _followedChampion = 0;
            if (FollowBox.IsChecked == true && !_busy && _draft != null)
            {
                var mine = _draft.MyTeam.FirstOrDefault(p => p.CellId == _draft.LocalPlayerCellId);
                if (mine != null && Roles.TryParse(mine.AssignedPosition, out var role)) RoleBox.SelectedItem = role;
                if (mine?.VisibleChampion > 0 && mine.VisibleChampion != _followedChampion)
                {
                    _followedChampion = mine.VisibleChampion;
                    ChampionBox.SelectedItem = _data?.Champions.FirstOrDefault(c => c.Id == mine.VisibleChampion);
                }
            }
            RefreshSuggestions(); UpdatePickButtons();
            if (_draft?.IsAram == true)
            {
                _autoDraft.PauseCurrentTurn();
                AutomationText.Text = "ARAM: selección automática desactivada; usa los cambios disponibles en el banco.";
            }
            else if (_draft?.CurrentBan != null) await AdvanceBanAsync();
            else { _autoBanAttempted = null; await AdvanceAutomationAsync(); }
            AdvanceLoadout(state);
        }
        catch (Exception) when (!_closed.IsCancellationRequested)
        {
            _lcu.Disconnect(); _draft = null; _pickable = null; _followedChampion = 0;
            _timer.Interval = TimeSpan.FromSeconds(2);
            _autoDraft.PauseCurrentTurn();
            _loadout.Reset(); SetLoadoutStatus("Runas y objetos: cliente desconectado.");
            AutomationText.Text = "League desconectado; no se enviarán selecciones automáticas.";
            AutomationSummary.Text = "Auto detenido: League desconectado.";
            ConnectionText.Text = "○ League desconectado"; DraftText.Text = "Abre League para conectar. Reintento automático cada 6 s."; TeamsText.Text = "";
            UpdatePickButtons(); RefreshSuggestions();
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (_closed.IsCancellationRequested) { }
        finally { _polling = false; }
    }
    private void RefreshSuggestions() => _ = RefreshSuggestionsAsync();
    private async Task RefreshSuggestionsAsync()
    {
        if (_initializing || _data == null || _searchDelay.IsEnabled || _closed.IsCancellationRequested) return;
        var data = _data; var context = _context; var role = SelectedRole;
        var snapshot = CaptureDraftSnapshot(out var draftKey);
        var pickable = _pickable?.ToHashSet();
        var search = SearchBox.Text?.Trim() ?? "";
        var favorites = _favorites.ForRole(role).ToHashSet();
        bool favoritesValid = _favoritesValid;
        var key = $"{SelectedRole}|{search}|{_data.Date}|{_context?.Date}|{draftKey}|{string.Join(",", _pickable?.OrderBy(x => x) ?? Enumerable.Empty<int>())}|{string.Join(",", favorites.OrderBy(x => x))}|{_favoritesValid}";
        if (key == _lastSuggestions && ReferenceEquals(data, _lastSuggestionsData) && ReferenceEquals(context, _lastSuggestionsContext)) return;
        _lastSuggestions = key; _lastSuggestionsData = data; _lastSuggestionsContext = context;
        _suggestionsCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _suggestionsCancellation = cancellation;
        var version = ++_suggestionsVersion;
        try
        {
            if (snapshot?.IsAram == true)
            {
                var availableIds = snapshot.AramAvailableChampionIds;
                if (snapshot.LocalVisibleChampion > 0) availableIds.Add(snapshot.LocalVisibleChampion);
                var candidates = data.Champions.Where(c => availableIds.Contains(c.Id)).ToList();
                var missing = candidates.Where(c => !_aramStats.ContainsKey((data.Patch, c.Id))).ToList();
                if (missing.Count > 0)
                {
                    using var limiter = new SemaphoreSlim(4, 4);
                    await Task.WhenAll(missing.Select(async champion =>
                    {
                        await limiter.WaitAsync(cancellation.Token);
                        try
                        {
                            var stat = await new LolalyticsProvider(_http).GetAramStatsAsync(champion, data.Patch, cancellation.Token);
                            lock (_aramStats) _aramStats[(data.Patch, champion.Id)] = stat;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                        finally { limiter.Release(); }
                    }));
                }
                if (version != _suggestionsVersion || cancellation.IsCancellationRequested || !ReferenceEquals(data, _data)) return;
                TeamsText.Text = "ARAM · estadísticas globales de ARAM; el puntaje usa WR y partidas de ARAM. No se aplican cruces de líneas.";
                SetDraftSummary("Las recomendaciones se ordenan con datos de ARAM de Lolalytics (Emerald+), usando confianza Wilson al 95%.");
                var rowsAram = candidates.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Select(c =>
                    {
                        if (!_rowsById.TryGetValue(c.Id, out var row)) _rowsById[c.Id] = row = new ChampionListItem(c);
                        row.SetPortraitPatch(data.Patch);
                        row.Update(c, role, favorites.Contains(c.Id), true, _aramStats.GetValueOrDefault((data.Patch, c.Id)), favoritesValid);
                        return row;
                    })
                    .OrderByDescending(c => c.IsFavorite).ThenByDescending(c => c.Recommendation?.Score ?? double.MinValue).ThenByDescending(c => c.Recommendation?.Games ?? 0).ThenBy(c => c.Champion.Name).ToList();
                IncrementalCollection.Synchronize(_championRows, rowsAram);
                return;
            }
            bool includeLocal = snapshot?.CurrentPick == null;
            var work = await DraftComputation.CalculateAsync(data, context, role, snapshot, pickable, includeLocal, true, cancellation.Token);
            if (version != _suggestionsVersion || cancellation.IsCancellationRequested || !ReferenceEquals(data, _data) || !ReferenceEquals(context, _context)) return;
            UpdateDraftWinrate(snapshot, work);
            var recommendations = work.Suggestions.ToDictionary(s => s.Champion.Id);
            var unavailable = snapshot?.Unavailable(includeLocal) ?? [];
            var rows = data.Champions.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(c =>
                {
                    if (!_rowsById.TryGetValue(c.Id, out var row)) _rowsById[c.Id] = row = new ChampionListItem(c);
                    row.SetPortraitPatch(data.Patch);
                    row.Update(c, role, favorites.Contains(c.Id), !unavailable.Contains(c.Id) && (pickable == null || pickable.Contains(c.Id)), recommendations.GetValueOrDefault(c.Id), favoritesValid);
                    return row;
                })
                .OrderByDescending(c => c.IsFavorite).ThenByDescending(c => c.Recommendation?.Score ?? double.MinValue).ThenByDescending(c => c.Recommendation?.Games ?? 0).ThenBy(c => c.Champion.Name).ToList();
            IncrementalCollection.Synchronize(_championRows, rows);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _suggestionsVersion && !_closed.IsCancellationRequested) { _lastSuggestions = null; Show("No se pudo actualizar la lista", ex.Message, InfoBarSeverity.Warning); } }
        finally { if (ReferenceEquals(_suggestionsCancellation, cancellation)) _suggestionsCancellation = null; cancellation.Dispose(); }
    }
    private void UpdateDraftWinrate(DraftSession? snapshot, DraftComputationResult work)
    {
        if (_data == null || snapshot == null)
        {
            TeamsText.Text = ""; SetDraftSummary("Orden inicial: confianza del winrate y partidas del rol seleccionado.");
            return;
        }
        var composition = work.Composition!;
        string Describe(TeamComposition team) => string.Join(" · ", team.Picks.OrderBy(p => p.Key)
            .Select(p => $"{_data.NameOf(p.Value)} ({p.Key}{(team.InferredRoles.Contains(p.Key) ? "*" : "")})"));
        TeamsText.Text = "Aliados: " + Describe(composition.Allies) + "\nRivales: " + Describe(composition.Enemies);
        if (!snapshot.HasCompositionPicks) { SetDraftSummary("Sin picks: confianza del rol seleccionado; el puntaje cambiará a draft al aparecer picks."); return; }
        if (_context == null) { SetDraftSummary("WR del draft no disponible: faltan estadísticas de contexto de 30 días."); return; }
        var result = work.Analysis!;
        SetDraftSummary($"WR estimado del draft actual: {result.Winrate:P1} · confianza {(result.HasConfidence ? (result.ConfidenceLower * 100).ToString("F1") : "—")} · rango {result.ConfidenceLower:P1}–{result.ConfidenceUpper:P1} · {result.AllyPicks + result.EnemyPicks}/10 picks · {result.MatchupPairs} cruces y {result.SynergyPairs} sinergias." +
            (result.InferredRoles > 0 ? $" {result.InferredRoles} roles estimados (*)." : ""));
    }
    private void SetDraftSummary(string text) { DraftWinrateText.Text = text; ChampionDraftSummary.Text = text; }
    private void SetLoadoutStatus(string text) { LoadoutStatus.Text = text; ChampionLoadoutSummary.Text = text; UpdateLoadoutPreview(); }
    private void UpdatePickButtons()
    {
        bool enabled = _lcu.Connected && _draft?.IsAram != true && _draft?.CurrentPick != null && SelectedChampion != null && (_pickable?.Contains(SelectedChampion.Id) ?? false);
        HoverButton.IsEnabled = enabled; LockButton.IsEnabled = enabled;
    }
    private void FiltersChanged(object sender, SelectionChangedEventArgs e) { RefreshSuggestions(); UpdateFavoritesText(); UpdateLoadoutPreview(); }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        _suggestionsCancellation?.Cancel(); ++_suggestionsVersion; _lastSuggestions = null;
        _searchDelay.Stop(); if (!_closed.IsCancellationRequested) _searchDelay.Start();
    }
    private void SuggestionChanged(object sender, SelectionChangedEventArgs e) { if (SuggestionsList.SelectedItem is ChampionListItem s) ChampionBox.SelectedItem = s.Champion; }
    private void ChampionChanged(object sender, SelectionChangedEventArgs e) { UpdatePickButtons(); UpdateLoadoutPreview(); }
    private void PrimaryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _data == null || PrimaryBox.SelectedItem is not Choice c) return;
        var boxes = new[] { P0, P1, P2, P3 };
        for (int i = 0; i < 4; i++) { boxes[i].ItemsSource = _data.Runes(c.Id, i); boxes[i].SelectedIndex = 0; }
        RenderPrimaryRuneGrid();
    }
    private void SecondaryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _data == null || SecondaryBox.SelectedItem is not Choice c) return;
        var choices = _data.Runes(c.Id).Where(x => _data.RuneSlot(x.Id) > 0).ToList();
        S0.ItemsSource = choices; S1.ItemsSource = choices;
        S0.SelectedIndex = 0; S1.SelectedIndex = choices.Count > 3 ? 3 : 0;
        RenderSecondaryRuneGrid();
    }
    private ToggleButton RuneIconButton(Choice rune, bool selected, bool enabled, Action<ToggleButton> click)
    {
        var button = new ToggleButton { Width = 48, Height = 48, Padding = new Thickness(4), IsChecked = selected, IsEnabled = enabled };
        AutomationProperties.SetName(button, rune.Name);
        if (rune.IconUrl is { } url)
        {
            var converter = (PortraitImageConverter)Root.Resources["PortraitImageConverter"];
            button.Content = new Image { Width = 36, Height = 36, Source = converter.Convert(url, typeof(ImageSource), null!, "") as ImageSource };
        }
        else button.Content = new TextBlock { Text = rune.Name, TextWrapping = TextWrapping.Wrap, FontSize = 9, TextAlignment = TextAlignment.Center };
        ToolTipService.SetToolTip(button, rune.Name);
        button.Click += (_, _) => click(button);
        return button;
    }
    private void RenderPrimaryRuneGrid()
    {
        if (_data == null || PrimaryRuneGrid == null) return;
        PrimaryRuneGrid.Children.Clear();
        var boxes = new[] { P0, P1, P2, P3 };
        for (int rowIndex = 0; rowIndex < boxes.Length; rowIndex++)
        {
            int index = rowIndex;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = $"Fila {rowIndex + 1}", Width = 48, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            foreach (var rune in boxes[rowIndex].Items.OfType<Choice>())
                row.Children.Add(RuneIconButton(rune, (boxes[rowIndex].SelectedItem as Choice)?.Id == rune.Id, true, _ =>
                {
                    boxes[index].SelectedItem = rune;
                    RenderPrimaryRuneGrid();
                }));
            PrimaryRuneGrid.Children.Add(row);
        }
    }
    private void RenderSecondaryRuneGrid()
    {
        if (_data == null || SecondaryRuneGrid == null) return;
        SecondaryRuneGrid.Children.Clear();
        var selected = new[] { S0.SelectedItem as Choice, S1.SelectedItem as Choice };
        for (int slot = 1; slot <= 3; slot++)
        {
            int rowSlot = slot;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = $"Fila {slot + 1}", Width = 48, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var current = selected.FirstOrDefault(x => x != null && _data.RuneSlot(x.Id) == rowSlot);
            bool enabled = current != null || selected.Any(x => x == null);
            foreach (var rune in S0.Items.OfType<Choice>().Where(x => _data.RuneSlot(x.Id) == rowSlot))
                row.Children.Add(RuneIconButton(rune, current?.Id == rune.Id, enabled, button =>
                {
                    var s0 = S0.SelectedItem as Choice;
                    var s1 = S1.SelectedItem as Choice;
                    if (s0?.Id == rune.Id || s1?.Id == rune.Id)
                    {
                        if (s0?.Id == rune.Id) S0.SelectedItem = null;
                        if (s1?.Id == rune.Id) S1.SelectedItem = null;
                    }
                    else if (s0 != null && _data.RuneSlot(s0.Id) == rowSlot) S0.SelectedItem = rune;
                    else if (s1 != null && _data.RuneSlot(s1.Id) == rowSlot) S1.SelectedItem = rune;
                    else if (s0 == null) S0.SelectedItem = rune;
                    else if (s1 == null) S1.SelectedItem = rune;
                    RenderSecondaryRuneGrid();
                }));
            SecondaryRuneGrid.Children.Add(row);
        }
    }
    private static void Select(ComboBox box, int id) { box.SelectedItem = box.Items.OfType<Choice>().FirstOrDefault(c => c.Id == id); }
    private void NewProfile()
    {
        if (_data == null) return;
        _editing = null; ProfilesBox.SelectedItem = null;
        ProfileName.Text = "Mi configuración"; SourceText.Text = "Personalizada";

        Select(PrimaryBox, 8100); Select(SecondaryBox, 8200);
        PrimaryChanged(PrimaryBox, null!); SecondaryChanged(SecondaryBox, null!);
        Select(S0, 8226); Select(S1, 8210); Select(F0, 5008); Select(F1, 5008); Select(F2, 5001);
        RenderPrimaryRuneGrid(); RenderSecondaryRuneGrid();
        ItemsText.Text = "Inicio: 1056, 2003, 2003\nNúcleo: 6655, 3020, 3089";
        if (SelectedChampion == null) ChampionBox.SelectedItem = _data.Champions.FirstOrDefault(c => c.Key == "Ahri") ?? _data.Champions[0];
        ProfileChampionBox.SelectedItem = SelectedChampion ?? _data.Champions.First(); ProfileRoleBox.SelectedItem = SelectedRole;
    }
    private void LoadProfile(BuildProfile p)
    {
        _editing = p;
        ProfileChampionBox.SelectedItem = _data?.Champions.FirstOrDefault(c => c.Id == p.ChampionId); ProfileRoleBox.SelectedItem = p.Role;
        ProfileName.Text = p.Name; SourceText.Text = p.Source + " · " + p.Patch + " · " + p.UpdatedAt.ToLocalTime().ToString("g") + (p.Perks.Length == 0 ? " · Sin runas; puedes enviar los objetos directamente a League." : "");
        ChampionBox.SelectedItem = _data?.Champions.FirstOrDefault(c => c.Id == p.ChampionId); RoleBox.SelectedItem = p.Role;
        Select(PrimaryBox, p.PrimaryStyleId); Select(SecondaryBox, p.SubStyleId); PrimaryChanged(PrimaryBox, null!); SecondaryChanged(SecondaryBox, null!);
        var boxes = new[] { P0, P1, P2, P3, S0, S1, F0, F1, F2 };
        for (int i = 0; i < boxes.Length; i++) { if (p.Perks.Length > i) Select(boxes[i], p.Perks[i]); else boxes[i].SelectedItem = null; }
        RenderPrimaryRuneGrid(); RenderSecondaryRuneGrid();
        ItemsText.Text = ItemBlockText.Format(p.Blocks); UpdateLoadoutPreview();
    }
    private async Task ApplySpanishNamesAsync(Dataset data)
    {
        try
        {
            var names = await new SpanishNameCatalog(_http, Path.Combine(_directory, "cache")).LoadAsync(data.Patch, _closed.Token);
            if (_closed.IsCancellationRequested || !ReferenceEquals(_data, data)) return;
            BuildProfile? current = null;
            try { current = ReadProfile(); } catch { }
            data.ApplyLocalizedNames(names);
            _initializing = true;
            PrimaryBox.ItemsSource = data.Choices("runePathData"); SecondaryBox.ItemsSource = data.Choices("runePathData");
            F0.ItemsSource = data.Shards(0); F1.ItemsSource = data.Shards(1); F2.ItemsSource = data.Shards(2);
            _initializing = false;
            if (current != null) LoadProfile(current); else NewProfile();
            UpdateLoadoutPreview();
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception) { /* If Spanish names are unavailable, keep the existing names. */ }
        finally { _initializing = false; }
    }
    private BuildProfile ReadProfile()
    {
        if (_data == null || ProfileChampionBox.SelectedItem is not Champion) throw new InvalidOperationException("Selecciona el campeón de la configuración.");
        var profile = new BuildProfile
        {
            Id = _editing?.Id ?? Guid.NewGuid(), Name = ProfileName.Text.Trim(), ChampionId = ((Champion)ProfileChampionBox.SelectedItem).Id, Role = ProfileRoleBox.SelectedItem is Role profileRole ? profileRole : SelectedRole,
            PrimaryStyleId = (PrimaryBox.SelectedItem as Choice)?.Id ?? 0, SubStyleId = (SecondaryBox.SelectedItem as Choice)?.Id ?? 0,
            Perks = new[] { P0, P1, P2, P3, S0, S1, F0, F1, F2 }.Select(b => (b.SelectedItem as Choice)?.Id ?? 0).ToArray(), Source = "Personalizada", Patch = _data.Patch,
            AssociatedMaps = _editing?.AssociatedMaps.ToArray() ?? [11],
            EarlySkills = _editing?.ChampionId == ((Champion)ProfileChampionBox.SelectedItem).Id && _editing.Role == (ProfileRoleBox.SelectedItem is Role metadataRole ? metadataRole : SelectedRole) ? _editing.EarlySkills.ToArray() : [],
            SkillPriority = _editing?.ChampionId == ((Champion)ProfileChampionBox.SelectedItem).Id && _editing.Role == (ProfileRoleBox.SelectedItem is Role priorityRole ? priorityRole : SelectedRole) ? _editing.SkillPriority.ToArray() : [],
            ItemEvidence = _editing?.ChampionId == ((Champion)ProfileChampionBox.SelectedItem).Id && _editing.Role == (ProfileRoleBox.SelectedItem is Role evidenceRole ? evidenceRole : SelectedRole) ? _editing.ItemEvidence.ToList() : []
        };
        if (profile.Perks.All(id => id == 0)) profile.Perks = [];
        profile.Blocks = ItemBlockText.Parse(ItemsText.Text, _editing?.Blocks);
        _data.Validate(profile, requireRunes: false);
        if (_editing != null && profile.ChampionId == _editing.ChampionId && profile.Role == _editing.Role && profile.PrimaryStyleId == _editing.PrimaryStyleId && profile.SubStyleId == _editing.SubStyleId && profile.Perks.SequenceEqual(_editing.Perks) && Json.Write(profile.Blocks) == Json.Write(_editing.Blocks)) { profile.Source = _editing.Source; profile.Patch = _editing.Patch; }
        return profile;
    }
    private HashSet<Guid> RetainedProfileIds(BuildProfile profile) => _profiles.Where(p => p.ChampionId == profile.ChampionId && p.Role == profile.Role).Select(p => p.Id).Append(profile.Id).ToHashSet();
    private async Task SaveAsync()
    {
        if (!_profilesValid) throw new InvalidOperationException("Corrige o recupera profiles.json antes de guardar. El archivo original se ha conservado.");
        var p = ReadProfile(); var next = _profiles.Where(x => x.Id != p.Id).Append(p).ToList();
        await Store.SaveAsync(next); _profiles = next; ProfilesBox.ItemsSource = _profiles; ProfilesBox.SelectedItem = p; _editing = p;
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e) { if (ProfilesBox.SelectedItem is BuildProfile p && !_initializing) LoadProfile(p); }
    private void NewClick(object sender, RoutedEventArgs e) => NewProfile();
    private async void SaveClick(object sender, RoutedEventArgs e) => await RunAsync(SaveAsync, "Perfil guardado");
    private async void ConnectClick(object sender, RoutedEventArgs e) { _lastConnect = DateTime.MinValue; await PollAsync(); if (!_lcu.Connected) Show("League no detectado", "Abre League o usa Ruta de League para indicar su archivo lockfile.", InfoBarSeverity.Warning); }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await LoadDataAsync();
    private async void HoverClick(object sender, RoutedEventArgs e)
    {
        _autoDraft.PauseCurrentTurn(_draft);
        await RunAsync(() => _lcu.PickAsync(SelectedChampion!.Id, false, _closed.Token), "Campeón preseleccionado");
    }
    private async void LockClick(object sender, RoutedEventArgs e)
    {
        var champion = SelectedChampion; if (champion == null) return;
        _autoDraft.PauseCurrentTurn(_draft);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Confirmar " + champion.Name, Content = "Esta acción confirma tu campeón en la selección de League.", PrimaryButtonText = "Confirmar campeón", CloseButtonText = "Cancelar" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RunAsync(() => _lcu.PickAsync(champion.Id, true, _closed.Token), "Campeón confirmado");
    }
    private async void LolalyticsClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_data == null || ProfileChampionBox.SelectedItem is not Champion champion) throw new InvalidOperationException("Selecciona el campeón de la configuración.");
        var role = ProfileRoleBox.SelectedItem is Role pr ? pr : SelectedRole;
        var profile = await new LolalyticsProvider(_http).GetAsync(champion, role, _data.Patch, _closed.Token);
        _data.Validate(profile); profile.Name = champion.Name + " - " + role; ProfilesBox.SelectedItem = null; LoadProfile(profile);
    }, "Build cargada · revisa y guarda el perfil");
    private void OpenLolalyticsClick(object sender, RoutedEventArgs e) { if (ProfileChampionBox.SelectedItem is Champion pc && _data != null) Process.Start(new ProcessStartInfo(LolalyticsProvider.PageUrl(pc, ProfileRoleBox.SelectedItem is Role pr ? pr : SelectedRole, _data.Patch)) { UseShellExecute = true }); }
    private async void ImportRunesClick(object sender, RoutedEventArgs e) => await RunAsync(async () => await _lcu.ImportRunesAsync(ReadProfile(), _data!, _closed.Token), "Runas aplicadas a League");
    private async void ImportItemsClick(object sender, RoutedEventArgs e) => await RunAsync(async () => { await SaveAsync(); await _lcu.ImportItemsAsync(_editing!, _data!, _closed.Token, retainedProfileIds: RetainedProfileIds(_editing!)); }, "Conjunto de objetos enviado a League");
    private async void LockfileClick(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = _lcu.LockfilePath ?? "", PlaceholderText = @"C:\Riot Games\League of Legends\lockfile", MinWidth = 420 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Ruta al lockfile de League", Content = box, PrimaryButtonText = "Guardar y conectar", CloseButtonText = "Cancelar" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RunAsync(async () => { _lcu.LockfilePath = box.Text.Trim(); await File.WriteAllTextAsync(Path.Combine(_directory, "lockfile-path.txt"), _lcu.LockfilePath); _lcu.Disconnect(); _lastConnect = DateTime.MinValue; await PollAsync(); if (!_lcu.Connected) throw new IOException("No se pudo conectar. Comprueba la ruta y que League esté abierto."); }, "Conectado a League");
    }
    private void ItemSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput || _data == null) return;
        _item = null; sender.ItemsSource = _data.Choices("itemData").Where(x => x.ToString().Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).Take(15).ToList();
    }
    private void ItemChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs e) { _item = e.SelectedItem as Choice; if (_item != null) sender.Text = _item.ToString(); }
    private void AddItemClick(object sender, RoutedEventArgs e) { if (_item != null) ItemsText.Text = ItemBlockText.AppendItem(ItemsText.Text, _item.Id); }
    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        var current = ProfilesBox.SelectedItem as BuildProfile; if (current == null) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Eliminar " + current.Name, Content = "Se eliminará de tus perfiles locales.", PrimaryButtonText = "Eliminar", CloseButtonText = "Cancelar" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RunAsync(async () =>
        {
            if (!_profilesValid) throw new IOException("No se pueden sobrescribir perfiles dañados.");
            var next = _profiles.Where(x => x.Id != current.Id).ToList();
            await Store.SaveAsync(next);
            _profiles = next;
            _loadout.Reset();
            ProfilesBox.ItemsSource = _profiles;
            NewProfile();
            SetLoadoutStatus("Perfil eliminado. Actualizando la configuración para el campeón confirmado…");
        }, "Perfil eliminado");
    }
    private async void JsonClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        string text = "";
        try { text = Json.Write(ReadProfile()); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { }
        var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 440, Height = 420 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Copia el JSON o pega builds de League", Content = box, PrimaryButtonText = "Importar como nuevos", CloseButtonText = "Cerrar" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ImportJsonAsync(box.Text);
    }, "JSON procesado · los perfiles importados aparecen en Mis perfiles");
    private async Task ImportJsonAsync(string text)
    {
        if (_data == null) throw new InvalidOperationException("Espera a que se cargue el catálogo.");
        if (!_profilesValid) throw new InvalidOperationException("Recupera profiles.json antes de importar; se conserva el archivo original.");
        var imported = BuildImporter.Parse(text, _data, ProfileRoleBox.SelectedItem is Role pr ? pr : SelectedRole, (ProfileChampionBox.SelectedItem as Champion)?.Id);
        var editorChampionId = (ProfileChampionBox.SelectedItem as Champion)?.Id;
        var editorRole = ProfileRoleBox.SelectedItem is Role selectedRole ? selectedRole : SelectedRole;
        var editorPerks = new[] { P0, P1, P2, P3, S0, S1, F0, F1, F2 }.Select(b => (b.SelectedItem as Choice)?.Id ?? 0).ToArray();
        var editorRunesComplete = editorPerks.All(id => id > 0) && PrimaryBox.SelectedItem is Choice && SecondaryBox.SelectedItem is Choice;
        foreach (var itemProfile in imported.Where(p => p.Perks.Length == 0))
        {
            BuildProfile? source = editorRunesComplete && editorChampionId == itemProfile.ChampionId && editorRole == itemProfile.Role
                ? new BuildProfile { ChampionId = itemProfile.ChampionId, Role = itemProfile.Role, PrimaryStyleId = ((Choice)PrimaryBox.SelectedItem!).Id, SubStyleId = ((Choice)SecondaryBox.SelectedItem!).Id, Perks = editorPerks }
                : _profiles.Where(p => p.ChampionId == itemProfile.ChampionId && p.Role == itemProfile.Role && p.Perks.Length == 9).OrderByDescending(p => p.UpdatedAt).FirstOrDefault();
            if (source == null) continue;
            itemProfile.PrimaryStyleId = source.PrimaryStyleId; itemProfile.SubStyleId = source.SubStyleId; itemProfile.Perks = source.Perks.ToArray();
            itemProfile.Source += " · runas conservadas";
        }
        var next = _profiles.Concat(imported).ToList();
        await Store.SaveAsync(next);
        _profiles = next; ProfilesBox.ItemsSource = _profiles; ProfilesBox.SelectedItem = imported[0];
    }
    private async void ImportJsonFileClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        if ((await file.GetBasicPropertiesAsync()).Size > 4 * 1024 * 1024) throw new InvalidDataException("El archivo supera el límite de 4 MB.");
        await ImportJsonAsync(await File.ReadAllTextAsync(file.Path));
    }, "Importación finalizada · revisa Mis perfiles");
    private void UpdateFavoritesText()
    {
        if (FavoritesText == null) return;
        var ids = _favorites.ForRole(SelectedRole);
        FavoritesText.Text = $"{SelectedRole}: " + (ids.Count == 0 ? "sin favoritos." : string.Join(", ", ids.Select(id => _data?.NameOf(id) ?? id.ToString()).OrderBy(n => n)));
        UpdateAutomationSummary(SelectedRole);
    }
    private void UpdateAutomationSummary(Role role)
    {
        if (AutomationSummary == null) return;
        if (!_favoritesValid) AutomationSummary.Text = "Auto desactivado: revisa la configuración de favoritos.";
        else if (!_favorites.AutoHoverEnabled) AutomationSummary.Text = "Selección automática desactivada.";
        else if (_draft?.CurrentPick != null && _autoDraft.Status.Contains("pausada")) AutomationSummary.Text = "Auto pausado para este turno. Selecciona manualmente.";
        else if (_autoDraft.Status.StartsWith("Confirmado automáticamente", StringComparison.Ordinal)) AutomationSummary.Text = _autoDraft.Status;
        else AutomationSummary.Text = $"Auto · {role} · {(_favorites.ForRole(role).Count == 0 ? "mejor opción del rol" : _favorites.ForRole(role).Count + " favoritos")} · confirmación {(_favorites.AutoLockEnabled ? $"a {_favorites.AutoLockSeconds} s" : "manual")}.";
    }
    private void UpdateAutomationControls()
    {
        _updatingAutomation = true;
        AutoHoverBox.IsChecked = _favorites.AutoHoverEnabled;
        AutoLockBox.IsChecked = _favorites.AutoLockEnabled;
        AutoLockSecondsBox.Value = Math.Clamp(_favorites.AutoLockSeconds, 1, 30);
        AutoLoadoutBox.IsChecked = _favorites.AutoLoadoutEnabled;
        AutoBanBox.IsChecked = _favorites.AutoBanEnabled;
        AutoHoverBox.IsEnabled = _favoritesValid;
        AutoLockBox.IsEnabled = _favoritesValid && _favorites.AutoHoverEnabled;
        AutoLockSecondsBox.IsEnabled = _favoritesValid && _favorites.AutoHoverEnabled;
        AutoLoadoutBox.IsEnabled = _favoritesValid;
        AutoBanBox.IsEnabled = _favoritesValid;
        _updatingAutomation = false;
        UpdateFavoritesText();
        RefreshBanPriorities();
    }
    private async Task SaveFavoritesAsync()
    {
        if (!_favoritesValid) throw new InvalidOperationException("Recupera favorites.json antes de modificarlo; se conserva el original.");
        await _favoritesSave.WaitAsync();
        try { await new FavoriteSettingsStore(_directory).SaveAsync(_favorites); }
        catch
        {
            _favoritesValid = false; _favorites.AutoHoverEnabled = false;
            UpdateAutomationControls();
            throw;
        }
        finally { _favoritesSave.Release(); }
    }
    private async void AutomationChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || _updatingAutomation) return;
        _favorites.AutoHoverEnabled = AutoHoverBox.IsChecked == true;
        _favorites.AutoLockEnabled = AutoLockBox.IsChecked == true;
        _favorites.AutoLoadoutEnabled = AutoLoadoutBox.IsChecked == true;
        _favorites.AutoBanEnabled = AutoBanBox.IsChecked == true;
        if (!_favorites.AutoLoadoutEnabled) _loadout.Reset();
        AutoLockBox.IsEnabled = _favoritesValid && _favorites.AutoHoverEnabled;
        UpdateAutomationSummary(SelectedRole);
        try { await SaveFavoritesAsync(); }
        catch (Exception ex) { Show("No se pudo guardar la automatización", ex.Message, InfoBarSeverity.Error); }
    }
    private async void AutoLockSecondsChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_initializing || _updatingAutomation || !_favoritesValid || !double.IsFinite(sender.Value)) return;
        var seconds = Math.Clamp((int)Math.Round(sender.Value), 1, 30);
        if (sender.Value != seconds) { sender.Value = seconds; return; }
        if (_favorites.AutoLockSeconds == seconds) return;
        _favorites.AutoLockSeconds = seconds;
        UpdateAutomationSummary(SelectedRole);
        try { await SaveFavoritesAsync(); }
        catch (Exception ex) { Show("No se pudo guardar el tiempo de confirmación", ex.Message, InfoBarSeverity.Error); }
    }
    private async void FavoriteStarClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: ChampionListItem row }) return;
        await RunAsync(async () =>
        {
            if (!_favoritesValid) throw new InvalidOperationException("Recupera favorites.json antes de modificar tus favoritos.");
            var selected = _favorites.ForRole(row.Role).ToHashSet();
            if (!selected.Add(row.Champion.Id)) selected.Remove(row.Champion.Id);
            _favorites.Favorites[row.Role] = selected;
            await SaveFavoritesAsync();
        }, "Favoritos guardados", showSuccess: false);
        _lastSuggestions = null;
        RefreshSuggestions(); UpdateFavoritesText();
    }
    private void BanRoleChanged(object sender, SelectionChangedEventArgs e) => RefreshBanPriorities();
    private void RefreshBanPriorities()
    {
        if (BanPriorityList == null || BanChampionBox == null) return;
        var selectedId = (BanPriorityList.SelectedItem as Champion)?.Id;
        var champions = _data == null ? new List<Champion>() : _favorites.BanPrioritiesFor(SelectedBanRole)
            .Select(id => _data.Champions.FirstOrDefault(champion => champion.Id == id)).OfType<Champion>().ToList();
        BanPriorityList.ItemsSource = champions;
        if (selectedId.HasValue) BanPriorityList.SelectedItem = champions.FirstOrDefault(champion => champion?.Id == selectedId.Value);
        if (BanChampionBox.SelectedItem is not Champion || _data?.Champions.Contains((Champion)BanChampionBox.SelectedItem) != true)
            BanChampionBox.SelectedItem = _data?.Champions.FirstOrDefault();
        BanChampionBox.IsEnabled = _favoritesValid && _data != null;
    }
    private async Task EditBanPrioritiesAsync(Action<List<int>> edit)
    {
        var role = SelectedBanRole;
        await RunAsync(async () =>
        {
            if (!_favoritesValid) throw new InvalidOperationException("Recupera favorites.json antes de modificar la prioridad de baneos.");
            var priorities = _favorites.BanPrioritiesFor(role).ToList();
            var previous = priorities.ToArray();
            edit(priorities);
            if (priorities.SequenceEqual(previous)) return;
            _favorites.BanPriorities[role] = priorities;
            await SaveFavoritesAsync();
        }, "Prioridad de baneos guardada", showSuccess: false);
        RefreshBanPriorities();
    }
    private async void AddBanPriorityClick(object sender, RoutedEventArgs e)
    {
        if (BanChampionBox.SelectedItem is not Champion champion) return;
        await EditBanPrioritiesAsync(priorities => { if (!priorities.Contains(champion.Id)) priorities.Add(champion.Id); });
    }
    private async void MoveBanPriorityUpClick(object sender, RoutedEventArgs e)
    {
        var index = BanPriorityList.SelectedIndex;
        await EditBanPrioritiesAsync(priorities => { if (index > 0 && index < priorities.Count) (priorities[index - 1], priorities[index]) = (priorities[index], priorities[index - 1]); });
        if (index > 0) BanPriorityList.SelectedIndex = index - 1;
    }
    private async void MoveBanPriorityDownClick(object sender, RoutedEventArgs e)
    {
        var index = BanPriorityList.SelectedIndex;
        await EditBanPrioritiesAsync(priorities => { if (index >= 0 && index + 1 < priorities.Count) (priorities[index + 1], priorities[index]) = (priorities[index], priorities[index + 1]); });
        if (index >= 0 && index + 1 < _favorites.BanPrioritiesFor(SelectedBanRole).Count) BanPriorityList.SelectedIndex = index + 1;
    }
    private async void RemoveBanPriorityClick(object sender, RoutedEventArgs e)
    {
        var index = BanPriorityList.SelectedIndex;
        await EditBanPrioritiesAsync(priorities => { if (index >= 0 && index < priorities.Count) priorities.RemoveAt(index); });
        if (index >= 0 && BanPriorityList.Items.Count > 0) BanPriorityList.SelectedIndex = Math.Min(index, BanPriorityList.Items.Count - 1);
    }
    private async Task AdvanceBanAsync()
    {
        var draft = _draft;
        var action = draft?.CurrentBan;
        if (draft == null || action == null) { _autoBanAttempted = null; return; }
        var turn = (draft.GameId, action.Id);
        if (_autoBanAttempted == turn) return;
        if (action.ChampionId > 0)
        {
            _autoBanAttempted = turn;
            BanStatusText.Text = "Ya hay un campeón elegido para este ban; no se cambiará la selección actual.";
            return;
        }
        var role = SelectedRole;
        var mine = draft.MyTeam.FirstOrDefault(player => player.CellId == draft.LocalPlayerCellId);
        if (mine != null && Roles.TryParse(mine.AssignedPosition, out var assignedRole)) role = assignedRole;
        if (!_favoritesValid) { BanStatusText.Text = "Baneo automático detenido: revisa favorites.json."; return; }
        if (!_favorites.AutoBanEnabled) { BanStatusText.Text = $"Turno de ban · {role}. Baneo automático desactivado."; return; }
        var priorities = _favorites.BanPrioritiesFor(role);
        if (priorities.Count == 0) { BanStatusText.Text = $"No hay prioridades de baneos configuradas para {role}."; return; }
        try
        {
            var bannable = await _lcu.BannableAsync(_closed.Token);
            if (_closed.IsCancellationRequested || _draft?.GameId != draft.GameId || _draft.CurrentBan?.Id != action.Id) return;
            var unavailable = draft.Unavailable();
            var championId = priorities.FirstOrDefault(id => bannable.Contains(id) && !unavailable.Contains(id) && !draft.AllyHasPickIntent(id));
            if (championId <= 0) { BanStatusText.Text = $"Ninguna prioridad de {role} se puede banear sin afectar una selección aliada."; return; }
            _autoBanAttempted = turn;
            var sent = await _lcu.AutomaticBanAsync(draft.GameId, action.Id, championId,
                () => !_closed.IsCancellationRequested && !_busy && _favoritesValid && _favorites.AutoBanEnabled && _favorites.BanPrioritiesFor(role).Contains(championId), _closed.Token);
            if (!sent) { _autoBanAttempted = null; BanStatusText.Text = "No se envió el ban: el turno cambió, el campeón dejó de estar disponible o un compañero lo preseleccionó."; return; }
            BanStatusText.Text = $"Baneado automáticamente: {_data?.NameOf(championId) ?? championId.ToString()} · prioridad {role}.";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { BanStatusText.Text = "Baneo automático pausado. " + ex.Message; }
    }
    private async Task AdvanceAutomationAsync()
    {
        if (_data == null || !_favoritesValid || _busy || _loadingData) return;
        var role = SelectedRole;
        var mine = _draft?.MyTeam.FirstOrDefault(p => p.CellId == _draft.LocalPlayerCellId);
        bool assigned = mine != null && Roles.TryParse(mine.AssignedPosition, out _);
        if (assigned) Roles.TryParse(mine!.AssignedPosition, out role);
        var data = _data; var context = _context;
        try
        {
            IReadOnlyList<Suggestion> recommendations = Array.Empty<Suggestion>();
            if (_draft?.CurrentPick != null && _favorites.AutoHoverEnabled && (context != null || !_draft.HasCompositionPicks))
            {
                var snapshot = CaptureDraftSnapshot(out _);
                var work = await DraftComputation.CalculateAsync(data, context, role, snapshot, _pickable?.ToHashSet(), false, false, _closed.Token);
                if (!ReferenceEquals(data, _data) || !ReferenceEquals(context, _context) || _loadingData || _busy || _closed.IsCancellationRequested || (!assigned && role != SelectedRole)) return;
                recommendations = work.Suggestions;
            }
            await _autoDraft.TickAsync(data, context, _draft, _pickable, _favorites, role, DateTimeOffset.UtcNow,
                request => _lcu.AutomaticPickAsync(request, () => !_closed.IsCancellationRequested && !_busy && !_loadingData && _favoritesValid &&
                    _favorites.AutoHoverEnabled && (_favorites.ForRole(request.Role).Count == 0 || _favorites.ForRole(request.Role).Contains(request.ChampionId)) && (!request.Complete || _favorites.AutoLockEnabled), _closed.Token), recommendations);
            if (!_closed.IsCancellationRequested) { AutomationText.Text = _autoDraft.Status; UpdateAutomationSummary(role); }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closed.IsCancellationRequested) { AutomationText.Text = "Automatización pausada para este turno. " + ex.Message; UpdateAutomationSummary(role); } }
    }
    private void AdvanceLoadout(ClientState state)
    {
        if (_data == null || _loadingData || _busy) return;
        try
        {
            BuildProfile? profile = null;
            int id = state.Draft?.LocalLockedChampion ?? 0;
            var mine = state.Draft?.MyTeam.FirstOrDefault(p => p.CellId == state.Draft.LocalPlayerCellId);
            var role = mine != null && Roles.TryParse(mine.AssignedPosition, out var assigned) ? assigned : SelectedRole;
            if (state.Draft?.IsAram != true)
            {
                if (_editing?.ChampionId == id && _editing.Role == role && (ProfileChampionBox.SelectedItem as Champion)?.Id == id && ProfileRoleBox.SelectedItem is Role profileRole && profileRole == role) profile = ReadProfile();
                profile ??= _profiles.Where(p => p.ChampionId == id && p.Role == role && !p.IsAram).OrderByDescending(p => p.UpdatedAt).FirstOrDefault();
            }
            _loadout.Observe(state, _data, role, profile, _favoritesValid && _profilesValid && _favorites.AutoLoadoutEnabled,
                (champion, position, patch, isAram, ct) => isAram ? new LolalyticsProvider(_http).GetAramAsync(champion, patch, ct) : new LolalyticsProvider(_http).GetAsync(champion, position, patch, ct),
                async (prepared, target, ct) =>
                {
                    await _lcu.ImportRunesAsync(prepared, _data!, ct, target);
                    ct.ThrowIfCancellationRequested();
                    await _lcu.ImportItemsAsync(prepared, _data!, ct, target, RetainedProfileIds(prepared));
                });
            SetLoadoutStatus(_loadout.Status);
        }
        catch (Exception ex) { _loadout.Reset(); SetLoadoutStatus("Revisa el perfil antes de aplicar automáticamente: " + ex.Message); }
    }
    private void RetryLoadoutClick(object sender, RoutedEventArgs e) => _loadout.Reset();
    private void ViewPreparedClick(object sender, RoutedEventArgs e)
    {
        if (_loadout.Prepared is { } profile) LoadProfile(Json.Read<BuildProfile>(Json.Write(profile)));
    }
    private async void AboutClick(object sender, RoutedEventArgs e)
    {
        await new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "DraftOptimizer · versión inicial", CloseButtonText = "Cerrar",
            Content = "Inspirado en DraftGap de Vigo Vlugt (MIT). Estadísticas de DraftGap / Lolalytics. El índice de selección usa un cálculo propio; no replica el modelo completo de DraftGap.\n\nDraftOptimizer is not endorsed by Riot Games and does not reflect the views or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot Games and all associated properties are trademarks or registered trademarks of Riot Games, Inc."
        }.ShowAsync();
    }
}




