using System.Globalization;
using System.Windows.Threading;
using DraftOptimizer.Core;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DraftOptimizer;

public sealed partial class MainWindow : Window
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftOptimizer");
    private string _profileDirectory;
    private readonly FirebaseCloudSync _firebase = new();
    private readonly SemaphoreSlim _cloudSyncGate = new(1, 1);
    private readonly DispatcherTimer _cloudPollTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? _cloudSyncDelay;
    private CancellationTokenSource? _profileEditDelay;
    private Task? _cloudSyncTask;
    private Task? _profileAutosaveTask;
    private bool _loadingProfile;
    private bool _updatingProfiles;
    private bool _cloudLocalDirty;
    private string? _cloudUpdateTime;
    private HashSet<Guid> _deletedProfileIds = [];
    private long _loadoutSuppressedGameId;
    private readonly HashSet<(int ChampionId, Role Role)> _loadoutSuppressedForGame = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly LcuClient _lcu = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Dataset? _data, _context;
    private DraftSession? _draft;
    private HashSet<int>? _pickable;
    private ObservableCollection<BuildProfile> _profiles = [];
    private BuildProfile? _editing;
    private bool _initializing = true, _polling, _busy, _loadingData, _profilesValid = true;
    private int _followedChampion;
    private DateTime _lastConnect;
    private Choice? _item;
    private string? _lastSuggestions;
    private FavoriteSettings _favorites = new();
    private readonly AutoDraftController _autoDraft = new();
    private readonly SemaphoreSlim _favoritesSave = new(1, 1);
    private bool _favoritesValid = true, _updatingAutomation;
    private bool _aramRoleMode;
    private Role _roleBeforeAram = Role.Middle;
    private readonly ObservableCollection<ChampionListItem> _championRows = [];
    private readonly Dictionary<int, ChampionListItem> _rowsById = [];
    private readonly Dictionary<(string Patch, int ChampionId), Suggestion> _aramStats = [];
    private readonly LoadoutCoordinator _loadout = new();
    private (long GameId, int ActionId)? _autoBanAttempted;
    private (long GameId, int ActionId, int ChampionId)? _lastSelectionHover;
    private ProfileStore Store => new(_profileDirectory);
    private Champion? SelectedChampion => ChampionBox.SelectedItem as Champion;
    private Role SelectedRole => _aramRoleMode ? _roleBeforeAram : RoleBox.SelectedItem is Role r ? r : _roleBeforeAram;
    private Role SelectedBanRole => BanRoleBox.SelectedItem is Role role ? role : SelectedRole;

    public MainWindow()
    {
        InitializeComponent();
        _profileDirectory = _directory;
        ProfilesBox.ItemsSource = _profiles;
        InitializeSimulation();
        InitializeBackgroundWork();
        SuggestionsList.ItemsSource = _championRows;
        SetChampionPresentation(SuggestionsList, portraits: true);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DraftOptimizer/0.1");
        _timer.Tick += async (_, _) => await PollAsync();
        _cloudPollTimer.Tick += async (_, _) => await PollCloudAsync();
        Closed += (_, _) => { _timer.Stop(); _cloudPollTimer.Stop(); _cloudSyncDelay?.Cancel(); _profileEditDelay?.Cancel(); _closed.Cancel(); _draftBuildOptionsCancellation?.Cancel(); _loadout.Dispose(); _lcu.Dispose(); _http.Dispose(); _firebase.Dispose(); _cloudSyncGate.Dispose(); _data?.Dispose(); _context?.Dispose(); };
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnLoaded;
        ProfileRoleBox.ItemsSource = Enum.GetValues<Role>(); ProfileRoleBox.SelectedItem = Role.Middle;
        RoleBox.ItemsSource = Enum.GetValues<Role>(); RoleBox.SelectedItem = _roleBeforeAram;
        BanRoleBox.ItemsSource = Enum.GetValues<Role>(); BanRoleBox.SelectedItem = Role.Middle;
        Directory.CreateDirectory(_directory);
        var settings = Path.Combine(_directory, "lockfile-path.txt");
        if (File.Exists(settings)) _lcu.LockfilePath = await File.ReadAllTextAsync(settings);
        await LoadProfileDataAsync(_directory);
        UpdateAutomationControls();
        _initializing = false;
        await InitializeConfidenceAsync();
        InitializeDatasetPublisher();
        InitializeSharedDatasetReader();
        await RestoreFirebaseSessionAsync();
        if (!_closed.IsCancellationRequested) { _timer.Start(); await PollAsync(); }
        await LoadDataAsync();
    }
    private void Show(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        NoticeTitle.Text = title;
        NoticeMessage.Text = message;
        NoticeTitle.Foreground = severity switch
        {
            InfoBarSeverity.Warning => Brushes.Gold,
            InfoBarSeverity.Error => Brushes.LightSalmon,
            InfoBarSeverity.Success => Brushes.Aquamarine,
            _ => Brushes.White
        };
        Notice.Visibility = Visibility.Visible;
    }

    private enum InfoBarSeverity { Informational, Warning, Error, Success }

    private void DismissNotice(object sender, RoutedEventArgs e) => Notice.Visibility = Visibility.Collapsed;

    private async Task LoadProfileDataAsync(string directory)
    {
        _profileDirectory = directory;
        _deletedProfileIds = [];
        Directory.CreateDirectory(directory);
        try
        {
            var deletedPath = Path.Combine(directory, "deleted-profile-ids.json");
            _deletedProfileIds = File.Exists(deletedPath) ? Json.Read<HashSet<Guid>>(await File.ReadAllTextAsync(deletedPath)) : [];
            var profiles = await Store.LoadAsync();
            var filtered = profiles.Where(profile => !_deletedProfileIds.Contains(profile.Id)).ToList();
            if (filtered.Count != profiles.Count) await Store.SaveAsync(filtered);
            ReplaceProfiles(filtered); _profilesValid = true;
        }
        catch (Exception ex) { ReplaceProfiles([]); _profilesValid = false; Show("No se pudieron leer tus perfiles", ex.Message, InfoBarSeverity.Error); }
        try { _favorites = await new FavoriteSettingsStore(directory).LoadAsync(); _favoritesValid = true; }
        catch (Exception ex) { _favoritesValid = false; _favorites = new() { AutoHoverEnabled = false }; Show("No se pudieron leer los favoritos", ex.Message, InfoBarSeverity.Error); }
        _editing = null;
        UpdateAutomationControls();
    }

    private static string UserDirectory(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid) || uid.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("Firebase devolvió un UID no válido.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftOptimizer", "profiles-by-user", uid);
    }

    private async Task SaveDeletedProfileIdsAsync(HashSet<Guid>? deletedIds = null)
    {
        Directory.CreateDirectory(_profileDirectory);
        var path = Path.Combine(_profileDirectory, "deleted-profile-ids.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, Json.Write(deletedIds ?? _deletedProfileIds));
        File.Move(temporary, path, true);
    }

    private async Task RestoreFirebaseSessionAsync()
    {
        try
        {
            if (await _firebase.RestoreSessionAsync(_closed.Token)) await ActivateFirebaseUserAsync();
            else SetFirebaseUi();
        }
        catch (Exception ex) { SetFirebaseUi(); CloudSyncStatus.Text = "Sesión sin conexión · " + ex.Message; }
    }

    private async Task ActivateFirebaseUserAsync()
    {
        var uid = _firebase.UserId ?? throw new InvalidOperationException("No hay una cuenta autenticada.");
        _profileEditDelay?.Cancel();
        ProfilesBox.SelectedItem = null; ProfileChampionBox.SelectedItem = null; _editing = null;
        await LoadProfileDataAsync(UserDirectory(uid));
        var cloud = await _firebase.LoadAsync(_closed.Token);
        if (cloud?.Payload is not { } payload)
        {
            _cloudLocalDirty = true;
            if (!await SyncCloudAsync(_closed.Token)) throw new IOException("No se pudo crear el documento de sincronización de esta cuenta.");
        }
        else
        {
            var snapshot = Json.Read<CloudProfileSnapshot>(payload);
            await ApplyCloudSnapshotAsync(snapshot, persistLocally: true);
            _cloudUpdateTime = cloud.UpdateTime;
            _cloudLocalDirty = false;
        }
        if (_data != null && _profiles.Count == 0) NewProfile();
        else if (_data != null && ProfilesBox.SelectedItem is null) ProfilesBox.SelectedItem = _profiles.OrderByDescending(p => p.UpdatedAt).First();
        SetFirebaseUi();
        _cloudPollTimer.Start();
        _ = PublishDailyDatasetAsync();
        if (_data != null && !_firebase.CanPublishDataset) _ = LoadDataAsync();
    }

    private async Task ApplyCloudSnapshotAsync(CloudProfileSnapshot snapshot, bool persistLocally)
    {
        if (snapshot.Profiles == null || snapshot.Favorites == null) throw new InvalidDataException("El documento sincronizado no contiene perfiles válidos.");
        _deletedProfileIds.UnionWith(snapshot.DeletedProfileIds ?? []);
        var activeProfiles = snapshot.Profiles.Where(profile => !_deletedProfileIds.Contains(profile.Id)).ToList();
        var favorites = snapshot.Favorites;
        if (favorites.Favorites == null || favorites.BanPriorities == null || favorites.Favorites.Any(p => !Enum.IsDefined(p.Key) || p.Value == null || p.Value.Any(id => id <= 0)) ||
            favorites.BanPriorities.Any(p => !Enum.IsDefined(p.Key) || p.Value == null || p.Value.Any(id => id <= 0) || p.Value.Distinct().Count() != p.Value.Count))
            throw new InvalidDataException("Los favoritos sincronizados no son válidos.");
        if (!Enum.IsDefined(favorites.FlashKey)) favorites.FlashKey = FlashKey.Profile;
        favorites.AutoLockSeconds = Math.Clamp(favorites.AutoLockSeconds, 1, 30);
        if (snapshot.Profiles.Any(p => p == null || p.Id == Guid.Empty || !Enum.IsDefined(p.Role) || p.ChampionId <= 0))
            throw new InvalidDataException("Uno de los perfiles de runas/objetos sincronizados no es válido.");
        if (persistLocally)
        {
            await Store.SaveAsync(activeProfiles);
            await new FavoriteSettingsStore(_profileDirectory).SaveAsync(favorites);
            await SaveDeletedProfileIdsAsync();
        }
        ReplaceProfiles(activeProfiles);
        _favorites = favorites;
        _profilesValid = _favoritesValid = true;
        _editing = null;
        UpdateAutomationControls();
        if (_data != null && _profiles.Count > 0) ProfilesBox.SelectedItem = _profiles.OrderByDescending(p => p.UpdatedAt).First();
        _lastSuggestions = null; RefreshSuggestions();
    }

    private void SetFirebaseUi()
    {
        var signedIn = _firebase.IsSignedIn;
        FirebaseEmailBox.IsEnabled = !signedIn;
        FirebasePasswordBox.IsEnabled = !signedIn;
        FirebaseSignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        FirebaseRegisterButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        FirebaseSignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        FirebaseImportLocalButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (signedIn) FirebaseEmailBox.Text = _firebase.Email ?? FirebaseEmailBox.Text;
        CloudSyncStatus.Text = signedIn
            ? $"Sincronización activa · {_firebase.Email ?? _firebase.UserId} · se sincroniza tras cada cambio."
            : "Sin sesión · los cambios se guardan en este equipo.";
    }

    private void ReplaceProfiles(IEnumerable<BuildProfile> profiles)
    {
        _updatingProfiles = true;
        try
        {
            ProfilesBox.SelectedItem = null;
            _profiles.Clear();
            foreach (var profile in profiles) _profiles.Add(profile);
        }
        finally { _updatingProfiles = false; }
    }

    private async Task SignInFirebaseAsync(bool createAccount)
    {
        var email = FirebaseEmailBox.Text.Trim();
        var password = FirebasePasswordBox.Password;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) throw new InvalidOperationException("Escribe tu correo y contraseña.");
        var hadPendingEdit = _profileEditDelay is { IsCancellationRequested: false };
        _profileEditDelay?.Cancel();
        if (hadPendingEdit && ProfileChampionBox.SelectedItem is Champion) await SaveAsync();
        FirebaseSignInButton.IsEnabled = FirebaseRegisterButton.IsEnabled = false;
        CloudSyncStatus.Text = "Conectando con Firebase…";
        try
        {
            if (createAccount) await _firebase.RegisterAsync(email, password, _closed.Token);
            else await _firebase.SignInAsync(email, password, _closed.Token);
            FirebasePasswordBox.Password = "";
            await ActivateFirebaseUserAsync();
        }
        finally { FirebaseSignInButton.IsEnabled = FirebaseRegisterButton.IsEnabled = true; }
    }

    private async void FirebaseSignInClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try { await SignInFirebaseAsync(createAccount: false); }
        catch (Exception ex) { CloudSyncStatus.Text = "No se pudo iniciar sesión · " + ex.Message; Show("Firebase", ex.Message, InfoBarSeverity.Error); }
    }

    private async void FirebaseRegisterClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try { await SignInFirebaseAsync(createAccount: true); }
        catch (Exception ex) { CloudSyncStatus.Text = "No se pudo crear la cuenta · " + ex.Message; Show("Firebase", ex.Message, InfoBarSeverity.Error); }
    }

    private async void FirebaseSignOutClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var hadPendingEdit = _profileEditDelay is { IsCancellationRequested: false };
        _profileEditDelay?.Cancel();
        if (hadPendingEdit && ProfileChampionBox.SelectedItem is Champion)
            try { await SaveAsync(); } catch { }
        _cloudPollTimer.Stop(); _cloudSyncDelay?.Cancel();
        await SyncCloudAsync();
        _firebase.SignOut(); _cloudUpdateTime = null;
        await LoadProfileDataAsync(_directory);
        if (_data != null && _profiles.Count > 0) ProfilesBox.SelectedItem = _profiles.OrderByDescending(p => p.UpdatedAt).First();
        else if (_data != null) NewProfile();
        SetFirebaseUi();
    }

    private async void FirebaseImportLocalClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !_firebase.IsSignedIn) return;
        if (!ShowPrompt("Importar configuración local", "Se copiarán los favoritos, ajustes y perfiles de este equipo a la cuenta de Firebase actual. Si ya hay datos en la nube, se reemplazarán.", "Importar y sincronizar", "Cancelar")) return;
        try
        {
            var legacy = _directory;
            ReplaceProfiles(await new ProfileStore(legacy).LoadAsync());
            _deletedProfileIds.Clear();
            _favorites = await new FavoriteSettingsStore(legacy).LoadAsync();
            _profilesValid = _favoritesValid = true;
            await Store.SaveAsync(_profiles);
            await SaveDeletedProfileIdsAsync();
            await new FavoriteSettingsStore(_profileDirectory).SaveAsync(_favorites);
            UpdateAutomationControls();
            await SyncCloudAsync();
        }
        catch (Exception ex) { Show("No se pudo importar la configuración local", ex.Message, InfoBarSeverity.Error); }
    }

    private void QueueCloudSync()
    {
        if (!_firebase.IsSignedIn || _closed.IsCancellationRequested) return;
        _cloudSyncDelay?.Cancel(); _cloudSyncDelay?.Dispose();
        var delay = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _cloudSyncDelay = delay;
        _cloudLocalDirty = true;
        _cloudSyncTask = DebouncedCloudSyncAsync(delay);
    }

    private async Task CancelProfileAutosaveAsync()
    {
        var pending = _profileAutosaveTask;
        _profileEditDelay?.Cancel();
        if (pending != null) try { await pending; } catch { }
        _profileAutosaveTask = null;
    }

    private async Task WaitForQueuedCloudSyncAsync()
    {
        var pending = _cloudSyncTask;
        if (pending != null) try { await pending; } catch { }
        _cloudSyncTask = null;
    }

    private async Task DebouncedCloudSyncAsync(CancellationTokenSource delay)
    {
        try { await Task.Delay(500, delay.Token); await SyncCloudAsync(delay.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_cloudSyncDelay, delay)) { _cloudSyncDelay = null; delay.Dispose(); }
        }
    }

    private async Task<bool> SyncCloudAsync(CancellationToken ct = default)
    {
        if (!_firebase.IsSignedIn) return true;
        await _cloudSyncGate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var remote = await _firebase.LoadAsync(ct);
                var deletedProfileIds = new HashSet<Guid>(_deletedProfileIds);
                if (remote?.Payload is { } remotePayload)
                {
                    var remoteSnapshot = Json.Read<CloudProfileSnapshot>(remotePayload);
                    deletedProfileIds.UnionWith(remoteSnapshot.DeletedProfileIds ?? []);
                }
                var activeProfiles = _profiles.Where(profile => !deletedProfileIds.Contains(profile.Id)).ToList();
                var payload = Json.Write(new CloudProfileSnapshot
                {
                    Profiles = activeProfiles, Favorites = _favorites, DeletedProfileIds = deletedProfileIds
                });
                try
                {
                    _cloudUpdateTime = await _firebase.SaveAsync(payload, ct, remote?.UpdateTime, expectAbsent: remote == null);
                    _deletedProfileIds = deletedProfileIds;
                    if (activeProfiles.Count != _profiles.Count)
                    {
                        foreach (var profile in _profiles.Where(profile => _deletedProfileIds.Contains(profile.Id)).ToList()) _profiles.Remove(profile);
                        if (_editing != null && _deletedProfileIds.Contains(_editing.Id)) _editing = null;
                        await Store.SaveAsync(activeProfiles);
                    }
                    await SaveDeletedProfileIdsAsync();
                    _cloudLocalDirty = false;
                    SetFirebaseUi();
                    ProfileSaveStatus.Text = "Perfil y ajustes sincronizados con Firebase.";
                    return true;
                }
                catch (FirebaseCloudSync.CloudConflictException) when (attempt < 2) { }
            }
            throw new FirebaseCloudSync.CloudConflictException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            CloudSyncStatus.Text = "Cambios guardados localmente · error al sincronizar: " + ex.Message;
            return false;
        }
        finally { _cloudSyncGate.Release(); }
    }

    private async Task<(List<BuildProfile> Profiles, FavoriteSettings Favorites, HashSet<Guid> DeletedIds, string? UpdateTime)?> DeleteProfileFromCloudFirstAsync(Guid profileId)
    {
        await _cloudSyncGate.WaitAsync(_closed.Token);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var remote = await _firebase.LoadAsync(_closed.Token);
                var remoteSnapshot = remote?.Payload is { } remotePayload ? Json.Read<CloudProfileSnapshot>(remotePayload) : null;
                if (remoteSnapshot?.Profiles?.Any(profile => profile.Id == profileId) != true)
                    return null;

                var deletedIds = new HashSet<Guid>(_deletedProfileIds);
                deletedIds.UnionWith(remoteSnapshot?.DeletedProfileIds ?? []);
                deletedIds.Add(profileId);

                var profilesById = new Dictionary<Guid, BuildProfile>();
                foreach (var profile in remoteSnapshot?.Profiles ?? [])
                    if (!deletedIds.Contains(profile.Id)) profilesById[profile.Id] = profile;
                foreach (var profile in _profiles)
                {
                    if (deletedIds.Contains(profile.Id)) continue;
                    if (!profilesById.TryGetValue(profile.Id, out var remoteProfile) || profile.UpdatedAt > remoteProfile.UpdatedAt)
                        profilesById[profile.Id] = profile;
                }
                var profiles = profilesById.Values.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var favorites = _cloudLocalDirty ? _favorites : remoteSnapshot?.Favorites ?? _favorites;
                var payload = Json.Write(new CloudProfileSnapshot { Profiles = profiles, Favorites = favorites, DeletedProfileIds = deletedIds });
                try
                {
                    var updateTime = await _firebase.SaveAsync(payload, _closed.Token, remote!.UpdateTime);
                    return (profiles, favorites, deletedIds, updateTime);
                }
                catch (FirebaseCloudSync.CloudConflictException) when (attempt < 2) { }
            }
            throw new FirebaseCloudSync.CloudConflictException();
        }
        finally { _cloudSyncGate.Release(); }
    }

    private async Task PollCloudAsync()
    {
        if (_busy || !_firebase.IsSignedIn || _cloudSyncDelay is { IsCancellationRequested: false } ||
            _profileEditDelay is { IsCancellationRequested: false }) return;
        try
        {
            if (_cloudLocalDirty) { await SyncCloudAsync(_closed.Token); return; }
            if (ReferenceEquals(MainTabs.SelectedItem, LoadoutTab)) return;
            var remote = await _firebase.LoadAsync(_closed.Token);
            if (remote?.Payload is not { } payload || remote.UpdateTime == _cloudUpdateTime) return;
            var snapshot = Json.Read<CloudProfileSnapshot>(payload);
            await ApplyCloudSnapshotAsync(snapshot, persistLocally: true);
            _cloudUpdateTime = remote.UpdateTime;
            _cloudLocalDirty = false;
            CloudSyncStatus.Text = $"Sincronizado · {_firebase.Email ?? _firebase.UserId} · cambios recibidos de otro dispositivo.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CloudSyncStatus.Text = "Sesión activa · no se pudo revisar la nube: " + ex.Message; }
    }
    private async Task RunAsync(Func<Task> action, string success, bool showSuccess = true)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); if (showSuccess) Show(success, "", InfoBarSeverity.Success); }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { Show("No se pudo completar", ex.Message, InfoBarSeverity.Error); }
        finally { _busy = false; }
    }
    private void BindCatalog(Dataset data)
    {
        _loadout.Reset();
            BuildProfile? current = null;
            if (_data != null) { try { current = ReadProfile(); } catch { } }
            var old = _data; _data = data;
            _initializing = true;
            ChampionBox.ItemsSource = data.Champions; ProfileChampionBox.ItemsSource = data.Champions;
            BanChampionBox.ItemsSource = data.Champions; RefreshBanPriorities();
            PrimaryBox.ItemsSource = data.Choices("runePathData"); SecondaryBox.ItemsSource = data.Choices("runePathData");
            F0.ItemsSource = data.Shards(0); F1.ItemsSource = data.Shards(1); F2.ItemsSource = data.Shards(2);
            _initializing = false;
            if (current != null) LoadProfile(current);
            else if (_profiles.Count > 0) ProfilesBox.SelectedItem = _profiles.OrderByDescending(p => p.UpdatedAt).First();
            else NewProfile();
            UpdateDraftBuildOptionsForSelection();
            UpdateFavoritesText();
            old?.Dispose(); RefreshSimulation();
            _ = ApplySpanishNamesAsync(data);
    }
    private async Task LoadDataAsync()
    {
        if (_loadingData) return;
        _loadingData = true;
        _loadout.Reset();
        try
        {
            var provider = new DatasetProvider(_http, Path.Combine(_directory, "cache"));
            var (catalog, _) = await provider.LoadAsync("current-patch", _closed.Token);
            using var catalogLease = catalog;
            if (_data == null)
            {
                var basic = System.Text.Json.Nodes.JsonNode.Parse(catalog.Root.GetRawText())!;
                foreach (var champion in basic["championData"]!.AsObject())
                    champion.Value!["statsByRole"] = new System.Text.Json.Nodes.JsonObject();
                BindCatalog(new Dataset(basic.ToJsonString()));
                await PollAsync();
                DataText.Text = "Catálogo disponible · descargando estadísticas Diamante+…";
            }
            if (!_firebase.CanPublishDataset)
            {
                await ApplySharedDatasetAsync();
                return;
            }
            _sharedDatasetVersion = null;
            var statistics = new DiamondStatisticsProvider(_http, Path.Combine(_directory, "cache"));
            var progress = new Progress<string>(message => DataText.Text = message);
            var (data, cached) = await statistics.LoadAsync(catalog, "7-days", _closed.Token, progress);
            BindCatalog(data);
            DataText.Text = $"Estadísticas de los últimos 7 días · catálogo {data.Patch} · {data.Date} · {(cached ? "CACHÉ LOCAL" : "datos en línea")} · Lolalytics Diamante+";
            Show("Catálogo disponible", cached ? "Usando datos guardados; revisa la fecha antes de importar." : "Cargando estadísticas de enfrentamientos y sinergias…");
            RefreshSuggestions();
            try
            {
                var (context, contextCached) = await statistics.LoadAsync(catalog, "30-days", _closed.Token, progress);
                _context?.Dispose(); _context = context;
                DataText.Text += $" · contexto 30 días {context.Date}{(contextCached ? " (caché)" : "")}";
                RefreshSuggestions(); RefreshSimulation();
                if (_profilesValid && _favoritesValid) Notice.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { Show("Catálogo disponible sin contexto", "El orden usa solo estadísticas base. " + ex.Message, InfoBarSeverity.Warning); }
        }
        catch (Exception ex) { Show(_data == null ? "No se pudo cargar el catálogo" : "Estadísticas Diamante+ pendientes", ex.Message, InfoBarSeverity.Error); }
        finally { _initializing = false; _loadingData = false; }
    }
    private async Task PollAsync(bool allowWhileBusy = false)
    {
        if (_polling || _closed.IsCancellationRequested || (_busy && !allowWhileBusy)) return;
        _polling = true;
        try
        {
            if (!_lcu.Connected)
            {
                if ((DateTime.UtcNow - _lastConnect).TotalSeconds < 6) return;
                _lastConnect = DateTime.UtcNow;
                await _lcu.ConnectAsync(_closed.Token);
            }
            var state = await _lcu.StateAsync(_closed.Token);
            _draft = state.Draft;
            SetAramRoleMode(_draft?.IsAram == true);
            AdvanceLoadout(state);
            if (_draft?.CurrentBan != null && !_draft.IsAram) await AdvanceBanAsync();
            _pickable = _draft == null || _draft.IsFinalization || _draft.IsAram || _draft.LocalLockedChampion > 0 ? null : await _lcu.PickableAsync(_closed.Token);
            ConnectionText.Text = "● League conectado · " + state.Phase;
            _timer.Interval = _draft == null ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(_draft.CurrentPick == null && _draft.CurrentBan == null ? 500 : 200);
            var remaining = _draft?.Timer?.RemainingMilliseconds(DateTimeOffset.UtcNow);
            DraftText.Text = _draft == null ? "Esperando la selección de campeones." : _draft.IsAram ? "ARAM · opciones disponibles del banco." : _draft.CurrentPick != null ? "Es tu turno de seleccionar." + (remaining.HasValue ? $" Quedan {Math.Ceiling(remaining.Value / 1000):F0} s." : "") : "Draft en curso · esperando tu turno.";
            if (_draft == null) _followedChampion = 0;
            if (FollowBox.IsChecked == true && !_busy && _draft != null)
            {
                var mine = _draft.MyTeam.FirstOrDefault(p => p.CellId == _draft.LocalPlayerCellId);
                if (mine != null && Roles.TryParse(mine.AssignedPosition, out var role)) RoleBox.SelectedItem = role;
                if (mine?.VisibleChampion > 0 && mine.VisibleChampion != _followedChampion)
                {
                    _followedChampion = mine.VisibleChampion;
                    ChampionBox.SelectedItem = _data?.Champions.FirstOrDefault(c => c.Id == mine.VisibleChampion);
                }
            }
            RefreshSuggestions(); UpdatePickButtons();
            if (_draft?.IsAram == true)
            {
                _autoDraft.PauseCurrentTurn();
                AutomationText.Text = "ARAM: selección automática desactivada; usa los cambios disponibles en el banco.";
            }
            else if (_draft?.CurrentBan == null) { _autoBanAttempted = null; await AdvanceAutomationAsync(); }
        }
        catch (Exception) when (!_closed.IsCancellationRequested)
        {
            _lcu.Disconnect(); _draft = null; _pickable = null; _followedChampion = 0;
            _timer.Interval = TimeSpan.FromSeconds(2);
            _autoDraft.PauseCurrentTurn();
            _loadout.Reset(); SetLoadoutStatus("Runas y objetos: cliente desconectado.");
            AutomationText.Text = "League desconectado; no se enviarán selecciones automáticas.";
            AutomationSummary.Text = "Auto detenido: League desconectado.";
            ConnectionText.Text = "○ League desconectado"; DraftText.Text = "Abre League para conectar. Reintento automático cada 6 s."; TeamsText.Text = "";
            UpdatePickButtons(); RefreshSuggestions();
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (_closed.IsCancellationRequested) { }
        finally { _polling = false; }
    }
    private void RefreshSuggestions() => _ = RefreshSuggestionsAsync();
    private async Task RefreshSuggestionsAsync()
    {
        if (_initializing || _data == null || _searchDelay.IsEnabled || _closed.IsCancellationRequested) return;
        var data = _data; var context = _context; var role = SelectedRole;
        var snapshot = CaptureDraftSnapshot(out var draftKey);
        var pickable = _pickable?.ToHashSet();
        var search = SearchBox.Text?.Trim() ?? "";
        var favorites = _favorites.ForRole(role).ToHashSet();
        bool favoritesValid = _favoritesValid;
        var key = $"{SelectedRole}|{search}|{_data.Date}|{_context?.Date}|{draftKey}|{string.Join(",", _pickable?.OrderBy(x => x) ?? Enumerable.Empty<int>())}|{string.Join(",", favorites.OrderBy(x => x))}|{_favoritesValid}";
        if (key == _lastSuggestions && ReferenceEquals(data, _lastSuggestionsData) && ReferenceEquals(context, _lastSuggestionsContext)) return;
        _lastSuggestions = key; _lastSuggestionsData = data; _lastSuggestionsContext = context;
        _suggestionsCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _suggestionsCancellation = cancellation;
        var version = ++_suggestionsVersion;
        try
        {
            if (snapshot?.IsAram == true)
            {
                var availableIds = snapshot.AramAvailableChampionIds;
                if (snapshot.LocalVisibleChampion > 0) availableIds.Add(snapshot.LocalVisibleChampion);
                availableIds.UnionWith(snapshot.MyTeam.SelectMany(player => new[] { player.ChampionId, player.VisibleChampion, player.ChampionPickIntent }));
                var candidates = data.Champions.Where(c => availableIds.Contains(c.Id)).ToList();
                var missing = candidates.Where(c => !_aramStats.ContainsKey((data.Patch, c.Id))).ToList();
                var statErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
                if (missing.Count > 0)
                {
                    using var limiter = new SemaphoreSlim(4, 4);
                    await Task.WhenAll(missing.Select(async champion =>
                    {
                        await limiter.WaitAsync(cancellation.Token);
                        try
                        {
                            var stat = await new LolalyticsProvider(_http).GetAramStatsAsync(champion, data.Patch, cancellation.Token);
                            lock (_aramStats) _aramStats[(data.Patch, champion.Id)] = stat;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { statErrors.Enqueue($"{champion.Name}: {ex.Message}"); }
                        finally { limiter.Release(); }
                    }));
                }
                if (version != _suggestionsVersion || cancellation.IsCancellationRequested || !ReferenceEquals(data, _data)) return;
                TeamsText.Text = "ARAM · estadísticas globales de ARAM; el puntaje usa WR y partidas de ARAM. No se aplican cruces de líneas.";
                SetDraftSummary($"Las recomendaciones se ordenan con datos de ARAM de Lolalytics (Emerald+), usando confianza Wilson al {WinrateConfidence.LevelLabel}.");
                if (!statErrors.IsEmpty && candidates.All(c => !_aramStats.ContainsKey((data.Patch, c.Id))))
                    Show("Estadísticas ARAM no disponibles", statErrors.TryPeek(out var message) ? message : "No se recibieron datos de winrate.", InfoBarSeverity.Warning);
                var aramConfidence = ConfidencePlacement.RankSuggestions(candidates
                    .Select(champion => _aramStats.GetValueOrDefault((data.Patch, champion.Id)))
                    .OfType<Suggestion>());
                var rowsAram = candidates.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Select(c =>
                    {
                        if (!_rowsById.TryGetValue(c.Id, out var row)) _rowsById[c.Id] = row = new ChampionListItem(c);
                        row.SetPortraitPatch(data.Patch);
                        row.SetConfidencePlacement(aramConfidence.TryGetValue(c.Id, out var placement) ? placement : null);
                        row.Update(c, role, favorites.Contains(c.Id), true, _aramStats.GetValueOrDefault((data.Patch, c.Id)), favoritesValid);
                        return row;
                    })
                    .OrderByDescending(c => c.IsFavorite).ThenByDescending(c => c.Recommendation?.Score ?? double.MinValue).ThenByDescending(c => c.Recommendation?.Games ?? 0).ThenBy(c => c.Champion.Name).ToList();
                IncrementalCollection.Synchronize(_championRows, rowsAram);
                return;
            }
            bool includeLocal = snapshot?.CurrentPick == null;
            var work = await DraftComputation.CalculateAsync(data, context, role, snapshot, pickable, includeLocal, true, cancellation.Token);
            if (version != _suggestionsVersion || cancellation.IsCancellationRequested || !ReferenceEquals(data, _data) || !ReferenceEquals(context, _context)) return;
            UpdateDraftWinrate(snapshot, work);
            var recommendations = work.Suggestions.ToDictionary(s => s.Champion.Id);
            var unavailable = snapshot?.Unavailable(includeLocal) ?? [];
            var rows = data.Champions.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(c =>
                {
                    if (!_rowsById.TryGetValue(c.Id, out var row)) _rowsById[c.Id] = row = new ChampionListItem(c);
                    row.SetPortraitPatch(data.Patch);
                    row.SetConfidencePlacement(work.Placements.TryGetValue(c.Id, out var placement) ? placement : null);
                    row.Update(c, role, favorites.Contains(c.Id), !unavailable.Contains(c.Id) && (pickable == null || pickable.Contains(c.Id)), recommendations.GetValueOrDefault(c.Id), favoritesValid);
                    return row;
                })
                .OrderByDescending(c => c.IsFavorite).ThenByDescending(c => c.Recommendation?.Score ?? double.MinValue).ThenByDescending(c => c.Recommendation?.Games ?? 0).ThenBy(c => c.Champion.Name).ToList();
            IncrementalCollection.Synchronize(_championRows, rows);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _suggestionsVersion && !_closed.IsCancellationRequested) { _lastSuggestions = null; Show("No se pudo actualizar la lista", ex.Message, InfoBarSeverity.Warning); } }
        finally { if (ReferenceEquals(_suggestionsCancellation, cancellation)) _suggestionsCancellation = null; cancellation.Dispose(); }
    }
    private void UpdateDraftWinrate(DraftSession? snapshot, DraftComputationResult work)
    {
        if (_data == null || snapshot == null)
        {
            TeamsText.Text = ""; SetDraftSummary("Orden inicial: confianza del winrate y partidas del rol seleccionado.");
            return;
        }
        var composition = work.Composition!;
        string Describe(TeamComposition team) => string.Join(" · ", team.Picks.OrderBy(p => p.Key)
            .Select(p => $"{_data.NameOf(p.Value)} ({p.Key}{(team.InferredRoles.Contains(p.Key) ? "*" : "")})"));
        TeamsText.Text = "Aliados: " + Describe(composition.Allies) + "\nRivales: " + Describe(composition.Enemies);
        if (!snapshot.HasCompositionPicks) { SetDraftSummary("Sin picks: confianza del rol seleccionado; el puntaje cambiará a draft al aparecer picks."); return; }
        if (_context == null) { SetDraftSummary("WR del draft no disponible: faltan estadísticas de contexto de 30 días."); return; }
        var result = work.Analysis!;
        SetDraftSummary($"WR estimado del draft actual: {result.Winrate:P1} · confianza {(result.HasConfidence ? (result.ConfidenceLower * 100).ToString("F1") : "—")} · rango {result.ConfidenceLower:P1}–{result.ConfidenceUpper:P1} · {result.AllyPicks + result.EnemyPicks}/10 picks · {result.MatchupPairs} cruces y {result.SynergyPairs} sinergias." +
            (result.InferredRoles > 0 ? $" {result.InferredRoles} roles estimados (*)." : ""));
    }
    private void SetDraftSummary(string text) { DraftWinrateText.Text = text; ChampionDraftSummary.Text = text; }
    private void SetLoadoutStatus(string text) { LoadoutStatus.Text = text; ChampionLoadoutSummary.Text = text; UpdateLoadoutPreview(); }
    private void UpdatePickButtons()
    {
        bool enabled = _lcu.Connected && _draft?.IsAram != true && _draft?.CurrentPick != null && SelectedChampion != null && (_pickable?.Contains(SelectedChampion.Id) ?? false);
        HoverButton.IsEnabled = enabled; LockButton.IsEnabled = enabled;
    }
    private void SetAramRoleMode(bool isAram)
    {
        if (_aramRoleMode == isAram) return;
        if (isAram && RoleBox.SelectedItem is Role currentRole) _roleBeforeAram = currentRole;
        _aramRoleMode = isAram;
        RoleBox.IsEnabled = !isAram;
        RoleBox.ToolTip = isAram ? "ARAM · selección de línea deshabilitada" : "Selecciona una línea";
        UpdateDraftBuildOptionsForSelection();
        UpdateLoadoutPreview();
        _lastSuggestions = null;
        UpdateFavoritesText();
        RefreshSuggestions();
    }
    private void FiltersChanged(object sender, SelectionChangedEventArgs e) { RefreshSuggestions(); UpdateFavoritesText(); UpdateDraftBuildOptionsForSelection(); UpdateLoadoutPreview(); }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        _suggestionsCancellation?.Cancel(); ++_suggestionsVersion; _lastSuggestions = null;
        _searchDelay.Stop(); if (!_closed.IsCancellationRequested) _searchDelay.Start();
    }
    private async void SuggestionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuggestionsList.SelectedItem is not ChampionListItem suggestion || !suggestion.Available) return;
        ChampionBox.SelectedItem = suggestion.Champion;
        var draft = _draft;
        var action = draft?.CurrentPick;
        if (_busy || !_lcu.Connected || draft == null || draft.IsAram || action == null || _pickable?.Contains(suggestion.Champion.Id) != true) return;
        var turn = (draft.GameId, action.Id, suggestion.Champion.Id);
        if (_lastSelectionHover == turn) return;
        _lastSelectionHover = turn;
        _autoDraft.PauseCurrentTurn(draft);
        await RunAsync(() => _lcu.PickAsync(suggestion.Champion.Id, complete: false, _closed.Token), $"{suggestion.Champion.Name} preseleccionado en League");
    }
    private void ChampionChanged(object sender, SelectionChangedEventArgs e) { UpdatePickButtons(); UpdateDraftBuildOptionsForSelection(); UpdateLoadoutPreview(); }
    private void PrimaryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _data == null || PrimaryBox.SelectedItem is not Choice c) return;
        var boxes = new[] { P0, P1, P2, P3 };
        for (int i = 0; i < 4; i++) { boxes[i].ItemsSource = _data.Runes(c.Id, i); boxes[i].SelectedIndex = 0; }
        RenderPrimaryRuneGrid();
        ScheduleProfileAutosave();
    }
    private void SecondaryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _data == null || SecondaryBox.SelectedItem is not Choice c) return;
        var choices = _data.Runes(c.Id).Where(x => _data.RuneSlot(x.Id) > 0).ToList();
        S0.ItemsSource = choices; S1.ItemsSource = choices;
        S0.SelectedIndex = 0; S1.SelectedIndex = choices.Count > 3 ? 3 : 0;
        RenderSecondaryRuneGrid();
        ScheduleProfileAutosave();
    }
    private ToggleButton RuneIconButton(Choice rune, bool selected, bool enabled, Action<ToggleButton> click)
    {
        var button = new ToggleButton { Width = 48, Height = 48, Padding = new Thickness(4), IsChecked = selected, IsEnabled = enabled };
        AutomationProperties.SetName(button, rune.Name);
        if (rune.IconUrl is { } url)
        {
            button.Content = new PortraitImage { Width = 36, Height = 36, Url = url };
        }
        else button.Content = new TextBlock { Text = rune.Name, TextWrapping = TextWrapping.Wrap, FontSize = 9, TextAlignment = TextAlignment.Center };
        ToolTipService.SetToolTip(button, rune.Name);
        button.Click += (_, _) => click(button);
        return button;
    }
    private void RenderPrimaryRuneGrid()
    {
        if (_data == null || PrimaryRuneGrid == null) return;
        PrimaryRuneGrid.Children.Clear();
        var boxes = new[] { P0, P1, P2, P3 };
        for (int rowIndex = 0; rowIndex < boxes.Length; rowIndex++)
        {
            int index = rowIndex;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = $"Fila {rowIndex + 1}", Width = 48, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            foreach (var rune in boxes[rowIndex].Items.OfType<Choice>())
                row.Children.Add(RuneIconButton(rune, (boxes[rowIndex].SelectedItem as Choice)?.Id == rune.Id, true, _ =>
                {
                    boxes[index].SelectedItem = rune;
                    RenderPrimaryRuneGrid();
                }));
            PrimaryRuneGrid.Children.Add(row);
        }
    }
    private void RenderSecondaryRuneGrid()
    {
        if (_data == null || SecondaryRuneGrid == null) return;
        SecondaryRuneGrid.Children.Clear();
        var selected = new[] { S0.SelectedItem as Choice, S1.SelectedItem as Choice };
        for (int slot = 1; slot <= 3; slot++)
        {
            int rowSlot = slot;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = $"Fila {slot + 1}", Width = 48, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var current = selected.FirstOrDefault(x => x != null && _data.RuneSlot(x.Id) == rowSlot);
            bool enabled = current != null || selected.Any(x => x == null);
            foreach (var rune in S0.Items.OfType<Choice>().Where(x => _data.RuneSlot(x.Id) == rowSlot))
                row.Children.Add(RuneIconButton(rune, current?.Id == rune.Id, enabled, button =>
                {
                    var s0 = S0.SelectedItem as Choice;
                    var s1 = S1.SelectedItem as Choice;
                    if (s0?.Id == rune.Id || s1?.Id == rune.Id)
                    {
                        if (s0?.Id == rune.Id) S0.SelectedItem = null;
                        if (s1?.Id == rune.Id) S1.SelectedItem = null;
                    }
                    else if (s0 != null && _data.RuneSlot(s0.Id) == rowSlot) S0.SelectedItem = rune;
                    else if (s1 != null && _data.RuneSlot(s1.Id) == rowSlot) S1.SelectedItem = rune;
                    else if (s0 == null) S0.SelectedItem = rune;
                    else if (s1 == null) S1.SelectedItem = rune;
                    RenderSecondaryRuneGrid();
                }));
            SecondaryRuneGrid.Children.Add(row);
        }
    }
    private static void Select(ComboBox box, int id) { box.SelectedItem = box.Items.OfType<Choice>().FirstOrDefault(c => c.Id == id); }
    private void NewProfile()
    {
        if (_data == null) return;
        _loadingProfile = true;
        try
        {
            _editing = null; ProfilesBox.SelectedItem = null;
            LoadSpellChoices();
            ProfileName.Text = "Mi configuración"; SourceText.Text = "Personalizada";
            Select(PrimaryBox, 8100); Select(SecondaryBox, 8200);
            PrimaryChanged(PrimaryBox, null!); SecondaryChanged(SecondaryBox, null!);
            Select(S0, 8226); Select(S1, 8210); Select(F0, 5008); Select(F1, 5008); Select(F2, 5001);
            RenderPrimaryRuneGrid(); RenderSecondaryRuneGrid();
            ItemsText.Text = "Inicio: 1056, 2003, 2003\nNúcleo: 6655, 3020, 3089";
            if (SelectedChampion == null) ChampionBox.SelectedItem = _data.Champions.FirstOrDefault(c => c.Key == "Ahri") ?? _data.Champions[0];
            ProfileChampionBox.SelectedItem = SelectedChampion ?? _data.Champions.First(); ProfileRoleBox.SelectedItem = SelectedRole;
        }
        finally { _loadingProfile = false; }
    }
    private void LoadProfile(BuildProfile p)
    {
        _loadingProfile = true;
        try
        {
            _editing = p;
            LoadSpellChoices(p);
            ProfileChampionBox.SelectedItem = _data?.Champions.FirstOrDefault(c => c.Id == p.ChampionId); ProfileRoleBox.SelectedItem = p.Role;
            ProfileName.Text = p.Name; SourceText.Text = p.Source + " · " + p.Patch + " · " + p.UpdatedAt.ToLocalTime().ToString("g") + (p.Perks.Length == 0 ? " · Sin runas; puedes enviar los objetos directamente a League." : "");
            ChampionBox.SelectedItem = _data?.Champions.FirstOrDefault(c => c.Id == p.ChampionId); RoleBox.SelectedItem = p.Role;
            Select(PrimaryBox, p.PrimaryStyleId); Select(SecondaryBox, p.SubStyleId); PrimaryChanged(PrimaryBox, null!); SecondaryChanged(SecondaryBox, null!);
            var boxes = new[] { P0, P1, P2, P3, S0, S1, F0, F1, F2 };
            for (int i = 0; i < boxes.Length; i++) { if (p.Perks.Length > i) Select(boxes[i], p.Perks[i]); else boxes[i].SelectedItem = null; }
            RenderPrimaryRuneGrid(); RenderSecondaryRuneGrid();
            ItemsText.Text = ItemBlockText.Format(p.Blocks); UpdateLoadoutPreview();
        }
        finally { _loadingProfile = false; }
    }
    private async Task ApplySpanishNamesAsync(Dataset data)
    {
        try
        {
            var names = await new SpanishNameCatalog(_http, Path.Combine(_directory, "cache")).LoadAsync(data.Patch, _closed.Token);
            if (_closed.IsCancellationRequested || !ReferenceEquals(_data, data)) return;
            BuildProfile? current = null;
            try { current = ReadProfile(); } catch { }
            data.ApplyLocalizedNames(names);
            _initializing = true;
            PrimaryBox.ItemsSource = data.Choices("runePathData"); SecondaryBox.ItemsSource = data.Choices("runePathData");
            F0.ItemsSource = data.Shards(0); F1.ItemsSource = data.Shards(1); F2.ItemsSource = data.Shards(2);
            _initializing = false;
            if (current != null) LoadProfile(current); else NewProfile();
            UpdateLoadoutPreview();
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception) { /* If Spanish names are unavailable, keep the existing names. */ }
        finally { _initializing = false; }
    }
    private BuildProfile ReadProfile()
    {
        if (_data == null || ProfileChampionBox.SelectedItem is not Champion) throw new InvalidOperationException("Selecciona el campeón de la configuración.");
        var profile = new BuildProfile
        {
            Id = _editing?.Id ?? Guid.NewGuid(), Name = ProfileName.Text.Trim(), ChampionId = ((Champion)ProfileChampionBox.SelectedItem).Id, Role = ProfileRoleBox.SelectedItem is Role profileRole ? profileRole : SelectedRole,
            PrimaryStyleId = (PrimaryBox.SelectedItem as Choice)?.Id ?? 0, SubStyleId = (SecondaryBox.SelectedItem as Choice)?.Id ?? 0,
            Perks = new[] { P0, P1, P2, P3, S0, S1, F0, F1, F2 }.Select(b => (b.SelectedItem as Choice)?.Id ?? 0).ToArray(), Source = "Personalizada", Patch = _data.Patch,
            AssociatedMaps = _editing?.AssociatedMaps.ToArray() ?? [11], IsAram = _editing?.IsAram ?? false,
            Spell1Id = (Spell1Box.SelectedItem as Choice)?.Id ?? 0, Spell2Id = (Spell2Box.SelectedItem as Choice)?.Id ?? 0,
            EarlySkills = _editing?.ChampionId == ((Champion)ProfileChampionBox.SelectedItem).Id && _editing.Role == (ProfileRoleBox.SelectedItem is Role metadataRole ? metadataRole : SelectedRole) ? _editing.EarlySkills.ToArray() : [],
            SkillPriority = _editing?.ChampionId == ((Champion)ProfileChampionBox.SelectedItem).Id && _editing.Role == (ProfileRoleBox.SelectedItem is Role priorityRole ? priorityRole : SelectedRole) ? _editing.SkillPriority.ToArray() : [],
            ItemEvidence = _editing?.ChampionId == ((Champion)ProfileChampionBox.SelectedItem).Id && _editing.Role == (ProfileRoleBox.SelectedItem is Role evidenceRole ? evidenceRole : SelectedRole) ? _editing.ItemEvidence.ToList() : []
        };
        if (profile.Perks.All(id => id == 0)) profile.Perks = [];
        profile.Blocks = ItemBlockText.Parse(ItemsText.Text, _editing?.Blocks);
        _data.Validate(profile, requireRunes: false);
        if (_editing != null && profile.ChampionId == _editing.ChampionId && profile.Role == _editing.Role && profile.PrimaryStyleId == _editing.PrimaryStyleId && profile.SubStyleId == _editing.SubStyleId && profile.Spell1Id == _editing.Spell1Id && profile.Spell2Id == _editing.Spell2Id && profile.Perks.SequenceEqual(_editing.Perks) && Json.Write(profile.Blocks) == Json.Write(_editing.Blocks)) { profile.Source = _editing.Source; profile.Patch = _editing.Patch; }
        return profile;
    }
    private HashSet<Guid> RetainedProfileIds(BuildProfile profile) => _profiles.Where(p => p.ChampionId == profile.ChampionId && p.Role == profile.Role).Select(p => p.Id).Append(profile.Id).ToHashSet();
    private async Task SaveAsync()
    {
        if (!_profilesValid) throw new InvalidOperationException("Corrige o recupera profiles.json antes de guardar. El archivo original se ha conservado.");
        var p = ReadProfile(); var next = _profiles.Where(x => x.Id != p.Id).Append(p).ToList();
        await Store.SaveAsync(next);
        _updatingProfiles = true;
        try
        {
            var index = -1;
            for (var i = 0; i < _profiles.Count; i++) if (_profiles[i].Id == p.Id) { index = i; break; }
            if (index >= 0) _profiles[index] = p; else _profiles.Add(p);
            ProfilesBox.SelectedItem = p;
            _editing = p;
        }
        finally { _updatingProfiles = false; }
        QueueCloudSync();
        UpdateDraftBuildOptionsForSelection();
    }
    private void ProfileEditorChanged(object sender, SelectionChangedEventArgs e) => ScheduleProfileAutosave();
    private void ProfileNameChanged(object sender, TextChangedEventArgs e) => ScheduleProfileAutosave();
    private void ItemsTextChanged(object sender, TextChangedEventArgs e) => ScheduleProfileAutosave();
    private void ProfileRuneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, S0) || ReferenceEquals(sender, S1)) RenderSecondaryRuneGrid();
        ScheduleProfileAutosave();
    }

    private void ScheduleProfileAutosave()
    {
        if (_initializing || _loadingProfile || _data == null || ProfileChampionBox?.SelectedItem is not Champion || _closed.IsCancellationRequested) return;
        _profileEditDelay?.Cancel(); _profileEditDelay?.Dispose();
        var delay = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _profileEditDelay = delay;
        ProfileSaveStatus.Text = "Guardando cambios…";
        _profileAutosaveTask = AutosaveProfileAsync(delay);
    }

    private async Task AutosaveProfileAsync(CancellationTokenSource delay)
    {
        try
        {
            await Task.Delay(900, delay.Token);
            await SaveAsync();
            ProfileSaveStatus.Text = _firebase.IsSignedIn
                ? "Perfil guardado · sincronizando con Firebase…"
                : "Perfil guardado automáticamente en este equipo.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ProfileSaveStatus.Text = "No se pudo guardar automáticamente · " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_profileEditDelay, delay))
            {
                _profileEditDelay = null; delay.Dispose(); _profileAutosaveTask = null;
            }
        }
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e) { if (!_updatingProfiles && ProfilesBox.SelectedItem is BuildProfile p && !_initializing) LoadProfile(p); }
    private void NewClick(object sender, RoutedEventArgs e) => NewProfile();
    private async void SaveClick(object sender, RoutedEventArgs e) => await RunAsync(SaveAsync, "Perfil guardado");
    private async void ConnectClick(object sender, RoutedEventArgs e) { _lastConnect = DateTime.MinValue; await PollAsync(); if (!_lcu.Connected) Show("League no detectado", "Abre League o usa Ruta de League para indicar su archivo lockfile.", InfoBarSeverity.Warning); }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await LoadDataAsync();
    private async void HoverClick(object sender, RoutedEventArgs e)
    {
        _autoDraft.PauseCurrentTurn(_draft);
        await RunAsync(() => _lcu.PickAsync(SelectedChampion!.Id, false, _closed.Token), "Campeón preseleccionado");
    }
    private async void LockClick(object sender, RoutedEventArgs e)
    {
        var champion = SelectedChampion; if (champion == null) return;
        _autoDraft.PauseCurrentTurn(_draft);
        await RunAsync(() => _lcu.PickAsync(champion.Id, true, _closed.Token), "Campeón confirmado");
    }
    private async void LolalyticsClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_data == null || ProfileChampionBox.SelectedItem is not Champion champion) throw new InvalidOperationException("Selecciona el campeón de la configuración.");
        var role = ProfileRoleBox.SelectedItem is Role pr ? pr : SelectedRole;
        var provider = new LolalyticsProvider(_http);
        var profile = _aramRoleMode
            ? await provider.GetAramAsync(champion, _data.Patch, _closed.Token)
            : await provider.GetAsync(champion, role, _data.Patch, _closed.Token);
        profile.Role = role;
        _data.Validate(profile);
        ProfilesBox.SelectedItem = null;
        LoadProfile(profile);
        Show("Build de Lolalytics cargada", profile.Spell1Id > 0
            ? "Se cargaron runas, objetos y hechizos de invocador."
            : "Lolalytics no entregó una pareja de hechizos compatible para esta build.", InfoBarSeverity.Success);
    }, "Build cargada", showSuccess: false);

    private void OpenLolalyticsClick(object sender, RoutedEventArgs e)
    {
        if (ProfileChampionBox.SelectedItem is not Champion champion || _data == null) return;
        var role = ProfileRoleBox.SelectedItem is Role profileRole ? profileRole : SelectedRole;
        var url = _aramRoleMode ? LolalyticsProvider.AramPageUrl(champion, _data.Patch) : LolalyticsProvider.PageUrl(champion, role, _data.Patch);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    private void LoadSpellChoices(BuildProfile? profile = null)
    {
        var choices = new List<Choice> { new(0, "Sin configurar") };
        if (_data != null) choices.AddRange(_data.SummonerSpells(profile?.IsAram == true));
        Spell1Box.ItemsSource = Spell2Box.ItemsSource = choices;
        Select(Spell1Box, profile?.Spell1Id ?? 0);
        Select(Spell2Box, profile?.Spell2Id ?? 0);
    }

    private async void ImportSummonerSpellsClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var profile = ReadProfile();
        await _lcu.ImportSummonerSpellsAsync(profile, _data!, _closed.Token, flashKey: _favorites.FlashKey);
    }, "Hechizos de invocador aplicados a League");

    private async void ImportRunesClick(object sender, RoutedEventArgs e) => await RunAsync(async () => await _lcu.ImportRunesAsync(ReadProfile(), _data!, _closed.Token), "Runas aplicadas a League");
    private async void ImportItemsClick(object sender, RoutedEventArgs e) => await RunAsync(async () => { await SaveAsync(); await _lcu.ImportItemsAsync(_editing!, _data!, _closed.Token, retainedProfileIds: RetainedProfileIds(_editing!)); }, "Conjunto enviado y verificado en la cuenta activa de League");
    private async void LockfileClick(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = _lcu.LockfilePath ?? "", MinWidth = 420 };
        if (ShowPrompt("Ruta al lockfile de League", box, "Guardar y conectar", "Cancelar")) await RunAsync(async () => { _lcu.LockfilePath = box.Text.Trim(); await File.WriteAllTextAsync(Path.Combine(_directory, "lockfile-path.txt"), _lcu.LockfilePath); _lcu.Disconnect(); _lastConnect = DateTime.MinValue; await PollAsync(allowWhileBusy: true); if (!_lcu.Connected) throw new IOException("No se pudo conectar. Comprueba la ruta y que League esté abierto."); }, "Conectado a League");
    }
    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (_busy || ProfilesBox.SelectedItem is not BuildProfile current) return;
        var message = _firebase.IsSignedIn
            ? "Se eliminará el perfil de este equipo y de Firebase si está guardado en tu cuenta."
            : "Se eliminará el perfil guardado en este equipo.";
        if (ShowPrompt("Eliminar " + current.Name, message, "Eliminar", "Cancelar"))
            await RunAsync(() => DeleteProfileAsync(current), "Perfil eliminado");
    }

    private async Task DeleteProfileAsync(BuildProfile current)
    {
        if (!_profilesValid) throw new IOException("No se pueden sobrescribir perfiles dañados.");
        await CancelProfileAutosaveAsync();
        await WaitForQueuedCloudSyncAsync();

        var cloud = _firebase.IsSignedIn ? await DeleteProfileFromCloudFirstAsync(current.Id) : null;
        var deletedIds = cloud?.DeletedIds ?? new HashSet<Guid>(_deletedProfileIds) { current.Id };
        var profiles = cloud?.Profiles ?? _profiles.Where(profile => profile.Id != current.Id).ToList();
        await SaveDeletedProfileIdsAsync(deletedIds);
        _deletedProfileIds = deletedIds;
        await Store.SaveAsync(profiles);
        if (cloud is { } synced)
        {
            if (Json.Write(_favorites) != Json.Write(synced.Favorites))
                await new FavoriteSettingsStore(_profileDirectory).SaveAsync(synced.Favorites);
            _favorites = synced.Favorites;
            _cloudUpdateTime = synced.UpdateTime;
            _cloudLocalDirty = false;
            CloudSyncStatus.Text = "Perfil eliminado de Firebase y de este equipo.";
        }

        SuppressAutomaticLoadoutForCurrentGame(current);
        _editing = null;
        ReplaceProfiles(profiles);
        UpdateDraftBuildOptionsForSelection();
        _loadout.Reset();
        NewProfile();
        SetLoadoutStatus("Perfil eliminado. Actualizando la configuración para el campeón confirmado…");
    }
    private async void JsonClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        string text = "";
        try { text = Json.Write(ReadProfile()); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { }
        var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 440, Height = 420 };
        if (ShowPrompt("Copia el JSON o pega builds de League", box, "Importar como nuevos", "Cerrar")) await ImportJsonAsync(box.Text);
    }, "JSON procesado · los perfiles importados aparecen en Mis perfiles");
    private async Task ImportJsonAsync(string text)
    {
        if (_data == null) throw new InvalidOperationException("Espera a que se cargue el catálogo.");
        if (!_profilesValid) throw new InvalidOperationException("Recupera profiles.json antes de importar; se conserva el archivo original.");
        var imported = BuildImporter.Parse(text, _data, ProfileRoleBox.SelectedItem is Role pr ? pr : SelectedRole, (ProfileChampionBox.SelectedItem as Champion)?.Id);
        var editorChampionId = (ProfileChampionBox.SelectedItem as Champion)?.Id;
        var editorRole = ProfileRoleBox.SelectedItem is Role selectedRole ? selectedRole : SelectedRole;
        var editorPerks = new[] { P0, P1, P2, P3, S0, S1, F0, F1, F2 }.Select(b => (b.SelectedItem as Choice)?.Id ?? 0).ToArray();
        var editorRunesComplete = editorPerks.All(id => id > 0) && PrimaryBox.SelectedItem is Choice && SecondaryBox.SelectedItem is Choice;
        foreach (var itemProfile in imported.Where(p => p.Perks.Length == 0))
        {
            BuildProfile? source = editorRunesComplete && editorChampionId == itemProfile.ChampionId && editorRole == itemProfile.Role
                ? new BuildProfile { ChampionId = itemProfile.ChampionId, Role = itemProfile.Role, PrimaryStyleId = ((Choice)PrimaryBox.SelectedItem!).Id, SubStyleId = ((Choice)SecondaryBox.SelectedItem!).Id, Perks = editorPerks }
                : _profiles.Where(p => p.ChampionId == itemProfile.ChampionId && p.Role == itemProfile.Role && p.Perks.Length == 9).OrderByDescending(p => p.UpdatedAt).FirstOrDefault();
            if (source == null) continue;
            itemProfile.PrimaryStyleId = source.PrimaryStyleId; itemProfile.SubStyleId = source.SubStyleId; itemProfile.Perks = source.Perks.ToArray();
            itemProfile.Source += " · runas conservadas";
        }
        var next = _profiles.Concat(imported).ToList();
        await Store.SaveAsync(next);
        ReplaceProfiles(next);
        ProfilesBox.SelectedItem = imported[0];
        QueueCloudSync();
        UpdateDraftBuildOptionsForSelection();
    }
    private async void ImportJsonFileClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Archivos JSON (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return;
        if (new FileInfo(picker.FileName).Length > 4 * 1024 * 1024) throw new InvalidDataException("El archivo supera el límite de 4 MB.");
        await ImportJsonAsync(await File.ReadAllTextAsync(picker.FileName));
    }, "Importación finalizada · revisa Mis perfiles");
    private void UpdateFavoritesText()
    {
        if (FavoritesText == null) return;
        var ids = _favorites.ForRole(SelectedRole);
        FavoritesText.Text = $"{SelectedRole}: " + (ids.Count == 0 ? "sin favoritos." : string.Join(", ", ids.Select(id => _data?.NameOf(id) ?? id.ToString()).OrderBy(n => n)));
        UpdateAutomationSummary(SelectedRole);
    }
    private void UpdateAutomationSummary(Role role)
    {
        if (AutomationSummary == null) return;
        if (!_favoritesValid) AutomationSummary.Text = "Auto desactivado: revisa la configuración de favoritos.";
        else if (!_favorites.AutoHoverEnabled) AutomationSummary.Text = "Selección automática desactivada.";
        else if (_draft?.CurrentPick != null && _autoDraft.Status.Contains("pausada")) AutomationSummary.Text = "Auto pausado para este turno. Selecciona manualmente.";
        else if (_autoDraft.Status.StartsWith("Confirmado automáticamente", StringComparison.Ordinal)) AutomationSummary.Text = _autoDraft.Status;
        else AutomationSummary.Text = $"Auto · {role} · {(_favorites.ForRole(role).Count == 0 ? "mejor opción del rol" : _favorites.ForRole(role).Count + " favoritos")} · confirmación {(_favorites.AutoLockEnabled ? $"a {_favorites.AutoLockSeconds} s" : "manual")}.";
    }
    private void UpdateAutomationControls()
    {
        _updatingAutomation = true;
        AutoHoverBox.IsChecked = _favorites.AutoHoverEnabled;
        AutoLockBox.IsChecked = _favorites.AutoLockEnabled;
        AutoLockSecondsBox.Text = Math.Clamp(_favorites.AutoLockSeconds, 1, 30).ToString(CultureInfo.CurrentCulture);
        AutoLoadoutBox.IsChecked = _favorites.AutoLoadoutEnabled;
        FlashKeyBox.SelectedValue = _favorites.FlashKey;
        FlashKeyBox.IsEnabled = _favoritesValid;
        AutoBanBox.IsChecked = _favorites.AutoBanEnabled;
        AutoHoverBox.IsEnabled = _favoritesValid;
        AutoLockBox.IsEnabled = _favoritesValid && _favorites.AutoHoverEnabled;
        AutoLockSecondsControl.IsEnabled = _favoritesValid && _favorites.AutoHoverEnabled;
        AutoLoadoutBox.IsEnabled = _favoritesValid;
        AutoBanBox.IsEnabled = _favoritesValid;
        _updatingAutomation = false;
        UpdateFavoritesText();
        RefreshBanPriorities();
        UpdateLoadoutPreview();
    }
    private async Task SaveFavoritesAsync()
    {
        if (!_favoritesValid) throw new InvalidOperationException("Recupera favorites.json antes de modificarlo; se conserva el original.");
        await _favoritesSave.WaitAsync();
        try
        {
            await new FavoriteSettingsStore(_profileDirectory).SaveAsync(_favorites);
            QueueCloudSync();
        }
        catch
        {
            _favoritesValid = false; _favorites.AutoHoverEnabled = false;
            UpdateAutomationControls();
            throw;
        }
        finally { _favoritesSave.Release(); }
    }
    private async void AutomationChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || _updatingAutomation) return;
        _favorites.AutoHoverEnabled = AutoHoverBox.IsChecked == true;
        _favorites.AutoLockEnabled = AutoLockBox.IsChecked == true;
        _favorites.AutoLoadoutEnabled = AutoLoadoutBox.IsChecked == true;
        _favorites.AutoBanEnabled = AutoBanBox.IsChecked == true;
        if (!_favorites.AutoLoadoutEnabled) _loadout.Reset();
        AutoLockBox.IsEnabled = _favoritesValid && _favorites.AutoHoverEnabled;
        AutoLockSecondsControl.IsEnabled = AutoLockBox.IsEnabled;
        UpdateAutomationSummary(SelectedRole);
        try { await SaveFavoritesAsync(); }
        catch (Exception ex) { Show("No se pudo guardar la automatización", ex.Message, InfoBarSeverity.Error); }
    }
    private async void FlashKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _updatingAutomation || !_favoritesValid ||
            FlashKeyBox.SelectedValue is not FlashKey flashKey || _favorites.FlashKey == flashKey) return;

        _favorites.FlashKey = flashKey;
        UpdateLoadoutPreview();
        try { await SaveFavoritesAsync(); }
        catch (Exception ex) { Show("No se pudo guardar la tecla de Destello", ex.Message, InfoBarSeverity.Error); }
    }

    private async void ImportLeagueFavoritesClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (!_favoritesValid) throw new InvalidOperationException("Recupera favorites.json antes de modificar tus favoritos.");
            if (!_lcu.Connected) throw new InvalidOperationException("Abre League y espera a que DraftOptimizer se conecte.");
            var imported = await _lcu.ReadFavoritesAsync(_closed.Token);
            var added = 0;
            foreach (var (role, ids) in imported)
            {
                var favorites = _favorites.ForRole(role);
                var count = favorites.Count;
                favorites.UnionWith(ids);
                _favorites.Favorites[role] = favorites;
                added += favorites.Count - count;
            }
            if (added > 0) await SaveFavoritesAsync();
            _lastSuggestions = null;
            RefreshSuggestions();
            UpdateFavoritesText();
            Show("Favoritos de League", added > 0 ? $"Favoritos añadidos por posición: {added}." : "No se encontraron favoritos nuevos.", InfoBarSeverity.Success);
        }, "Favoritos importados", showSuccess: false);
    }

    private async void FavoriteStarClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: ChampionListItem row }) return;
        await RunAsync(async () =>
        {
            if (!_favoritesValid) throw new InvalidOperationException("Recupera favorites.json antes de modificar tus favoritos.");
            var selected = _favorites.ForRole(row.Role).ToHashSet();
            if (!selected.Add(row.Champion.Id)) selected.Remove(row.Champion.Id);
            _favorites.Favorites[row.Role] = selected;
            await SaveFavoritesAsync();
        }, "Favoritos guardados", showSuccess: false);
        _lastSuggestions = null;
        RefreshSuggestions(); UpdateFavoritesText();
    }
    private void BanRoleChanged(object sender, SelectionChangedEventArgs e) => RefreshBanPriorities();
    private void RefreshBanPriorities()
    {
        if (BanPriorityList == null || BanChampionBox == null) return;
        var selectedId = (BanPriorityList.SelectedItem as Champion)?.Id;
        var champions = _data == null ? new List<Champion>() : _favorites.BanPrioritiesFor(SelectedBanRole)
            .Select(id => _data.Champions.FirstOrDefault(champion => champion.Id == id)).OfType<Champion>().ToList();
        BanPriorityList.ItemsSource = champions;
        if (selectedId.HasValue) BanPriorityList.SelectedItem = champions.FirstOrDefault(champion => champion?.Id == selectedId.Value);
        if (BanChampionBox.SelectedItem is not Champion || _data?.Champions.Contains((Champion)BanChampionBox.SelectedItem) != true)
            BanChampionBox.SelectedItem = _data?.Champions.FirstOrDefault();
        BanChampionBox.IsEnabled = _favoritesValid && _data != null;
    }
    private async Task EditBanPrioritiesAsync(Action<List<int>> edit)
    {
        var role = SelectedBanRole;
        await RunAsync(async () =>
        {
            if (!_favoritesValid) throw new InvalidOperationException("Recupera favorites.json antes de modificar la prioridad de baneos.");
            var priorities = _favorites.BanPrioritiesFor(role).ToList();
            var previous = priorities.ToArray();
            edit(priorities);
            if (priorities.SequenceEqual(previous)) return;
            _favorites.BanPriorities[role] = priorities;
            await SaveFavoritesAsync();
        }, "Prioridad de baneos guardada", showSuccess: false);
        RefreshBanPriorities();
    }
    private async void AddBanPriorityClick(object sender, RoutedEventArgs e)
    {
        if (BanChampionBox.SelectedItem is not Champion champion) return;
        await EditBanPrioritiesAsync(priorities => { if (!priorities.Contains(champion.Id)) priorities.Add(champion.Id); });
    }
    private async void MoveBanPriorityUpClick(object sender, RoutedEventArgs e)
    {
        var index = BanPriorityList.SelectedIndex;
        await EditBanPrioritiesAsync(priorities => { if (index > 0 && index < priorities.Count) (priorities[index - 1], priorities[index]) = (priorities[index], priorities[index - 1]); });
        if (index > 0) BanPriorityList.SelectedIndex = index - 1;
    }
    private async void MoveBanPriorityDownClick(object sender, RoutedEventArgs e)
    {
        var index = BanPriorityList.SelectedIndex;
        await EditBanPrioritiesAsync(priorities => { if (index >= 0 && index + 1 < priorities.Count) (priorities[index + 1], priorities[index]) = (priorities[index], priorities[index + 1]); });
        if (index >= 0 && index + 1 < _favorites.BanPrioritiesFor(SelectedBanRole).Count) BanPriorityList.SelectedIndex = index + 1;
    }
    private async void RemoveBanPriorityClick(object sender, RoutedEventArgs e)
    {
        var index = BanPriorityList.SelectedIndex;
        await EditBanPrioritiesAsync(priorities => { if (index >= 0 && index < priorities.Count) priorities.RemoveAt(index); });
        if (index >= 0 && BanPriorityList.Items.Count > 0) BanPriorityList.SelectedIndex = Math.Min(index, BanPriorityList.Items.Count - 1);
    }
    private async Task AdvanceBanAsync()
    {
        var draft = _draft;
        var action = draft?.CurrentBan;
        if (draft == null || action == null) { _autoBanAttempted = null; return; }
        var turn = (draft.GameId, action.Id);
        if (_autoBanAttempted == turn) return;
        if (action.ChampionId != 0)
        {
            BanStatusText.Text = "Ya hay un campeón elegido para este ban; no se cambiará la selección actual.";
            return;
        }
        var role = SelectedRole;
        var mine = draft.MyTeam.FirstOrDefault(player => player.CellId == draft.LocalPlayerCellId);
        if (mine != null && Roles.TryParse(mine.AssignedPosition, out var assignedRole)) role = assignedRole;
        if (!_favoritesValid) { BanStatusText.Text = "Baneo automático detenido: revisa favorites.json."; return; }
        if (!_favorites.AutoBanEnabled) { BanStatusText.Text = $"Turno de ban · {role}. Baneo automático desactivado."; return; }
        var priorities = _favorites.BanPrioritiesFor(role);
        if (priorities.Count == 0) { BanStatusText.Text = $"No hay prioridades de baneos configuradas para {role}."; return; }
        var remaining = draft.Timer?.RemainingMilliseconds(DateTimeOffset.UtcNow);
        if (remaining is null)
        {
            BanStatusText.Text = $"Turno de ban · {role}. Esperando un temporizador válido de League.";
            return;
        }
        if (remaining <= 0)
        {
            BanStatusText.Text = $"Se agotó el tiempo del baneo · {role}.";
            return;
        }
        try
        {
            var championId = await _lcu.AutomaticBanAsync(draft.GameId, action.Id, role, priorities.ToArray(),
                id => !_closed.IsCancellationRequested && !_busy && _favoritesValid && _favorites.AutoBanEnabled &&
                    _favorites.BanPrioritiesFor(role).Contains(id), _closed.Token);
            if (championId is not { } banned)
            {
                BanStatusText.Text = $"Baneo pendiente · {role}. {_lcu.AutomaticBanStatus}";
                return;
            }
            _autoBanAttempted = turn;
            BanStatusText.Text = $"Baneado automáticamente: {_data?.NameOf(banned) ?? banned.ToString()} · prioridad {role}.";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { BanStatusText.Text = "No se pudo enviar el baneo automático. " + ex.Message; }
    }
    private async Task AdvanceAutomationAsync()
    {
        if (_data == null || !_favoritesValid || _busy || _loadingData) return;
        var role = SelectedRole;
        var mine = _draft?.MyTeam.FirstOrDefault(p => p.CellId == _draft.LocalPlayerCellId);
        bool assigned = mine != null && Roles.TryParse(mine.AssignedPosition, out _);
        if (assigned) Roles.TryParse(mine!.AssignedPosition, out role);
        var data = _data; var context = _context;
        try
        {
            IReadOnlyList<Suggestion> recommendations = Array.Empty<Suggestion>();
            if (_draft?.CurrentPick != null && _favorites.AutoHoverEnabled && (context != null || !_draft.HasCompositionPicks))
            {
                var snapshot = CaptureDraftSnapshot(out _);
                var work = await DraftComputation.CalculateAsync(data, context, role, snapshot, _pickable?.ToHashSet(), false, false, _closed.Token);
                if (!ReferenceEquals(data, _data) || !ReferenceEquals(context, _context) || _loadingData || _busy || _closed.IsCancellationRequested || (!assigned && role != SelectedRole)) return;
                recommendations = work.Suggestions;
            }
            await _autoDraft.TickAsync(data, context, _draft, _pickable, _favorites, role, DateTimeOffset.UtcNow,
                request => _lcu.AutomaticPickAsync(request, () => !_closed.IsCancellationRequested && !_busy && !_loadingData && _favoritesValid &&
                    _favorites.AutoHoverEnabled && (_favorites.ForRole(request.Role).Count == 0 || _favorites.ForRole(request.Role).Contains(request.ChampionId)) && (!request.Complete || _favorites.AutoLockEnabled), _closed.Token), recommendations);
            if (!_closed.IsCancellationRequested) { AutomationText.Text = _autoDraft.Status; UpdateAutomationSummary(role); }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closed.IsCancellationRequested) { AutomationText.Text = "Automatización pausada para este turno. " + ex.Message; UpdateAutomationSummary(role); } }
    }
    private void AdvanceLoadout(ClientState state)
    {
        if (_data == null || _busy) return;
        try
        {
            BuildProfile? profile = null;
            int id = state.Draft?.LocalLockedChampion ?? 0;
            var mine = state.Draft?.MyTeam.FirstOrDefault(p => p.CellId == state.Draft.LocalPlayerCellId);
            var role = mine != null && Roles.TryParse(mine.AssignedPosition, out var assigned) ? assigned : SelectedRole;
            if (_loadoutSuppressedGameId != (state.Draft?.GameId ?? 0))
            {
                _loadoutSuppressedGameId = state.Draft?.GameId ?? 0;
                _loadoutSuppressedForGame.Clear();
            }
            if (id > 0 && _loadoutSuppressedForGame.Contains((id, role)))
            {
                _loadout.Reset();
                SetLoadoutStatus("Aplicación automática pausada para este campeón porque eliminaste su perfil en esta partida.");
                return;
            }
            if (id > 0)
                profile = SelectedDraftBuildProfile(id, role);
            _loadout.Observe(state, _data, role, profile, _favoritesValid && _profilesValid && _favorites.AutoLoadoutEnabled,
                async (champion, position, patch, isAram, ct) =>
                {
                    var provider = new LolalyticsProvider(_http);
                    if (!isAram) return (await provider.GetOptionsAsync(champion, position, patch, ct)).Recommended.Profile;
                    var aram = await provider.GetAramAsync(champion, patch, ct);
                    aram.Role = position;
                    return aram;
                },
                async (prepared, target, ct) =>
                {
                    await _lcu.ImportRunesAsync(prepared, _data!, ct, target);
                    ct.ThrowIfCancellationRequested();
                    await _lcu.ImportItemsAsync(prepared, _data!, ct, target, RetainedProfileIds(prepared));
                    if (prepared.Spell1Id != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        await _lcu.ImportSummonerSpellsAsync(prepared, _data!, ct, target, flashKey: _favorites.FlashKey);
                    }
                });
            SetLoadoutStatus(_loadout.Status);
        }
        catch (Exception ex) { _loadout.Reset(); SetLoadoutStatus("Revisa el perfil antes de aplicar automáticamente: " + ex.Message); }
    }

    private void SuppressAutomaticLoadoutForCurrentGame(BuildProfile profile)
    {
        var draft = _draft;
        if (draft == null || draft.GameId <= 0) return;
        var player = draft.MyTeam.FirstOrDefault(p => p.CellId == draft.LocalPlayerCellId);
        var role = player != null && Roles.TryParse(player.AssignedPosition, out var assigned) ? assigned : SelectedRole;
        if (role != profile.Role) return;
        if (_loadoutSuppressedGameId != draft.GameId)
        {
            _loadoutSuppressedGameId = draft.GameId;
            _loadoutSuppressedForGame.Clear();
        }
        _loadoutSuppressedForGame.Add((profile.ChampionId, profile.Role));
    }
    private void RetryLoadoutClick(object sender, RoutedEventArgs e) => _loadout.Reset();
    private void ViewPreparedClick(object sender, RoutedEventArgs e)
    {
        if (_loadout.Prepared is { } profile) LoadProfile(Json.Read<BuildProfile>(Json.Write(profile)));
    }
    private void AboutClick(object sender, RoutedEventArgs e) =>
        ShowPrompt("DraftOptimizer · Acerca de",
            "Inspirado en DraftGap de Vigo Vlugt (MIT). Estadísticas de DraftGap / Lolalytics. El índice de selección usa un cálculo propio; no replica el modelo completo de DraftGap.\n\nDraftOptimizer is not endorsed by Riot Games and does not reflect the views or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot Games and all associated properties are trademarks or registered trademarks of Riot Games, Inc.", null, "Cerrar");
}




