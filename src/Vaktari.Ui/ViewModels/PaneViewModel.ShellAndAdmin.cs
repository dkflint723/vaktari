using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// What this pane hands to the operating system: the desktop's own context
/// menu, running something as another user, and mounting a disk image.
///
/// **Everything here is a request the application cannot fulfil itself.** It
/// either asks the shell to build a menu it does not own, or asks for elevation
/// it does not have — which is why the whole file is guards and refusals. A
/// listing that is not a real folder has no paths to hand over; a selection
/// that is not runnable must not be offered a Run row; and the mount commands
/// keep their own gates beside them rather than across the file.
///
/// The providers themselves stay in PaneViewModel with the other static hooks
/// — ShellMenu sits in a run with Trash and Vcs, and splitting one out of that
/// run would say less than leaving it.
///
/// Split out under roadmap 22; nothing moved changed.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>Whether to offer the entry at all.</summary>
    public bool HasShellMenu => ShellMenu is not null && !IsTrashListing && !IsRecentListing;

    /// <summary>
    /// Mounting disk images, or null where this machine cannot. Static like the
    /// other providers on this type.
    /// </summary>
    public static Vaktari.Core.Places.IDiskImages? DiskImages { get; set; }

    /// <summary>
    /// Where the machine's drives come from, for the This PC listing. Static
    /// like the other providers here; the sidebar holds the same one, which is
    /// the point — two enumerations of the drives would eventually disagree.
    /// </summary>
    public static Vaktari.Core.Places.IPlacesProvider? Places { get; set; }

    /// <summary>
    /// The search backend, for the `vaktari:search:` listing. Static like the
    /// other providers here.
    ///
    /// **Null is an empty result, never a crash** — the same rule the rest of
    /// this block follows. A desktop with no index still lets you type a
    /// question; it just cannot answer it.
    /// </summary>
    public static Vaktari.Core.Search.ISearchProvider? Search { get; set; }

    /// <summary>
    /// Reads this platform's kind of shortcut, so opening one can follow it.
    /// Static like the other providers here; null where the desktop has no such
    /// indirection to read.
    /// </summary>
    public static Vaktari.Core.FileSystem.IShortcutMaker? Shortcuts { get; set; }

    /// <summary>
    /// Whether the selected file is an image this machine could mount, and is
    /// not mounted already.
    ///
    /// **The bin and Recent are excluded.** Both hold rows naming where a file
    /// USED to be, so mounting there would attach whatever occupies that path
    /// now.
    /// </summary>
    public bool CanMountSelection =>
        DiskImages is { IsAvailable: true } images
        && !IsTrashListing
        && !IsRecentListing
        && SelectedEntry is { IsDirectory: false } entry
        && images.CanMount(entry.FullPath)
        && images.MountOf(entry.FullPath) is null;

    /// <summary>The other half: an image this application has mounted, which
    /// can therefore be put away again.</summary>
    public bool CanUnmountSelection =>
        DiskImages is { IsAvailable: true } images
        && !IsTrashListing
        && !IsRecentListing
        && SelectedEntry is { IsDirectory: false } entry
        && images.MountOf(entry.FullPath) is not null;

    private Vaktari.Core.FileSystem.IShellMenu? _shellMenu;

    /// <summary>What the live menu was built for, so an open one is reused
    /// rather than rebuilt and a stale one is still replaced.</summary>
    private IReadOnlyList<string>? _shellPaths;

    private bool _shellBuilding;

    /// <summary>
    /// What the shell offered, ready to bind.
    ///
    /// **A mixed list, holding real Separator controls among the records.**
    /// Avalonia uses a Control in an ItemsSource as its own container, which is
    /// the only way a data-driven menu can draw a rule — and the shell's menu
    /// leans on rules heavily enough that dropping them turns twenty entries
    /// into an undifferentiated column.
    /// </summary>
    public ObservableCollection<object> ShellMenuItems { get; } = new() { Waiting() };

    /// <summary>
    /// The row that holds the submenu open before there is anything in it.
    ///
    /// **Avalonia will not open a submenu with no items, and draws no chevron
    /// for one** — so an empty ItemsSource makes "Windows menu" look like a
    /// plain command and never fire SubmenuOpened, which is the event that
    /// builds the thing. The placeholder is what makes lazy loading possible at
    /// all, not decoration.
    /// </summary>
    private static Vaktari.Core.FileSystem.ShellMenuEntry Waiting() =>
        new("Reading the shell…", -1, IsEnabled: false);

    /// <summary>Bumped per open, so a build that lands late can tell that it is
    /// no longer wanted.</summary>
    private int _shellGeneration;

    /// <summary>
    /// Builds the shell menu when its submenu opens, and not before.
    ///
    /// **Off the UI thread, and this is not an optimisation.** Building gives
    /// every shell extension on the machine a turn, any one of which can be
    /// slow or hang outright; waiting for that here would freeze the window for
    /// as long as it took, which is the exact failure this feature is shaped to
    /// avoid. The entries arrive when they arrive and the menu fills in.
    ///
    /// **And nothing waits on it anywhere**, which is what lets the build run
    /// for as long as the machine needs. The provider hands back a task rather
    /// than a menu, so no thread is blocked while a handler thinks and nothing
    /// has to decide when to stop believing it — the placeholder stays on
    /// screen and says the shell is being read. It used to be given four
    /// seconds and then answered as though the shell had offered nothing.
    ///
    /// Not quite every thread, and the difference is worth being honest about:
    /// an async method body runs on its caller up to the first incomplete
    /// await, so the apartment thread this hands off to is now CONSTRUCTED and
    /// started here, on the UI thread, where the deleted Task.Run used to put
    /// that on the pool. It is a Thread constructor and a Start; no handler
    /// code runs on this thread at any point.
    ///
    /// Lazily for a second reason: no ordinary right-click should pay for
    /// something that lives behind one more hover.
    /// </summary>
    public async Task OpenShellMenuAsync()
    {
        if (ShellMenu is not { } provider) return;

        // The selection, or the folder when the click was on empty space — the
        // same rule the rest of the menu follows.
        //
        // **Empty means a different QUESTION, not the same question about the
        // folder.** A click on nothing wants what the folder offers about
        // itself as a place; asking for the folder's own menu answers with what
        // its row in the parent listing offers, which acts on it from outside.
        // The shell keeps those as two separately bound menus and this used to
        // ask for the first one either way.
        var paths = SelectionPaths();
        var background = paths.Count == 0;

        if (background) paths = [CurrentPath];

        // **Built once for a given selection, and never rebuilt underneath
        // itself.** The caller guards its own event, but this is the property
        // that has to hold: rebuilding starts by clearing the collection the
        // menu is drawn from, so a second call while one is on screen makes the
        // open submenu disappear. Keyed on the paths rather than on a flag so
        // that a menu left behind by a close event that never arrived is still
        // replaced when the selection moves on.
        if (_shellPaths is { } built && built.SequenceEqual(paths, StringComparer.Ordinal)
            && (_shellMenu is not null || _shellBuilding))
            return;

        CloseShellMenu();

        _shellPaths = paths;
        _shellBuilding = true;

        var generation = _shellGeneration;

        var menu = await (background
            ? provider.BuildBackgroundAsync(paths[0])
            : provider.BuildAsync(paths)).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _shellBuilding = false;

            // Closed, or reopened, while the shell was thinking. Releasing it
            // here is what keeps a slow build from leaking the apartment thread
            // it was running on.
            if (generation != _shellGeneration)
            {
                menu?.Dispose();
                return;
            }

            _shellMenu = menu;

            var rows = Flatten(menu?.Entries ?? []).ToList();

            // Never empty: an empty ItemsSource closes the submenu out from
            // under the pointer, and "nothing" reads as a fault rather than as
            // an answer.
            //
            // **This row now means the shell answered, and answered nothing.**
            // It used to mean that as well as "the shell was still thinking
            // when a four-second timer went off", so a slow machine was told
            // there was nothing here. They are separate rows now: until these
            // rows go up, what is on screen is the placeholder CloseShellMenu
            // left, which says the shell is being read.
            if (rows.Count == 0)
                rows.Add(new Vaktari.Core.FileSystem.ShellMenuEntry(
                    "Nothing offered here", -1, IsEnabled: false));

            ShowShellRows(rows);
        });
    }

    /// <summary>
    /// Puts these rows on screen without the collection ever being empty on the
    /// way there.
    ///
    /// **A clear followed by adds is not a replacement — it is an emptying and
    /// then a replacement, and the gap between the two is a state that readers
    /// land in.** Both callers used to do exactly that, three lines under a
    /// comment promising "never empty", and both were measured doing it: with
    /// the clear-then-fill version,
    /// ShellMenuBindingTests.The_rows_are_never_empty_while_they_are_replaced
    /// records a notification raised with Count 0 on every close and on every
    /// rebuild. That gap is what the intermittent
    /// "Assert.Contains … Collection: []" in
    /// Nothing_offered_is_said_only_after_the_shell_has_answered was reading;
    /// the same gap is what Avalonia's own reader — the ItemsControl the
    /// submenu is drawn from, which handles the Reset synchronously — sees
    /// when it closes the submenu out from under the pointer.
    ///
    /// New rows in first, old rows out afterwards, so the count never touches
    /// zero. A menu's worth of rows is twenty or thirty at the outside, so the
    /// extra notifications cost nothing worth weighing against that.
    ///
    /// **Never called with nothing to show**, and there is no branch here for
    /// it: the close passes the placeholder, and the rebuild passes the
    /// "Nothing offered here" row when the shell gave it none. An empty list
    /// here would empty the collection, which is the thing being prevented.
    /// </summary>
    private void ShowShellRows(IReadOnlyList<object> rows)
    {
        foreach (var row in rows) ShellMenuItems.Add(row);

        while (ShellMenuItems.Count > rows.Count) ShellMenuItems.RemoveAt(0);
    }

    private static IEnumerable<object> Flatten(
        IReadOnlyList<Vaktari.Core.FileSystem.ShellMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator) yield return new Avalonia.Controls.Separator();
            else yield return entry;
        }
    }

    /// <summary>
    /// Releases it, which ends the thread holding the handlers.
    ///
    /// **The ids are offsets into one live menu.** Releasing while the user is
    /// still looking would leave every row pointing at nothing; never releasing
    /// leaks an apartment thread per right-click. So this is tied to the
    /// context menu closing, and to opening the next one.
    ///
    /// Back to the placeholder rather than to empty, so the entry keeps its
    /// chevron and can be opened again.
    /// </summary>
    public void CloseShellMenu()
    {
        _shellGeneration++;

        ShowShellRows([Waiting()]);

        _shellMenu?.Dispose();
        _shellMenu = null;
        _shellPaths = null;
        _shellBuilding = false;
    }

    // ---- administrator ----------------------------------------------------

    /// <summary>
    /// Whether the right-click that opened this menu was held with Shift.
    ///
    /// **Behind a modifier because it is not an everyday action.** Elevating is
    /// how a person gets past a permission deliberately set against them, so it
    /// belongs where it is reachable and not where it is stumbled into —
    /// Explorer puts it behind the same gesture.
    ///
    /// Set by the window, which is the only thing that sees the press: a
    /// context menu opening carries no record of which keys were down.
    /// </summary>
    [ObservableProperty] private bool _adminRequested;

    partial void OnAdminRequestedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAdminEntries));
        OnPropertyChanged(nameof(CanRunSelectionAsAdministrator));
        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
    }

    /// <summary>Whether to show the section at all.</summary>
    public bool ShowAdminEntries =>
        AdminRequested && _launcher?.CanElevate == true && !IsTrashListing && !IsRecentListing;

    /// <summary>
    /// Whether "run as administrator" would mean anything for what is selected.
    ///
    /// **Which files those are is the launcher's question now, and a list of
    /// Windows file extensions used to answer it here.** That list is right on
    /// Windows — the runas verb on a .txt does nothing at all, no error, no
    /// elevation, no editor — and it is no answer whatever on a desktop where
    /// an executable usually has no extension. Sitting in the view model it
    /// answered for every platform, so when Linux gained pkexec this entry
    /// would still have been invisible there for want of a file it recognised.
    ///
    /// **No longer behind Shift.** Explorer shows "Run as administrator" for
    /// every executable on a plain right-click; only its EXTENDED verbs hide
    /// behind Shift. Copying the gate onto this entry meant an ordinary
    /// right-click on an .exe showed no elevation at all, and the person went
    /// hunting through submenus for something that looked buried — because it
    /// was. The admin TERMINAL keeps the Shift gate: that one is an extended
    /// verb by Explorer's own convention too.
    /// </summary>
    public bool CanRunSelectionAsAdministrator =>
        _launcher is { CanElevate: true } launcher
        && !IsTrashListing && !IsRecentListing
        && SelectedEntry is { IsDirectory: false } entry
        && launcher.CanElevateFile(entry.FullPath);

    /// <summary>
    /// Whether "Run" would mean anything for what is selected.
    ///
    /// **There was no such row, and on a desktop there was no other way.**
    /// Windows needs none — its shell runs an executable that is
    /// double-clicked — but the desktop's opener never runs anything, so
    /// "Run as administrator" was the only entry in the whole menu that could
    /// start a program, and it needed pkexec and root to do it. A person with
    /// a script they had just written could either elevate it or nothing.
    ///
    /// The platform's question, asked of the launcher, for the reason
    /// <see cref="CanRunSelectionAsAdministrator"/> gives at length: an
    /// extension list answered it here once, and there is no such list on a
    /// desktop where an executable usually has no extension at all.
    ///
    /// Not on either listing <see cref="RunnableListing"/> rules out.
    /// </summary>
    public bool CanRunSelection =>
        _launcher is { } launcher
        && RunnableListing
        && SelectedEntry is { IsDirectory: false } entry
        && launcher.CanRunFile(entry.FullPath);

    /// <summary>
    /// Whether starting a program off this listing means anything, whichever
    /// gesture asks.
    ///
    /// **Not in the bin and not in the recent listing, where a row carries the
    /// path something USED to occupy** — running whatever holds that path now
    /// is the worst version of the wrong-file fault this application has
    /// already fixed twice.
    ///
    /// One member because the rule was written twice and only the menu row's
    /// copy was complete. Measured on a headless pane at
    /// <see cref="VirtualPaths.Files"/>: CanRunSelection answered false for a
    /// row the launcher calls a program, and OpenAsync on that same row still
    /// raised the question — whose Run answer is the callback that starts the
    /// file. So the row was hidden and a double-click offered to start the
    /// program anyway. That listing is the one this change fills, too:
    /// <see cref="Start"/> records every run as a recent file, so the programs
    /// you have run are exactly the rows the gap sat under.
    /// </summary>
    private bool RunnableListing => !IsTrashListing && !IsRecentListing;

    /// <summary>
    /// Starts what is selected.
    ///
    /// **No question here, and that is the difference from a double-click.**
    /// The gesture there is ambiguous — it has meant "open this" everywhere
    /// else — so it asks; this row says the word "Run" and clicking it is the
    /// answer. GNOME's "Run as a program" behaves the same way.
    ///
    /// Every runnable file in the selection, like the elevated verb beside it:
    /// choosing this with three installers selected and having one start is the
    /// silent loss that rule exists to prevent.
    /// </summary>
    [RelayCommand]
    public void RunSelection()
    {
        if (!CanRunSelection || _launcher is not { } launcher) return;

        var runnable = EntriesToActOn()
            .Where(e => !e.IsDirectory && launcher.CanRunFile(e.FullPath))
            .ToList();

        if (runnable.Count == 0 || TooMany(runnable.Count)) return;

        foreach (var entry in runnable) Start(entry, run: true);
    }

    /// <summary>
    /// Hands the selection to the system to start elevated. The consent dialog
    /// is the system's — Windows' own, or the one polkit puts up — and Vaktari
    /// itself stays unelevated whatever is chosen.
    /// </summary>
    [RelayCommand]
    public void RunAsAdministrator()
    {
        if (!CanRunSelectionAsAdministrator || _launcher is not { } launcher) return;

        // Every one that is actually runnable. Selecting three installers and
        // choosing this ran one of them — and elevation is the worst place for
        // "it did something, but not what you asked".
        //
        // **The full path, not the name.** The rule here used to be an
        // extension match, which either spelling satisfies; the launcher's rule
        // can be the file's own mode bits, and a bare name asks the question
        // about a path relative to wherever this process happens to be running.
        var runnable = EntriesToActOn()
            .Where(e => !e.IsDirectory && launcher.CanElevateFile(e.FullPath))
            .ToList();

        if (runnable.Count == 0 || TooMany(runnable.Count)) return;

        foreach (var entry in runnable) launcher.OpenElevated(entry.FullPath);
    }

    /// <summary>
    /// Mounts the selected disk image and shows what is inside it.
    ///
    /// **Navigating there is the point of the verb.** Explorer opens the drive
    /// it just mounted, and a Mount that left the person looking at the .iso
    /// they started from would make them go and find it. The arrival watcher
    /// puts the drive in the sidebar a moment later either way; this is the
    /// direct answer to a direct request.
    /// </summary>
    [RelayCommand]
    public async Task MountImageAsync()
    {
        if (!CanMountSelection || DiskImages is not { } images
            || SelectedEntry is not { } entry) return;

        var name = entry.Name;

        Status = $"mounting {name}…";

        try
        {
            var mounted = await images.MountAsync(entry.FullPath, CancellationToken.None)
                .ConfigureAwait(true);

            await NavigateAsync(mounted.MountPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The tool's own sentence, which says whether the file is not an
            // image or the machine cannot mount one.
            Status = Vaktari.Core.FileSystem.Failures.Describe(ex, $"mount {name}");
        }

        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
    }

    /// <summary>Detaches an image this application mounted. The file itself is
    /// untouched.</summary>
    [RelayCommand]
    public async Task UnmountImageAsync()
    {
        if (!CanUnmountSelection || DiskImages is not { } images
            || SelectedEntry is not { } entry) return;

        var name = entry.Name;
        var mounted = images.MountOf(entry.FullPath);

        Status = $"unmounting {name}…";

        try
        {
            // Out of the way first, for the same reason ejecting a drive moves
            // the panes: a pane inside the mounted image holds it open.
            if (mounted is not null && IsInside(CurrentPath, mounted.MountPath))
                await NavigateAsync(
                    Path.GetDirectoryName(entry.FullPath)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                    .ConfigureAwait(true);

            await images.UnmountAsync(entry.FullPath, CancellationToken.None).ConfigureAwait(true);

            Status = $"unmounted {name}";
        }
        catch (Exception ex)
        {
            Status = Vaktari.Core.FileSystem.Failures.Describe(ex, $"unmount {name}");
        }

        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
    }

    private static bool IsInside(string? path, string root)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (Vaktari.Core.FileSystem.PathRules.Same(path, root)) return true;

        var prefix = Vaktari.Core.FileSystem.PathRules.Normalise(root);
        var full = Vaktari.Core.FileSystem.PathRules.Normalise(path);

        return full.StartsWith(prefix, Vaktari.Core.FileSystem.PathRules.Comparison)
               && (prefix.EndsWith(Path.DirectorySeparatorChar)
                   || (full.Length > prefix.Length
                       && full[prefix.Length] == Path.DirectorySeparatorChar));
    }

    /// <summary>An elevated terminal in this folder, in the preferred terminal.</summary>
    [RelayCommand]
    public void OpenAdminTerminalHere()
    {
        if (!ShowAdminEntries) return;

        _launcher?.OpenElevatedTerminal(CurrentPath, Terminals.FirstOrDefault());
    }

    /// <summary>Runs one of the shell's entries.</summary>
    [RelayCommand]
    public void InvokeShellEntry(Vaktari.Core.FileSystem.ShellMenuEntry? entry)
    {
        // A parent row exists to open its children; invoking it would ask the
        // handler to run a command it never issued.
        if (entry is null || entry.HasChildren || entry.IsSeparator) return;

        _shellMenu?.Invoke(entry.Id);
    }
}
