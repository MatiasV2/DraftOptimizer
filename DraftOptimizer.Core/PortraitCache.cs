using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DraftOptimizer.Core;

public sealed class PortraitCache(HttpClient http, string directory, int concurrency = 4) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, concurrency));
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _pending = new();
    public async Task<string> GetAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "ddragon.leagueoflegends.com")
            throw new ArgumentException("Retrato no válido.", nameof(url));
        var job = _pending.GetOrAdd(url, key => new(() => Task.Run(() => FetchAsync(key, _shutdown.Token)), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return await job.Value.WaitAsync(ct).ConfigureAwait(false); }
        finally { if (job.IsValueCreated && job.Value.IsCompleted) _pending.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(url, job)); }
    }
    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".png";
        var path = Path.Combine(directory, name);
        if (File.Exists(path)) return path;
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            if (File.Exists(path)) return path;
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            const int limit = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Retrato demasiado grande.");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[16384]; int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > limit) throw new InvalidDataException("Retrato demasiado grande.");
                bytes.Write(buffer, 0, read);
            }
            var image = bytes.ToArray();
            if (image.Length < 8 || !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("Retrato PNG inválido.");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, image, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true); temporary = null;
            return path;
        }
        finally { if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } } _slots.Release(); }
    }
    public void Dispose() => _shutdown.Cancel();
}
