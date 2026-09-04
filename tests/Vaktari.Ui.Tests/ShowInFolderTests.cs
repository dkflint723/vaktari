using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// "Show this file where it lives", which is what another application asks for
/// when you press its download button's Open Containing Folder.
///
/// **The difference from opening the folder is the whole feature.** Opening it
/// and selecting nothing does not answer "which one did I just save" in a
/// Downloads folder of four hundred files — and nothing in the MIME system can
/// express "and select this", which is why the request comes over the bus at
/// all.
/// </summary>
public sealed class ShowInFolderTests : OwnedViewModels
{
    /// <summary>Hands back a fixed listing, so what gets selected is the view
    /// model's arithmetic and not the disk's.</summary>
    private sealed class Canned(params string[] names) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield return [.. names.Select(n => new FileEntry(
                n, Path.Combine(path, n), 1, DateTimeOffset.UnixEpoch, EntryFlags.None))];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(new FileEntry(
                Path.GetFileName(path), path, 1, DateTimeOffset.UnixEpoch, EntryFlags.None));

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private static string In(string name) => Path.Combine(Path.GetTempPath(), name);

    private ShellViewModel Shell(params string[] names)
    {
        var shell = Own(new ShellViewModel(new Canned(names)));
        shell.Start(null, Path.GetTempPath());

        return shell;
    }

    /// <summary>
    /// The point of it: the folder is opened AND the item is lit.
    /// </summary>
    [AvaloniaFact]
    public async Task An_item_is_shown_selected_in_the_folder_holding_it()
    {
        var shell = Shell("a.txt", "b.txt");

        await shell.ShowAsync([In("b.txt")]);

        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                     shell.ActiveTab!.CurrentPath.TrimEnd(Path.DirectorySeparatorChar));

        Assert.Equal("b.txt", shell.ActiveTab.SelectedEntry?.Name);
    }

    /// <summary>
    /// **Grouped by folder, not one tab per item.** "Show these four downloads"
    /// is one place with four things lit — and every reveal navigates, so two
    /// items in one folder loaded it twice and the second selection cleared the
    /// first, landing with only the last one lit.
    /// </summary>
    [AvaloniaFact]
    public async Task Several_items_in_one_folder_are_all_lit_in_one_tab()
    {
        var shell = Shell("a.txt", "b.txt", "c.txt");
        var before = shell.ActiveGroup.Tabs.Count;

        await shell.ShowAsync([In("a.txt"), In("c.txt")]);

        Assert.Equal(before, shell.ActiveGroup.Tabs.Count);

        Assert.Equal(
            ["a.txt", "c.txt"],
            shell.ActiveTab!.SelectedEntries.Select(e => e.Name).Order());
    }

    /// <summary>
    /// A FOLDER asked about by ShowItems is selected in its parent rather than
    /// entered — the one difference from the search reveal, and the reason
    /// ShowAsync exists beside it. Entering it would put you inside the very
    /// folder you were being shown, with the folder itself off screen.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_is_shown_in_its_parent_rather_than_entered()
    {
        var shell = Shell("Reports");

        await shell.ShowAsync([In("Reports")]);

        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                     shell.ActiveTab!.CurrentPath.TrimEnd(Path.DirectorySeparatorChar));

        Assert.Equal("Reports", shell.ActiveTab.SelectedEntry?.Name);
    }

    /// <summary>
    /// Items in two different folders open two tabs, and the FIRST folder named
    /// is the one left in front.
    ///
    /// Setting the active tab inside the loop leaves the LAST one showing,
    /// which is the opposite of what the insertion order is kept for — and
    /// nothing else here can tell the two apart, because every other case has
    /// one folder.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_folders_open_two_tabs_and_the_first_is_left_in_front()
    {
        var shell = Shell("a.txt");
        var before = shell.ActiveGroup.Tabs.Count;

        var first = Path.Combine(Path.GetTempPath(), "one");
        var second = Path.Combine(Path.GetTempPath(), "two");

        await shell.ShowAsync(
            [Path.Combine(first, "a.txt"), Path.Combine(second, "a.txt")]);

        Assert.Equal(before + 2, shell.ActiveGroup.Tabs.Count);

        Assert.Equal(
            first.TrimEnd(Path.DirectorySeparatorChar),
            shell.ActiveTab!.CurrentPath.TrimEnd(Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// A filesystem root has no parent to be shown in, and opening the root
    /// itself would answer a question that was not asked — that question is
    /// ShowFolders.
    /// </summary>
    [AvaloniaFact]
    public async Task A_root_has_no_folder_to_be_shown_in()
    {
        var shell = Shell("a.txt");
        var before = shell.ActiveGroup.Tabs.Count;

        await shell.ShowAsync([Path.GetPathRoot(Path.GetTempPath())!]);

        Assert.Equal(before, shell.ActiveGroup.Tabs.Count);
    }

    [AvaloniaFact]
    public async Task Nothing_asked_for_is_nothing_done()
    {
        var shell = Shell("a.txt");
        var before = shell.ActiveGroup.Tabs.Count;

        await shell.ShowAsync([]);

        Assert.Equal(before, shell.ActiveGroup.Tabs.Count);
    }

    // ---- and the window routes it there ------------------------------------

    /// <summary>
    /// **The half no view-model test can see.** Everything above calls
    /// ShowAsync directly, so all of it passes with Items wired to OpenPaths —
    /// which opens the folder and selects nothing, precisely the failure this
    /// feature exists to fix.
    /// </summary>
    [Fact]
    public void The_window_asks_the_shell_to_show_them()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "internal async void OnShowRequested(ShowRequest request)");

        var items = body.IndexOf("case ShowKind.Items:", StringComparison.Ordinal);
        var folders = body.IndexOf("case ShowKind.Folders:", StringComparison.Ordinal);

        Assert.True(items >= 0 && folders > items, "the three verbs are not routed here");

        // Items reaches ShowAsync, and does so BEFORE the Folders arm — which
        // is the one that legitimately opens without selecting.
        var shows = body.IndexOf("_shell.ShowAsync(request.Paths)", StringComparison.Ordinal);

        Assert.True(shows > items && shows < folders,
                    "ShowItems does not reach ShowAsync, so it opens the folder and "
                    + "selects nothing");
    }

    /// <summary>
    /// Only the instance that owns the single-instance lock answers for the
    /// desktop. A window opened by an instance that LOST the lock is a
    /// temporary second copy, and one claiming a desktop-wide role would take
    /// "show in folder" with it for as long as it lived.
    /// </summary>
    [Fact]
    public void Only_the_instance_that_owns_the_lock_answers_for_the_desktop()
        => Assert.Contains(
            "Program.Instance is not null && platform.FileManagerService is { } fileManager",
            RepoSource.Ui("MainWindow.axaml.cs"),
            StringComparison.Ordinal);
}
