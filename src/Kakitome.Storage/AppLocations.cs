using System.Runtime.InteropServices;
using Kakitome.Application.Maintenance;

namespace Kakitome.Storage;

/// <summary>Resolved data roots (docs/06). Backups default to <c>Documents\Kakitome\Backups</c>, next to the Library.</summary>
public sealed partial class AppLocations(AppDataPaths paths, string? backupsDirectory = null) : IAppLocations
{
    public static string DefaultBackupsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kakitome", "Backups");

    public string AppDataRoot => paths.Root;

    public string CacheDirectory => paths.CacheDirectory;

    public string ModelsDirectory => paths.ModelsDirectory;

    public string BackupsDirectory { get; } = Path.GetFullPath(backupsDirectory ?? DefaultBackupsDirectory);

    public bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        uint length = 0;
        const int AppModelErrorNoPackage = 15700;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
