using DraftOptimizer.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DraftOptimizer;

public sealed class PortraitImageConverter : IValueConverter, IDisposable
{
    private readonly Dictionary<string, BitmapImage> _images = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _closed = new();
    private readonly PortraitCache _files;
    public PortraitImageConverter()
    {
        _files = new(_http, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftOptimizer", "cache", "portraits"));
    }
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (_closed.IsCancellationRequested || value is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        if (_images.TryGetValue(url, out var existing)) return existing;
        var bitmap = new BitmapImage { DecodePixelWidth = 72 };
        while (_images.Count >= 256 && _order.Count > 0) _images.Remove(_order.Dequeue());
        _images.Add(url, bitmap); _order.Enqueue(url);
        _ = LoadAsync(bitmap, url);
        return bitmap;
    }
    private async Task LoadAsync(BitmapImage bitmap, string url)
    {
        try
        {
            var path = await _files.GetAsync(url, _closed.Token).ConfigureAwait(false);
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_closed.IsCancellationRequested) bitmap.UriSource = new Uri(path);
            });
        }
        catch (ObjectDisposedException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or ArgumentException)
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_images.TryGetValue(url, out var image) && ReferenceEquals(image, bitmap)) _images.Remove(url);
            });
        }
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    public void Dispose() { _closed.Cancel(); _files.Dispose(); _http.Dispose(); }
}
