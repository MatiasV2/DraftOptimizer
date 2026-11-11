namespace DraftOptimizer.Core;

public sealed class ProfileStore(string directory)
{
    public string PathName => Path.Combine(directory, "profiles.json");
    public async Task<List<BuildProfile>> LoadAsync()
    {
        if (!File.Exists(PathName)) return [];
        // Never overwrite a corrupt profile collection with an empty one.
        return Json.Read<List<BuildProfile>>(await File.ReadAllTextAsync(PathName));
    }
    public async Task SaveAsync(IEnumerable<BuildProfile> profiles)
    {
        Directory.CreateDirectory(directory);
        var temp = PathName + ".tmp";
        await File.WriteAllTextAsync(temp, Json.Write(profiles));
        if (File.Exists(PathName)) File.Copy(PathName, PathName + ".bak", true);
        File.Move(temp, PathName, true);
    }
}
