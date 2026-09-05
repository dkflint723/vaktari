using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;
using Vaktari.Core.Session;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The behaviours Windows taught people, which an application is judged against
/// whether or not it set out to imitate one.
///
/// Each of these is invisible until it is missing, and then it reads as the
/// application being wrong rather than as a feature nobody wrote.
/// </summary>
public sealed class ExplorerConventionTests : OwnedViewModels
{
    // ---- renaming ----------------------------------------------------------

    /// <summary>
    /// **The name, not the extension.** Press F2 and type, and Vaktari used to
    /// take the .txt with it — every file renamed that way lost its extension
    /// unless the whole name was retyped.
    /// </summary>
    [Theory]
    [InlineData("notes.txt", false, 5)]          // notes
    [InlineData("archive.tar.gz", false, 11)]    // archive.tar — the LAST dot
    [InlineData("README", false, 6)]             // nothing to keep back
    [InlineData(".gitignore", false, 10)]        // a leading dot begins a name
    [InlineData("trailing.", false, 9)]          // nothing after the dot
    [InlineData("v1.2.3", true, 6)]              // a folder is selected whole
    [InlineData("Documents", true, 9)]
    public void A_rename_selects_the_name_and_keeps_the_extension(
        string name, bool isDirectory, int expected)
    {
        Assert.Equal(expected, RenameSelection.LengthFor(name, isDirectory));
    }

    [Fact]
    public void An_empty_name_selects_nothing()
    {
        Assert.Equal(0, RenameSelection.LengthFor("", false));
        Assert.Equal(0, RenameSelection.LengthFor(null, false));
    }

    // ---- middle click ------------------------------------------------------

    /// <summary>
    /// A tab and a listing row both carry a PaneViewModel, which is the point of
    /// the strip — so a middle click has to be told apart by its container, or
    /// clicking a folder would close the tab it was aimed into.
    /// </summary>
    [AvaloniaFact]
    public void A_tab_and_a_row_are_told_apart_by_their_container()
    {
        var strip = new TabStrip { ItemsSource = new[] { "one", "two" } };
        var window = new Window { Content = strip, Width = 300, Height = 200 };

        window.Show();
        window.Measure(new Size(300, 200));
        window.Arrange(new Rect(0, 0, 300, 200));

        var container = strip.ContainerFromIndex(1);

        // The container the strip generates is what the middle-click handler
        // looks for on the way up. If Avalonia ever stops generating this type,
        // closing a tab by middle click stops working silently.
        Assert.IsType<TabStripItem>(container);

        window.Close();
    }

    // ---- dragging ----------------------------------------------------------

    // ---- which way a column sorts the first time it is clicked --------------

    /// <summary>Hands back a fixed listing, so the order under test is the
    /// comparator's and not the disk's.</summary>
    private sealed class Canned(IReadOnlyList<FileEntry> rows) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return rows;
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

    private static FileEntry Row(string name, DateTimeOffset when)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1, when, EntryFlags.None);

    [Theory]
    [InlineData(SortField.Modified, true)]
    [InlineData(SortField.Size, true)]
    [InlineData(SortField.Name, false)]
    [InlineData(SortField.Kind, false)]
    [InlineData(SortField.Created, true)]
    public void A_column_knows_which_way_it_sorts_first(SortField field, bool descending)
        => Assert.Equal(descending, SortDefaults.DescendingFirst(field));

    /// <summary>
    /// **The first click on "modified" put the oldest file on top.** The
    /// download that has just finished is the reason anybody clicks that
    /// heading, and it landed at the very bottom of the folder.
    ///
    /// Asserts the row ORDER, not just the flag: a direction nothing read would
    /// satisfy the flag and leave the listing exactly as it was.
    /// </summary>
    [AvaloniaFact]
    public async Task The_first_click_on_modified_puts_the_newest_file_on_top()
    {
        var now = DateTimeOffset.Now;

        var pane = Own(new PaneViewModel(
            new Canned([Row("old.txt", now.AddDays(-9)), Row("new.txt", now)]))
        {
            ViewportWidth = 1400,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        pane.SortByCommand.Execute("modified");

        Assert.True(pane.SortDescending);
        Assert.Equal("new.txt", pane.Entries[0].Name);

        // The second click still reverses, which is the other half of it.
        pane.SortByCommand.Execute("modified");

        Assert.False(pane.SortDescending);
        Assert.Equal("old.txt", pane.Entries[0].Name);

        // Name is not one of the two. Flipping the default unconditionally
        // passes everything above and fails here.
        pane.SortByCommand.Execute("name");

        Assert.False(pane.SortDescending);
        Assert.Equal("new.txt", pane.Entries[0].Name);
    }

    private static readonly string OnC = OperatingSystem.IsWindows() ? @"C:\work.txt" : "/work/a.txt";
    private static readonly string AlsoC = OperatingSystem.IsWindows() ? @"C:\other" : "/other";
    private static readonly string OnD = OperatingSystem.IsWindows() ? @"D:ackup" : "/mnt/backup";

    /// <summary>
    /// **Windows decides by volume; Vaktari decided by origin.** Explorer moves
    /// within a drive and copies between drives, because a move inside a volume
    /// is effectively free while one across volumes is a copy and a delete.
    /// Dragging onto a place on another disk therefore did something materially
    /// different from what Windows would have done, and said nothing about it.
    /// </summary>
    /// <summary>
    /// **Ctrl+Shift is a chord, not a pair of fallbacks.** Read as two
    /// modifiers it fell through to Ctrl's copy; Explorer has meant "create a
    /// shortcut here" by it for thirty years. It wins over volume and origin
    /// alike — a deliberate chord outranks every default.
    /// </summary>
    [Fact]
    public void Control_and_shift_together_mean_a_shortcut()
    {
        Assert.Equal(
            DragIntent.Link,
            DragEffect.For(control: true, shift: true, internalDrag: true, [OnC], AlsoC));

        Assert.Equal(
            DragIntent.Link,
            DragEffect.For(control: true, shift: true, internalDrag: false, [OnC], OnD));

        // And the chord's halves keep their own meanings.
        Assert.Equal(
            DragIntent.Copy,
            DragEffect.For(control: true, shift: false, internalDrag: true, [OnC], AlsoC));

        Assert.Equal(
            DragIntent.Move,
            DragEffect.For(control: false, shift: true, internalDrag: true, [OnC], OnD));
    }

    [WindowsFact]
    public void An_unmodified_drag_moves_within_a_drive_and_copies_between_them()
    {
        Assert.Equal(
            DragIntent.Move,
            DragEffect.For(false, false, internalDrag: true, [OnC], AlsoC));

        Assert.Equal(
            DragIntent.Copy,
            DragEffect.For(false, false, internalDrag: true, [OnC], OnD));
    }

    /// <summary>A key held down wins outright, as it does everywhere.</summary>
    [Fact]
    public void A_modifier_decides_regardless_of_volume()
    {
        Assert.Equal(DragIntent.Copy, DragEffect.For(true, false, true, [OnC], AlsoC));
        Assert.Equal(DragIntent.Move, DragEffect.For(false, true, true, [OnC], OnD));
    }

    /// <summary>
    /// From another application a plain drag copies: moving would take somebody
    /// else's file away on a gesture that never said so.
    /// </summary>
    [Fact]
    public void A_drag_from_outside_the_application_copies()
    {
        Assert.Equal(DragIntent.Copy, DragEffect.For(false, false, internalDrag: false, [OnC], AlsoC));
    }

    /// <summary>
    /// **Mixed origins copy.** A selection spanning two drives has no single
    /// right answer, and copying is the one that leaves every original where it
    /// was.
    /// </summary>
    [WindowsFact]
    public void A_selection_spanning_two_drives_copies()
    {
        Assert.Equal(
            DragIntent.Copy,
            DragEffect.For(false, false, true, [OnC, @"D:\other.txt"], AlsoC));
    }

    /// <summary>Nothing to reason about is not a licence to move.</summary>
    [Fact]
    public void An_empty_drag_copies()
    {
        Assert.Equal(DragIntent.Copy, DragEffect.For(false, false, true, [], AlsoC));
    }

    // ---- the keyboard ------------------------------------------------------

    /// <summary>
    /// **Which number-pad keys the framework answers for us, and which it does
    /// not.** This is the fact the binding list depends on: Avalonia folds the
    /// pad's arithmetic keys onto the top row's when matching a gesture, so
    /// Ctrl and the pad's plus or minus reach bindings written for OemPlus and
    /// OemMinus — but nothing folds onto D0, so the pad's nought reaches a
    /// binding only if one is written for it by name.
    ///
    /// Asked of Avalonia rather than assumed, because the answer is what
    /// decides how many bindings the markup needs: reading the fold as covering
    /// all three leaves the reset dead, and reading it as covering none adds two
    /// bindings that already work.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+OemPlus", Key.Add, true)]
    [InlineData("Ctrl+OemMinus", Key.Subtract, true)]
    [InlineData("Ctrl+D0", Key.NumPad0, false)]
    [InlineData("Ctrl+NumPad0", Key.NumPad0, true)]
    public void The_pad_reaches_only_the_zoom_keys_the_framework_folds(
        string gesture, Key pressed, bool reaches)
        => Assert.Equal(reaches, KeyGesture.Parse(gesture).Matches(
            new KeyEventArgs { Key = pressed, KeyModifiers = KeyModifiers.Control }));

    /// <summary>
    /// **Two names for the same habit.** Which of these somebody reaches for
    /// depends on where they learned it: Explorer answers Alt+D and Ctrl+E,
    /// browsers answer Ctrl+L and Ctrl+F, and an application that answers only
    /// one half feels broken to whoever learned the other.
    ///
    /// The number pad is the same habit in another place. Avalonia folds the
    /// pad's plus and minus onto OemPlus and OemMinus when matching a gesture,
    /// so those two need no second spelling; it folds nothing onto D0, so the
    /// reset is bound twice.
    ///
    /// **Both spellings of the reset are pinned here because the F1
    /// cross-check cannot tell them apart** — it prints both as "Ctrl+0", so
    /// either binding alone satisfies it and the other could be deleted with a
    /// green suite.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+NumPad0", "ZoomReset")]
    [InlineData("Ctrl+D0", "ZoomReset")]

    // **Pinned here because the F1 cross-check cannot see them.** Its listed
    // side drops any entry containing a space, and these read "Ctrl+Page Down"
    // on the sheet — so the round-trip skips them and they would be deletable
    // with a green suite. The layout keys are here for the other half of the
    // reason: the round-trip only asks whether a gesture is bound at all, never
    // which of the three layouts it selects.
    [InlineData("Ctrl+PageDown", "NextTab")]
    [InlineData("Ctrl+PageUp", "PreviousTab")]
    [InlineData("Ctrl+Shift+D1", "ShowAsDetails")]
    [InlineData("Ctrl+Shift+D2", "ShowAsCompact")]
    [InlineData("Ctrl+Shift+D3", "ShowAsGrid")]
    [InlineData("Ctrl+L", "BeginEditPath")]
    [InlineData("Alt+D", "BeginEditPath")]
    [InlineData("Ctrl+F", "BeginSearch")]
    [InlineData("Ctrl+E", "BeginSearch")]
    [InlineData("F2", "BeginRename")]
    [InlineData("F5", "Refresh")]
    [InlineData("Ctrl+Shift+N", "NewFolder")]
    [InlineData("Ctrl+H", "ToggleHidden")]
    [InlineData("Alt+Up", "GoUp")]
    [InlineData("Alt+Left", "GoBack")]
    [InlineData("Alt+Right", "GoForward")]
    [InlineData("Ctrl+Z", "Undo")]
    [InlineData("Ctrl+Y", "Redo")]
    [InlineData("Ctrl+Shift+Z", "Redo")]
    public void The_expected_shortcut_is_bound(string gesture, string command)
    {
        // Either binding site counts, and the command has to match at whichever
        // one implements it — a gesture pointing at the wrong command is
        // missing rather than present.
        //
        // **Undo and redo live in the second one, and had to.** As markup
        // KeyBindings they were claimed ahead of the focused control, so
        // pressing Ctrl+Z to take back a mistyped character in the address bar
        // reversed the last copy, move or delete on disk instead.
        var site = KeyBindingSites.Markup().TryGetValue(gesture, out var markup)
            ? markup
            : KeyBindingSites.CodeBehind().GetValueOrDefault(gesture);

        Assert.True(site is not null, $"{gesture} is bound nowhere — neither a Window "
                                      + "KeyBinding nor a case in OnWindowKeyDown.");

        Assert.Contains(command, site!, StringComparison.Ordinal);
    }

    private static string MarkupPath(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Vaktari.Ui", name);

            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"could not find {name} above {AppContext.BaseDirectory}");
    }
}
