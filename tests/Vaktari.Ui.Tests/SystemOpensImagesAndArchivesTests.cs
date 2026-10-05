using Avalonia.Headless.XUnit;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **What Vaktari no longer does itself, the system still does.** Mount and
/// Unmount left the menu, and Extract all narrowed to zip and tar.gz; in both
/// cases the answer for everything else is the double-click, which hands the
/// file to the system's own program — Explorer mounts an .iso, an archive tool
/// opens a .7z. So opening one must reach the launcher, by its own path, and
/// nothing on the way may treat it as a folder or as something to extract.
/// </summary>
public sealed class SystemOpensImagesAndArchivesTests : OwnedViewModels
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-sysopen").FullName;

    public override void Dispose()
    {
        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* temp */ }

        GC.SuppressFinalize(this);
    }

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

    private sealed class Recording : IApplicationLauncher
    {
        public List<string> Opened { get; } = [];

        public bool CanElevate => false;
        public bool CanElevateFile(string path) => false;
        public void OpenElevated(string path) { }
        public void OpenElevatedTerminal(string directory, TerminalOption? terminal = null) { }

        public Exception? Open(string path)
        {
            Opened.Add(path);
            return null;
        }

        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    [AvaloniaTheory]
    [InlineData("install.iso")]
    [InlineData("disk.img")]
    [InlineData("photos.7z")]
    [InlineData("backup.rar")]
    [InlineData("source.tar.xz")]
    public async Task Opening_one_hands_it_to_the_system(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "contents do not matter to the hand-over");

        var launcher = new Recording();
        var pane = Own(new PaneViewModel(new Inert(), launcher: launcher) { CurrentPath = _root });
        var entry = new FileEntry(name, path, 0, DateTimeOffset.Now, EntryFlags.None);

        await pane.OpenAsync(entry);

        Assert.Equal([path], launcher.Opened);
        Assert.Equal(_root, pane.CurrentPath);
    }
}
