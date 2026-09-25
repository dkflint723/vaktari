using Avalonia.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Clicking a row to open it, and deciding when a click is that.
///
/// **Two routes can each legitimately decide a row was double-clicked**, and
/// which one fires depends on whether Avalonia's gesture formed — so both call
/// TryOpen and a repeat within 600ms is dropped. That is the whole reason this
/// is a concern rather than two handlers: the de-duplication only works if the
/// two routes and the memory they share stay together.
///
/// The memory is three fields, and every read and write of all three is in
/// this file. <c>_lastTapPath</c> is the row clicked once and not yet paired;
/// <c>_lastOpenPath</c> and <c>_lastOpenAt</c> are what TryOpen drops a
/// duplicate against. Nothing outside asks any of them; the two other ways a
/// row opens only tell this file to forget, through ForgetTheClick.
///
/// **It is not the only way a file opens**, and the window's own name for the
/// preference is not the one the rows bind to. Enter opens through
/// pane.OpenSelectedAsync (OnWindowKeyDown) and the listing menu's Open
/// through ActiveTab.OpenSelectedCommand (the listing's ContextMenu); both
/// reach the view model without passing here. TryOpen is the single place the
/// POINTER opens, which is the claim its own summary should make and does not
/// — left as it stands, because this is a pure move. The three listing styles
/// bind Classes.singleclick to PaneViewModel.OpensOnSingleClick, a different
/// member on a different class that also subtracts the trash listing; the
/// static property here is the window's own shorthand.
///
/// **The wiring stays in the constructor**, in MainWindow.axaml.cs's run of
/// AddHandler calls, which is the house pattern rather than a separation
/// invented here — OnWindowKeyDown is wired in that same run and lives in
/// MainWindow.Keyboard.cs, OnRenameBoxLostFocus likewise in
/// MainWindow.Prompt.cs. Neither handler is
/// reachable from markup; MainWindow.axaml carries no Tapped or DoubleTapped
/// attribute at all.
///
/// Three source-walkers are borrowed and none of them comes along.
/// <c>EntryAt</c> and <c>InRenameBox</c> keep callers in MainWindow.axaml.cs.
/// <c>TabStripEmptySpaceAt</c> does not — after this cut its only production
/// caller in the repository is OnDoubleTapped, below. It stays in
/// MainWindow.HitTesting.cs all the same, beside the twenty-two other private
/// static members it is a peer of, because that file is where this window
/// answers "what is under this pointer" and a walker filed under its one
/// caller would be the first exception to that. The distance is unchanged in
/// any case: its caller is already in a different file from its declaration
/// today, which is what keeps this out of the HoverTab shape.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Single click when the preference says so, or when it defers to a desktop
    /// that says so.
    ///
    /// The rule itself moved to <see cref="PaneViewModel.SingleClickOpens"/>,
    /// because the LISTING has to ask the same question — a row under the
    /// pointer wears a hand and an underlined name when one click opens it, and
    /// a style can only reach a view model. Kept here as a name so the two
    /// click handlers below still read as they did.
    /// </summary>
    private static bool OpensOnSingleClick => PaneViewModel.SingleClickOpens;

    private string? _lastTapPath;

    private string? _lastOpenPath;
    private DateTime _lastOpenAt;

    /// <summary>
    /// The single place the POINTER opens, and it de-duplicates.
    ///
    /// **Not the single place anything opens**, which this said for a long
    /// time and which is worth being exact about: Enter opens through
    /// pane.OpenSelectedAsync in MainWindow.Keyboard.cs and the listing menu's
    /// Open through ActiveTab.OpenSelectedCommand in the markup, and neither
    /// passes here. What is true is that every route a POINTER can take does.
    ///
    /// TWO routes can each legitimately decide a row was double-clicked, and
    /// which one fires depends on whether Avalonia's gesture formed — so rather
    /// than bet on one, both call this and a repeat within the interval is
    /// dropped. Opening a folder twice is invisible; launching an application
    /// twice is not.
    /// </summary>
    private void TryOpen(FileEntry entry)
    {
        var now = DateTime.UtcNow;

        if (_lastOpenPath == entry.FullPath
            && now - _lastOpenAt < TimeSpan.FromMilliseconds(600))
            return;

        _lastOpenPath = entry.FullPath;
        _lastOpenAt = now;

        // **Forgotten once it has been acted on.** The row that was just opened
        // stayed remembered as "clicked once", so opening a folder, pressing
        // Back, and clicking that folder a single time to rename it entered it
        // again — the click before the double-click was still counting, minutes
        // later. There is deliberately no time limit on the pair, which is what
        // made the stale value reach so far.
        _lastTapPath = null;

        _ = _shell.ActiveTab?.OpenAsync(entry);
    }

    /// <summary>
    /// **A row opened with Enter or the menu stayed remembered as "clicked
    /// once".** Click a row, press Enter to open it, go Back, and one click on
    /// that row opened it again — re-launching a program, not just re-entering
    /// a folder — because TryOpen's own forgetting is the only one there was,
    /// and neither of those routes passes through TryOpen. The same complaint
    /// TryOpen's comment fixed for the pointer, reached by the other two roads.
    ///
    /// A method rather than a bare assignment at each caller, so every write of
    /// the pair's memory stays in this file, as the summary above promises.
    /// Called from OnWindowKeyDown's Enter, and wherever the listing's menu
    /// opens — the menu opening at all, not its Open row, because a menu
    /// between two clicks is no more a double-click than a Ctrl+click is. That
    /// is two callers, not one: a right-click raises the menu's Opening and
    /// reaches OnListingMenuOpening, but the Menu key opens it through
    /// ContextMenu.Open in OpenListingMenu, which raises no Opening at all.
    /// </summary>
    private void ForgetTheClick() => _lastTapPath = null;

    /// <summary>
    /// **Avalonia raises Tapped for the FIRST click and DoubleTapped for the
    /// second — it does not raise Tapped twice.** A previous attempt here
    /// counted two taps and therefore never fired when the gesture worked
    /// properly.
    ///
    /// So this is only the FALLBACK: it catches the case where the gesture does
    /// not form because the row's visual changed between clicks (selection
    /// swaps a ContentPresenter for a Border, which is why clicking the empty
    /// part of a line used to take four clicks while the icon and filename
    /// worked). DoubleTapped remains the normal path.
    /// </summary>
    private void OnTapped(object? sender, TappedEventArgs e)
    {
        // **A click in the row's own rename box counted as a click on the
        // row.** With the single-click preference on, one click to place the
        // caret opened the file being renamed; with it off, two did. The
        // remembered row is cleared as well, so a click before the rename
        // cannot pair with one aimed at the text.
        if (InRenameBox(e.Source))
        {
            _lastTapPath = null;
            return;
        }

        if (EntryAt(e.Source) is not { } entry) return;

        // **A modified click is a selection gesture and never an open.**
        // Ctrl+click to add a file to a selection LAUNCHED it in single-click
        // mode, and in double-click mode two Ctrl+clicks on the same row did —
        // so extending a selection opened whatever it passed over. Shift+click
        // to select a range did the same to the far end of the range.
        //
        // The remembered row is cleared as well as the open suppressed: the
        // second half of a two-click open must not be able to arrive from a
        // gesture that was never asking for one.
        if (e.KeyModifiers is not KeyModifiers.None)
        {
            _lastTapPath = null;
            return;
        }

        if (OpensOnSingleClick)
        {
            TryOpen(entry);
            return;
        }

        // [stated] the rule the user wants: clicking the same row twice opens
        // it, full stop. NO time limit — a 500 ms window meant a first click was
        // spent on selection and only a fast second one counted, so opening
        // something felt like select-then-double-click.
        //
        // Clicking a DIFFERENT row resets, which is what keeps this from firing
        // on anything you did not click twice in a row.
        if (_lastTapPath == entry.FullPath)
        {
            _lastTapPath = null;
            TryOpen(entry);
            return;
        }

        _lastTapPath = entry.FullPath;
    }


    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Selecting a word in the editor is not asking for the file to open.
        if (InRenameBox(e.Source)) return;

        // **Nothing happened on the blank half of the tab strip.** Both
        // references and every browser open a tab there.
        //
        // BEFORE the single-click branch below, deliberately: that preference
        // governs how a FILE is opened, and reading it first would have taken
        // this gesture away from everyone who opens files with one click — the
        // "+" beside the strip does not change meaning with that setting
        // either. Through the group rather than the shell, so in a split the
        // tab opens on the side that was double-clicked and not on the side
        // that happens to have focus.
        if (TabStripEmptySpaceAt(e.Source) is { } group)
        {
            group.NewTabHereCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // The normal path, restored. TryOpen drops a duplicate if the fallback
        // in OnTapped has already acted on this same row.
        if (OpensOnSingleClick) return;

        // Same rule as OnTapped: Ctrl+double-click is still a selection
        // gesture, and Explorer does not open on it either.
        if (e.KeyModifiers is not KeyModifiers.None) return;

        if (EntryAt(e.Source) is { } entry) TryOpen(entry);
    }
}
