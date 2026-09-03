using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

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
        Breadcrumbs.Add(new PathSegment(
            Core.Naming.ComputerTitle, VirtualPaths.Computer,
            new RelayCommand(() => Detached(NavigateAsync(VirtualPaths.Computer), "navigate")),
            false));

        for (var i = 0; i < levels.Count; i++)
        {
            var target = levels[i];

            // LeafName, not the raw segment: it gives a root back as itself, so
            // the first crumb reads "/" or "C:\" rather than blank.
            Breadcrumbs.Add(new PathSegment(
                PathRules.LeafName(target), target,
                new RelayCommand(() => Detached(NavigateAsync(target), "navigate")),
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

        // Selected rather than opened: landing on the file with it highlighted
        // is the least surprising answer, and launching something because a
        // path was pasted would be a side effect nobody asked for.
        SelectedEntry = Entries.FirstOrDefault(e => PathRules.Same(e.FullPath, file));
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
    }
}
