namespace Osoko.Core;

/// <summary>
/// Well-known on-disk locations. Everything Osoko writes lives in the per-user
/// profile; the application never requires administrator rights.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "Osoko";

    public const string ScratchFolderName = "Scratch";

    /// <summary><c>%LOCALAPPDATA%\Osoko</c></summary>
    public static string LocalAppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    /// <summary><c>%LOCALAPPDATA%\Osoko\settings.json</c></summary>
    public static string SettingsFilePath => Path.Combine(LocalAppDataDirectory, "settings.json");

    /// <summary><c>%LOCALAPPDATA%\Osoko\Scratch</c> — temporary screenshot cache.</summary>
    public static string ScratchDirectory => Path.Combine(LocalAppDataDirectory, ScratchFolderName);

    /// <summary><c>%LOCALAPPDATA%\Osoko\logs</c></summary>
    public static string LogDirectory => Path.Combine(LocalAppDataDirectory, "logs");

    /// <summary><c>%LOCALAPPDATA%\Osoko\logs\osoko.log</c></summary>
    public static string LogFilePath => Path.Combine(LogDirectory, "osoko.log");

    /// <summary><c>%USERPROFILE%\Pictures\Screenshots</c> — Windows' default Snipping Tool output folder.</summary>
    public static string PicturesScreenshotsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// <summary>The current user's desktop folder.</summary>
    public static string DesktopDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Creates the application data folders if they do not exist yet.</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(LocalAppDataDirectory);
        Directory.CreateDirectory(ScratchDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> points inside Osoko's scratch cache.
    /// Osoko only ever deletes files that live there.
    /// </summary>
    public static bool IsScratchFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var scratch = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ScratchDirectory));
            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidate, scratch, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
