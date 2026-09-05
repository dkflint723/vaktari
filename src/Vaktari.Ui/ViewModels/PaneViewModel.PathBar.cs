using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One folder the path box is offering, as a row under it.
///
/// <c>FullPath</c> keeps the trailing separator the completer puts on every
/// offer — it is what says "and you can keep typing inside this" — and
/// <c>Name</c> is the leaf, which is the only part that differs between rows.
/// </summary>
public sealed record PathSuggestion(string FullPath, string Name, ICommand Apply);

/// <summary>
/// The path bar: the clickable ancestors, and the text field it becomes when
/// you type into it.
/// </summary>
public sealed partial class PaneViewModel
{
    // ---- breadcrumbs ---------------------------------------------------

    /// <summary>
    /// The path as clickable ancestors, Dolphin-style. Navigating two levels up
    /// is one click rather than two, and the shape of the location is readable
    /// without parsing a string.
    /// </summary>
    public ObservableCollection<PathSegment> Breadcrumbs { get; } = new();

    /// <summary>
    /// Extends the typed path to the next matching folder. Bound to Tab while
    /// the path box is open.
    /// </summary>
    [RelayCommand]
    public void CompletePath()
    {
        if (!IsPathEditing) return;

        if (_completer.Complete(PathText ?? "") is not { } completed)
        {
            Status = "no matching folder";
            return;
        }

        // Set through the field so OnPathTextChanged does not treat our own
        // write as the user typing and reset the cycle.
        _completingPath = true;
        try { PathText = completed; }
        finally { _completingPath = false; }
    }

    partial void OnPathTextChanged(string value)
    {
        // Typing invalidates the candidate list; completing does not.
        if (!_completingPath) _completer.Reset();

        // **PathText is written from a pool thread, and this collection is
        // bound.** LoadListingAsync writes it in its synchronous prologue, and
        // undo and redo reach that prologue off the UI thread — they await the
        // refresh with ConfigureAwait(false). So an undo performed while the
        // box was open rebuilt an ItemsSource, and read a directory to do it,
        // on whichever pool thread happened to be carrying the operation.
        // Measured at two CollectionChanged notifications off the UI thread per
        // undo, in AddressBarSuggestionTests.
        //
        // The same treatment OnCurrentPathChanged already gives Breadcrumbs,
        // for the same reason — except that this one stays synchronous when it
        // is already on the right thread, because typing must narrow the offer
        // in the same turn the keystroke arrives.
        //
        // PathText rather than value on the posted route: by the time it runs,
        // the newest write is the one that should be on screen.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RebuildPathSuggestions(PathText));
            return;
        }

        // Both ways round: a keystroke narrows the offer, and a Tab that lands
        // inside a folder turns the offer into that folder's own children.
        RebuildPathSuggestions(value);
    }

    // ---- the completion list -------------------------------------------

    /// <summary>
    /// How many folders the dropdown shows. Deep enough to cover an ordinary
    /// folder's worth of children, short enough that a list typed over the
    /// listing does not bury it.
    /// </summary>
    private const int SuggestionRows = 10;

    /// <summary>
    /// What the typed path could grow into, as rows under the box.
    ///
    /// **The completer worked these out on every Tab and handed back exactly
    /// one of them.** The box offered no way to see the alternatives, so the
    /// only route to the second candidate was to press Tab again and read the
    /// text — and the only route to knowing Tab did anything at all was the
    /// tooltip.
    /// </summary>
    public ObservableCollection<PathSuggestion> PathSuggestions { get; } = new();

    /// <summary>
    /// Whether the dropdown is showing. Driven only from
    /// <see cref="RebuildPathSuggestions"/> and
    /// <see cref="ClosePathSuggestions"/>, so it can never stand open over an
    /// empty list.
    /// </summary>
    [ObservableProperty] private bool _isPathSuggestionsOpen;

    /// <summary>
    /// Re-offers for <paramref name="text"/>.
    ///
    /// **Gated on IsPathEditing, because PathText is written on the way in and
    /// on the way out of editing.** BeginEditPath fills the box before it opens
    /// it and RevertPathText refills it before it closes it — so without the
    /// gate the dropdown sprang open under crumbs that were not even in edit
    /// mode, and Ctrl+L opened a list nobody had typed a character to ask for.
    ///
    /// That one gate and no other: an empty box needs no test of its own, since
    /// nothing to search in already answers with an empty offer.
    /// </summary>
    private void RebuildPathSuggestions(string text)
    {
        PathSuggestions.Clear();

        if (IsPathEditing)
        {
            foreach (var folder in PathCompleter.Suggestions(text, SuggestionRows))
            {
                // Picking a row types it, which is what Tab does — NOT a
                // navigation. The offer ends in a separator, so the write comes
                // back through here and the list becomes the chosen folder's
                // own children: clicking down a tree is then the same gesture
                // repeated, and Enter is still the one key that goes anywhere.
                //
                // Written plainly rather than through the completing guard: a
                // row was picked rather than cycled to, so the next Tab should
                // start again from what is now in the box.
                PathSuggestions.Add(new PathSuggestion(
                    folder,
                    PathRules.LeafName(folder),
                    new RelayCommand(() => PathText = folder)));
            }
        }

        IsPathSuggestionsOpen = PathSuggestions.Count > 0;
    }

    /// <summary>
    /// Puts the dropdown away.
    ///
    /// Called by both routes out of the box rather than left to the text
    /// changing: NavigateToPathText reads PathText and never writes it, so
    /// nothing would have closed the list behind a path that had just been
    /// navigated to.
    /// </summary>
    private void ClosePathSuggestions()
    {
        PathSuggestions.Clear();
        IsPathSuggestionsOpen = false;
    }

    [RelayCommand]
    public void BeginEditPath()
    {
        // **Pressing it twice wiped what had been typed.** Ctrl+L, Alt+D and a
        // double-click on the bar all land here, and the second one reset the
        // box to the folder you are in — so half a typed path went, silently,
        // for a keystroke whose meaning is "put me in the address bar", which
        // is where you already were.
        //
        // **And then it did nothing at all**, which is the other half of what
        // both references do: theirs re-select the text, so a second press is
        // how you replace a path you have half-edited without reaching for the
        // mouse. Only the box appearing focused it here, and on the second
        // press it is already on screen — so the keystroke reached nothing.
        if (IsPathEditing)
        {
            FocusPathBox = true;
            FocusPathBox = false;
            return;
        }

        _completer.Reset();

        // **A virtual listing put its own scheme in the box.** Ctrl+L in This
        // PC filled the address bar with "vaktari:computer" — an internal name,
        // in the one place whose whole contract is that what it holds is a path
        // you can read, edit and press Enter on. Empty is the honest answer:
        // there is no path here, and typing one is what the keystroke is for.
        PathText = IsRealFolder ? CurrentPath : "";
        IsPathEditing = true;
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (string.IsNullOrEmpty(CurrentPath)) return;

        // A recent listing has no hierarchy to walk up, so it gets one crumb
        // naming itself. Splitting it on '/' would produce "vaktari:recent"
        // and "files" as if they were folders.
        if (VirtualPaths.IsVirtual(CurrentPath))
        {
            Breadcrumbs.Add(new PathSegment(
                VirtualPaths.Label(CurrentPath), CurrentPath,
                new RelayCommand(() => { }), true));
            return;
        }

        // Ancestors already answers this on both platforms — it starts at the
        // root, "/" or "C:\", and walks down to the path itself.
        //
        // It replaces a split on '/' that prefixed a hardcoded "/" crumb. On
        // Windows that produced "/ / C:\Users\flint": the split found no '/' to
        // break on, so the whole path stayed one unclickable crumb, behind a
        // root that does not exist there. Linux is unchanged — Ancestors("/x/y")
        // is ["/", "/x", "/x/y"], which is the same three crumbs as before.
        var levels = PathRules.Ancestors(CurrentPath);

        // **The machine sits above the drive.** The crumbs used to top out at
        // "C:\" with nothing above it, which is where Explorer shows This PC —
        // and it is the one crumb that makes the other drives reachable without
        // going to the sidebar.
        //
        // Built by Crumb like every other one, so it carries the menu too: This
        // PC's is the machine's drives, which is what makes the OTHER drive
        // reachable from the bar rather than only from the sidebar.
        Breadcrumbs.Add(Crumb(Core.Naming.ComputerTitle, VirtualPaths.Computer, isLast: false));

        for (var i = 0; i < levels.Count; i++)
        {
            var target = levels[i];

            // **The root crumb read "C:\".** The tab above it was taught the
            // sidebar's name for a drive; the crumb directly under it was not,
            // so one window showed "Windows (C:)" and "C:\" for one drive,
            // three inches apart, with the raw one in the bar you read to know
            // where you are.
            //
            // Asked for every crumb rather than only the first, because the
            // dictionary behind it holds drive roots and network share roots
            // and nothing else — measured: it is built from the devices and
            // shares groups only, never from the user's pinned places — so an
            // ordinary folder gets null and falls through. A share root that
            // happens to be an ANCESTOR is named too, which is the same
            // improvement one level down.
            //
            // LeafName underneath, unchanged: it gives a root back as itself,
            // so a crumb with no better name reads "/" or "C:\" rather than
            // blank.
            // Through Crumb, which hangs the "what is inside this" menu off the
            // separator after it — the LAST crumb included, where that menu is
            // the folders of the listing you are looking at, and pressing one
            // goes in without touching the rows.
            Breadcrumbs.Add(Crumb(
                Places?.NameFor(target) ?? PathRules.LeafName(target), target,
                i == levels.Count - 1));
        }

        // Second, so it sits between the root and whatever survives. Added for
        // any path with something between its root and its leaf, whether or not
        // the bar is currently too narrow — the panel decides that, because only
        // the panel knows how wide the toolbar is, and it changes as the window
        // and the split are dragged.
        //
        // Its command opens the path editor: a person who cannot see the middle
        // of the path is the most likely person to want to read or edit it.
        // Counted from the crumbs actually present, which now include the
        // machine at the front — inserting at a fixed index would have put the
        // ellipsis before the drive rather than after it.
        if (levels.Count > 2)
        {
            Breadcrumbs.Insert(2, PathSegment.Ellipsis(
                new RelayCommand(BeginEditPath)));
        }
    }

    /// <summary>
    /// Enter in the path box. A command rather than a code-behind KeyDown
    /// handler because there is now one path box per split side, and named
    /// controls inside a template cannot be reached from code-behind.
    /// </summary>
    [RelayCommand]
    public Task NavigateToPathText()
    {
        IsPathEditing = false;
        ClosePathSuggestions();

        if (string.IsNullOrWhiteSpace(PathText)) return Task.CompletedTask;

        // **Expanded here and nowhere else.** This is the one place a path
        // arrives as something a person typed, and %ProgramFiles% is how
        // Windows names that folder everywhere else they will have met it.
        // A path read from disk or from settings is used exactly as written,
        // because a folder whose real name contains a percent sign is legal.
        var typed = PathVariables.Expand(PathText);

        // **Typing the name goes there**, which is how a Windows user reaches
        // This PC — it was a missing directory before, because the name is not
        // a path. Matched case-insensitively on both platforms: this is a label
        // somebody typed, not a filename.
        if (string.Equals(typed.Trim(), Core.Naming.ComputerTitle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(typed.Trim(), VirtualPaths.Computer, StringComparison.OrdinalIgnoreCase))
            return NavigateAsync(VirtualPaths.Computer);

        // **A file is a perfectly reasonable thing to paste in here**, and it
        // used to be handed to the directory enumerator, which answered with
        // the operating system's own "The directory name is invalid." over an
        // empty listing. The route that receives paths FROM the desktop already
        // resolves a file to its folder; the typed route never learned to.
        // **"..", "src", "../sibling" — all of them went nowhere.** A relative
        // path was handed to the enumerator as written, so it resolved against
        // the process's working directory rather than the folder on screen,
        // and typing ".." in a folder took you somewhere unrelated or nowhere
        // at all. Explorer and Dolphin both resolve against the current folder,
        // which is the only reading that makes sense in an address bar.
        typed = AgainstCurrentFolder(typed);

        if (!Directory.Exists(typed) && File.Exists(typed))
            return OpenTypedFileAsync(typed);

        return NavigateAsync(typed);
    }

    /// <summary>
    /// Roots a relative path against the folder on screen.
    ///
    /// Left alone when it is already rooted, when the listing is virtual (This
    /// PC has no folder to be relative to), or when the combination is not a
    /// path at all — a name with a colon in it on Windows would throw, and a
    /// typed name that cannot be resolved should reach the ordinary "not there"
    /// message rather than an exception.
    /// </summary>
    private string AgainstCurrentFolder(string typed)
    {
        if (typed.Length == 0) return typed;
        if (VirtualPaths.IsVirtual(typed)) return typed;
        if (Path.IsPathRooted(typed)) return typed;

        if (CurrentPath.Length == 0 || VirtualPaths.IsVirtual(CurrentPath)) return typed;

        try
        {
            return Path.GetFullPath(typed, CurrentPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                      or PathTooLongException)
        {
            return typed;
        }
    }

    /// <summary>
    /// Shows a typed file in its folder and opens it — which is what somebody
    /// pasting a file path is asking for.
    /// </summary>
    private async Task OpenTypedFileAsync(string file)
    {
        var folder = Path.GetDirectoryName(file);

        if (string.IsNullOrEmpty(folder)) return;

        await NavigateAsync(folder).ConfigureAwait(true);

        // **It highlighted the file and stopped.** The argument written here
        // was that launching something because a path was pasted is a side
        // effect nobody asked for — but nobody types a full path to a file and
        // presses Enter in order to look at its name in a list. Explorer and
        // Dolphin both open it; this method's own summary has said "and opens
        // it" all along while the body did not; and Enter means "open this"
        // everywhere else in the application, so it has to mean it here.
        //
        // Landing on the folder with the row lit stays, because that is the
        // other half of the answer rather than an alternative to it: the pane
        // ends up showing where the thing you just opened lives, so whatever
        // you want to do next has somewhere to happen.
        //
        // RowFor, not FirstOrDefault: FileEntry is a record struct, so a miss
        // handed back default(FileEntry) — a FileEntry? that is not null and
        // whose FullPath is null. Assigning THAT lit no row while HasSelection
        // went on reporting a selection, and every verb reading it was pointed
        // at a null path.
        var row = RowFor(file);

        SelectedEntry = row;

        // Through OpenAsync rather than the launcher directly, so the typed
        // route opens things the same way Enter on a row does: one place
        // decides what opening means, and it is the place that redirects a
        // shortcut-to-a-folder into the pane and records the file as recent.
        // Its bin refusal is inert on this route — File.Exists has already said
        // this is a real file on disk, so the folder just navigated to is never
        // the bin — but sharing the method is what keeps the two routes one
        // decision if opening grows another guard.
        //
        // The row when the listing holds one, an entry built from the path when
        // it does not: a concealed file has no row while ShowHidden is off, and
        // whether a row is drawn must not decide whether a path somebody typed
        // in full opens. OpenAsync reads the full path and the directory flag,
        // and the caller has already established this is a file rather than a
        // directory.
        await OpenAsync(row ?? new FileEntry(
            PathRules.LeafName(file), file, 0, default, EntryFlags.None))
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Escape, or clicking away: put back what is actually being shown.
    ///
    /// Guarded, because it is now reachable from lost-focus as well as Escape.
    /// NavigateToPathText clears IsPathEditing before it reads PathText, so an
    /// unguarded revert would fire in that gap and overwrite the path the user
    /// just typed — Enter would appear to navigate nowhere.
    /// </summary>
    [RelayCommand]
    public void RevertPathText()
    {
        if (!IsPathEditing) return;

        PathText = CurrentPath;
        IsPathEditing = false;
        ClosePathSuggestions();
    }
}
