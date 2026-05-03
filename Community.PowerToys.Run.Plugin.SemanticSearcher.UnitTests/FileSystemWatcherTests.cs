using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.UnitTests;

/// <summary>
/// Verifies that FileSystemWatcher fires the correct events for file and folder
/// create, rename, move, and delete operations on the Desktop — the actual watched path.
/// Uses a 2-second timeout per event to account for OS event latency.
/// </summary>
[TestClass]
public class FileSystemWatcherTests
{
    private static readonly string WatchRoot = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    private static readonly string TestBase   = Path.Combine(WatchRoot, "_WatcherTest_" + Guid.NewGuid().ToString("N")[..8]);
    private const int TimeoutMs = 2000;

    private FileSystemWatcher _watcher = null!;
    private readonly List<string> _events = [];
    private readonly object _eventsLock = new();

    [TestInitialize]
    public void Setup()
    {
        Directory.CreateDirectory(TestBase);

        _watcher = new FileSystemWatcher(WatchRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true,
        };

        _watcher.Created += (_, e) => Log($"Created:{e.FullPath}");
        _watcher.Changed += (_, e) => Log($"Changed:{e.FullPath}");
        _watcher.Deleted += (_, e) => Log($"Deleted:{e.FullPath}");
        _watcher.Renamed += (_, e) => Log($"Renamed:{e.OldFullPath}->{e.FullPath}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _watcher.Dispose();
        if (Directory.Exists(TestBase))
            Directory.Delete(TestBase, recursive: true);
    }

    // ── File events ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Watcher_FileCreated_FiresCreatedEvent()
    {
        string file = Path.Combine(TestBase, "create_test.txt");
        File.WriteAllText(file, "hello");

        bool fired = WaitForEvent(e => e.StartsWith("Created:") && e.Contains("create_test.txt"));
        Assert.IsTrue(fired, "Expected Created event for new file — watcher did not fire within timeout.");
    }

    [TestMethod]
    public void Watcher_FileRenamed_FiresRenamedEvent()
    {
        string original = Path.Combine(TestBase, "original.txt");
        string renamed  = Path.Combine(TestBase, "renamed.txt");
        File.WriteAllText(original, "hello");
        Thread.Sleep(300); // let create event settle
        ClearEvents();

        File.Move(original, renamed);

        bool fired = WaitForEvent(e => e.StartsWith("Renamed:") && e.Contains("original.txt") && e.Contains("renamed.txt"));
        Assert.IsTrue(fired, "Expected Renamed event for file rename — watcher did not fire within timeout.");
    }

    [TestMethod]
    public void Watcher_FileMovedIntoWatchedPath_FiresCreatedOrRenamedEvent()
    {
        // Simulate: file moves in from outside watched tree (use a sub-folder outside the test base)
        string outsideDir = Path.Combine(Path.GetTempPath(), "_WatcherTest_outside_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(outsideDir);
        string source = Path.Combine(outsideDir, "movein.txt");
        string dest   = Path.Combine(TestBase, "movein.txt");

        try
        {
            File.WriteAllText(source, "moving in");
            Thread.Sleep(300);
            ClearEvents();

            File.Move(source, dest);

            // When src is outside the watch root: watcher sees Created (not Renamed)
            bool fired = WaitForEvent(e => (e.StartsWith("Created:") || e.StartsWith("Renamed:")) && e.Contains("movein.txt"));
            Assert.IsTrue(fired, "Expected Created/Renamed event when file moved into watched path.");
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [TestMethod]
    public void Watcher_FileMovedOutOfWatchedPath_FiresDeletedOrRenamedEvent()
    {
        string source = Path.Combine(TestBase, "moveout.txt");
        string outsideDir = Path.Combine(Path.GetTempPath(), "_WatcherTest_outside_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(outsideDir);
        string dest = Path.Combine(outsideDir, "moveout.txt");

        try
        {
            File.WriteAllText(source, "moving out");
            Thread.Sleep(300);
            ClearEvents();

            File.Move(source, dest);

            // When dest is outside watch root: watcher sees Deleted (not Renamed)
            bool fired = WaitForEvent(e => (e.StartsWith("Deleted:") || e.StartsWith("Renamed:")) && e.Contains("moveout.txt"));
            Assert.IsTrue(fired, "Expected Deleted/Renamed event when file moved out of watched path.");
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [TestMethod]
    public void Watcher_FileDeleted_FiresDeletedEvent()
    {
        string file = Path.Combine(TestBase, "delete_test.txt");
        File.WriteAllText(file, "delete me");
        Thread.Sleep(300);
        ClearEvents();

        File.Delete(file);

        bool fired = WaitForEvent(e => e.StartsWith("Deleted:") && e.Contains("delete_test.txt"));
        Assert.IsTrue(fired, "Expected Deleted event — watcher did not fire within timeout.");
    }

    // ── Directory events ─────────────────────────────────────────────────────

    [TestMethod]
    public void Watcher_DirectoryRenamed_FiresRenamedEvent()
    {
        string original = Path.Combine(TestBase, "FolderA");
        string renamed  = Path.Combine(TestBase, "FolderB");
        Directory.CreateDirectory(original);
        Thread.Sleep(300);
        ClearEvents();

        Directory.Move(original, renamed);

        bool fired = WaitForEvent(e => e.StartsWith("Renamed:") && e.Contains("FolderA") && e.Contains("FolderB"));
        Assert.IsTrue(fired, "Expected Renamed event for directory rename — watcher did not fire within timeout.");
    }

    [TestMethod]
    public void Watcher_DirectoryWithFilesRenamed_FiresRenamedEventForDirectory()
    {
        string original = Path.Combine(TestBase, "FolderWithFiles");
        string renamed  = Path.Combine(TestBase, "FolderWithFilesRenamed");
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "inside.txt"), "content");
        Thread.Sleep(300);
        ClearEvents();

        Directory.Move(original, renamed);

        bool fired = WaitForEvent(e => e.StartsWith("Renamed:") && e.Contains("FolderWithFiles"));
        Assert.IsTrue(fired, "Expected Renamed event for directory with files — watcher did not fire within timeout.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Log(string evt)
    {
        lock (_eventsLock) { _events.Add(evt); }
    }

    private void ClearEvents()
    {
        lock (_eventsLock) { _events.Clear(); }
    }

    private bool WaitForEvent(Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_eventsLock)
            {
                if (_events.Exists(e => predicate(e))) return true;
            }
            Thread.Sleep(50);
        }
        return false;
    }
}
