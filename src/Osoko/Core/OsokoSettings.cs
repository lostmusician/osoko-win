using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Osoko.Core;

/// <summary>
/// User preferences, serialized to <c>%LOCALAPPDATA%\Osoko\settings.json</c>.
/// Instances are observable so open windows update live when a setting changes.
/// </summary>
public sealed class OsokoSettings : INotifyPropertyChanged
{
    public const int MinCollapseDelaySeconds = 3;
    public const int MaxCollapseDelaySeconds = 60;
    public const int MinStackItemCount = 1;
    public const int MaxStackItemCount = 15;

    private bool _autoCollapseEnabled = true;
    private int _autoCollapseDelaySeconds = 10;
    private int _maxStackItems = 5;
    private bool _consumeOnDrop = true;
    private bool _autoCopyToClipboard;
    private bool _playShutterSound = true;
    private bool _showTrayIcon = true;
    private bool _promptToRenameOnSave;
    private string _saveLocationPath = string.Empty;
    private bool _launchAtStartup;
    private bool _monitorClipboard = true;
    private bool _monitorScreenshotsFolder = true;
    private bool _cleanPicturesFolder = true;
    private bool _captureClipboardFromAnyApp;
    private bool _enableGlobalHotkey;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Collapse the shelf to an edge pill after a period of inactivity.</summary>
    public bool AutoCollapseEnabled
    {
        get => _autoCollapseEnabled;
        set => Set(ref _autoCollapseEnabled, value);
    }

    /// <summary>Idle seconds before the shelf slides away (3–60).</summary>
    public int AutoCollapseDelaySeconds
    {
        get => _autoCollapseDelaySeconds;
        set => Set(ref _autoCollapseDelaySeconds, Math.Clamp(value, MinCollapseDelaySeconds, MaxCollapseDelaySeconds));
    }

    /// <summary>Maximum number of thumbnails kept on the shelf (1–15). Pinned items are exempt.</summary>
    public int MaxStackItems
    {
        get => _maxStackItems;
        set => Set(ref _maxStackItems, Math.Clamp(value, MinStackItemCount, MaxStackItemCount));
    }

    /// <summary>Remove an item from the shelf once it has been successfully dragged out.</summary>
    public bool ConsumeOnDrop
    {
        get => _consumeOnDrop;
        set => Set(ref _consumeOnDrop, value);
    }

    /// <summary>Automatically place every new capture on the clipboard.</summary>
    public bool AutoCopyToClipboard
    {
        get => _autoCopyToClipboard;
        set => Set(ref _autoCopyToClipboard, value);
    }

    /// <summary>Play subtle audio cues on capture / copy / delete.</summary>
    public bool PlayShutterSound
    {
        get => _playShutterSound;
        set => Set(ref _playShutterSound, value);
    }

    /// <summary>Show the Osoko icon in the notification area.</summary>
    public bool ShowTrayIcon
    {
        get => _showTrayIcon;
        set => Set(ref _showTrayIcon, value);
    }

    /// <summary>Ask for a file name (and location) when saving a capture.</summary>
    public bool PromptToRenameOnSave
    {
        get => _promptToRenameOnSave;
        set => Set(ref _promptToRenameOnSave, value);
    }

    /// <summary>Folder used by the one-click "Save" action. Defaults to the Desktop.</summary>
    public string SaveLocationPath
    {
        get => _saveLocationPath;
        set => Set(ref _saveLocationPath, value ?? string.Empty);
    }

    /// <summary>Start Osoko with Windows (per-user Run key).</summary>
    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => Set(ref _launchAtStartup, value);
    }

    /// <summary>Ingest images that land on the clipboard (Snipping Tool &amp; friends).</summary>
    public bool MonitorClipboard
    {
        get => _monitorClipboard;
        set => Set(ref _monitorClipboard, value);
    }

    /// <summary>Watch <c>%USERPROFILE%\Pictures\Screenshots</c> for new captures.</summary>
    public bool MonitorScreenshotsFolder
    {
        get => _monitorScreenshotsFolder;
        set => Set(ref _monitorScreenshotsFolder, value);
    }

    /// <summary>Move intercepted files out of Pictures so the folder stays clean.</summary>
    public bool CleanPicturesFolder
    {
        get => _cleanPicturesFolder;
        set => Set(ref _cleanPicturesFolder, value);
    }

    /// <summary>
    /// Also ingest clipboard images copied from apps other than Snipping Tool
    /// (browsers, messaging clients, …). Off by default to avoid surprise captures.
    /// </summary>
    public bool CaptureClipboardFromAnyApp
    {
        get => _captureClipboardFromAnyApp;
        set => Set(ref _captureClipboardFromAnyApp, value);
    }

    /// <summary>Register the optional Alt+Shift+S hotkey that launches Snipping Tool.</summary>
    public bool EnableGlobalHotkey
    {
        get => _enableGlobalHotkey;
        set => Set(ref _enableGlobalHotkey, value);
    }

    /// <summary>Creates a settings instance with production defaults.</summary>
    public static OsokoSettings CreateDefault() => new()
    {
        SaveLocationPath = AppPaths.DesktopDirectory,
    };

    /// <summary>Clamps every value into its supported range. Safe to call repeatedly.</summary>
    public void Normalize()
    {
        AutoCollapseDelaySeconds = Math.Clamp(AutoCollapseDelaySeconds, MinCollapseDelaySeconds, MaxCollapseDelaySeconds);
        MaxStackItems = Math.Clamp(MaxStackItems, MinStackItemCount, MaxStackItemCount);

        if (string.IsNullOrWhiteSpace(SaveLocationPath))
        {
            SaveLocationPath = AppPaths.DesktopDirectory;
        }
    }

    /// <summary>Deep copy used by the settings dialog so edits can be cancelled.</summary>
    public OsokoSettings Clone()
    {
        var clone = (OsokoSettings)MemberwiseClone();
        clone.PropertyChanged = null;
        return clone;
    }

    /// <summary>Copies every value from <paramref name="other"/> into this instance.</summary>
    public void CopyFrom(OsokoSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        AutoCollapseEnabled = other.AutoCollapseEnabled;
        AutoCollapseDelaySeconds = other.AutoCollapseDelaySeconds;
        MaxStackItems = other.MaxStackItems;
        ConsumeOnDrop = other.ConsumeOnDrop;
        AutoCopyToClipboard = other.AutoCopyToClipboard;
        PlayShutterSound = other.PlayShutterSound;
        ShowTrayIcon = other.ShowTrayIcon;
        PromptToRenameOnSave = other.PromptToRenameOnSave;
        SaveLocationPath = other.SaveLocationPath;
        LaunchAtStartup = other.LaunchAtStartup;
        MonitorClipboard = other.MonitorClipboard;
        MonitorScreenshotsFolder = other.MonitorScreenshotsFolder;
        CleanPicturesFolder = other.CleanPicturesFolder;
        CaptureClipboardFromAnyApp = other.CaptureClipboardFromAnyApp;
        EnableGlobalHotkey = other.EnableGlobalHotkey;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
