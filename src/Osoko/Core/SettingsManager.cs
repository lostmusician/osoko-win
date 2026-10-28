using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Osoko.Core;

/// <summary>
/// Loads, saves and applies <see cref="OsokoSettings"/>. Also owns the per-user
/// "launch at startup" registry value under <c>HKCU\…\CurrentVersion\Run</c>.
/// </summary>
public sealed class SettingsManager
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "Osoko";
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new CardActionKindJsonConverter() },
    };

    private readonly Func<string?> _executablePathProvider;

    /// <summary>
    /// Creates a manager. <paramref name="settingsDirectory"/> defaults to
    /// <c>%LOCALAPPDATA%\Osoko</c> and exists mainly so tests can use a temp folder.
    /// </summary>
    public SettingsManager(string? settingsDirectory = null, Func<string?>? executablePathProvider = null)
    {
        SettingsDirectory = string.IsNullOrWhiteSpace(settingsDirectory)
            ? AppPaths.LocalAppDataDirectory
            : Path.GetFullPath(settingsDirectory);

        _executablePathProvider = executablePathProvider ?? (() => Environment.ProcessPath);
        Settings = Load();
    }

    /// <summary>Directory holding <c>settings.json</c>.</summary>
    public string SettingsDirectory { get; }

    /// <summary>Full path of the JSON settings file.</summary>
    public string SettingsFilePath => Path.Combine(SettingsDirectory, SettingsFileName);

    /// <summary>The live settings instance. Never null.</summary>
    public OsokoSettings Settings { get; private set; }

    /// <summary>Raised after settings have been loaded or saved.</summary>
    public event EventHandler<OsokoSettings>? SettingsChanged;

    /// <summary>
    /// Reads settings from disk. Missing, unreadable or corrupt files fall back to
    /// defaults instead of crashing the app on startup.
    /// </summary>
    public OsokoSettings Load()
    {
        var settings = OsokoSettings.CreateDefault();

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var loaded = JsonSerializer.Deserialize<OsokoSettings>(json, JsonOptions);
                    if (loaded is not null)
                    {
                        settings = loaded;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Debug.WriteLine($"[Osoko] Failed to load settings: {ex.Message}");
            QuarantineBrokenSettingsFile();
            settings = OsokoSettings.CreateDefault();
        }

        settings.Normalize();
        Settings = settings;
        SettingsChanged?.Invoke(this, settings);
        return settings;
    }

    /// <summary>Persists the current settings instance.</summary>
    public void Save() => Save(Settings);

    /// <summary>
    /// Persists <paramref name="settings"/>, makes it the current instance and raises
    /// <see cref="SettingsChanged"/>. Writes atomically via a temp file.
    /// </summary>
    public void Save(OsokoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Normalize();
        Directory.CreateDirectory(SettingsDirectory);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = SettingsFilePath + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
        catch (IOException)
        {
            // Antivirus / indexer can hold the temp file briefly — retry the direct write.
            TryDelete(tempPath);
            File.WriteAllText(SettingsFilePath, json);
        }
        finally
        {
            TryDelete(tempPath);
        }

        Settings = settings;
        SettingsChanged?.Invoke(this, settings);
    }

    /// <summary>Restores defaults and writes them to disk.</summary>
    public OsokoSettings Reset()
    {
        var defaults = OsokoSettings.CreateDefault();
        Save(defaults);
        return defaults;
    }

    // ---------------------------------------------------------------------
    // Launch at Windows startup
    // ---------------------------------------------------------------------

    /// <summary>Reads whether Osoko is registered to start with Windows.</summary>
    public bool IsLaunchAtStartupEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Debug.WriteLine($"[Osoko] Could not read startup entry: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Adds or removes the per-user startup entry. Returns true when the registry
    /// reflects the requested state afterwards.
    /// </summary>
    public bool SetLaunchAtStartup(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var command = BuildStartupCommand();
                if (command is null)
                {
                    return false;
                }

                key.SetValue(RunValueName, command, RegistryValueKind.String);
            }
            else if (key.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            Settings.LaunchAtStartup = enabled;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Debug.WriteLine($"[Osoko] Could not update startup entry: {ex.Message}");
            return false;
        }
    }

    /// <summary>Quoted command line stored in the Run key.</summary>
    internal string? BuildStartupCommand()
    {
        var executable = _executablePathProvider();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        // When launched via `dotnet Osoko.dll` (developer scenario) we must include the
        // managed entry point, otherwise Windows would start a bare dotnet host.
        var fileName = Path.GetFileNameWithoutExtension(executable);
        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Osoko.dll");
            if (File.Exists(assemblyPath))
            {
                return $"\"{executable}\" \"{assemblyPath}\"";
            }
        }

        return $"\"{executable}\"";
    }

    private void QuarantineBrokenSettingsFile()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var backup = SettingsFilePath + ".corrupt";
                File.Move(SettingsFilePath, backup, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Osoko] Could not quarantine corrupt settings: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the temp file is overwritten on the next save.
        }
    }
}
