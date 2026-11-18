namespace DraftOptimizer.Core;

public sealed class FavoriteSettings
{
    public bool AutoHoverEnabled { get; set; } = true;
    public bool AutoLockEnabled { get; set; }
    public int AutoLockSeconds { get; set; } = 1;
    public FlashKey FlashKey { get; set; }
    public bool AutoLoadoutEnabled { get; set; } = true;
    public bool AutoBanEnabled { get; set; }
    public Dictionary<Role, HashSet<int>> Favorites { get; set; } = [];
    public Dictionary<Role, List<int>> BanPriorities { get; set; } = [];
    public HashSet<int> ForRole(Role role) => Favorites.TryGetValue(role, out var champions) ? champions : [];
    public List<int> BanPrioritiesFor(Role role) => BanPriorities.TryGetValue(role, out var champions) ? champions : [];
}

public sealed class FavoriteSettingsStore(string directory)
{
    public string PathName => Path.Combine(directory, "favorites.json");
    public async Task<FavoriteSettings> LoadAsync()
    {
        if (!File.Exists(PathName)) return new();
        var settings = Json.Read<FavoriteSettings>(await File.ReadAllTextAsync(PathName));
        if (settings.Favorites == null || settings.Favorites.Any(p => !Enum.IsDefined(p.Key) || p.Value == null || p.Value.Any(id => id <= 0)) ||
            settings.BanPriorities == null || settings.BanPriorities.Any(p => !Enum.IsDefined(p.Key) || p.Value == null || p.Value.Any(id => id <= 0) || p.Value.Distinct().Count() != p.Value.Count))
            throw new InvalidDataException("La configuración de favoritos es inválida.");
        if (!Enum.IsDefined(settings.FlashKey)) settings.FlashKey = FlashKey.Profile;
        settings.AutoLockSeconds = Math.Clamp(settings.AutoLockSeconds, 1, 30);
        return settings;
    }
    public async Task SaveAsync(FavoriteSettings settings)
    {
        Directory.CreateDirectory(directory);
        var temporary = PathName + ".tmp";
        await File.WriteAllTextAsync(temporary, Json.Write(settings));
        if (File.Exists(PathName)) File.Copy(PathName, PathName + ".bak", true);
        File.Move(temporary, PathName, true);
    }
}
