using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Two panes side by side, and moving things between them.
///
/// **Exactly two, and closing always keeps the left.** That is a stated limit
/// rather than an accident — a third pane would make "the other side" stop
/// meaning anything, and every transfer here is phrased in terms of it.
/// Copying and moving to the other side are the reason a split is worth
/// having, so they are answered from the same place that owns the split.
/// </summary>
public sealed partial class ShellViewModel
{
    // ---- split ---------------------------------------------------------

    /// <summary>
    /// F3, matching Dolphin. Opening a split clones the current location so the
    /// second side starts somewhere useful rather than at home.
    /// </summary>
    [RelayCommand]
    public void ToggleSplit()
    {
        if (Right is null)
        {
            var right = CreateGroup();

            // Populated before being assigned, so the column never flashes empty.
            if (_rememberedRight is { Tabs.Count: > 0 } remembered)
                Restore(right, remembered);
            else
                right.AddTab(ActiveTab?.CurrentPath
                             ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            Right = right;
            ActiveGroup = right;
        }
        else
        {
            // Closing the split always keeps the left side, so which half
            // survives is predictable rather than depending on focus.
            var closing = Right;

            // Vaktari's default keeps what the closing side was showing, so
            // reopening the split lands back in place — closing a split should
            // not be a quiet way to lose a location. Dolphin discards it, and
            // people used to that can have it.
            _rememberedRight = Settings.AppSettings.Current.General.ClosingSplitDiscardsOtherPane
                ? null
                : closing.ToPaneState();

            Right = null;
            ActiveGroup = Left;
            closing.DisposeAll();
        }
    }

    /// <summary>Tab, matching Dolphin.</summary>
    [RelayCommand]
    public void FocusOtherPane()
    {
        if (OtherGroup is { } other) ActiveGroup = other;
    }

    [RelayCommand]
    public void CopyToOtherPane() => TransferToOther(move: false);

    [RelayCommand]
    public void MoveToOtherPane() => TransferToOther(move: true);

    /// <summary>
    /// Somewhere to send files that is not the other pane. Built from the same
    /// Places the sidebar shows, so the destinations offered are the ones the
    /// user already keeps — no separate list to maintain, and pinning a folder
    /// makes it a transfer target for free.
    /// </summary>
    /// <summary>
    /// The destination the other half of a split IS, as the first row of the
    /// transfer submenus. It was two more top-level entries — four transfer
    /// rows in a flat run — and folding it here is what let the pair collapse
    /// to two. Routed by its id in TransferTo, since it has no path of its own
    /// until the moment it is used.
    /// </summary>
    internal const string OtherPaneTargetId = "vaktari:other-pane";

    private static readonly PlaceItemViewModel OtherPaneTarget = new(new Place
    {
        Id = OtherPaneTargetId,
        Label = "The other pane",
        Path = "",
        Kind = PlaceKind.Virtual,
        Icon = "",
    });

    /// <summary>
    /// The last row of both transfer submenus, and the only destination on
    /// either that is not already on the list.
    ///
    /// **Copy to could only reach a folder somebody had pinned.** The list is
    /// the sidebar's places, so sending a file anywhere else meant pinning that
    /// folder first — adding a permanent row to the sidebar to make a one-off
    /// copy — or opening a split, navigating the other half there, and using
    /// "The other pane". A one-off destination is what a Copy to is usually
    /// for, and it was the one thing the menu could not name.
    ///
    /// Last rather than first: the pinned places are the frequent answers, and
    /// Dolphin's own transfer submenu ends with its picker too. Routed by its
    /// id in TransferTo like the other-pane row, since it has no path until the
    /// picker returns one.
    /// </summary>
    internal const string BrowseTargetId = "vaktari:browse";

    private static readonly PlaceItemViewModel BrowseTarget = new(new Place
    {
        Id = BrowseTargetId,
        Label = "Choose a folder…",
        Path = "",
        Kind = PlaceKind.Virtual,
        Icon = "",
    });

    public IReadOnlyList<PlaceItemViewModel> TransferTargets
    {
        get
        {
            var targets = Sidebar.Groups
                .SelectMany(g => g.Places)

                // An unmounted volume or unreachable share would look like a valid
                // destination and fail on use.
                .Where(p => p.IsAvailable && !string.IsNullOrEmpty(p.Path))

                // Sending a folder into itself is the one destination that is
                // never meaningful — and neither is sending it into something
                // inside itself, which equality alone allowed. Contains also
                // picks the platform's case rules: two paths differing only in
                // case are one folder on NTFS and two on ext4.
                .Where(p => !PathRules.Contains(SelectedFolderOf(ActiveTab), p.Path)
                            && !PathRules.Same(p.Path, ActiveTab?.CurrentPath))
                .ToList();

            if (CanTransferToOtherPane) targets.Insert(0, OtherPaneTarget);

            // Unconditional, and that is deliberate: it is also what a submenu
            // whose every place was filtered out has left to show, instead of
            // an empty popup.
            targets.Add(BrowseTarget);

            return targets;
        }
    }

    private void NotifyTransferTargets() => OnPropertyChanged(nameof(TransferTargets));

    /// <summary>
    /// The menu entries that need something to act on. Computed here rather
    /// than on the pane because the preference and the selection live on
    /// opposite sides, so nothing raises them on its own — and they go stale in
    /// three ways, not one: the selection changes, the active tab changes
    /// underneath them, and the split appears or goes away.
    /// </summary>
    private void NotifySelectionMenu()
    {
        OnPropertyChanged(nameof(ShowCopyToInMenu));
        OnPropertyChanged(nameof(CanShowProperties));
        OnPropertyChanged(nameof(ShowMoveToInMenu));
        OnPropertyChanged(nameof(ShowDuplicateInMenu));
        OnPropertyChanged(nameof(ShowOpenInNewTabInMenu));
        OnPropertyChanged(nameof(ShowOpenInNewWindowInMenu));
        OnPropertyChanged(nameof(ShowAddSelectionToPlaces));
        OnPropertyChanged(nameof(ShowAddCurrentToPlaces));
        OnPropertyChanged(nameof(CanTransferToOtherPane));
    }

    [RelayCommand]
    private void CopySelectionTo(PlaceItemViewModel? place) => TransferTo(place, move: false);

    [RelayCommand]
    private void MoveSelectionTo(PlaceItemViewModel? place) => TransferTo(place, move: true);

    /// <summary>Raised by "Choose a folder…"; the window owns the picker,
    /// exactly as it owns the settings dialog's startup-folder one.</summary>
    public event EventHandler<TransferBrowseRequest>? TransferBrowseRequested;

    private void TransferTo(PlaceItemViewModel? place, bool move)
    {
        if (place is null || ActiveTab is not { } source) return;

        // The first row of the submenu is not a folder at all.
        if (place.Id == OtherPaneTargetId)
        {
            TransferToOther(move);
            return;
        }

        var paths = SelectionOf(source);
        if (paths.Count == 0) { source.Status = "nothing selected"; return; }

        // Nor is the last one. Asked for before the picker opens rather than
        // after it closes, so nothing is asked for at all when there is nothing
        // to send.
        if (place.Id == BrowseTargetId)
        {
            // A place brings its own label; a picked folder brings only a path,
            // and a whole path in a one-line status bar pushes the count off the
            // end of it — so it is named by its leaf, through the same LeafName
            // a crumb and a tab title use, which already answers "a drive root
            // is its own leaf".
            TransferBrowseRequested?.Invoke(this, new TransferBrowseRequest(
                move,
                source.CurrentPath,
                folder => TransferInto(
                    source, paths, folder, PathRules.LeafName(folder), move)));

            return;
        }

        TransferInto(source, paths, place.Path, place.Label, move);
    }

    /// <summary>
    /// The transfer itself, once something has named a destination — a place
    /// row, or the folder that came back from the picker.
    ///
    /// **Both refusals live here rather than in the target list, because the
    /// picker answers with folders the list never saw.** The list drops a place
    /// inside the SelectedEntry and a place equal to the folder being viewed;
    /// the picker reaches any folder on the machine, and it OPENS at the folder
    /// being viewed — which makes that second one the destination a single
    /// wrong click gives.
    ///
    /// **And that one had no refusal anywhere.** Measured against
    /// WindowsFileOperations: moving a file, and moving a folder, into the
    /// folder it already lives in both came back <c>state=Completed</c> with no
    /// error and the directory unchanged, because a target that IS the source
    /// has nothing to do — so a status line reading "moving 1 item(s) to X" was
    /// reporting a transfer that never happened. The copy half answered
    /// Completed too and left a "notes - Copy.txt" behind, which is what
    /// Duplicate is for.
    ///
    /// Asked per path rather than against CurrentPath, so a Recents or search
    /// listing — where CurrentPath is a virtual path and the rows come from all
    /// over the disk — is judged by where its files actually are.
    ///
    /// The containment refusal, by contrast, is not new behaviour but the same
    /// answer sooner. Measured the same way, copying "work" into "work\deep"
    /// came back <c>state=Failed</c> before a byte moved, saying that "work"
    /// cannot be copied into a folder inside it — and LinuxFileOperations
    /// carries the identical guard. So a destination the list did offer, inside
    /// a folder that was selected but not focused, was refused by the engine
    /// rather than taken; what it was not, was refused in time, because that
    /// answer arrives asynchronously over a status line already reading
    /// "copying 1 item(s) to deep". Worded exactly as the other-pane route
    /// words it, since it is one refusal reached three ways.
    /// </summary>
    private void TransferInto(
        PaneViewModel source, IReadOnlyList<string> paths,
        string destination, string label, bool move)
    {
        if (!Directory.Exists(destination))
        {
            source.Status = $"{label} is not reachable";
            return;
        }

        if (paths.All(p => PathRules.Same(PathRules.Parent(p), destination)))
        {
            source.Status = $"already in {label}";
            return;
        }

        if (paths.Any(p => PathRules.Contains(p, destination)))
        {
            source.Status = "that folder cannot be sent into itself";
            return;
        }

        // Routed through a pane already showing the destination when there is
        // one, so its listing refreshes itself; otherwise through the same
        // helper, which keeps the conflict policy in exactly one place.
        var open = new[] { Left, Right }
            .Where(g => g is not null)
            .SelectMany(g => g!.Tabs)
            .FirstOrDefault(t => PathRules.Same(t.CurrentPath, destination));

        if (open is not null) open.PasteInto(paths, move);
        else source.PasteIntoFolder(destination, paths, move);

        source.Status = move
            ? $"moving {paths.Count} item(s) to {label}"
            : $"copying {paths.Count} item(s) to {label}";
    }

    private static List<string> SelectionOf(PaneViewModel pane)
        => pane.SelectionPaths().ToList();

    private void TransferToOther(bool move)
    {
        if (_ops is null || OtherGroup?.ActiveTab is not { } target) return;
        if (ActiveTab is not { } source) return;

        var paths = SelectionOf(source);
        if (paths.Count == 0) return;

        // **The one route with no containment check at all.** Sending a folder
        // to the other pane while that pane is showing somewhere inside it
        // copies the folder into its own subtree.
        if (paths.Any(p => PathRules.Contains(p, target.CurrentPath)))
        {
            source.Status = "that folder cannot be sent into itself";
            return;
        }

        target.PasteInto(paths, move);
    }
}
