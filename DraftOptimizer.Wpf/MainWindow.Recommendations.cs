using DraftOptimizer.Core;
using System.Windows;
using System.Windows.Controls;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DraftOptimizer;

public sealed partial class MainWindow
{
    private BuildRecommendationOptions? _draftBuildOptions;
    private string? _draftBuildOptionsKey;
    private CancellationTokenSource? _draftBuildOptionsCancellation;
    private bool _updatingBuildOptionControls;
    private readonly System.Collections.Generic.List<RadioButton> _personalBuildButtons = [];

    private string? BuildOptionKey(int championId, Role role) => _data == null ? null : $"{_data.Patch}|{championId}|{role}";

    private BuildOption? SelectedDraftBuildOption(int championId, Role role)
    {
        if (_draftBuildOptions == null || _draftBuildOptionsKey != BuildOptionKey(championId, role)) return null;
        if (_personalBuildButtons.Any(button => button.IsChecked == true)) return null;
        return HighWinrateBuildOption.IsChecked == true ? _draftBuildOptions.HighestWinrate : _draftBuildOptions.Popular;
    }

    private BuildProfile? SelectedDraftBuildProfile(int championId, Role role)
    {
        if (_draftBuildOptionsKey != BuildOptionKey(championId, role)) return null;
        var personal = _personalBuildButtons.FirstOrDefault(button => button.IsChecked == true)?.Tag as BuildProfile;
        if (personal != null) return personal;
        if (_draftBuildOptions == null) return null;
        return HighWinrateBuildOption.IsChecked == true ? _draftBuildOptions.HighestWinrate.Profile : _draftBuildOptions.Popular.Profile;
    }

    private void PopulatePersonalBuildOptions(Champion champion, Role role)
    {
        var selectedPersonalId = _personalBuildButtons.FirstOrDefault(button => button.IsChecked == true)?.Tag is BuildProfile selected ? selected.Id : (Guid?)null;
        PersonalBuildOptions.Children.Clear();
        _personalBuildButtons.Clear();
        var profiles = _profiles.Where(profile => profile.ChampionId == champion.Id && profile.Role == role && !profile.IsAram)
            .OrderByDescending(profile => profile.UpdatedAt).ToList();
        PersonalBuildOptionsTitle.Visibility = profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        for (int index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var button = new RadioButton
            {
                GroupName = "DraftBuildOption",
                Content = OptionContent($"Personal {index + 1} · {profile.Name}", profile),
                Tag = profile
            };
            button.Checked += BuildOptionChanged;
            button.IsChecked = profile.Id == selectedPersonalId;
            _personalBuildButtons.Add(button);
            PersonalBuildOptions.Children.Add(button);
        }
        if (selectedPersonalId.HasValue && !_personalBuildButtons.Any(button => button.IsChecked == true) && _draftBuildOptions != null)
        {
            _updatingBuildOptionControls = true;
            PopularBuildOption.IsChecked = ReferenceEquals(_draftBuildOptions.Recommended, _draftBuildOptions.Popular);
            HighWinrateBuildOption.IsChecked = ReferenceEquals(_draftBuildOptions.Recommended, _draftBuildOptions.HighestWinrate);
            _updatingBuildOptionControls = false;
        }
    }

    private async void UpdateDraftBuildOptionsForSelection()
    {
        if (BuildOptionsStatus == null || _initializing) return;
        var champion = SelectedChampion;
        var key = champion == null ? null : BuildOptionKey(champion.Id, SelectedRole);
        if (key == _draftBuildOptionsKey && (_draftBuildOptions != null || _draftBuildOptionsCancellation != null))
        {
            if (champion != null) PopulatePersonalBuildOptions(champion, SelectedRole);
            return;
        }
        _draftBuildOptionsCancellation?.Cancel();
        _draftBuildOptionsCancellation?.Dispose();
        _draftBuildOptionsCancellation = null;
        _draftBuildOptions = null;
        _draftBuildOptionsKey = key;
        _personalBuildButtons.Clear();
        PersonalBuildOptions.Children.Clear();
        PersonalBuildOptionsTitle.Visibility = Visibility.Collapsed;
        PopularBuildOption.Visibility = Visibility.Collapsed;
        HighWinrateBuildOption.Visibility = Visibility.Collapsed;
        if (champion == null || _data == null)
        {
            BuildOptionsStatus.Text = "Selecciona un campeón para comparar opciones.";
            UpdateLoadoutPreview();
            return;
        }
        var role = SelectedRole;
        PopulatePersonalBuildOptions(champion, role);
        var data = _data;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _draftBuildOptionsCancellation = cancellation;
        BuildOptionsStatus.Text = "Cargando runas y builds de Lolalytics D2+…";
        try
        {
            var options = await new LolalyticsProvider(_http).GetOptionsAsync(champion, role, data.Patch, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_draftBuildOptionsKey != key || !ReferenceEquals(data, _data)) return;
            data.Validate(options.Popular.Profile);
            data.Validate(options.HighestWinrate.Profile);
            _draftBuildOptions = options;
            PopularBuildOption.Content = OptionContent("Opción 1 · Más popular", options.Popular);
            HighWinrateBuildOption.Content = OptionContent("Opción 2 · Mejor winrate", options.HighestWinrate);
            _updatingBuildOptionControls = true;
            if (!_personalBuildButtons.Any(button => button.IsChecked == true))
            {
                PopularBuildOption.IsChecked = ReferenceEquals(options.Recommended, options.Popular);
                HighWinrateBuildOption.IsChecked = ReferenceEquals(options.Recommended, options.HighestWinrate);
            }
            PopularBuildOption.Visibility = Visibility.Visible;
            HighWinrateBuildOption.Visibility = Visibility.Visible;
            _updatingBuildOptionControls = false;
            BuildOptionsStatus.Text = $"Predeterminada: {(ReferenceEquals(options.Recommended, options.Popular) ? "la más popular" : "la de mejor winrate")}. Puedes cambiarla.";
            UpdateLoadoutPreview();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_draftBuildOptionsKey == key)
            {
                _updatingBuildOptionControls = false;
                BuildOptionsStatus.Text = "No se pudieron cargar las opciones: " + ex.Message;
                UpdateLoadoutPreview();
            }
        }
    }

    private UIElement OptionContent(string title, BuildOption option) => OptionContent(title, option.Profile);

    private UIElement OptionContent(string title, BuildProfile profile)
    {
        var content = new StackPanel { MaxWidth = 250 };
        content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var icons = new StackPanel { Orientation = Orientation.Horizontal };
        if (_data != null)
        {
            var choices = _data.Choices("runeData").Concat(_data.Choices("statShardData")).ToDictionary(choice => choice.Id);
            foreach (var id in profile.Perks)
            {
                if (!choices.TryGetValue(id, out var rune)) continue;
                var image = new PortraitImage { Width = 23, Height = 23, Url = rune.IconUrl };
                ToolTipService.SetToolTip(image, rune.Name);
                icons.Children.Add(image);
            }
        }
        content.Children.Add(icons);
        return content;
    }

    private void BuildOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingBuildOptionControls || _draftBuildOptionsKey == null) return;
        var personal = _personalBuildButtons.FirstOrDefault(button => button.IsChecked == true)?.Tag as BuildProfile;
        _updatingBuildOptionControls = true;
        if (personal != null)
        {
            PopularBuildOption.IsChecked = false;
            HighWinrateBuildOption.IsChecked = false;
        }
        else
        {
            foreach (var button in _personalBuildButtons) button.IsChecked = false;
        }
        _updatingBuildOptionControls = false;
        BuildOptionsStatus.Text = personal != null
            ? $"Perfil personal elegido: {personal.Name}."
            : HighWinrateBuildOption.IsChecked == true ? "Opción 2 elegida: mejor winrate." : "Opción 1 elegida: más popular.";
        UpdateLoadoutPreview();
    }
}
