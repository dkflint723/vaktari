using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The watcher for folders that deliver no notifications.
///
/// **A folder on a network mount never updated on its own**: inotify reports
/// what passed through this kernel, and a file written by another machine is
/// not that. This reads the folder on a timer and reports the differences —
/// added, changed, removed, gone — as the events the listing already handles.
///
/// Driven by hand: the interval is set to never, and each test takes a tick
/// itself, so what a tick reports is asserted without a timer in the way. One
/// test lets the timer run, because a watch nothing ticks is no watch.
/// </summary>
public sealed class PollingWatchTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-poll-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly List<FileSystemChange> _seen = [];

    public PollingWatchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private PollingWatch Watching()
        => new(_root, change => { lock (_seen) _seen.Add(change); }, Timeout.InfiniteTimeSpan);

    private string In(string name) => Path.Combine(_root, name);

    [Fact]
    public void A_file_that_appears_is_added()
    {
        using var watch = Watching();

        File.WriteAllText(In("new.txt"), "hello");
        watch.Tick();

        Assert.Equal([new FileSystemChange(ChangeKind.Added, In("new.txt"))], _seen);
    }

    [Fact]
    public void A_file_that_grows_is_changed()
    {
        File.WriteAllText(In("log.txt"), "one");

        using var watch = Watching();

        File.AppendAllText(In("log.txt"), " two");
        watch.Tick();

        Assert.Equal([new FileSystemChange(ChangeKind.Changed, In("log.txt"))], _seen);
    }

    [Fact]
    public void A_file_that_leaves_is_removed()
    {
        File.WriteAllText(In("old.txt"), "x");

        using var watch = Watching();

        File.Delete(In("old.txt"));
        watch.Tick();

        Assert.Equal([new FileSystemChange(ChangeKind.Removed, In("old.txt"))], _seen);
    }

    [Fact]
    public void A_folder_that_is_not_there_any_more_is_gone()
    {
        var folder = Directory.CreateDirectory(In("share")).FullName;

        using var watch = new PollingWatch(folder, change => _seen.Add(change), Timeout.InfiniteTimeSpan);

        Directory.Delete(folder);
        watch.Tick();

        Assert.Equal([new FileSystemChange(ChangeKind.Gone, folder)], _seen);
    }

    /// <summary>A tick over a folder that has not moved says nothing — the
    /// listing would otherwise re-stat every row every five seconds.</summary>
    [Fact]
    public void Nothing_changed_reports_nothing()
    {
        File.WriteAllText(In("same.txt"), "x");
        Directory.CreateDirectory(In("sub"));

        using var watch = Watching();

        watch.Tick();
        watch.Tick();

        Assert.Empty(_seen);
    }

    /// <summary>The second tick reports against the first, not against the
    /// folder as it was when the watch began.</summary>
    [Fact]
    public void Each_tick_compares_with_the_last()
    {
        using var watch = Watching();

        File.WriteAllText(In("a.txt"), "x");
        watch.Tick();
        watch.Tick();

        Assert.Single(_seen);
    }

    [Fact]
    public async Task The_timer_drives_it()
    {
        using var watch = new PollingWatch(
            _root, change => { lock (_seen) _seen.Add(change); }, TimeSpan.FromMilliseconds(50));

        File.WriteAllText(In("arrived.txt"), "x");

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < until)
        {
            lock (_seen) if (_seen.Count > 0) break;
            await Task.Delay(20);
        }

        lock (_seen)
            Assert.Contains(new FileSystemChange(ChangeKind.Added, In("arrived.txt")), _seen);
    }

    /// <summary>Nothing is reported after Dispose, however late a tick lands.</summary>
    [Fact]
    public void A_disposed_watch_says_nothing()
    {
        var watch = Watching();

        watch.Dispose();

        File.WriteAllText(In("late.txt"), "x");
        watch.Tick();

        Assert.Empty(_seen);
    }
}
