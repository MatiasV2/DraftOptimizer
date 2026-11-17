using DraftOptimizer.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace DraftOptimizer;

// Cache access stays on the WPF dispatcher. Finished bitmaps are frozen before publication.
internal sealed class PortraitImageLoader : IDisposable
{
    private readonly Dictionary<string, Task<BitmapImage>> _images = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _closed = new();
    private readonly PortraitCache _files;

    public PortraitImageLoader(HttpClient http, string directory)
    {
        _http = http;
        _files = new(http, directory);
    }

    public async Task<BitmapImage?> GetAsync(string url)
    {
        if (_closed.IsCancellationRequested) return null;
        if (!_images.TryGetValue(url, out var pending))
        {
            if (_images.Count >= 256) _images.Remove(_images.Keys.First());
            _images.Add(url, pending = LoadAsync(url, _closed.Token));
        }
        try
        {
            return await pending;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or NotSupportedException)
        {
            if (_images.TryGetValue(url, out var current) && ReferenceEquals(current, pending))
                _images.Remove(url);
            return null;
        }
    }

    private async Task<BitmapImage> LoadAsync(string url, CancellationToken ct)
    {
        string path;
        try
        {
            path = await _files.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsTransient(ex))
        {
            await Task.Delay(350, ct).ConfigureAwait(false);
            path = await _files.GetAsync(url, ct).ConfigureAwait(false);
        }

        using var file = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = file;
        bitmap.DecodePixelWidth = 72;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static bool IsTransient(Exception ex) =>
        ex is OperationCanceledException ||
        ex is HttpRequestException error && (error.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)error.StatusCode >= 500);

    public void Dispose()
    {
        _closed.Cancel();
        _files.Dispose();
        _http.Dispose();
    }
}
