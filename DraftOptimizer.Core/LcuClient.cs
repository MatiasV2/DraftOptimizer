using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DraftOptimizer.Core;

public sealed record LcuCredentials(int Port, string Password)
{
    public override string ToString() => "LCU (credenciales ocultas)";
    public static LcuCredentials ParseLockfile(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length != 5 || parts[4] != "https" || !int.TryParse(parts[2], out var port) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(parts[3]))
            throw new InvalidDataException("Lockfile de League inválido.");
        return new(port, parts[3]);
    }
}

public sealed class LcuClient : IDisposable
{
    private const int GuardianAngelItemId = 3026;
    private HttpClient? _client;
    public LcuClient() { }
    internal LcuClient(HttpClient client)
    {
        if (client.BaseAddress is not { Scheme: "https", Host: "127.0.0.1" }) throw new ArgumentException("LCU requiere HTTPS local.");
        _client = client;
    }
    public string? LockfilePath { get; set; }
    public bool Connected => _client != null;
    public static async Task<LcuCredentials> DiscoverAsync(string? explicitPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return LcuCredentials.ParseLockfile(await ReadSharedAsync(explicitPath, ct));
        foreach (var process in Process.GetProcessesByName("LeagueClientUx").Concat(Process.GetProcessesByName("LeagueClient")))
        {
            using (process)
            try
            {
                var path = Path.Combine(Path.GetDirectoryName(process.MainModule!.FileName)!, "lockfile");
                if (File.Exists(path)) return LcuCredentials.ParseLockfile(await ReadSharedAsync(path, ct));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException) { }
        }
        // Fallback for clients whose module path is inaccessible. Output is kept only in memory.
        using var ps = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process -Filter 'Name = ''LeagueClientUx.exe''' | Select-Object -ExpandProperty CommandLine\"",
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        }) ?? throw new IOException("No se pudo detectar League.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var outputTask = ps.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorsTask = ps.StandardError.ReadToEndAsync(timeout.Token);
            await ps.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            await errorsTask;
            var port = Regex.Match(output, @"--app-port=(\d+)");
            var password = Regex.Match(output, "--remoting-auth-token=([^\\s\"]+)");
            if (port.Success && password.Success && int.TryParse(port.Groups[1].Value, out var number) && number is > 0 and <= 65535)
                return new(number, password.Groups[1].Value);
        }
        finally { if (!ps.HasExited) ps.Kill(); }
        throw new IOException("Abre el cliente de League o indica la ruta de su lockfile.");
    }
    private static async Task<string> ReadSharedAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Disconnect();
        var credentials = await DiscoverAsync(LockfilePath, ct);
        var handler = new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            // This client is exclusively loopback, never shared with Internet requests.
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri is { Scheme: "https", Host: "127.0.0.1" } uri && uri.Port == credentials.Port
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{credentials.Port}/"), Timeout = TimeSpan.FromSeconds(5) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + credentials.Password)));
        try { await SendAsync(HttpMethod.Get, "lol-gameflow/v1/gameflow-phase", null, ct); }
        catch { Disconnect(); throw; }
    }
    public async Task<ClientState> StateAsync(CancellationToken ct = default)
    {
        var phase = (await SendAsync(HttpMethod.Get, "lol-gameflow/v1/gameflow-phase", null, ct)).GetString() ?? "None";
        DraftSession? draft = null;
        if (phase == "ChampSelect") draft = (await SendAsync(HttpMethod.Get, "lol-champ-select/v1/session", null, ct)).Deserialize<DraftSession>(Json.Options);
        return new(phase, draft);
    }
    public async Task<HashSet<int>> PickableAsync(CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Get, "lol-champ-select/v1/pickable-champion-ids", null, ct)).EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
    public async Task<HashSet<int>> BannableAsync(CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Get, "lol-champ-select/v1/bannable-champion-ids", null, ct)).EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
    public async Task PickAsync(int championId, bool complete, CancellationToken ct = default)
    {
        var state = await StateAsync(ct);
        var draft = state.Draft ?? throw new InvalidOperationException("No estás en selección de campeón.");
        if (!(await PickableAsync(ct)).Contains(championId)) throw new InvalidOperationException("El campeón no está disponible para tu cuenta o fue bloqueado.");
        var action = draft.CurrentPick ?? throw new InvalidOperationException("Aún no es tu turno de seleccionar.");
        await SendAsync(HttpMethod.Patch, $"lol-champ-select/v1/session/actions/{action.Id}", new { championId, completed = complete }, ct);
    }
    public async Task<bool> AutomaticPickAsync(AutomaticPickRequest pick, Func<bool> stillEnabled, CancellationToken ct = default)
    {
        bool Matches(DraftSession? draft)
        {
            if (draft == null || draft.GameId != pick.GameId || draft.LocalPlayerCellId != pick.CellId ||
                draft.CurrentPick?.Id != pick.ActionId || draft.LocalVisibleChampion != pick.ExpectedVisibleChampion) return false;
            var assigned = draft.MyTeam.FirstOrDefault(p => p.CellId == pick.CellId)?.AssignedPosition ?? "";
            if (Roles.TryParse(assigned, out var role) && role != pick.Role) return false;
            if (draft.Unavailable(includeLocalPlayer: false).Contains(pick.ChampionId)) return false;
            var remaining = draft.Timer?.RemainingMilliseconds(DateTimeOffset.UtcNow);
            if (pick.Complete && (!remaining.HasValue || remaining.Value <= 0 || remaining.Value > Math.Clamp(pick.LockInSeconds, 1, 30) * 1000d)) return false;
            return true;
        }
        if (!stillEnabled() || !Matches((await StateAsync(ct)).Draft)) return false;
        if (!(await PickableAsync(ct)).Contains(pick.ChampionId)) return false;
        // Re-read after availability: avoid locking a different turn or a manual change.
        if (!Matches((await StateAsync(ct)).Draft) || !stillEnabled()) return false;
        await SendAsync(HttpMethod.Patch, $"lol-champ-select/v1/session/actions/{pick.ActionId}", new { championId = pick.ChampionId, completed = pick.Complete }, ct);
        return true;
    }
    public async Task<bool> AutomaticBanAsync(long gameId, int actionId, int championId, Func<bool> stillEnabled, CancellationToken ct = default)
    {
        bool Matches(DraftSession? draft)
        {
            var remaining = draft?.Timer?.RemainingMilliseconds(DateTimeOffset.UtcNow);
            return draft != null && draft.GameId == gameId && draft.CurrentBan is { ChampionId: 0 } currentBan && currentBan.Id == actionId &&
                remaining is <= 1000 && !draft.AllyHasPickIntent(championId) && !draft.Unavailable().Contains(championId);
        }
        if (!stillEnabled() || !Matches((await StateAsync(ct)).Draft)) return false;
        if (!(await BannableAsync(ct)).Contains(championId)) return false;
        // Re-read the ban action and ally intents immediately before committing the ban.
        if (!stillEnabled() || !Matches((await StateAsync(ct)).Draft)) return false;
        await SendAsync(HttpMethod.Patch, $"lol-champ-select/v1/session/actions/{actionId}", new { championId, completed = true }, ct);
        return true;
    }
    public async Task ImportRunesAsync(BuildProfile profile, Dataset dataset, CancellationToken ct = default, FinalLoadoutTarget? target = null)
    {
        dataset.Validate(profile);
        var summonerId = await CurrentSummonerIdAsync(ct);
        await EnsureProfileChampionAsync(profile, ct, target);
        var pages = await SendAsync(HttpMethod.Get, "lol-perks/v1/pages", null, ct);
        var runePageName = profile.IsAram ? "DraftOptimizer ARAM" : "DraftOptimizer";
        var existing = pages.EnumerateArray().FirstOrDefault(x => x.TryGetProperty("name", out var n) && n.GetString() == runePageName && x.TryGetProperty("isEditable", out var edit) && edit.GetBoolean());
        var payload = new { name = runePageName, primaryStyleId = profile.PrimaryStyleId, subStyleId = profile.SubStyleId, selectedPerkIds = profile.Perks, current = true };
        if (target != null) await EnsureProfileChampionAsync(profile, ct, target);
        await EnsureCurrentSummonerAsync(summonerId, ct);
        if (existing.ValueKind == JsonValueKind.Object)
            await SendAsync(HttpMethod.Put, $"lol-perks/v1/pages/{existing.GetProperty("id").GetInt64()}", payload, ct);
        else
            await SendAsync(HttpMethod.Post, "lol-perks/v1/pages", payload, ct);
        await EnsureCurrentSummonerAsync(summonerId, ct);
    }
    private async Task<long> CurrentSummonerIdAsync(CancellationToken ct)
    {
        var summoner = await SendAsync(HttpMethod.Get, "lol-summoner/v1/current-summoner", null, ct);
        if (!summoner.TryGetProperty("summonerId", out var value))
            throw new InvalidDataException("League no entregó un ID válido para la cuenta activa. Reconecta el cliente después de cambiar de cuenta.");
        long id;
        var valid = value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out id) : long.TryParse(value.ToString(), out id);
        if (!valid || id <= 0) throw new InvalidDataException("League no entregó un ID válido para la cuenta activa. Reconecta el cliente después de cambiar de cuenta.");
        return id;
    }
    private async Task EnsureCurrentSummonerAsync(long expectedSummonerId, CancellationToken ct)
    {
        if (await CurrentSummonerIdAsync(ct) != expectedSummonerId)
            throw new InvalidOperationException("La cuenta de League cambió durante el envío. No se confirmó la importación; reconecta y vuelve a intentarlo.");
    }
    private async Task EnsureProfileChampionAsync(BuildProfile profile, CancellationToken ct, FinalLoadoutTarget? target = null)
    {
        var state = await StateAsync(ct);
        if (state.Phase is not ("None" or "Lobby" or "Matchmaking" or "ReadyCheck" or "ChampSelect"))
            throw new InvalidOperationException("Importa la configuración antes de empezar la partida.");
        var mine = state.Draft?.MyTeam.FirstOrDefault(p => p.CellId == state.Draft.LocalPlayerCellId);
        if (target != null && (state.Phase != "ChampSelect" || state.Draft == null || state.Draft.GameId != target.GameId || (target.RequireFinalization && !state.Draft.IsFinalization) ||
            state.Draft.LocalLockedChampion != target.ChampionId || profile.ChampionId != target.ChampionId ||
            (mine != null && Roles.TryParse(mine.AssignedPosition, out var role) && role != target.Role)))
            throw new InvalidOperationException("La partida, el campeón o el estado del draft cambió; se canceló la importación automática.");
        if (mine != null && mine.VisibleChampion > 0 && mine.VisibleChampion != profile.ChampionId)
            throw new InvalidOperationException("El perfil no corresponde al campeón seleccionado en League.");
    }
    public async Task ImportItemsAsync(BuildProfile profile, Dataset dataset, CancellationToken ct = default, FinalLoadoutTarget? target = null, IReadOnlySet<Guid>? retainedProfileIds = null)
    {
        dataset.Validate(profile, requireRunes: false);
        await EnsureProfileChampionAsync(profile, ct, target);
        var summonerId = await CurrentSummonerIdAsync(ct);
        var path = $"lol-item-sets/v1/item-sets/{summonerId}/sets";
        var document = JsonNode.Parse((await SendAsync(HttpMethod.Get, path, null, ct)).GetRawText())!.AsObject();
        var sets = document["itemSets"]?.AsArray() ?? new JsonArray();
        if (document["itemSets"] == null) document["itemSets"] = sets;
        var uid = $"draftoptimizer-{profile.Id:D}";
        var title = profile.IsAram ? $"{dataset.NameOf(profile.ChampionId)} - ARAM" : $"{dataset.NameOf(profile.ChampionId)} - {profile.Role}";
        var retained = retainedProfileIds?.ToHashSet() ?? [];
        retained.Add(profile.Id);
        for (int i = sets.Count - 1; i >= 0; i--)
        {
            var old = sets[i];
            var oldUid = old?["uid"]?.ToString();
            if (string.IsNullOrWhiteSpace(oldUid) || !oldUid.StartsWith("draftoptimizer-", StringComparison.OrdinalIgnoreCase) ||
                old?["title"]?.ToString() != title ||
                old?["associatedChampions"] is not JsonArray champions || !champions.Any(x => int.TryParse(x?.ToString(), out var id) && id == profile.ChampionId)) continue;
            var oldId = oldUid["draftoptimizer-".Length..];
            if (Guid.TryParse(oldId, out var profileId) && !retained.Contains(profileId)) sets.RemoveAt(i);
        }
        var set = new
        {
            uid, title, type = "custom", map = "any", mode = "any", associatedMaps = profile.AssociatedMaps,
            associatedChampions = new[] { profile.ChampionId }, preferredItemSlots = Array.Empty<object>(), sortrank = 0,
            blocks = profile.Blocks.Select(b => new { type = ItemBlockText.CleanTitle(b.Name), hideIfSummonerSpell = b.HideIfSummonerSpell, showIfSummonerSpell = b.ShowIfSummonerSpell, items = b.Items.GroupBy(i => i).Select(g => new { id = g.Key.ToString(), count = g.Count() + (g.Key == GuardianAngelItemId ? 1 : 0) }) })
        };
        var node = JsonSerializer.SerializeToNode(set, Json.Options);
        var index = sets.Select((x, i) => (x, i)).Where(p => p.x?["uid"]?.GetValue<string>() == uid).Select(p => p.i).DefaultIfEmpty(-1).First();
        if (index >= 0) sets[index] = node; else sets.Add(node);
        if (target != null) await EnsureProfileChampionAsync(profile, ct, target);
        await EnsureCurrentSummonerAsync(summonerId, ct);
        await SendAsync(HttpMethod.Put, path, document, ct);
        await EnsureCurrentSummonerAsync(summonerId, ct);
        var savedDocument = JsonNode.Parse((await SendAsync(HttpMethod.Get, path, null, ct)).GetRawText())!.AsObject();
        var saved = savedDocument["itemSets"] is JsonArray savedSets && savedSets.Any(item =>
            item is JsonObject savedSet && savedSet["uid"]?.ToString() == uid && savedSet["associatedChampions"] is JsonArray champions &&
            champions.Any(champion => int.TryParse(champion?.ToString(), out var associatedId) && associatedId == profile.ChampionId));
        if (!saved) throw new IOException("League respondió al envío, pero la build no aparece en los conjuntos de la cuenta activa. Comprueba que sigues conectado a la cuenta correcta y vuelve a intentarlo.");
        await EnsureCurrentSummonerAsync(summonerId, ct);
    }
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Conecta primero con League.");
        if (path.StartsWith('/') || path.Contains(":") || path.Contains("..")) throw new ArgumentException("Ruta LCU inválida.");
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body, options: Json.Options);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) Disconnect();
            throw new HttpRequestException($"League rechazó {method} {path}: HTTP {(int)response.StatusCode}. Si son runas, revisa el espacio para páginas editables.", null, response.StatusCode);
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return JsonSerializer.SerializeToElement(new { });
        using var parsed = JsonDocument.Parse(text);
        return parsed.RootElement.Clone();
    }
    public void Disconnect() { _client?.Dispose(); _client = null; }
    public void Dispose() => Disconnect();
}
