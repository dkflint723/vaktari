using Vaktari.Ui.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Reading what a drop is carrying.
///
/// **A drop that cannot be taken used to do nothing at all.** That is
/// indistinguishable from one that missed the pane, or from a bug — and
/// dragging out of a zip opened in Explorer does exactly that, which reads as
/// the application being unreliable rather than as a limit of what it is given.
///
/// The limit is real and worth stating: Windows offers the contents of a zip as
/// a descriptor plus one stream per item, retrieved by index from the native
/// data object. Avalonia hands a drop handler formats and bytes and does not
/// expose that object, so there is no route to the contents from here. What
/// there is a route to is recognising the case and saying so.
///
/// Against the decision rather than the whole reader: Avalonia's storage items
/// are explicitly not implementable outside the framework, so a test that needs
/// one cannot exist. Splitting the two lines that produce paths from the
/// reasoning about them is what keeps the reasoning testable at all.
/// </summary>
public sealed class DroppedFileTests
{
    private static readonly string Destination =
        OperatingSystem.IsWindows() ? @"C:\dest" : "/dest";

    private static readonly string Elsewhere =
        OperatingSystem.IsWindows() ? @"C:\from" : "/from";

    private static string At(string folder, string name) => Path.Combine(folder, name);

    private static DroppedFiles Read(string[] paths, params string[] formats) =>
        DroppedFileReader.Decide(paths, formats, Destination, copying: false);

    private static DroppedFiles Copying(params string[] paths) =>
        DroppedFileReader.Decide(paths, ["File"], Destination, copying: true);

    [Fact]
    public void Ordinary_files_come_through()
    {
        var dropped = Read([At(Elsewhere, "a.txt"), At(Elsewhere, "b.txt")], "File");

        Assert.True(dropped.Any);
        Assert.Equal(2, dropped.Paths.Count);
        Assert.Empty(dropped.Refusal);
    }

    /// <summary>
    /// **The zip case, which is what "unreliable" actually was.** Explorer
    /// offers the contents of an archive as virtual files: real names, no
    /// paths. The drop carried something, so "there are no files in that" would
    /// be wrong; what it carried cannot be copied, so pretending otherwise
    /// would be worse.
    /// </summary>
    [Theory]
    [InlineData("FileGroupDescriptorW")]
    [InlineData("FileGroupDescriptor")]
    [InlineData("FileContents")]
    public void Files_inside_an_archive_are_refused_with_a_reason(string format)
    {
        var dropped = Read([], format);

        Assert.False(dropped.Any);
        Assert.Contains("inside an archive", dropped.Refusal, StringComparison.Ordinal);
        Assert.Contains("extract them first", dropped.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file dropped into the folder it already lives in achieves nothing
    /// whether copying or moving — but it is not a failure, and saying "there
    /// are no files in that" about a perfectly good file would be a lie.
    /// </summary>
    [Fact]
    public void A_file_dropped_where_it_already_is_says_so()
    {
        var dropped = Read([At(Destination, "a.txt")], "File");

        Assert.False(dropped.Any);
        Assert.Equal("that is already here", dropped.Refusal);
    }

    /// <summary>The folder itself, dropped onto its own listing — or onto its
    /// own row in a selection of one, which is the same two paths and deserves
    /// the same answer. **This said "that is already here"**, which is the
    /// wording for a file that is going nowhere; a folder handed itself as its
    /// destination is a different refusal and now says which.</summary>
    [Fact]
    public void A_folder_dropped_onto_itself_says_so()
    {
        Assert.Equal(
            "a folder cannot be moved into itself", Read([Destination], "File").Refusal);
    }

    /// <summary>
    /// **Half a selection used to be swallowed by the other half.** Dragging A,
    /// B and C onto A filtered A out of the list — it is the destination — and
    /// moved B and C inside it, which is not a smaller version of what was
    /// asked for but a different thing entirely. A six-pixel twitch over a
    /// selected folder was enough to start it, and the cursor showed an
    /// ordinary move throughout. The engine refuses this by name and never saw
    /// it, because the reader had already removed the source it looks for.
    /// </summary>
    [Fact]
    public void A_selection_dropped_onto_one_of_its_own_folders_is_refused()
    {
        // Beside the destination, which is where the siblings of a folder you
        // can drop onto actually are — and is what makes them survive the
        // already-here filter and reach the paste.
        var beside = Path.GetDirectoryName(Destination)!;

        var dropped = DroppedFileReader.Decide(
            [Destination, At(beside, "b.txt"), At(beside, "c.txt")],
            ["File"], Destination, copying: false);

        Assert.Empty(dropped.Paths);
        Assert.Equal("a folder cannot be moved into itself", dropped.Refusal);
    }

    /// <summary>The same drag with Ctrl held, which takes the other branch of
    /// the copy-or-move rule a line below: a copy of B into A is no more what
    /// was asked for than a move of it.</summary>
    [Fact]
    public void A_selection_copied_onto_one_of_its_own_folders_is_refused()
    {
        var beside = Path.GetDirectoryName(Destination)!;

        var dropped = DroppedFileReader.Decide(
            [Destination, At(beside, "b.txt")], ["File"], Destination, copying: true);

        Assert.Empty(dropped.Paths);
        Assert.Equal("a folder cannot be copied into itself", dropped.Refusal);
    }

    /// <summary>
    /// **A "create shortcut here" drag is refused with the rest.** Ctrl+Shift
    /// onto a folder in the selection made shortcuts to the others and quietly
    /// left one out; it now does nothing at all, and says a copy was refused
    /// when no copy was asked for. Nothing is lost or half-done either way —
    /// shortcuts only add — so this is written down rather than worked around:
    /// Decide is handed copy-or-move and is never told the third intent
    /// exists. Restoring it is a change to what the handlers ask.
    /// </summary>
    [Fact]
    public void A_shortcut_drag_onto_a_selected_folder_is_refused_too()
    {
        var beside = Path.GetDirectoryName(Destination)!;

        // What OnDrop asks for a Ctrl+Shift drag: intent Link, so move is
        // false, so copying is true. MainWindow.axaml.cs, Read(..., !move).
        var dropped = DroppedFileReader.Decide(
            [Destination, At(beside, "b.txt")], ["File"], Destination, copying: true);

        Assert.Empty(dropped.Paths);
    }

    /// <summary>
    /// **Not a test of this fix — a guard against it rotting.** This passes on
    /// the broken code too, and says so deliberately: OnDragOver is not being
    /// changed, and what makes the refusal reach the cursor is that DragOver
    /// already asks the reader the same question the drop asks. There is no
    /// seam to see the effect itself from — a DragEventArgs cannot be built
    /// outside the framework — so the call site is read instead, and the day
    /// somebody decides the effect from the raw paths again, this says so.
    /// </summary>
    [Fact]
    public void The_cursor_asks_the_reader_what_the_drop_asks()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"), "private void OnDragOver(");

        Assert.Contains(".Read(e.DataTransfer, destination,", body, StringComparison.Ordinal);
        Assert.Contains("if (!takeable)", body, StringComparison.Ordinal);
    }

    /// <summary>Some of them usable is a drop worth taking, and the ones
    /// already here are quietly left out rather than duplicated.</summary>
    [Fact]
    public void A_mixed_drop_takes_the_ones_that_would_move()
    {
        var dropped = Read([At(Destination, "here.txt"), At(Elsewhere, "new.txt")], "File");

        Assert.True(dropped.Any);
        Assert.Equal(At(Elsewhere, "new.txt"), Assert.Single(dropped.Paths));
    }

    /// <summary>
    /// **Ctrl+drag onto the folder a file is already in makes a copy.** That is
    /// how Explorer duplicates, and Vaktari discarded those paths whatever the
    /// gesture meant — so the drag reported "that is already here" and did
    /// nothing. Which key is held has to be decided before the filtering, not
    /// after.
    /// </summary>
    [Fact]
    public void Copying_onto_the_same_folder_duplicates_rather_than_refusing()
    {
        var dropped = Copying(At(Destination, "a.txt"));

        Assert.True(dropped.Any);
        Assert.Equal(At(Destination, "a.txt"), Assert.Single(dropped.Paths));
    }

    /// <summary>But a folder still cannot be copied into itself, whichever key
    /// is held: the destination would be inside the thing being read.</summary>
    [Fact]
    public void A_folder_cannot_be_copied_into_itself()
    {
        var dropped = Copying(Destination);

        Assert.False(dropped.Any);
        Assert.Equal("a folder cannot be copied into itself", dropped.Refusal);
    }

    [Fact]
    public void A_drop_with_no_files_at_all_says_that_instead()
    {
        Assert.Equal("there are no files in that", Read([], "Text").Refusal);
        Assert.Equal("that drop carried nothing", Read([]).Refusal);
    }

    /// <summary>Every refusal says something. A silent one is the bug.</summary>
    [Fact]
    public void Every_refusal_carries_a_reason()
    {
        foreach (var dropped in new[]
                 {
                     Read([]),
                     Read([], "Text"),
                     Read([], "FileGroupDescriptorW"),
                     Read([At(Destination, "a.txt")], "File"),
                 })
        {
            Assert.False(dropped.Any);
            Assert.NotEmpty(dropped.Refusal);
        }
    }

    /// <summary>
    /// **A folder cannot be dropped into something inside itself.** The check
    /// tested equality while the comment above it said containment, so
    /// dropping A onto A was refused and dropping A into A/sub went ahead —
    /// which is the case that actually goes wrong, because the destination is
    /// then inside the thing being read.
    /// </summary>
    [Fact]
    public void A_folder_cannot_be_dropped_into_its_own_subfolder()
    {
        var parent = OperatingSystem.IsWindows() ? @"C:\dest" : "/dest";
        var ancestor = Path.GetDirectoryName(parent)!;

        // Dropping the folder that CONTAINS the destination, into it.
        var dropped = DroppedFileReader.Decide(
            [parent], ["File"], Path.Combine(parent, "sub"), copying: true);

        Assert.Empty(dropped.Paths);
        Assert.Contains("itself", dropped.Refusal);
    }

    [Fact]
    public void An_unrelated_folder_whose_name_merely_starts_the_same_is_allowed()
    {
        // "dest" must not claim "destination": a name prefix is not containment.
        var root = OperatingSystem.IsWindows() ? @"C:\" : "/";

        var dropped = DroppedFileReader.Decide(
            [Path.Combine(root, "destination")], ["File"], Destination, copying: true);

        Assert.Single(dropped.Paths);
    }
}
