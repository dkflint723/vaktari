using Avalonia.Threading;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Type-ahead: typing letters jumps to the next matching row, the way every
/// file manager has behaved since before they had search boxes.
/// </summary>
public sealed partial class PaneViewModel
{
    // ---- type-ahead -------------------------------------------------------

    private string _typeAhead = "";

    private DateTime _typeAheadAt;

    /// <summary>
    /// True while a prefix is still being typed.
    ///
    /// **A space is part of a filename far more often than it is a shortcut.**
    /// Space toggles the preview, so typing "new folder" flipped a 360-pixel
    /// overlay open on the fourth keystroke and threw the prefix away — which
    /// made every two-word name in the folder unreachable by typing. While a
    /// word is in progress the space belongs to it.
    /// </summary>
    public bool IsTypeAheadActive
        => _typeAhead.Length > 0 && DateTime.UtcNow - _typeAheadAt <= TypeAheadWindow;

    /// <summary>How long a partial prefix survives before the next keystroke
    /// starts a new search. Long enough to type a word, short enough that a
    /// keystroke a minute later is obviously a fresh start.</summary>
    private static readonly TimeSpan TypeAheadWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Jumps to the first entry whose name begins with what has been typed.
    ///
    /// Consecutive keystrokes accumulate — d, o, c finds "Documents" rather than
    /// jumping three times — and the SAME letter pressed again cycles through
    /// the entries starting with it, which is the convention every file manager
    /// and list control shares.
    ///
    /// Selection is the whole mechanism: the ListBoxes bind SelectedItem and
    /// scroll to it themselves, so there is nothing to scroll here.
    /// </summary>
    public void TypeAhead(string text)
    {
        // What is ON SCREEN, which is not Entries once a folder has been opened
        // in place: typing jumps to a row, and a row you can see and cannot
        // reach by typing its name is worse than one that is not there. The
        // same object as Entries whenever nothing is expanded.
        var rows = VisibleRows;

        if (string.IsNullOrEmpty(text) || rows.Count == 0) return;

        var now = DateTime.UtcNow;

        // A pause starts a new search rather than extending a stale prefix.
        if (now - _typeAheadAt > TypeAheadWindow) _typeAhead = "";
        _typeAheadAt = now;

        var repeat = _typeAhead.Length == 1
            && text.Length == 1
            && char.ToUpperInvariant(_typeAhead[0]) == char.ToUpperInvariant(text[0]);

        if (!repeat) _typeAhead += text;

        // Cycling continues past the current row; a new prefix re-anchors at the
        // top, so it always finds the FIRST match rather than the next one after
        // wherever the selection happened to be.
        var current = SelectedEntry is { } selected ? IndexIn(rows, selected) : -1;
        var start = repeat && current >= 0 ? current + 1 : 0;

        for (var offset = 0; offset < rows.Count; offset++)
        {
            var index = (start + offset) % rows.Count;

            if (!rows[index].Name.StartsWith(_typeAhead, StringComparison.OrdinalIgnoreCase))
                continue;

            SelectedEntry = rows[index];
            return;
        }

        // No match. The prefix stays, so continuing to type does not suddenly
        // start matching against a shorter one — but a miss should not move the
        // selection somewhere arbitrary either.
    }

    /// <summary>
    /// Where a row sits in the listing on screen.
    ///
    /// <c>IList.IndexOf</c> is not available on the read-only view the rows
    /// arrive as, and FileEntry is a record struct carrying a length and a
    /// timestamp — so this asks by path, which is the same question every other
    /// "which row is this" in the pane asks.
    /// </summary>
    private static int IndexIn(IReadOnlyList<FileEntry> rows, FileEntry wanted)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].FullPath == wanted.FullPath) return i;

        return -1;
    }
}
