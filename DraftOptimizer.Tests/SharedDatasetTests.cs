using DraftOptimizer.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

static class SharedDatasetTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string json = """{"version":"16.23.1","date":"test","statisticsTier":"diamond_plus","statisticsWindow":"7-days","championData":{"103":{"id":"Ahri","name":"Ahri","statsByRole":{"2":{"games":1000,"wins":500}}}}}""";
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Optimal, true))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(json));
        var compressed = stream.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(compressed));
        var decoded = await SharedDatasetCodec.DecodeAsync(compressed, hash, default);
        using var data = new Dataset(decoded);
        check(decoded == json && data.Suggest(Role.Middle, null, null).Single().Games == 1000,
            "Dataset compartido comprimido conserva las muestras del ranking");
        try
        {
            await SharedDatasetCodec.DecodeAsync(compressed, new string('0', 64), default);
            check(false, "Rechaza fragmentos corruptos");
        }
        catch (InvalidDataException) { check(true, "El hash evita publicar una descarga compartida incompleta o corrupta"); }
        try
        {
            SharedDatasetCodec.Validate(json.Replace("7-days", "30-days"));
            check(false, "Rechaza otra ventana");
        }
        catch (InvalidDataException) { check(true, "El lector exige la ventana compartida de 7 días"); }
    }
}
