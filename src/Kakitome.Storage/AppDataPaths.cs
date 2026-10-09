namespace Kakitome.Storage;

/// <summary>
/// Fixed, resettable per-user locations (docs/06). Under MSIX these may be redirected to package-private
/// storage, which is acceptable for app state, logs and cache. The Library is deliberately not here.
/// </summary>
public sealed class AppDataPaths(string root, string? modelsDirectory = null)
{
    public static AppDataPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kakitome"));

    public string Root { get; } = Path.GetFullPath(root);

    public string DataDirectory => Path.Combine(Root, "Data");

    public string DatabaseFile => Path.Combine(DataDirectory, "kakitome.db");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string LogsDirectory => Path.Combine(Root, "Logs");

    public string CacheDirectory => Path.Combine(Root, "Cache");

    /// <summary>Downloaded models (separate from the Library; removable via Model Manager).</summary>
    public string ModelsDirectory { get; } = modelsDirectory is null ? Path.Combine(Path.GetFullPath(root), "Models") : Path.GetFullPath(modelsDirectory);
}
