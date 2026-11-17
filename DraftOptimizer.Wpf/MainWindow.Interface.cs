using DraftOptimizer.Core;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DraftOptimizer;

public sealed partial class MainWindow
{
    private bool _applyingItemSuggestion;

    private void ItemSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_data == null || ItemMatches == null || _applyingItemSuggestion) return;
        _item = null;
        var query = ItemSearch.Text.Trim();
        ItemMatches.ItemsSource = query.Length == 0
            ? null
            : _data.Choices("itemData")
                .Where(item => item.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(15).ToList();
        ItemSuggestions.IsOpen = ItemMatches.HasItems && ItemSearch.IsKeyboardFocusWithin;
    }

    private void ItemSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && ItemMatches.HasItems)
        {
            ItemSuggestions.IsOpen = true;
            int index = ItemMatches.SelectedIndex;
            if (e.Key == Key.Down)
                index = Math.Min(ItemMatches.Items.Count - 1, index + 1);
            else
                index = index < 0 ? ItemMatches.Items.Count - 1 : Math.Max(0, index - 1);
            ItemMatches.SelectedIndex = index;
            ItemMatches.ScrollIntoView(ItemMatches.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ItemSuggestions.IsOpen)
        {
            if (ItemMatches.SelectedItem is Choice selected)
                ChooseItem(selected);
            else if (ItemMatches.Items.Count == 1 && ItemMatches.Items[0] is Choice onlyMatch)
                ChooseItem(onlyMatch);
            e.Handled = true;
        }
        else if (e.Key is Key.Escape or Key.Tab && ItemSuggestions.IsOpen)
        {
            ItemSuggestions.IsOpen = false;
            e.Handled = e.Key == Key.Escape;
        }
    }

    private void ItemSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ItemMatches, source) is ListBoxItem { Content: Choice item })
        {
            e.Handled = true;
            ChooseItem(item);
        }
    }

    private void ChooseItem(Choice item)
    {
        _item = item;
        ItemSuggestions.IsOpen = false;
        _applyingItemSuggestion = true;
        try
        {
            ItemSearch.Text = item.ToString();
            ItemSearch.CaretIndex = ItemSearch.Text.Length;
        }
        finally { _applyingItemSuggestion = false; }
    }

    private void AddItemClick(object sender, RoutedEventArgs e)
    {
        if (_item != null)
            ItemsText.Text = ItemBlockText.AppendItem(ItemsText.Text, _item.Id);
    }

    private async void IncreaseSeconds(object sender, RoutedEventArgs e) => await CommitSecondsAsync(1);
    private async void DecreaseSeconds(object sender, RoutedEventArgs e) => await CommitSecondsAsync(-1);
    private async void CommitSecondsOnBlur(object sender, KeyboardFocusChangedEventArgs e) => await CommitSecondsAsync();

    private async void SecondsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            AutoLockSecondsBox.Text = _favorites.AutoLockSeconds.ToString(CultureInfo.CurrentCulture);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.Enter)
        {
            e.Handled = true;
            await CommitSecondsAsync(e.Key == Key.Up ? 1 : e.Key == Key.Down ? -1 : 0);
        }
    }

    private async Task CommitSecondsAsync(int adjustment = 0)
    {
        if (_initializing || _updatingAutomation || !_favoritesValid || !AutoLockSecondsControl.IsEnabled) return;
        int current = int.TryParse(AutoLockSecondsBox.Text, out int entered) ? entered : _favorites.AutoLockSeconds;
        int seconds = Math.Clamp(Math.Clamp(current, 0, 31) + adjustment, 1, 30);
        AutoLockSecondsBox.Text = seconds.ToString(CultureInfo.CurrentCulture);
        if (_favorites.AutoLockSeconds == seconds) return;

        _favorites.AutoLockSeconds = seconds;
        UpdateAutomationSummary(SelectedRole);
        try { await SaveFavoritesAsync(); }
        catch (Exception ex) { Show("No se pudo guardar el tiempo de confirmación", ex.Message, InfoBarSeverity.Error); }
    }
}
