using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.Thumbnails;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the rest of the listing has to do once a row stops drawing its file
/// name.
///
/// **A Windows shortcut only ever LOST characters — "Chrome.lnk" draws as
/// "Chrome" — so every other part of the pane that works in file names went on
/// agreeing with the screen by accident.** A Linux launcher draws the Name= key
/// out of the file, which need share no character with the file name at all:
/// org.kde.dolphin.desktop lists as "Dolphin". The two places that had been
/// riding on the accident are here — typing to reach a row, and the mark that
/// says two rows cannot be told apart — together with the guard that keeps the
/// read off a network mount.
///
/// The reader is stubbed rather than real: this assembly's build references the
/// Windows platform, and what is being asked about is the pane, not the parse.
/// </summary>
public sealed class LauncherRowNameTests : OwnedViewModels
{
    private readonly Func<string, string?>? _launcherName = FileKind.LauncherName;
    private readonly IReadOnlyList<string> _remoteRoots = ThumbnailLoader.RemoteRoots;

    public override void Dispose()
    {
        FileKind.LauncherName = _launcherName;
        ThumbnailLoader.RemoteRoots = _remoteRoots;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A provider that yields exactly the entries it is given — the
    /// same one <see cref="ConfusableListingTests"/> lists with.</summary>
    private sealed class Canned(IReadOnlyList<FileEntry> entries) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return entries;
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

    private static FileEntry Entry(string folder, string name) =>
        new(name, Path.Combine(folder, name), 1, DateTimeOffset.UnixEpoch, EntryFlags.None);

    /// <summary>
    /// Loads a folder of the given names through a real navigation, so what is
    /// asked about afterwards is the pane a person would be looking at.
    /// </summary>
    private async Task<PaneViewModel> Listing(string folder, params string[] names)
    {
        var shell = Own(new ShellViewModel(new Canned([.. names.Select(n => Entry(folder, n))])));

        shell.Start(null, folder);

        var pane = shell.ActiveTab!;

        for (var i = 0; i < 200 && pane.Entries.Count < names.Length; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal(names.Length, pane.Entries.Count);

        return pane;
    }

    /// <summary>The Name= key, for a stub: everything before the first dot of a
    /// reverse-DNS id, capitalised, which is what these files really do.</summary>
    private static void ReadTheNameKey()
        => FileKind.LauncherName = path =>
        {
            var file = Path.GetFileNameWithoutExtension(path);
            var last = file.Split('.')[^1];

            return last.Length == 0 ? null : char.ToUpperInvariant(last[0]) + last[1..];
        };

    // ---- typing reaches what the row says -----------------------------------

    /// <summary>
    /// **The row said Dolphin and D reached nothing.** Type-ahead matched the
    /// file name while the row drew the launcher's own, so in
    /// /usr/share/applications — the folder this whole change is about —
    /// pressing D found no row and pressing O found the one labelled Dolphin.
    /// The method's own rule is that a row you can see and cannot reach by
    /// typing its name is worse than one that is not there.
    /// </summary>
    [AvaloniaFact]
    public async Task Typing_reaches_a_launcher_by_the_name_it_shows()
    {
        ReadTheNameKey();

        // The decoy sorts first, so a miss leaves the selection on IT rather
        // than on the row being asked about — without which a match that never
        // happened would be indistinguishable from one that did.
        var pane = await Listing(
            Path.Combine(Path.GetTempPath(), "vaktari-launcher-typing"),
            "aardvark.txt", "org.kde.dolphin.desktop");

        Assert.NotEqual("org.kde.dolphin.desktop", pane.SelectedEntry?.Name);

        pane.TypeAhead("D");

        Assert.Equal("org.kde.dolphin.desktop", pane.SelectedEntry?.Name);
    }

    /// <summary>
    /// And the file name still reaches it, so nothing that used to be typeable
    /// stopped being — which is the difference between adding a second
    /// comparison and swapping the first one out.
    /// </summary>
    [AvaloniaFact]
    public async Task Typing_still_reaches_a_launcher_by_its_file_name()
    {
        ReadTheNameKey();

        var pane = await Listing(
            Path.Combine(Path.GetTempPath(), "vaktari-launcher-typing-file"),
            "aardvark.txt", "org.kde.dolphin.desktop");

        Assert.NotEqual("org.kde.dolphin.desktop", pane.SelectedEntry?.Name);

        pane.TypeAhead("o");
        pane.TypeAhead("r");
        pane.TypeAhead("g");

        Assert.Equal("org.kde.dolphin.desktop", pane.SelectedEntry?.Name);
    }

    // ---- and two rows that draw the same thing say so ------------------------

    /// <summary>
    /// **Two rows drawing the single word "Dolphin", with nothing to tell them
    /// apart.** The look-alike set was keyed on the file name, which in one
    /// directory is unique — so before a launcher drew its Name= key this could
    /// not fire for two of them at all, and afterwards it still did not, while
    /// the screen showed the exact collision the mark exists to explain.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_launchers_that_draw_the_same_name_are_marked()
    {
        ReadTheNameKey();

        var folder = Path.Combine(Path.GetTempPath(), "vaktari-launcher-lookalike");

        var pane = await Listing(
            folder, "org.kde.dolphin.desktop", "dolphin.desktop", "readme.txt");

        Assert.Contains(Path.Combine(folder, "org.kde.dolphin.desktop"), pane.Confusable);
        Assert.Contains(Path.Combine(folder, "dolphin.desktop"), pane.Confusable);
        Assert.DoesNotContain(Path.Combine(folder, "readme.txt"), pane.Confusable);
    }

    /// <summary>
    /// **And the chip's own words had to stop promising a difference.** It said
    /// "Another name here differs only by spacing or capitals", which was true
    /// of every pair it could reach while the set was keyed on file names. Two
    /// launchers drawing the same Name= do not differ at all, so on the rows
    /// this change added the sentence would have been false — and a mark whose
    /// explanation is wrong is worse than no mark.
    /// </summary>
    [AvaloniaFact]
    public void The_lookalike_chip_does_not_promise_a_difference()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        Assert.Contains("Look-alike", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("differs only by", markup, StringComparison.Ordinal);
    }

    // ---- and none of it happens over a wire ---------------------------------

    /// <summary>
    /// **The only thing in the row pipeline that opens a file, and it runs on
    /// the UI thread.** Measured against Vaktari.Linux: 97 µs for the first ask
    /// about one launcher, and 0.1 µs for every ask after it — the same as a
    /// row that is not a launcher. A fair price on a local disk, and a round
    /// trip each on an sshfs or gvfs mount. So a remote launcher keeps its file
    /// name, which is what the row showed before any of this existed.
    /// </summary>
    [AvaloniaFact]
    public void A_launcher_on_a_remote_mount_keeps_its_file_name()
    {
        FileKind.LauncherName = _ => "Konsole";
        ThumbnailLoader.RemoteRoots = [@"Z:\"];

        WindowServices.KeepLauncherNamesOffTheWire();

        Assert.Equal("org.kde.konsole.desktop", FileKind.DisplayName(Entry(@"Z:\apps", "org.kde.konsole.desktop")));
    }

    /// <summary>The other half, or the guard above would be indistinguishable
    /// from having removed the reader.</summary>
    [AvaloniaFact]
    public void A_launcher_on_the_local_disk_still_gives_its_own_name()
    {
        FileKind.LauncherName = _ => "Konsole";
        ThumbnailLoader.RemoteRoots = [@"Z:\"];

        WindowServices.KeepLauncherNamesOffTheWire();

        Assert.Equal("Konsole", FileKind.DisplayName(Entry(@"C:\apps", "org.kde.konsole.desktop")));
    }

    /// <summary>
    /// The wiring, which no unit can be asked about: the wrap has to be put on
    /// once, where the platform that fills the seam in was chosen.
    /// </summary>
    [AvaloniaFact]
    public void The_window_services_put_the_guard_on()
        => Assert.Contains(
            "KeepLauncherNamesOffTheWire();",
            RepoSource.Ui("WindowServices.cs"),
            StringComparison.Ordinal);
}
