using DraftOptimizer.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DraftOptimizer;

internal sealed partial class FirebaseCloudSync
{
    public bool CanPublishDataset => IsSignedIn && string.Equals(Email, "matias.faundez@outlook.cl", StringComparison.OrdinalIgnoreCase);
    private string DatasetUrl => $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/datasets/diamond-7-days";

    internal sealed record DatasetPublication(DateTimeOffset PublishedAt, string? UpdateTime);

    public async Task<DatasetPublication?> DatasetPublicationAsync(CancellationToken ct)
    {
        if (!CanPublishDataset) return null;
        await EnsureTokenAsync(ct);
        using var request = AuthenticatedRequest(HttpMethod.Get, DatasetUrl);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        var published = DateTimeOffset.Parse(root.GetProperty("fields").GetProperty("publishedAt").GetProperty("timestampValue").GetString()!, CultureInfo.InvariantCulture);
        return new(published, root.GetProperty("updateTime").GetString());
    }

    public async Task PublishDatasetAsync(string json, DatasetPublication? previous, CancellationToken ct)
    {
        if (!CanPublishDataset) throw new InvalidOperationException("Esta cuenta no puede publicar datasets.");
        if (previous != null && DateTimeOffset.UtcNow - previous.PublishedAt < TimeSpan.FromHours(24)) return;
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.GetProperty("statisticsWindow").GetString() != "7-days" ||
            parsed.RootElement.GetProperty("statisticsTier").GetString() != "diamond_plus")
            throw new InvalidDataException("Se requiere un dataset Diamante+ de 7 días.");

        var uid = UserId;
        var version = Guid.NewGuid().ToString("N");
        byte[] compressed;
        using (var stream = new MemoryStream())
        {
            using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, true))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes(json), ct);
            compressed = stream.ToArray();
        }
        const int chunkBytes = 450000;
        int count = (compressed.Length + chunkBytes - 1) / chunkBytes;
        for (int index = 0; index < count; index++)
        {
            await EnsureTokenAsync(ct);
            if (!CanPublishDataset || UserId != uid) throw new OperationCanceledException("La cuenta cambió.");
            int length = Math.Min(chunkBytes, compressed.Length - index * chunkBytes);
            var fields = new Dictionary<string, object>
            {
                ["data"] = new { bytesValue = Convert.ToBase64String(compressed, index * chunkBytes, length) },
                ["version"] = new { stringValue = version },
                ["index"] = new { integerValue = index.ToString(CultureInfo.InvariantCulture) }
            };
            using var request = AuthenticatedRequest(HttpMethod.Patch,
                DatasetUrl + $"/chunks/{version}-{index}?currentDocument.exists=false");
            request.Content = JsonContent.Create(new { fields });
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }

        await EnsureTokenAsync(ct);
        if (!CanPublishDataset || UserId != uid) throw new OperationCanceledException("La cuenta cambió.");
        var name = $"projects/{ProjectId}/databases/(default)/documents/datasets/diamond-7-days";
        var manifest = new Dictionary<string, object>
        {
            ["version"] = new { stringValue = version },
            ["chunks"] = new { integerValue = count.ToString(CultureInfo.InvariantCulture) },
            ["encoding"] = new { stringValue = "gzip" },
            ["sha256"] = new { stringValue = Convert.ToHexString(SHA256.HashData(compressed)) },
            ["tier"] = new { stringValue = "diamond_plus" },
            ["windowDays"] = new { integerValue = "7" }
        };
        object condition = previous?.UpdateTime is { } time ? new { updateTime = time } : (object)new { exists = false };
        using var commit = AuthenticatedRequest(HttpMethod.Post,
            $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents:commit");
        commit.Content = JsonContent.Create(new { writes = new[] { new {
            update = new { name, fields = manifest },
            currentDocument = condition,
            updateTransforms = new[] { new { fieldPath = "publishedAt", setToServerValue = "REQUEST_TIME" } }
        } } });
        using var committed = await _http.SendAsync(commit, ct);
        committed.EnsureSuccessStatusCode();
    }
}
