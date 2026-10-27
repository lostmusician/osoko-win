using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Osoko.Core;
using Osoko.Core.Services;

namespace Osoko.UI;

/// <summary>
/// Application host: enforces a single instance, wires the ingestion services to the
/// floating shelf, owns the tray icon and guarantees a clean shutdown. The app has no
/// MainWindow — the shelf is created eagerly but only shown when there is something on it.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Osoko_SingleInstance_Mutex";
    private const string ActivationEventName = @"Local\Osoko_Activate_Event";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;

    private SettingsManager? _settingsManager;
    private ScratchStore? _scratchStore;
    private ExportTracker? _exportTracker;
    private ScreenshotManager? _screenshotManager;
    private ClipboardWatcher? _clipboardWatcher;
    private DirectoryWatcher? _directoryWatcher;
    private WindowsOcrService? _ocrService;
    private AudioService? _audioService;
    private ClipboardService? _clipboardService;
    private FileDialogService? _fileDialogs;
    private HotkeyService? _hotkeyService;
    private TrayIconManager? _trayIcon;
    private FloatingShelfWindow? _shelf;
    private SettingsWindow? _settingsWindow;
    private bool _isShuttingDown;

    /// <summary>Typed access to the running application.</summary>
    public static new App Current => (App)Application.Current;

    /// <summary>The live settings object.</summary>
    public OsokoSettings Settings => _settingsManager?.Settings ?? OsokoSettings.CreateDefault();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!TryAcquireSingleInstance())
        {
            SignalExistingInstance();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            InitializeServices();
        }
        catch (Exception ex)
        {
            Log.Error("Osoko failed to start", ex);
            MessageBox.Show(
                "Osoko could not start.\n\n" + ex.Message,
                "Osoko",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;

        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();

        _directoryWatcher?.Dispose();
        _clipboardWatcher?.Dispose();
        _hotkeyService?.Dispose();
        _trayIcon?.Dispose();
        _shelf?.CloseForShutdown();
        _screenshotManager?.Dispose();
        _exportTracker?.Dispose();
        _ocrService?.Dispose();
        _audioService?.Dispose();

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Owned by another thread — nothing to release.
            }

            _instanceMutex.Dispose();
        }

        Log.Info("Osoko stopped.");
        base.OnExit(e);
    }

    // ---------------------------------------------------------------------
    // Composition root
    // ---------------------------------------------------------------------

    private void InitializeServices()
    {
        AppPaths.EnsureDirectories();

        var isFirstRun = !File.Exists(AppPaths.SettingsFilePath);

        _settingsManager = new SettingsManager();
        var settings = _settingsManager.Settings;

        // Keep the Run key and settings.json in sync.
        if (settings.LaunchAtStartup != _settingsManager.IsLaunchAtStartupEnabled())
        {
            _settingsManager.SetLaunchAtStartup(settings.LaunchAtStartup);
        }

        _scratchStore = new ScratchStore();
        _scratchStore.EnsureCreated();
        _scratchStore.CleanupOlderThan(TimeSpan.FromHours(24));

        _exportTracker = new ExportTracker();
        _clipboardService = new ClipboardService();
        _fileDialogs = new FileDialogService();
        _ocrService = new WindowsOcrService();
        _audioService = new AudioService(() => _settingsManager?.Settings.PlayShutterSound ?? false);

        _screenshotManager = new ScreenshotManager(
            settings,
            _exportTracker,
            new WpfImageLoader(),
            _clipboardService,
            _fileDialogs,
            _ocrService,
            path => _scratchStore?.DeleteFile(path));

        _screenshotManager.ScreenshotAdded += OnScreenshotAdded;
        _screenshotManager.StackChanged += OnStackChanged;
        _screenshotManager.ScreenshotSaved += OnScreenshotSaved;

        _shelf = new FloatingShelfWindow(_screenshotManager, settings, _audioService);
        _shelf.SettingsRequested += (_, _) => OpenSettings();
        _shelf.ApplySettings();

        _clipboardWatcher = new ClipboardWatcher(_scratchStore, settings)
        {
            Enabled = settings.MonitorClipboard,
        };
        _clipboardWatcher.ImageCaptured += OnClipboardImageCaptured;
        _clipboardService.OwnWriteStarted += (_, _) => _clipboardWatcher?.SuppressNextUpdate();

        _directoryWatcher = new DirectoryWatcher(
            AppPaths.PicturesScreenshotsDirectory,
            _scratchStore,
            _exportTracker,
            settings);
        _directoryWatcher.FileCaptured += OnFileCaptured;

        if (settings.MonitorScreenshotsFolder)
        {
            _directoryWatcher.Start();
        }

        _hotkeyService = new HotkeyService(() =>
            Dispatcher.BeginInvoke(new Action(HotkeyService.LaunchSnippingTool)));

        if (settings.EnableGlobalHotkey)
        {
            _hotkeyService.Register();
        }

        _trayIcon = new TrayIconManager(new TrayActions(
            ShowShelf: ShowShelfFromTray,
            OpenSettings: OpenSettings,
            ClearStack: ClearStack,
            OpenScratchFolder: OpenScratchFolder,
            Exit: Shutdown))
        {
            Visible = settings.ShowTrayIcon,
        };

        HookActivationListener();
        _ = _ocrService.PrewarmAsync();

        Log.Info($"Osoko started (first run: {isFirstRun}, tray: {settings.ShowTrayIcon}).");

        if (isFirstRun && settings.ShowTrayIcon)
        {
            _trayIcon.ShowNotification(
                "Osoko is ready",
                "Press Win + Shift + S to snip. Captures land on the shelf at the bottom-right corner.");
        }
    }

    // ---------------------------------------------------------------------
    // Ingestion
    // ---------------------------------------------------------------------

    private void OnClipboardImageCaptured(object? sender, ClipboardImageEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var item = _screenshotManager?.AddScreenshot(e.FilePath, e.Source, e.Fingerprint);
            if (item is null)
            {
                // Duplicate (already on the shelf) — do not keep a second copy around.
                _scratchStore?.DeleteFile(e.FilePath);
            }
        }));
    }

    private void OnFileCaptured(object? sender, ScreenshotFileEventArgs e)
    {
        var fingerprint = string.IsNullOrEmpty(e.Fingerprint) ? null : e.Fingerprint;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var item = _screenshotManager?.AddScreenshot(e.FilePath, ScreenshotSource.ScreenshotsFolder, fingerprint);
            if (item is null)
            {
                _scratchStore?.DeleteFile(e.FilePath);
            }
        }));
    }

    private void OnScreenshotAdded(object? sender, ScreenshotItem item)
    {
        _audioService?.Play(AudioCue.Capture);

        if (_settingsManager?.Settings.AutoCopyToClipboard == true && _clipboardService is not null)
        {
            try
            {
                _clipboardService.SetImage(item.FilePath);
            }
            catch (Exception ex)
            {
                Log.Error("Auto-copy to clipboard failed", ex);
            }
        }

        _trayIcon?.UpdateTooltip($"Osoko — {_screenshotManager?.Count ?? 0} on the shelf");
    }

    private void OnScreenshotSaved(object? sender, ScreenshotItem item) =>
        _trayIcon?.UpdateTooltip($"Osoko — saved {item.FileName}");

    private void OnStackChanged(object? sender, StackChangedEventArgs e)
    {
        var count = _screenshotManager?.Count ?? 0;
        _trayIcon?.UpdateTooltip(count == 0 ? "Osoko — screenshot shelf" : $"Osoko — {count} on the shelf");
    }

    // ---------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------

    private void ShowShelfFromTray()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _shelf?.ShowShelf(expand: true);
    }

    private void ClearStack()
    {
        if (_isShuttingDown)
        {
            return;
        }

        var removed = _screenshotManager?.ClearAll() ?? 0;
        if (removed > 0)
        {
            _audioService?.Play(AudioCue.Delete);
        }
    }

    private void OpenScratchFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ScratchDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.ScratchDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the scratch folder", ex);
        }
    }

    private void OpenSettings()
    {
        if (_isShuttingDown || _settingsManager is null || _fileDialogs is null)
        {
            return;
        }

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var workingCopy = _settingsManager.Settings.Clone();
        var window = new SettingsWindow(workingCopy, _fileDialogs);
        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;

        _shelf?.Suspend();

        var accepted = false;
        try
        {
            accepted = window.ShowDialog() == true;
        }
        finally
        {
            _shelf?.Resume();
        }

        if (accepted)
        {
            ApplySettings(workingCopy);
        }
    }

    private void ApplySettings(OsokoSettings updated)
    {
        if (_settingsManager is null)
        {
            return;
        }

        var live = _settingsManager.Settings;
        live.CopyFrom(updated);

        try
        {
            _settingsManager.Save(live);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
            _trayIcon?.ShowNotification("Osoko", "Settings could not be saved.", isError: true);
        }

        if (!_settingsManager.SetLaunchAtStartup(live.LaunchAtStartup))
        {
            Log.Warn("Could not update the Windows startup registry entry.");
        }

        if (_clipboardWatcher is not null)
        {
            _clipboardWatcher.Enabled = live.MonitorClipboard;
        }

        if (_directoryWatcher is not null)
        {
            if (live.MonitorScreenshotsFolder)
            {
                _directoryWatcher.Start();
            }
            else
            {
                _directoryWatcher.Stop();
            }
        }

        if (_hotkeyService is not null)
        {
            if (live.EnableGlobalHotkey)
            {
                _hotkeyService.Register();
            }
            else
            {
                _hotkeyService.Unregister();
            }
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = live.ShowTrayIcon;
        }

        _screenshotManager?.PruneToLimit();
        _shelf?.ApplySettings();

        Log.Info("Settings updated.");
    }

    // ---------------------------------------------------------------------
    // Single instance
    // ---------------------------------------------------------------------

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
            {
                return true;
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Single-instance mutex unavailable ({ex.Message}); continuing as the only instance.");
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        // The first instance may still be starting up, so give it a moment to create
        // the activation event before giving up.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle))
                {
                    using (handle)
                    {
                        handle.Set();
                    }

                    return;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                Log.Warn($"Could not signal the running instance: {ex.Message}");
                return;
            }

            Thread.Sleep(150);
        }

        Log.Warn("Another Osoko instance is running but did not respond to the activation signal.");
    }

    private void HookActivationListener()
    {
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _activationWait = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(ShowShelfFromTray)),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not hook the activation event: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Error("Unhandled domain exception", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
