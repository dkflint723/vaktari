using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// "Open in new tab", with more than one folder chosen.
///
/// **Five folders selected opened one tab.** The command's parameter is a
/// single row — the focused one — so the entry quietly dropped every folder but
/// that one, and said nothing about the rest. The same shape as Enter opening
/// one of five files, which EntriesToActOn was already written to answer.
///
/// And the row itself hid whenever the FOCUSED row was not a folder, so
/// clicking a file and then ctrl-clicking two folders took away the very entry
/// that would open both.
/// </summary>
public sealed class OpenInNewTabTests : OwnedViewModels
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

    private static FileEntry Folder(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 0,
               DateTimeOffset.UnixEpoch, EntryFlags.Directory);

    private static FileEntry File(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);

    private ShellViewModel Shell()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        return shell;
    }

    private static int Tabs(ShellViewModel shell) => shell.ActiveGroup.Tabs.Count;

    /// <summary>The finding itself.</summary>
    [AvaloniaFact]
    public void Three_folders_chosen_opens_three_tabs()
    {
        var shell = Shell();
        var pane = shell.ActiveTab!;
        var before = Tabs(shell);

        foreach (var name in new[] { "one", "two", "three" })
            pane.SelectedEntries.Add(Folder(name));

        shell.OpenInNewTabCommand.Execute(pane.SelectedEntries[0]);

        Assert.Equal(before + 3, Tabs(shell));
    }

    /// <summary>
    /// Folders only, the mirror of Enter — which launches the files and leaves
    /// the folders alone, because there is no navigating into five at once.
    /// </summary>
    [AvaloniaFact]
    public void And_the_files_among_them_are_left_alone()
    {
        var shell = Shell();
        var pane = shell.ActiveTab!;
        var before = Tabs(shell);

        pane.SelectedEntries.Add(Folder("one"));
        pane.SelectedEntries.Add(File("a.txt"));
        pane.SelectedEntries.Add(Folder("two"));

        shell.OpenInNewTabCommand.Execute(null);

        Assert.Equal(before + 2, Tabs(shell));
    }

    /// <summary>
    /// The tab you are in stays the tab you are in — which is the whole reason
    /// for asking for a new one rather than opening the folder.
    /// </summary>
    [AvaloniaFact]
    public void And_none_of_them_steals_the_foreground()
    {
        var shell = Shell();
        var pane = shell.ActiveTab!;

        pane.SelectedEntries.Add(Folder("one"));
        pane.SelectedEntries.Add(Folder("two"));

        shell.OpenInNewTabCommand.Execute(null);

        Assert.Same(pane, shell.ActiveTab);
    }

    /// <summary>
    /// With nothing selected the parameter is all there is, and it still has to
    /// work — that is the route from a right-click on a single unselected row.
    /// </summary>
    [AvaloniaFact]
    public void One_handed_over_with_nothing_selected_still_opens()
    {
        var shell = Shell();
        var before = Tabs(shell);

        shell.OpenInNewTabCommand.Execute(Folder("alone"));

        Assert.Equal(before + 1, Tabs(shell));
    }

    /// <summary>
    /// The bound Enter already obeys. Ctrl+A in a folder of four hundred
    /// subfolders is four hundred tabs, and it says so rather than doing
    /// nothing — the fix must not turn one dropped folder into a wall of tabs.
    /// </summary>
    [AvaloniaFact]
    public void Far_too_many_is_refused_and_says_why()
    {
        var shell = Shell();
        var pane = shell.ActiveTab!;
        var before = Tabs(shell);

        for (var i = 0; i < 40; i++) pane.SelectedEntries.Add(Folder("f" + i));

        shell.OpenInNewTabCommand.Execute(null);

        Assert.Equal(before, Tabs(shell));
        Assert.Contains("40", pane.Status);
        Assert.Contains("select fewer", pane.Status);
    }

    // ---- and the row is there to be clicked --------------------------------

    private PaneViewModel Pane()
    {
        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });
        pane.CurrentPath = Path.GetTempPath();

        return pane;
    }

    /// <summary>
    /// Click a file, then ctrl-click two folders: the focus is still the file,
    /// and the entry that would open both used to disappear.
    /// </summary>
    [AvaloniaFact]
    public void A_folder_anywhere_in_the_selection_offers_the_row()
    {
        var pane = Pane();

        pane.SelectedEntry = File("a.txt");
        pane.SelectedEntries.Add(File("a.txt"));
        pane.SelectedEntries.Add(Folder("one"));

        Assert.False(pane.HasDirectorySelected);
        Assert.True(pane.HasAnyDirectorySelected);
    }

    [AvaloniaFact]
    public void And_a_selection_of_files_alone_does_not()
    {
        var pane = Pane();

        pane.SelectedEntries.Add(File("a.txt"));
        pane.SelectedEntries.Add(File("b.txt"));

        Assert.False(pane.HasAnyDirectorySelected);
    }
}
