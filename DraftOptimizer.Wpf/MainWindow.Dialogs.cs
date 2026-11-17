using System.Windows;
using System.Windows.Controls;

namespace DraftOptimizer;

public sealed partial class MainWindow
{
    private bool ShowPrompt(string title, object content, string? confirm, string dismiss)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = 540,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(content as UIElement ?? new TextBlock
        {
            Text = content.ToString(),
            TextWrapping = TextWrapping.Wrap
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (confirm != null)
        {
            var accept = new Button { Content = confirm, IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
            accept.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(accept);
        }
        buttons.Children.Add(new Button { Content = dismiss, IsCancel = true, MinWidth = 90 });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }
}
