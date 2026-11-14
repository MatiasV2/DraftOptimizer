using DraftOptimizer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    private string? BuildOptionKey(int championId, Role role) => _data == null ? null : $"{_data.Patch}|{championId}|{role}";

    private BuildOption? SelectedDraftBuildOption(int championId, Role role)
    {
        if (_draftBuildOptions == null || _draftBuildOptionsKey != BuildOptionKey(championId, role)) return null;
        return HighWinrateBuildOption.IsChecked == true ? _draftBuildOptions.HighestWinrate : _draftBuildOptions.Popular;
    }

    private async void UpdateDraftBuildOptionsForSelection()
    {
        if (BuildOptionsStatus == null || _initializing) return;
        var champion = SelectedChampion;
        var key = champion == null ? null : BuildOptionKey(champion.Id, SelectedRole);
        if (key == _draftBuildOptionsKey && (_draftBuildOptions != null || _draftBuildOptionsCancellation != null)) return;
        _draftBuildOptionsCancellation?.Cancel();
        _draftBuildOptionsCancellation?.Dispose();
        _draftBuildOptionsCancellation = null;
        _draftBuildOptions = null;
        _draftBuildOptionsKey = key;
        PopularBuildOption.Visibility = Visibility.Collapsed;
        HighWinrateBuildOption.Visibility = Visibility.Collapsed;
        if (champion == null || _data == null)
        {
            BuildOptionsStatus.Text = "Selecciona un campeón para comparar opciones.";
            UpdateLoadoutPreview();
            return;
        }
        var role = SelectedRole;
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
            PopularBuildOption.IsChecked = ReferenceEquals(options.Recommended, options.Popular);
            HighWinrateBuildOption.IsChecked = ReferenceEquals(options.Recommended, options.HighestWinrate);
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

    private UIElement OptionContent(string title, BuildOption option)
    {
        var content = new StackPanel { Spacing = 4, MaxWidth = 250 };
        content.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        if (_data != null)
        {
            var choices = _data.Choices("runeData").Concat(_data.Choices("statShardData")).ToDictionary(choice => choice.Id);
            var converter = (PortraitImageConverter)Root.Resources["PortraitImageConverter"];
            foreach (var id in option.Profile.Perks)
            {
                if (!choices.TryGetValue(id, out var rune)) continue;
                var image = new Image { Width = 23, Height = 23, Source = rune.IconUrl is { } url ? converter.Convert(url, typeof(ImageSource), null!, "") as ImageSource : null };
                ToolTipService.SetToolTip(image, rune.Name);
                icons.Children.Add(image);
            }
        }
        content.Children.Add(icons);
        return content;
    }

    private void BuildOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingBuildOptionControls || _draftBuildOptions == null) return;
        BuildOptionsStatus.Text = "Opción elegida: " + (HighWinrateBuildOption.IsChecked == true ? "mejor winrate." : "más popular.");
        UpdateLoadoutPreview();
    }
}
