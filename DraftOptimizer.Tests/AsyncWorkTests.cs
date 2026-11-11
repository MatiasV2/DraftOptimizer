using DraftOptimizer.Core;
using System.Net;
using System.Text;

static class AsyncWorkTests
{
    private const string Catalogue = """{"version":"test","date":"test","championData":{"103":{"id":"Ahri","name":"Ahri","statsByRole":{"0":{"games":1000,"wins":600},"2":{"games":2000,"wins":1000}}},"238":{"id":"Zed","name":"Zed","statsByRole":{"0":{"games":1000,"wins":500},"2":{"games":2000,"wins":1100}}}}}""";
    public static async Task RunAsync(Action<bool, string> check)
    {
        var held = new Dataset(Catalogue); var lease = held.Retain(); held.Dispose();
        check(held.Patch == "test" && held.Suggest(Role.Top,null,null).Count==2,"Un cálculo retiene el JSON aunque se reemplace el catálogo de la ventana");
        lease.Dispose(); bool disposed=false; try { _=held.Patch; } catch(ObjectDisposedException){disposed=true;}
        check(disposed,"El JSON se libera al terminar el último lector, sin bloquear la interfaz");
        using var data = new Dataset(Catalogue);
        var jobs = Enumerable.Range(0,32).Select(async i =>
        {
            var role = i%2==0 ? Role.Top : Role.Middle;
            var result = await DraftComputation.CalculateAsync(data,null,role,null,null,true,true,CancellationToken.None);
            return result.Suggestions.First().Champion.Id == (role==Role.Top ? 103 : 238);
        });
        check((await Task.WhenAll(jobs)).All(x=>x),"Cálculos paralelos de roles distintos no mezclan resultados ni corrompen la caché");
        var phaseDraft = new DraftSession { LocalPlayerCellId=1, MyTeam=[new(){CellId=1,ChampionId=103}], Timer=new(){Phase="BAN_PICK"} };
        var initial=data.Suggest(Role.Middle,phaseDraft,data,[103],false).Single(); phaseDraft.Timer.Phase="FINALIZATION";
        var finalized=data.Suggest(Role.Middle,phaseDraft,data,[103],false).Single();
        check(initial.UsesGlobalConfidence && finalized.HasDraftConfidence,"Un cambio de fase invalida el cálculo incluso si no cambian los campeones");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped=false;
        try {await DraftComputation.CalculateAsync(data,null,Role.Top,null,null,true,true,cancelled.Token);} catch(OperationCanceledException){stopped=true;}
        check(stopped,"Una búsqueda cancelada no publica un análisis nuevo");
        var row = new DraftOptimizer.ChampionListItem(new(103,"Ahri","Ahri")); int notifications=0;
        row.Update(row.Champion,Role.Top,false,true,null,true); row.SetPortraitPatch("test");
        row.PropertyChanged += (_,e)=>{if(e.PropertyName=="PortraitUrl")notifications++;};
        row.Update(new(103,"Ahri","Ahri"),Role.Top,false,true,null,true);
        check(notifications==0,"Recargar datos equivalentes no vuelve a enlazar el retrato de cada celda");
        var directory = Path.Combine(Environment.CurrentDirectory,"work","async-cache-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"current-patch.json"); await File.WriteAllTextAsync(file,Catalogue);
        using(var streamed=await Dataset.LoadFileAsync(file)) check(streamed.Suggest(Role.Top,null,null).First().Champion.Id==103,"Lectura UTF-8 por stream conserva las mismas estadísticas");
        using var offline = new HttpClient(new Offline());
        var (fallback,cached)=await new DatasetProvider(offline,directory).LoadAsync("current-patch");
        using(fallback) check(cached && fallback.Champions.Count==2,"El proveedor conserva el fallback local al fallar la red");
        stopped=false; try {await new DatasetProvider(offline,directory).LoadAsync("current-patch",cancelled.Token);} catch(OperationCanceledException){stopped=true;}
        check(stopped,"Cerrar o cancelar la descarga no provoca una lectura adicional de la caché");
        var handler=new Images(); using var http = new HttpClient(handler);
        var portraits=Path.Combine(directory,"portraits");
        using(var cache=new PortraitCache(http,portraits))
        {
            var requests=Enumerable.Range(0,30).Select(i=>cache.GetAsync($"https://ddragon.leagueoflegends.com/cdn/test/img/champion/C{i%10}.png"));
            var paths=await Task.WhenAll(requests);
            check(handler.Calls==10 && handler.Maximum<=4,"Treinta solicitudes comparten diez descargas con un máximo de cuatro en paralelo");
            check(paths.All(File.Exists),"Los retratos se guardan completos antes de entregarlos a WinUI");
        }
        using(var second=new PortraitCache(http,portraits))
        {
            await second.GetAsync("https://ddragon.leagueoflegends.com/cdn/test/img/champion/C0.png");
            check(handler.Calls==10,"Una segunda instancia reutiliza el PNG de disco sin descargarlo otra vez");
        }
        foreach(var path in Directory.GetFiles(portraits)) File.Delete(path); Directory.Delete(portraits);
        File.Delete(file); Directory.Delete(directory);
    }
    private sealed class Offline:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));}
    }
    private sealed class Images:HttpMessageHandler
    {
        private readonly object _gate=new(); private int _active;
        public int Calls,Maximum;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            lock(_gate){Calls++;_active++;Maximum=Math.Max(Maximum,_active);}
            try {await Task.Delay(25,ct);return new(HttpStatusCode.OK){Content=new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="))};}
            finally{lock(_gate)_active--;}
        }
    }
}


