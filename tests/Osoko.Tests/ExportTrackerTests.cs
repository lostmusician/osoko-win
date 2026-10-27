using Osoko.Core.Services;

namespace Osoko.Tests;

/// <summary>
/// The export tracker protects Osoko's own outputs from being re-ingested by the file and
/// clipboard watchers, and holds dropped files for 15 minutes before deleting them.
/// </summary>
public sealed class ExportTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "Osoko.Tests",
        "exports",
        Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2025-01-31T10:00:00Z"));

    public ExportTrackerTests() => Directory.CreateDirectory(_directory);

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
            // Best effort.
        }
    }

    [Fact]
    public void New_Tracker_Is_Empty()
    {
        using var tracker = CreateTracker();

        Assert.Equal(0, tracker.Count);
        Assert.Equal(0, tracker.PendingCleanupCount);
        Assert.Empty(tracker.RegisteredPaths);
        Assert.False(tracker.IsExported(Path.Combine(_directory, "shot.png")));
    }

    [Fact]
    public void RegisterExported_Makes_Path_Queryable_Immediately()
    {
        var path = CreateFile("saved.png");
        using var tracker = CreateTracker();

        // Pre-registration: the watcher may fire before the save finishes.
        tracker.RegisterExported(path);

        Assert.True(tracker.IsExported(path));
        Assert.True(tracker.IsRegistered(path));
        Assert.Equal(1, tracker.Count);
        Assert.Equal(0, tracker.PendingCleanupCount);
        Assert.Equal(0, tracker.PurgeExpired());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void IsExported_Normalizes_Relative_And_Case_Differing_Paths()
    {
        var path = CreateFile("case.png");
        using var tracker = CreateTracker();
        tracker.RegisterExported(path);

        Assert.True(tracker.IsExported(path.ToUpperInvariant()));
        Assert.True(tracker.IsExported(Path.Combine(_directory, ".", "case.png")));
        Assert.False(tracker.IsExported(string.Empty));
        Assert.False(tracker.IsExported(null));
    }

    [Fact]
    public void ScheduleDeferredCleanup_Queues_File_For_Retention()
    {
        var path = CreateFile("dropped.png");
        using var tracker = CreateTracker();

        tracker.ScheduleDeferredCleanup(path, TimeSpan.FromMinutes(15));

        Assert.Equal(1, tracker.PendingCleanupCount);
        Assert.True(tracker.IsExported(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PurgeExpired_Keeps_Files_Inside_The_Retention_Window()
    {
        var path = CreateFile("retained.png");
        using var tracker = CreateTracker();
        tracker.ScheduleDeferredCleanup(path, TimeSpan.FromMinutes(15));

        _clock.Advance(TimeSpan.FromMinutes(14));
        var purged = tracker.PurgeExpired();

        Assert.Equal(0, purged);
        Assert.Equal(1, tracker.PendingCleanupCount);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PurgeExpired_Deletes_File_After_The_Retention_Window()
    {
        var path = CreateFile("expired.png");
        var deleted = new List<string>();
        using var tracker = CreateTracker(pathToDelete =>
        {
            deleted.Add(pathToDelete);
            File.Delete(pathToDelete);
        });
        tracker.ScheduleDeferredCleanup(path, TimeSpan.FromMinutes(15));

        _clock.Advance(TimeSpan.FromMinutes(15));

        var purged = tracker.PurgeExpired();

        Assert.Equal(1, purged);
        Assert.Contains(path, deleted);
        Assert.False(File.Exists(path));
        Assert.Equal(0, tracker.PendingCleanupCount);
        Assert.False(tracker.IsExported(path));
    }

    [Fact]
    public void Default_Retention_Is_Fifteen_Minutes()
    {
        var path = CreateFile("default.png");
        using var tracker = CreateTracker();
        tracker.ScheduleDeferredCleanup(path);

        _clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, tracker.PurgeExpired());

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, tracker.PurgeExpired());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Permanent_Exports_Are_Never_Purged()
    {
        var path = CreateFile("permanent.png");
        using var tracker = CreateTracker();
        tracker.RegisterExported(path);

        _clock.Advance(TimeSpan.FromDays(30));

        Assert.Equal(0, tracker.PurgeExpired());
        Assert.True(tracker.IsExported(path));
    }

    [Fact]
    public void CancelDeferredCleanup_Releases_The_File_Without_Deleting_It()
    {
        var path = CreateFile("cancelled.png");
        using var tracker = CreateTracker();
        tracker.ScheduleDeferredCleanup(path, TimeSpan.FromMinutes(5));

        Assert.True(tracker.CancelDeferredCleanup(path));

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, tracker.PurgeExpired());
        Assert.True(File.Exists(path));
        Assert.False(tracker.IsExported(path));
    }

    [Fact]
    public void Re_Scheduling_Extends_The_Retention_Window()
    {
        var path = CreateFile("extended.png");
        using var tracker = CreateTracker();
        tracker.ScheduleDeferredCleanup(path, TimeSpan.FromMinutes(1));

        _clock.Advance(TimeSpan.FromSeconds(50));
        tracker.ScheduleDeferredCleanup(path, TimeSpan.FromMinutes(5));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, tracker.PurgeExpired());

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(1, tracker.PurgeExpired());
    }

    [Fact]
    public void Clear_Drops_All_Registrations_Without_Deleting_Files()
    {
        var first = CreateFile("one.png");
        var second = CreateFile("two.png");
        using var tracker = CreateTracker();

        tracker.RegisterExported(first);
        tracker.ScheduleDeferredCleanup(second, TimeSpan.FromMinutes(1));

        tracker.Clear();

        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.IsExported(first));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void Missing_Files_Are_Ignored_By_Deferred_Deletion()
    {
        var path = Path.Combine(_directory, "never-existed.png");
        using var tracker = CreateTracker();
        tracker.ScheduleDeferredCleanup(path, TimeSpan.Zero);

        var purged = tracker.PurgeExpired();

        Assert.Equal(1, purged);
        Assert.False(File.Exists(path));
    }

    private ExportTracker CreateTracker(Action<string>? deleteFile = null) =>
        new(_clock.Now, deleteFile, enableBackgroundSweep: false);

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, name);
        return path;
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _current;

        internal FakeClock(DateTimeOffset start)
        {
            _current = start;
        }

        internal DateTimeOffset Now() => _current;

        internal void Advance(TimeSpan delta) => _current += delta;
    }
}
