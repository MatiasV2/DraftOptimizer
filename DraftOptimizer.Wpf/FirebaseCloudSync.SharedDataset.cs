using DraftOptimizer.Core;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DraftOptimizer;

internal sealed partial class FirebaseCloudSync
{
    internal sealed record SharedDataset(string Version, string Payload, bool Cached = false);

    public async Task<SharedDataset> LoadSharedDatasetAsync(string cachePath, CancellationToken ct)
    {
        SharedDataset? cached = null;
        try
        {
            if (File.Exists(cachePath))
            {
                cached = Json.Read<SharedDataset>(await File.ReadAllTextAsync(cachePath, ct));
                SharedDatasetCodec.Validate(cached.Payload);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        { cached = null; }

        try
        {



            using var request = new HttpRequestMessage(HttpMethod.Get, DatasetUrl);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new IOException("El administrador todavía no ha publicado el dataset de 7 días.");
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var fields = document.RootElement.GetProperty("fields");
            string ReadString(string name) => fields.GetProperty(name).GetProperty("stringValue").GetString()!;
            string version = ReadString("version");
            if (!Guid.TryParseExact(version, "N", out _) || ReadString("tier") != "diamond_plus" || ReadString("encoding") != "gzip" ||
                fields.GetProperty("windowDays").GetProperty("integerValue").GetString() != "7")
                throw new InvalidDataException("Manifiesto de dataset compartido inválido.");
            if (cached?.Version == version) return cached with { Cached = true };
            int count = int.Parse(fields.GetProperty("chunks").GetProperty("integerValue").GetString()!, CultureInfo.InvariantCulture);
            if (count is < 1 or > 128) throw new InvalidDataException("Cantidad de fragmentos inválida.");
            using var compressed = new MemoryStream();
            for (int index = 0; index < count; index++)
            {


                using var chunkRequest = new HttpRequestMessage(HttpMethod.Get, DatasetUrl + $"/chunks/{version}-{index}");
                using var chunkResponse = await _http.SendAsync(chunkRequest, ct);
                chunkResponse.EnsureSuccessStatusCode();
                using var chunk = await JsonDocument.ParseAsync(await chunkResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var parts = chunk.RootElement.GetProperty("fields");
                if (parts.GetProperty("version").GetProperty("stringValue").GetString() != version ||
                    parts.GetProperty("index").GetProperty("integerValue").GetString() != index.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("Fragmentos de versiones diferentes.");
                var bytes = parts.GetProperty("data").GetProperty("bytesValue").GetBytesFromBase64();
                if (bytes.Length > 450000) throw new InvalidDataException("Fragmento demasiado grande.");
                await compressed.WriteAsync(bytes, ct);
            }
            var payload = await SharedDatasetCodec.DecodeAsync(compressed.ToArray(), ReadString("sha256"), ct);

            var result = new SharedDataset(version, payload);
            var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(temporary, Json.Write(result), ct);
                File.Move(temporary, cachePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is IOException or HttpRequestException or TaskCanceledException or JsonException or
                InvalidOperationException or System.Collections.Generic.KeyNotFoundException or FormatException)
        {
            if (cached != null) return cached with { Cached = true };
            throw new IOException("No hay un dataset compartido disponible. " + ex.Message, ex);
        }
    }
}
