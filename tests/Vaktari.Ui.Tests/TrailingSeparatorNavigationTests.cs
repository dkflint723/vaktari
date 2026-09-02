using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A folder reached by a path that ends in a separator is the same folder.
///
/// **Found by an audit, and it had teeth.** The folder watcher decides whether
/// an event belongs on screen with
/// <c>Path.GetDirectoryName(change.Path) != watchedPath</c>, and
/// GetDirectoryName never returns a trailing separator — so once the pane's path
/// carried one, every event was discarded and the listing silently froze. A
/// download finishing, a file deleted from a terminal, a rename by another
/// program: none of it appeared until F5.
///
/// It was always reachable by typing the separator by hand. It became far easier
/// to reach in the same change that made Tab-completion work on Windows paths,
/// because every completion ends with a separator.
/// </summary>
public sealed class TrailingSeparatorNavigationTests : OwnedViewModels
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "vaktari-trailing-" + Guid.NewGuid().ToString("N"));

    public TrailingSeparatorNavigationTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "one.txt"), "1");
    }

    /// <summary>
    /// Both teardowns, in order: the view models first so nothing is still
    /// watching the folder when it goes.
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();

        // Only what this test built, under its own folder.
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    /// <summary>
    /// A provider that lists nothing. What is under test is which STRING the
    /// pane settles on, so the folder's contents are beside the point — and a
    /// real provider would drag a live FileSystemWatcher into a unit test.
    /// </summary>
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

    private PaneViewModel Pane()
    {
        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(null, Path.GetTempPath());

        return shell.ActiveTab!;
    }

    /// <summary>
    /// The pane settles on the folder itself, not on a second spelling of it —
    /// which is what every downstream string comparison depends on.
    /// </summary>
    [AvaloniaFact]
    public async Task Navigating_with_a_trailing_separator_settles_on_the_plain_path()
    {
        var pane = Pane();

        await pane.NavigateAsync(_folder + Path.DirectorySeparatorChar);

        Assert.Equal(_folder, pane.CurrentPath);
        Assert.False(
            pane.CurrentPath.EndsWith(Path.DirectorySeparatorChar),
            "a trailing separator here is what stops the watcher recognising its own folder");
    }

    /// <summary>
    /// And the plain spelling is untouched, so the fix cannot have been "trim
    /// something off every path".
    /// </summary>
    [AvaloniaFact]
    public async Task Navigating_without_one_is_unchanged()
    {
        var pane = Pane();

        await pane.NavigateAsync(_folder);

        Assert.Equal(_folder, pane.CurrentPath);
    }

    /// <summary>
    /// **A root IS its separator.** Trimming one to a bare "C:" would mean
    /// "wherever this process happens to be on that drive", which is the one
    /// thing nobody means by it — PathVariables makes the same point about
    /// typing one.
    /// </summary>
    [AvaloniaFact]
    public async Task A_root_keeps_its_separator()
    {
        var root = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Path.GetTempPath())!
            : "/";

        var pane = Pane();

        await pane.NavigateAsync(root);

        Assert.Equal(PathRules.Normalise(root), pane.CurrentPath);
        Assert.True(pane.CurrentPath.Length > 1 || !OperatingSystem.IsWindows());
    }

    /// <summary>
    /// The two spellings are one folder as far as the watcher's own test is
    /// concerned. This is the comparison that was failing, exercised directly.
    /// </summary>
    [Fact]
    public void The_watchers_own_comparison_accepts_both_spellings()
    {
        var child = Path.Combine(_folder, "one.txt");

        Assert.True(PathRules.Same(Path.GetDirectoryName(child), _folder));
        Assert.True(PathRules.Same(
            Path.GetDirectoryName(child), _folder + Path.DirectorySeparatorChar));
    }
}
