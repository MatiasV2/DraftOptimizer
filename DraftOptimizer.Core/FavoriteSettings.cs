namespace DraftOptimizer.Core;

public sealed class FavoriteSettings
{
    public bool AutoHoverEnabled { get; set; } = true;
    public bool AutoLockEnabled { get; set; }
    public int AutoLockSeconds { get; set; } = 1;
    public bool AutoLoadoutEnabled { get; set; } = true;
    public Dictionary<Role, HashSet<int>> Favorites { get; set; } = [];
    public HashSet<int> ForRole(Role role) => Favorites.TryGetValue(role, out var champions) ? champions : [];
}

public sealed class FavoriteSettingsStore(string directory)
{
    public string PathName => Path.Combine(directory, "favorites.json");
    public async Task<FavoriteSettings> LoadAsync()
    {
        if (!File.Exists(PathName)) return new();
        var settings = Json.Read<FavoriteSettings>(await File.ReadAllTextAsync(PathName));
        if (settings.Favorites == null || settings.Favorites.Any(p => !Enum.IsDefined(p.Key) || p.Value == null || p.Value.Any(id => id <= 0)))
            throw new InvalidDataException("La configuración de favoritos es inválida.");
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
