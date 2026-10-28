using System.Text.Json;
using Osoko.Core;

namespace Osoko.Tests;

/// <summary>
/// Settings persistence must be boring: defaults when missing, exact round-trip when
/// written, and never a crash when the JSON is damaged.
/// </summary>
public sealed class SettingsManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "Osoko.Tests",
        "settings",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    [Fact]
    public void Load_Uses_Documented_Defaults_When_No_File_Exists()
    {
        var manager = new SettingsManager(_directory);
        var settings = manager.Settings;

        Assert.True(settings.AutoCollapseEnabled);
        Assert.Equal(10, settings.AutoCollapseDelaySeconds);
        Assert.Equal(5, settings.MaxStackItems);
        Assert.True(settings.ConsumeOnDrop);
        Assert.False(settings.AutoCopyToClipboard);
        Assert.True(settings.PlayShutterSound);
        Assert.True(settings.ShowTrayIcon);
        Assert.False(settings.PromptToRenameOnSave);
        Assert.False(settings.LaunchAtStartup);
        Assert.True(settings.MonitorClipboard);
        Assert.True(settings.MonitorScreenshotsFolder);
        Assert.True(settings.CleanPicturesFolder);
        Assert.False(settings.CaptureClipboardFromAnyApp);
        Assert.False(settings.EnableGlobalHotkey);
        Assert.Equal(CardActionKind.SaveToFolder, settings.TopActionSlot1);
        Assert.Equal(CardActionKind.Pin, settings.TopActionSlot2);
        Assert.Equal(CardActionKind.CopyDownscaled, settings.BottomActionSlot1);
        Assert.Equal(CardActionKind.Share, settings.BottomActionSlot2);
        Assert.Equal(CardActionKind.CopyOriginal, settings.PrimaryClickAction);
        Assert.Equal(CardActionKind.OpenInPreview, settings.DoubleClickAction);
        Assert.True(settings.EnableHoverShortcuts);
        Assert.False(string.IsNullOrWhiteSpace(settings.SaveLocationPath));
        Assert.False(File.Exists(manager.SettingsFilePath));
    }

    [Fact]
    public void Save_And_Load_Round_Trips_Every_Value()
    {
        var manager = new SettingsManager(_directory);
        var settings = manager.Settings;

        settings.AutoCollapseEnabled = false;
        settings.AutoCollapseDelaySeconds = 27;
        settings.MaxStackItems = 9;
        settings.ConsumeOnDrop = false;
        settings.AutoCopyToClipboard = true;
        settings.PlayShutterSound = false;
        settings.ShowTrayIcon = false;
        settings.PromptToRenameOnSave = true;
        settings.SaveLocationPath = Path.Combine(_directory, "Saved shots");
        settings.LaunchAtStartup = true;
        settings.MonitorClipboard = false;
        settings.MonitorScreenshotsFolder = false;
        settings.CleanPicturesFolder = false;
        settings.CaptureClipboardFromAnyApp = true;
        settings.EnableGlobalHotkey = true;
        settings.TopActionSlot1 = CardActionKind.CopyOcr;
        settings.TopActionSlot2 = CardActionKind.None;
        settings.BottomActionSlot1 = CardActionKind.SaveAs;
        settings.BottomActionSlot2 = CardActionKind.CopyOriginal;
        settings.PrimaryClickAction = CardActionKind.CopyDownscaled;
        settings.DoubleClickAction = CardActionKind.Share;
        settings.EnableHoverShortcuts = false;

        manager.Save(settings);

        Assert.True(File.Exists(manager.SettingsFilePath));

        var reloaded = new SettingsManager(_directory).Settings;

        Assert.False(reloaded.AutoCollapseEnabled);
        Assert.Equal(27, reloaded.AutoCollapseDelaySeconds);
        Assert.Equal(9, reloaded.MaxStackItems);
        Assert.False(reloaded.ConsumeOnDrop);
        Assert.True(reloaded.AutoCopyToClipboard);
        Assert.False(reloaded.PlayShutterSound);
        Assert.False(reloaded.ShowTrayIcon);
        Assert.True(reloaded.PromptToRenameOnSave);
        Assert.Equal(settings.SaveLocationPath, reloaded.SaveLocationPath);
        Assert.True(reloaded.LaunchAtStartup);
        Assert.False(reloaded.MonitorClipboard);
        Assert.False(reloaded.MonitorScreenshotsFolder);
        Assert.False(reloaded.CleanPicturesFolder);
        Assert.True(reloaded.CaptureClipboardFromAnyApp);
        Assert.True(reloaded.EnableGlobalHotkey);
        Assert.Equal(CardActionKind.CopyOcr, reloaded.TopActionSlot1);
        Assert.Equal(CardActionKind.None, reloaded.TopActionSlot2);
        Assert.Equal(CardActionKind.SaveAs, reloaded.BottomActionSlot1);
        Assert.Equal(CardActionKind.CopyOriginal, reloaded.BottomActionSlot2);
        Assert.Equal(CardActionKind.CopyDownscaled, reloaded.PrimaryClickAction);
        Assert.Equal(CardActionKind.Share, reloaded.DoubleClickAction);
        Assert.False(reloaded.EnableHoverShortcuts);

        var json = File.ReadAllText(manager.SettingsFilePath);
        Assert.Contains("\"topActionSlot1\": \"copy_ocr\"", json);
        Assert.Contains("\"topActionSlot2\": \"none\"", json);
        Assert.Contains("\"doubleClickAction\": \"share\"", json);
    }

    [Fact]
    public void Save_Writes_Human_Readable_Camel_Case_Json()
    {
        var manager = new SettingsManager(_directory);
        manager.Settings.MaxStackItems = 7;
        manager.Save();

        var json = File.ReadAllText(manager.SettingsFilePath);

        Assert.Contains("\"maxStackItems\": 7", json);
        Assert.Contains("\"autoCollapseDelaySeconds\": 10", json);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("consumeOnDrop", out _));
    }

    [Fact]
    public void Corrupt_Json_Falls_Back_To_Defaults_And_Is_Quarantined()
    {
        Directory.CreateDirectory(_directory);
        var manager = new SettingsManager(_directory);
        File.WriteAllText(manager.SettingsFilePath, "{ this is not json ");

        var recovered = new SettingsManager(_directory);

        Assert.Equal(5, recovered.Settings.MaxStackItems);
        Assert.Equal(10, recovered.Settings.AutoCollapseDelaySeconds);
        Assert.True(File.Exists(manager.SettingsFilePath + ".corrupt"));
    }

    [Fact]
    public void Load_Clamps_Out_Of_Range_Values()
    {
        Directory.CreateDirectory(_directory);
        var manager = new SettingsManager(_directory);
        File.WriteAllText(
            manager.SettingsFilePath,
            """
            {
              "maxStackItems": 99,
              "autoCollapseDelaySeconds": 1,
              "saveLocationPath": ""
            }
            """);

        var reloaded = new SettingsManager(_directory).Settings;

        Assert.Equal(OsokoSettings.MaxStackItemCount, reloaded.MaxStackItems);
        Assert.Equal(OsokoSettings.MinCollapseDelaySeconds, reloaded.AutoCollapseDelaySeconds);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.SaveLocationPath));
    }

    [Fact]
    public void Reset_Restores_Defaults_And_Persists_Them()
    {
        var manager = new SettingsManager(_directory);
        manager.Settings.MaxStackItems = 12;
        manager.Save();

        var defaults = manager.Reset();

        Assert.Equal(5, defaults.MaxStackItems);
        Assert.Equal(5, new SettingsManager(_directory).Settings.MaxStackItems);
    }

    [Fact]
    public void SettingsChanged_Is_Raised_After_Save()
    {
        var manager = new SettingsManager(_directory);
        OsokoSettings? observed = null;
        manager.SettingsChanged += (_, settings) => observed = settings;

        manager.Settings.MaxStackItems = 4;
        manager.Save();

        Assert.NotNull(observed);
        Assert.Equal(4, observed!.MaxStackItems);
    }

    [Fact]
    public void Clone_Is_Independent_Of_The_Original()
    {
        var original = OsokoSettings.CreateDefault();
        original.MaxStackItems = 3;

        var clone = original.Clone();
        clone.MaxStackItems = 11;

        Assert.Equal(3, original.MaxStackItems);
        Assert.Equal(11, clone.MaxStackItems);
    }

    [Fact]
    public void CopyFrom_Transfers_Every_Editable_Value()
    {
        var source = OsokoSettings.CreateDefault();
        source.AutoCollapseEnabled = false;
        source.AutoCollapseDelaySeconds = 45;
        source.MaxStackItems = 2;
        source.ConsumeOnDrop = false;
        source.AutoCopyToClipboard = true;
        source.PlayShutterSound = false;
        source.ShowTrayIcon = false;
        source.PromptToRenameOnSave = true;
        source.SaveLocationPath = Path.Combine(Path.GetTempPath(), "elsewhere");
        source.LaunchAtStartup = true;
        source.MonitorClipboard = false;
        source.MonitorScreenshotsFolder = false;
        source.CleanPicturesFolder = false;
        source.CaptureClipboardFromAnyApp = true;
        source.EnableGlobalHotkey = true;
        source.TopActionSlot1 = CardActionKind.CopyOcr;
        source.TopActionSlot2 = CardActionKind.None;
        source.BottomActionSlot1 = CardActionKind.SaveAs;
        source.BottomActionSlot2 = CardActionKind.CopyOriginal;
        source.PrimaryClickAction = CardActionKind.CopyDownscaled;
        source.DoubleClickAction = CardActionKind.Share;
        source.EnableHoverShortcuts = false;

        var target = OsokoSettings.CreateDefault();
        target.CopyFrom(source);

        Assert.False(target.AutoCollapseEnabled);
        Assert.Equal(45, target.AutoCollapseDelaySeconds);
        Assert.Equal(2, target.MaxStackItems);
        Assert.False(target.ConsumeOnDrop);
        Assert.True(target.AutoCopyToClipboard);
        Assert.False(target.PlayShutterSound);
        Assert.False(target.ShowTrayIcon);
        Assert.True(target.PromptToRenameOnSave);
        Assert.Equal(source.SaveLocationPath, target.SaveLocationPath);
        Assert.True(target.LaunchAtStartup);
        Assert.False(target.MonitorClipboard);
        Assert.False(target.MonitorScreenshotsFolder);
        Assert.False(target.CleanPicturesFolder);
        Assert.True(target.CaptureClipboardFromAnyApp);
        Assert.True(target.EnableGlobalHotkey);
        Assert.Equal(CardActionKind.CopyOcr, target.TopActionSlot1);
        Assert.Equal(CardActionKind.None, target.TopActionSlot2);
        Assert.Equal(CardActionKind.SaveAs, target.BottomActionSlot1);
        Assert.Equal(CardActionKind.CopyOriginal, target.BottomActionSlot2);
        Assert.Equal(CardActionKind.CopyDownscaled, target.PrimaryClickAction);
        Assert.Equal(CardActionKind.Share, target.DoubleClickAction);
        Assert.False(target.EnableHoverShortcuts);
    }
}
