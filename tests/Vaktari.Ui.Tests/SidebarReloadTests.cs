using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The sidebar's reload contract, now that device arrivals can drive it.
///
/// **Awaiting a reload must mean the rebuild finished** — including one that
/// arrived while another was running. Coalescing by returning early broke that
/// promise for every caller written against it (UnpinAsync awaits this and then
/// expects the row gone), and it broke it INVISIBLY: locally the follow-up
/// landed fast enough that nothing noticed, and it failed on CI.
/// </summary>
public sealed class SidebarReloadTests : OwnedViewModels
{
    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>A provider whose answer the test can change between reloads,
    /// and which counts how often it was asked.</summary>
    private sealed class Changing : IPlacesProvider
    {
        public string Label { get; set; } = "first";
        public int Asked { get; private set; }

        /// <summary>When set, the import and the listing die the way a
        /// provider on a machine nobody tested on would.</summary>
        public bool ImportThrows { get; set; }
        public bool ListingThrows { get; set; }

        public event EventHandler? PlacesChanged;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
        {
            Asked++;

            if (ListingThrows) throw new IOException("the mount table would not be read");

            return ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup("DEVICES",
                [
                    new Place
                    {
                        Id = "dev:x",
                        Label = Label,
                        Path = Path.GetTempPath(),
                        Kind = PlaceKind.Device,
                        Icon = "device-desktop",
                    },
                ]),
            ]);
        }

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.Ejected("gone"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RenameAsync(string id, string label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<int> ImportExistingAsync(CancellationToken ct)
            => ImportThrows
                ? throw new InvalidOperationException("the bookmarks file would not parse")
                : ValueTask.FromResult(0);

        public void Raise() => PlacesChanged?.Invoke(this, EventArgs.Empty);
    }

    private (ShellViewModel Shell, Changing Places) Fresh()
    {
        var places = new Changing();
        var shell = Own(new ShellViewModel(new Inert(), places: places));
        shell.Start(null, Path.GetTempPath());

        return (shell, places);
    }

    /// <summary>
    /// **The regression.** A reload asked for while one is running must not be
    /// dropped, and awaiting it must not return before the rebuild that
    /// includes it has finished.
    /// </summary>
    [AvaloniaFact]
    public async Task A_reload_asked_for_during_another_still_finishes_before_the_await_returns()
    {
        var (shell, places) = Fresh();

        // First reload, deliberately not awaited yet.
        _ = shell.Sidebar.ReloadAsync();

        // The world changes, and a second reload is asked for.
        places.Label = "second";
        var second = shell.Sidebar.ReloadAsync();

        // **Awaiting ONLY the second, and asserting immediately.** No dispatcher
        // pumping afterwards: the whole contract is that this await returning
        // means the rebuild has already happened. Pumping here would let the
        // in-flight run catch up on its own and the test would pass against the
        // broken version — which is exactly what the first draft of this test
        // did, and why it proved nothing.
        await second;

        var labels = shell.Sidebar.Groups.SelectMany(g => g.Places).Select(p => p.Label).ToList();

        Assert.Contains("second", labels);
        Assert.DoesNotContain("first", labels);
    }

    /// <summary>
    /// And it is still coalesced: several requests during one run cost one
    /// extra rebuild, not one each. A four-partition stick announces itself
    /// once per volume, and each rebuild enumerates every drive.
    /// </summary>
    [AvaloniaFact]
    public async Task A_burst_of_reloads_does_not_rebuild_once_per_request()
    {
        var (shell, places) = Fresh();

        var burst = new[]
        {
            shell.Sidebar.ReloadAsync(),
            shell.Sidebar.ReloadAsync(),
            shell.Sidebar.ReloadAsync(),
            shell.Sidebar.ReloadAsync(),
            shell.Sidebar.ReloadAsync(),
        };

        await Task.WhenAll(burst);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // At most the run in flight plus one follow-up covering the rest.
        Assert.True(places.Asked <= 2, $"rebuilt {places.Asked} times for five requests");
        Assert.NotEmpty(shell.Sidebar.Groups);
    }

    /// <summary>A reload after everything has settled still runs — the guard
    /// must not latch.</summary>
    [AvaloniaFact]
    public async Task Reloading_again_later_still_rebuilds()
    {
        var (shell, places) = Fresh();

        await shell.Sidebar.ReloadAsync();
        var afterFirst = places.Asked;

        places.Label = "third";
        await shell.Sidebar.ReloadAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(places.Asked > afterFirst);

        Assert.Contains(
            "third", shell.Sidebar.Groups.SelectMany(g => g.Places).Select(p => p.Label));
    }

    // ---- what a failure leaves behind -----------------------------------------

    /// <summary>
    /// **An import that throws must not cost the sidebar its rows.** The
    /// startup import is one bookmarks file; the rows come from the mount
    /// table and the user's folders, which are still there to read. Before
    /// this the exception left InitializeAsync with nothing awaiting it — the
    /// shell starts it fire-and-forget — and the sidebar stayed empty with
    /// nothing anywhere to say why.
    ///
    /// The provider is told to throw BEFORE the shell exists: Start runs the
    /// import, so a flag set afterwards would test a second import that
    /// nothing in the application runs.
    /// </summary>
    [AvaloniaFact]
    public async Task An_import_that_throws_does_not_cost_the_sidebar_its_rows()
    {
        var places = new Changing { ImportThrows = true };
        var shell = Own(new ShellViewModel(new Inert(), places: places));

        await shell.Sidebar.InitializeAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(shell.Sidebar.Groups);

        // Kept on its own property: the rebuild that followed finished, and
        // says so by clearing ITS error, which must not erase the import's.
        Assert.IsType<InvalidOperationException>(shell.Sidebar.LastImportError);
        Assert.Null(shell.Sidebar.LastReloadError);
    }

    /// <summary>
    /// A rebuild that throws is still thrown to whoever awaited it — an unpin
    /// is owed the failure — and is kept for whoever did not: the sidebar says
    /// what killed it, and that nothing is still running, which is what a
    /// test on a machine nobody has sat at needs to read out.
    /// </summary>
    [AvaloniaFact]
    public async Task A_rebuild_that_throws_says_so_and_is_not_still_running()
    {
        var (shell, places) = Fresh();

        await shell.Sidebar.ReloadAsync();

        places.ListingThrows = true;

        await Assert.ThrowsAsync<IOException>(() => shell.Sidebar.ReloadAsync());

        Assert.IsType<IOException>(shell.Sidebar.LastReloadError);
        Assert.False(shell.Sidebar.IsReloading);

        // And a rebuild that finishes clears it, so the account is of the LAST
        // rebuild rather than of any that ever failed.
        places.ListingThrows = false;

        await shell.Sidebar.ReloadAsync();

        Assert.Null(shell.Sidebar.LastReloadError);
    }
}
