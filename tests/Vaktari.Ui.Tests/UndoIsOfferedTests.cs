using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Offering the undo, and saying what it did.
///
/// **It was a key and nothing else.** No menu anywhere in the application had
/// an Undo row, so the feature was invisible to anyone who had not read the
/// shortcuts sheet — and pressing Ctrl+Z said nothing about what it was going
/// to take back, so after a copy, a rename and a delete in quick succession the
/// only way to find out which one came back was to press it and look.
/// </summary>
public sealed class UndoIsOfferedTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace In = "clr-namespace:Vaktari.Ui.Input";

    private (PaneViewModel Pane, History Ops) Pane()
    {
        var ops = new History();

        return (Own(new PaneViewModel(new Silent(), ops)), ops);
    }

    /// <summary>Nothing done, so there is nothing for the row to offer — the
    /// background menu hides it on this answer.</summary>
    [AvaloniaFact]
    public void With_nothing_to_take_back_the_row_says_so()
    {
        var (pane, _) = Pane();

        pane.RefreshUndoState();

        Assert.False(pane.CanUndo);
        Assert.Equal("Undo", pane.UndoLabel);
    }

    /// <summary>
    /// And once something has happened it names it. Written as a transition
    /// rather than a bare assertion on a fresh pane: the field starts false, so
    /// "it is false here" is satisfied by a refresh that does nothing at all.
    /// </summary>
    [AvaloniaFact]
    public void Once_something_has_happened_the_row_names_it()
    {
        var (pane, ops) = Pane();

        pane.RefreshUndoState();

        Assert.False(pane.CanUndo);

        ops.Undoable = "copy of 3 items";
        pane.RefreshUndoState();

        Assert.True(pane.CanUndo);
        Assert.Equal("Undo copy of 3 items", pane.UndoLabel);
    }

    /// <summary>The redo row is live only when something has been undone.</summary>
    [AvaloniaFact]
    public void The_redo_row_is_live_only_when_something_was_undone()
    {
        var (pane, ops) = Pane();

        pane.RefreshUndoState();

        Assert.False(pane.CanRedo);
        Assert.Equal("Redo", pane.RedoLabel);

        ops.Redoable = "move of 3 items";
        pane.RefreshUndoState();

        Assert.True(pane.CanRedo);
        Assert.Equal("Redo move of 3 items", pane.RedoLabel);
    }

    /// <summary>An undo says what it took back.</summary>
    [AvaloniaFact]
    public async Task An_undo_says_what_it_took_back()
    {
        var (pane, ops) = Pane();

        ops.Undoable = "copy of 3 items";

        await pane.UndoAsync();

        Assert.Equal("undid copy of 3 items", pane.Status);
    }

    /// <summary>And a redo says what it put back.</summary>
    [AvaloniaFact]
    public async Task A_redo_says_what_it_put_back()
    {
        var (pane, ops) = Pane();

        ops.Redoable = "move of 3 items";

        await pane.RedoAsync();

        Assert.Equal("redid move of 3 items", pane.Status);
    }

    /// <summary>
    /// **Read before the work, because the work is what removes it.** The
    /// description is the top of the undo stack and undoing pops it, so a read
    /// after the await names whatever is underneath — or nothing at all.
    /// </summary>
    [AvaloniaFact]
    public async Task It_names_what_it_undid_and_not_what_is_left()
    {
        var (pane, ops) = Pane();

        ops.Undoable = "copy of 3 items";
        ops.UnderneathIt = "rename of readme.txt";

        await pane.UndoAsync();

        Assert.Equal("undid copy of 3 items", pane.Status);
    }

    /// <summary>
    /// **Said after the reload, not before it.** Every one of these ends by
    /// refreshing the listing, and a finished listing clears the status line —
    /// deliberately, so the item count does not appear twice — so a message
    /// written first lives for as long as the reload takes and is then blanked.
    /// </summary>
    [Fact]
    public void The_report_comes_after_the_refresh()
    {
        foreach (var method in new[] { "public async Task UndoAsync()", "public async Task RedoAsync()" })
        {
            var body = RepoSource.Body(
                RepoSource.Ui("ViewModels", "PaneViewModel.Operations.cs"), method);

            var refresh = body.IndexOf("await RefreshAsync()", StringComparison.Ordinal);
            var say = body.IndexOf("await SayAsync(", StringComparison.Ordinal);

            Assert.True(refresh >= 0, $"{method} no longer refreshes the way this test looks for");
            Assert.True(say > refresh,
                        $"{method} writes its report before the reload that erases it");
        }
    }

    /// <summary>
    /// The rows exist, on the background menu, and they are the commands they
    /// claim to be.
    ///
    /// **Shown only when there is something to take back**, which reverses
    /// the decision this test used to pin — "always shown, disabled when there
    /// is nothing", on the argument that a row which disappears is a row nobody
    /// learns is there. The maintainer overruled it when the listing menu was
    /// split: two grey rows on most openings of a menu opened a dozen times an
    /// hour were part of what made it large, and a row that appears with its
    /// label at the moment there is something to undo is found at exactly that
    /// moment. ListingMenusTests watches both rows come and go on a real
    /// window.
    ///
    /// On the background menu and nowhere else: what they take back is the last
    /// operation, which is rarely about the row under the pointer.
    ///
    /// Parsed rather than grepped: "Undo" appears in several bindings and a
    /// substring search cannot tell a menu row from a command name.
    /// </summary>
    [Fact]
    public void The_menu_offers_both()
    {
        var rows = ListingMenus.Markup(ListingMenus.Background)
            .Elements(Avalonia + "MenuItem")
            .Where(e => (string?)e.Attribute("Command") is { } c
                        && (c.Contains("UndoCommand") || c.Contains("RedoCommand")))
            .ToList();

        Assert.Equal(2, rows.Count);

        foreach (var row in rows)
        {
            // The label names what will happen, and the row is there only
            // when something will. A TextBlock rather than a header string:
            // the label is data, and a string header would read its first
            // underscore as an access key (ListingMenusTests has the case).
            Assert.Null(row.Attribute("Header"));
            var header = Assert.Single(row.Elements(Avalonia + "MenuItem.Header"));
            Assert.Contains("Label", (string?)Assert.Single(header.Elements(Avalonia + "TextBlock")).Attribute("Text") ?? "");
            Assert.Contains("ActiveTab.Can", (string?)row.Attribute("IsVisible") ?? "");
            Assert.Null(row.Attribute("IsEnabled"));
            Assert.Contains((string?)row.Attribute(In + "KeyHint.Command"), new[] { "Undo", "Redo" });
        }

        Assert.DoesNotContain(
            ListingMenus.Markup(ListingMenus.Item).Descendants(Avalonia + "MenuItem"),
            e => ((string?)e.Attribute("Command") ?? "").Contains("UndoCommand"));
    }

    /// <summary>
    /// **And they bring no separator of their own.** Avalonia draws every
    /// separator it is given and collapses none of them, so a rule of theirs
    /// would be a line on its own whenever both are hidden — which is now most
    /// of the time. They sit in Paste's block, under the rule every block has;
    /// the menu decides that rule as it opens, from what is drawn.
    /// </summary>
    [Fact]
    public void They_bring_no_rule_of_their_own()
    {
        var undo = Assert.Single(
            XDocument.Parse(RepoSource.Ui("MainWindow.axaml")).Descendants(Avalonia + "MenuItem"),
            e => ((string?)e.Attribute("Command") ?? "").Contains("UndoCommand"));

        // What sits directly above the pair. An ungated rule there is the one
        // that can lead the menu.
        var above = undo.ElementsBeforeSelf().LastOrDefault();

        Assert.NotNull(above);

        Assert.False(
            above!.Name.LocalName == "Separator" && above.Attribute("IsVisible") is null,
            "an ungated separator above these rows is the first item in the menu "
            + "whenever everything above it is hidden");

        // And nothing between the two of them either.
        var next = undo.ElementsAfterSelf().FirstOrDefault();

        Assert.Equal("MenuItem", next?.Name.LocalName);
        Assert.Contains("RedoCommand", (string?)next!.Attribute("Command") ?? "");
    }

    /// <summary>
    /// An engine whose history is whatever the test says it is.
    /// </summary>
    private sealed class History : IFileOperations
    {
        public string? Undoable { get; set; }
        public string? Redoable { get; set; }

        /// <summary>What the stack holds under the top one, so a read taken
        /// after the work names something different from a read taken before
        /// it.</summary>
        public string? UnderneathIt { get; set; }

        public bool CanUndo => Undoable is not null;
        public bool CanRedo => Redoable is not null;

        public string? UndoDescription => Undoable;
        public string? RedoDescription => Redoable;

        public ValueTask UndoAsync(CancellationToken ct)
        {
            Redoable = Undoable;
            Undoable = UnderneathIt;
            UnderneathIt = null;

            return ValueTask.CompletedTask;
        }

        public ValueTask RedoAsync(CancellationToken ct)
        {
            Undoable = Redoable;
            Redoable = null;

            return ValueTask.CompletedTask;
        }

        public IOperationHandle Copy(
            IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => throw new NotSupportedException();

        public IOperationHandle Move(
            IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => throw new NotSupportedException();

        public IOperationHandle Delete(IReadOnlyList<string> paths)
            => throw new NotSupportedException();

        public IOperationHandle Trash(IReadOnlyList<string> paths)
            => throw new NotSupportedException();

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
            => ValueTask.CompletedTask;

        public void RecordCreation(string path) { }

        /// <summary>The history here is whatever the test says it is, and no
        /// test says anything about grouping.</summary>
        public IUndoGroup? BeginRenameGroup() => null;
    }

    private sealed class Silent : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
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
