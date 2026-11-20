using DraftOptimizer.Core;
using System.Net;
using System.Text.Json.Nodes;

static class DiamondStatisticsTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string sparsePage = """
            <script type="qwik/json">{"objs":["diamond_plus","all","16.22","jungle",13,50,1,
            {"cid":"6","lane":"3","patch":"2","n":"4","wr":"5"},{},
            {"header":"7","nav":"8"},{"tier":"0","region":"1","patch":"2","lane":"3"}]}</script>
            """;
        var sparse = DiamondStatisticsProvider.ParsePage(sparsePage, 1, Role.Jungle, "16.22");
        check(sparse["games"]!.GetValue<int>() == 13 && sparse["matchup"]!.AsObject().Count == 0,
            "Una posición con pocas partidas se importa aunque Lolalytics omita summary");
        try
        {
            DiamondStatisticsProvider.ParsePage(sparsePage.Replace("""{"header":"7","nav":"8"}""", "{}"), 1, Role.Jungle, "16.22");
            check(false, "Cabecera incompleta se rechaza");
        }
        catch (InvalidDataException) { check(true, "Cabecera ausente informa un error de datos compatible con el fallback de caché"); }
        using var catalog = new Dataset("""{"version":"16.22.1","date":"test","championData":{"103":{"id":"Ahri","name":"Ahri","statsByRole":{"2":{"games":999999,"wins":999999}}}}}""");
        var directory = Path.Combine(Path.GetTempPath(), "draftoptimizer-diamond-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(directory, "diamond-plus", "16.22");
        Directory.CreateDirectory(folder);
        foreach (var role in Enum.GetValues<Role>())
        {
            var stats = new JsonObject
            {
                ["games"] = 1000, ["wins"] = 540d,
                ["matchup"] = new JsonObject { ["2"] = new JsonObject { ["238"] = new JsonObject { ["games"] = 100, ["wins"] = 60d } } },
                ["synergy"] = new JsonObject()
            };
            await File.WriteAllTextAsync(Path.Combine(folder, $"103-{role}.json"), stats.ToJsonString());
        }
        var unavailable = new Unavailable();
        using var http = new HttpClient(unavailable);
        var provider = new DiamondStatisticsProvider(http, directory);
        var first = await provider.LoadAsync(catalog, "current-patch", default);
        using (first.Data)
        {
            check(first.Data.Root.GetProperty("statisticsTier").GetString() == "diamond_plus" &&
                first.Data.Stats(103, Role.Middle, out var stats) && stats.GetProperty("games").GetInt32() == 1000 &&
                Math.Abs(stats.GetProperty("wins").GetDouble() - 500) < 1e-8,
                "Dataset Diamante+ sustituye las muestras anteriores y normaliza el sesgo de rango");
            check(first.Data.Root.GetProperty("championData").GetProperty("103").GetProperty("statsByRole").EnumerateObject().Count() == 5,
                "La fuente Diamante+ conserva las cinco posiciones para inferencia de roles");
        }
        var forced = await provider.LoadAsync(catalog, "current-patch", default, forceRefresh: true);
        using (forced.Data)
            check(forced.Cached && unavailable.Requests.Count == 5,
                "La publicación fuerza una descarga nueva incluso con caché reciente y distingue el respaldo tras un fallo");
        unavailable.Requests.Clear();
        foreach (var file in Directory.GetFiles(folder)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        var fallback = await provider.LoadAsync(catalog, "current-patch", default);
        using (fallback.Data)
            check(fallback.Cached && fallback.Data.Stats(103, Role.Top, out var stats) && stats.GetProperty("games").GetInt32() == 1000,
                "Sin red reutiliza el último dataset Diamante+ completo");
        try
        {
            var result = await provider.LoadAsync(catalog, "30-days", default);
            result.Data.Dispose();
            check(false, "Contexto requiere datos del mismo rango");
        }
        catch (IOException)
        {
            check(true, "Sin caché Diamante+ de 30 días informa el fallo y evita reutilizar estadísticas de Esmeralda+");
        }
        unavailable.Requests.Clear();
        try
        {
            var result = await provider.LoadAsync(catalog, "14-days", default);
            result.Data.Dispose();
            check(false, "Descarga fallida no publica un dataset");
        }
        catch (IOException)
        {
            check(unavailable.Requests.Count == 5 && unavailable.Requests.All(url => url.Contains("patch=14") && url.Contains("tier=diamond_plus")),
                "El dataset de publicación solicita exactamente 14 días en Diamante+ para las cinco posiciones");
        }
        unavailable.Requests.Clear();
        try
        {
            var result = await provider.LoadAsync(catalog, "7-days", default);
            result.Data.Dispose();
            check(false, "Sin datos semanales no crea un dataset");
        }
        catch (IOException)
        {
            check(unavailable.Requests.Count == 5 && unavailable.Requests.All(url => url.EndsWith("patch=7", StringComparison.Ordinal) && url.Contains("tier=diamond_plus")),
                "Estadísticas base solicitan siete días de Diamante+ para todas las posiciones");
            check(Directory.Exists(Path.Combine(directory, "diamond-plus", "7")),
                "La caché semanal se mantiene separada de las estadísticas del parche");
        }
    }

    private sealed class Unavailable : HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
