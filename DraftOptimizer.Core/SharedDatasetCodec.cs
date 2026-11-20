using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DraftOptimizer.Core;

public static class SharedDatasetCodec
{
    public static async Task<string> DecodeAsync(byte[] compressed, string expectedHash, CancellationToken ct)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(compressed)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La integridad del dataset compartido no coincide.");
        using var input = new MemoryStream(compressed, false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await gzip.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > 128 * 1024 * 1024)
                throw new InvalidDataException("El dataset compartido excede el tamaño admitido.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        var json = Encoding.UTF8.GetString(output.ToArray());
        Validate(json);
        return json;
    }

    public static void Validate(string json)
    {
        using var dataset = new Dataset(json);
        if (dataset.Root.GetProperty("statisticsWindow").GetString() != "7-days" ||
            dataset.Root.GetProperty("statisticsTier").GetString() != "diamond_plus" ||
            dataset.Champions.Count == 0)
            throw new InvalidDataException("El dataset compartido debe contener Diamante+ de 7 días.");
    }
}
