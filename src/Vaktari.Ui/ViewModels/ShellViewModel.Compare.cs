using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Holding the two sides of a split against each other, and moving what
/// differs from one to the other.
///
/// **Only while split, and it stops when the split does.** Everything here is
/// about a relationship between two panes rather than anything either pane
/// knows on its own — which is why it lives on the shell: a pane cannot see
/// its opposite number, and neither side owns the answer. The marks are per
/// pane, the decision is not.
///
/// **A comparison is answered from listings that have SETTLED**, so both
/// panes are watched and the marks are worked out again whenever either
/// finishes loading, sorting, filtering or changing on disk. A comparison
/// against a half-loaded side would mark rows as missing that are merely late.
///
/// Split out of ShellViewModel under roadmap 22; nothing moved changed.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Whether the two sides are being compared, row by row. Only while
    /// split: closing the split stops it. See <see cref="Recompare"/>.
    /// </summary>
    [ObservableProperty] private bool _isComparing;

    // The two panes whose listings are watched while comparing.
    private PaneViewModel? _comparedLeft;
    private PaneViewModel? _comparedRight;

    [RelayCommand]
    private void ToggleCompare()
    {
        if (!IsSplit) return;

        IsComparing = !IsComparing;
    }

    partial void OnIsComparingChanged(bool value) => Recompare();

    /// <summary>
    /// Compares the active tab on each side and marks both -- or, when not
    /// comparing, takes the marks away. Run again whenever either listing
    /// settles or either side shows another tab.
    ///
    /// **Hidden files only when both sides show them.** A side that hides
    /// them has none in its listing, so comparing it against one that shows
    /// them would mark every hidden file "only here" when the other folder
    /// may well have it too.
    ///
    /// **Two folders, each read to the end, or no marks.** See
    /// <see cref="WhyNotComparable"/>.
    /// </summary>
    private void Recompare()
    {
        var left = IsComparing ? Left.ActiveTab : null;
        var right = IsComparing ? Right?.ActiveTab : null;

        Watch(ref _comparedLeft, left);
        Watch(ref _comparedRight, right);

        if (left is not null && right is not null)
        {
            var result = WhyNotComparable(left, right) is null ? Between(left, right) : FolderComparison.None;

            left.CompareMarks = result.Left;
            right.CompareMarks = result.Right;
        }

        OnPropertyChanged(nameof(CompareSummary));
    }

    private static FolderComparison Between(PaneViewModel left, PaneViewModel right)
    {
        var hide = !left.ShowHidden || !right.ShowHidden;

        IEnumerable<FileEntry> Compared(PaneViewModel pane)
            => hide ? pane.Listed.Where(e => !e.IsConcealed) : pane.Listed;

        return FolderComparison.Between(Compared(left), Compared(right));
    }

    /// <summary>
    /// Why two panes cannot be compared, or null when they can.
    ///
    /// **Search results, the bin, the recent lists and the list of drives are
    /// views**, whose rows come from anywhere, so matching their names against
    /// a folder's says nothing about either.
    ///
    /// **And a folder is compared once it has been read to the end.** One still
    /// loading has only some of its rows and one that could not be read has
    /// none, so the other side's rows would read "only here" against it, and
    /// copying across from there would offer to copy them all in.
    /// </summary>
    private static string? WhyNotComparable(PaneViewModel left, PaneViewModel right)
    {
        if (!left.IsRealFolder || !right.IsRealFolder) return "one side is a view, not a folder";

        if (left.HasLoadError || right.HasLoadError) return "one side could not be read";

        if (left.IsLoading || right.IsLoading || !left.IsLoaded || !right.IsLoaded) return "one side is still loading";

        return null;
    }

    /// <summary>Moves the listing subscription to the pane now compared, and
    /// takes the marks off the one that no longer is.</summary>
    private void Watch(ref PaneViewModel? watched, PaneViewModel? now)
    {
        if (ReferenceEquals(watched, now)) return;

        if (watched is not null)
        {
            watched.ListingSettled -= OnComparedListingSettled;
            watched.PropertyChanged -= OnComparedPaneChanged;
            watched.CompareMarks = PaneViewModel.NoMarks;
        }

        watched = now;

        if (now is not null)
        {
            now.ListingSettled += OnComparedListingSettled;
            now.PropertyChanged += OnComparedPaneChanged;
        }
    }

    private void OnComparedListingSettled(object? sender, EventArgs e) => Recompare();

    /// <summary>
    /// **A side that starts loading is no longer the folder its marks were
    /// worked out against**, and marks were worked out only when a listing
    /// settled: while one side loaded, the other kept its marks against the
    /// folder just left, and copying across in that moment used them.
    /// Loading, loaded and a load error each decide whether the two can be
    /// compared at all, so each works the marks out again.
    ///
    /// Undo and redo reload from a pool thread, so the answer goes to the UI
    /// thread when it is not already on it.
    /// </summary>
    private void OnComparedPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PaneViewModel.IsLoading)
                                   or nameof(PaneViewModel.IsLoaded)
                                   or nameof(PaneViewModel.LoadError))) return;

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Recompare();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Recompare);
    }

    /// <summary>What the comparison found on the active side, for the status
    /// bar; empty when not comparing.</summary>
    public string CompareSummary
    {
        get
        {
            if (!IsComparing || ActiveTab is not { } pane || OtherGroup?.ActiveTab is not { } other) return "";

            if (WhyNotComparable(pane, other) is { } why) return "Not compared: " + why;

            var counts = pane.CompareMarks.Values
                .GroupBy(mark => mark)
                .ToDictionary(g => g.Key, g => g.Count());

            // **What the other side has and this one lacks was left out**, so
            // with an empty folder on this side the bar said "nothing differs"
            // beside a listing whose every row read "only here".
            var missing = other.CompareMarks.Values.Count(mark => mark == CompareMark.OnlyHere);

            if (counts.Count == 0 && missing == 0) return "Compared with the other side: nothing differs";

            var parts = new List<string>(5);

            void Add(CompareMark mark, string words)
            {
                if (counts.TryGetValue(mark, out var n)) parts.Add($"{n:N0} {words}");
            }

            Add(CompareMark.OnlyHere, "only here");
            Add(CompareMark.NewerHere, "newer here");
            Add(CompareMark.OlderHere, "older here");
            Add(CompareMark.Differs, "different");

            if (missing > 0) parts.Add($"{missing:N0} missing here");

            return "Compared with the other side: " + string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Asks the window to confirm copying what is newer or missing on the
    /// active side to the other one. An event rather than a command that
    /// prompts, because the prompt bar belongs to the window: the shape
    /// <see cref="EmptyTrashRequested"/> uses.
    /// </summary>
    public event EventHandler<CopyAcrossPlan>? CopyAcrossRequested;

    /// <summary>
    /// **Compares first, when the sides are not being compared yet.** The
    /// command box offers this whether or not the marks are showing, and
    /// "nothing here is newer" about two sides nobody had compared would
    /// answer a question that was never worked out.
    /// </summary>
    [RelayCommand]
    private void RequestCopyAcross()
    {
        if (ActiveTab is not { } pane) return;

        if (OtherGroup?.ActiveTab is not { } other)
        {
            pane.Status = "there is no other side to copy to — split the window first";
            return;
        }

        // Asked before comparing is switched on, so a refusal leaves the
        // window as it found it.
        if (WhyNotComparable(pane, other) is { } why)
        {
            pane.Status = "cannot copy across: " + why;
            return;
        }

        IsComparing = true;

        var plan = CopyAcrossPlan.From(Shown(pane), other.CurrentPath);
        var leftOut = Confirmations.LeftOut(plan);

        if (plan.Count == 0)
        {
            pane.Status = leftOut ?? "nothing here is newer or missing on the other side";
            return;
        }

        // Said while the prompt is up, beside the question it qualifies.
        if (leftOut is not null) pane.Status = leftOut;

        CopyAcrossRequested?.Invoke(this, plan);
    }

    /// <summary>
    /// The marks on the rows the listing shows. **A filter narrows what is
    /// copied as it narrows what is seen**: the comparison counts every row, so
    /// a folder filtered to its PDFs would otherwise have offered to copy the
    /// rest as well, under a prompt that named none of them.
    /// </summary>
    private static Dictionary<string, CompareMark> Shown(PaneViewModel pane)
    {
        var shown = pane.Entries.Select(e => e.FullPath).ToHashSet(StringComparer.Ordinal);

        return pane.CompareMarks
            .Where(mark => shown.Contains(mark.Key))
            .ToDictionary(mark => mark.Key, mark => mark.Value, StringComparer.Ordinal);
    }

    /// <summary>What an operation's starter has to add once it is done, taken
    /// by the bar when it says what the operation came to.</summary>
    private readonly Dictionary<IOperationHandle, Func<string?>> _afterwords = [];

    /// <summary>
    /// Copies what the prompt named into the folder it named: the plan's,
    /// not whatever the other side shows by the time the answer comes. Each
    /// clash is answered by <see cref="CopyAcrossPlan.Decide"/>, when the
    /// copy reaches it.
    ///
    /// One copy, so one step on the Undo row, which takes the copies back to
    /// the bin.
    /// </summary>
    public void RunCopyAcross(CopyAcrossPlan plan)
    {
        if (_ops is null || ActiveTab is not { } source) return;

        var handle = _ops.Copy(plan.Sources, plan.Destination,
            conflict => ValueTask.FromResult(plan.Decide(conflict)));

        // **A clash left alone was said nowhere**: a skip is neither a failure
        // nor a problem, so a replacement the prompt announced and the copy
        // then declined ended on a clean bar. Said when the copy is done.
        _afterwords[handle] = () => Confirmations.LeftAlone(plan);

        // Tracked by the tab showing the destination, so it refreshes and
        // selects what arrived, as a paste into it would.
        var showing = new[] { Left, Right }
            .Where(g => g is not null)
            .SelectMany(g => g!.Tabs)
            .FirstOrDefault(t => PathRules.Same(t.CurrentPath, plan.Destination));

        (showing ?? source).Adopt(handle);
    }

    /// <summary>
    /// A second pane, and something real to send. **CanActOnSelection rather
    /// than HasSelection**: a bin row names where a file USED to be, so copying
    /// from one copies whatever occupies that path now — the same hazard the
    /// delete and rename guards exist for, reached through the transfers.
    ///
    /// They were gated on the split alone, so an empty-space right-click in a
    /// split window offered them and they returned on the empty selection.
    /// </summary>
    public bool CanTransferToOtherPane => IsSplit && ActiveTab?.CanActOnSelection == true;
}
