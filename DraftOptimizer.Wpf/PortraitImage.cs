using System.Windows;
using System.Windows.Controls;

namespace DraftOptimizer;

public sealed class PortraitImage : Image
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(PortraitImage),
        new PropertyMetadata(null, OnUrlChanged));

    private int _requestId;

    public string? Url
    {
        get => (string?)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public PortraitImage()
    {
        Loaded += (_, _) =>
        {
            if (Source == null) LoadImage();
        };
    }

    private static void OnUrlChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var image = (PortraitImage)sender;
        ++image._requestId;
        image.SetCurrentValue(SourceProperty, null);
        if (image.IsLoaded) image.LoadImage();
    }

    private async void LoadImage()
    {
        if (Url is not { Length: > 0 } url || Application.Current is not App app) return;
        int requestId = ++_requestId;
        var bitmap = await app.Portraits.GetAsync(url);
        // A recycled container may have received a new URL while the previous request was pending.
        if (requestId == _requestId && bitmap != null)
            SetCurrentValue(SourceProperty, bitmap);
    }
}
