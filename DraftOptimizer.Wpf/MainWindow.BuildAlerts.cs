using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DraftOptimizer;

public sealed partial class MainWindow
{
    private Window? _buildAlert;
    private long _notifiedLoadoutFailure;

    private void ShowBuildAlert(string message, Action? retry = null)
    {
        if (_closed.IsCancellationRequested) return;
        if (_buildAlert != null) return;
        var alert = new Window
        {
            Owner = this, Title = "No se pudo cargar la configuración",
            Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(17, 29, 42)),
            Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "No se pudieron cargar o aplicar la build y las runas.",
            FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16), MaxHeight = 280 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (retry != null)
        {
            var retryButton = new Button { Content = "Reintentar", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
            retryButton.Click += (_, _) => { alert.Close(); retry(); };
            buttons.Children.Add(retryButton);
        }
        var close = new Button { Content = "Cerrar", MinWidth = 80, IsDefault = true };
        close.Click += (_, _) => alert.Close();
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        alert.Content = panel;
        _buildAlert = alert;
        alert.Closed += (_, _) => _buildAlert = null;
        alert.Show();
    }
}
