using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Vaktari.Ui.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The behaviours Windows taught people, which an application is judged against
/// whether or not it set out to imitate one.
///
/// Each of these is invisible until it is missing, and then it reads as the
/// application being wrong rather than as a feature nobody wrote.
/// </summary>
public sealed class ExplorerConventionTests
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
    /// **Two names for the same habit.** Which of these somebody reaches for
    /// depends on where they learned it: Explorer answers Alt+D and Ctrl+E,
    /// browsers answer Ctrl+L and Ctrl+F, and an application that answers only
    /// one half feels broken to whoever learned the other.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+L", "BeginEditPath")]
    [InlineData("Alt+D", "BeginEditPath")]
    [InlineData("Ctrl+F", "FocusSearch")]
    [InlineData("Ctrl+E", "FocusSearch")]
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
        var markup = File.ReadAllText(MarkupPath("MainWindow.axaml"));

        Assert.Contains($"Gesture=\"{gesture}\"", markup, StringComparison.Ordinal);

        // The binding and the gesture on one line, so a gesture pointing at the
        // wrong command counts as missing rather than as present.
        var line = markup
            .Split('\n')
            .FirstOrDefault(l => l.Contains($"Gesture=\"{gesture}\"", StringComparison.Ordinal)
                                 && l.Contains("KeyBinding", StringComparison.Ordinal));

        Assert.NotNull(line);
        Assert.Contains(command, line!, StringComparison.Ordinal);
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
