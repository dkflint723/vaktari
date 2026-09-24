using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Opening a folder whose name Windows rewrites.
///
/// **"data " showed the contents of "data".** Windows' path rules strip a
/// trailing space or dot before the call, so the listing of "data " was the
/// listing of its neighbour "data" — the crumbs named one folder and every row
/// and every path on it belonged to the other, and deleting there emptied the
/// wrong folder. The pane now refuses such a name the way it refuses a folder
/// that will not list, before anything is asked of the disk.
///
/// Windows only: elsewhere a trailing space is an ordinary character.
/// </summary>
public sealed class UnreachableFolderTests : OwnedViewModels
{
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_folder_named_with_a_trailing_space_is_refused_not_listed_as_its_neighbour()
    {
        var disk = new Recording();
        var pane = Own(new PaneViewModel(disk));

        var spaced = Path.Combine(Path.GetTempPath(), "data ");

        await pane.NavigateAsync(spaced);

        Assert.DoesNotContain(disk.Asked, p => p.StartsWith(spaced.TrimEnd(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ends with a space", pane.LoadError, StringComparison.Ordinal);
        Assert.Empty(pane.Entries);
        Assert.False(pane.IsLoading);
    }

    /// <summary>And an ordinary folder beside it is listed as ever.</summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task Its_neighbour_is_listed_as_ever()
    {
        var disk = new Recording();
        var pane = Own(new PaneViewModel(disk));

        var plain = Path.Combine(Path.GetTempPath(), "data");

        await pane.NavigateAsync(plain);

        Assert.Contains(plain, disk.Asked);
        Assert.Equal("", pane.LoadError);
    }

    /// <summary>
    /// **Nor is anything made in it.** The listing was refused but the pane
    /// still stood there, and Ctrl+Shift+N in "data." made "data\New folder".
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_new_folder_is_not_made_in_its_neighbour()
    {
        var root = Directory.CreateTempSubdirectory("vaktari-unreach").FullName;

        try
        {
            var pane = Own(new PaneViewModel(new Recording()));

            await pane.NavigateAsync(Path.Combine(root, "data."));
            await pane.NewFolderAsync();

            Assert.False(Directory.Exists(Path.Combine(root, "data")), "the new folder was made in the neighbour");
            Assert.Contains("ends with", pane.Status, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A disk that remembers which folders it was asked to list.</summary>
    private sealed class Recording : IFileSystemProvider
    {
        public List<string> Asked { get; } = [];

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            Asked.Add(path);
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
