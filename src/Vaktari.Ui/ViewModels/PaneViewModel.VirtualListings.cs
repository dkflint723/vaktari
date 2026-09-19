using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The listings that are not folders, and what a pane says while showing one.
///
/// **Each of these is somewhere you can BE rather than something you can do**,
/// which is why they are paths: the rows are FileEntry in Entries, so sorting,
/// filtering, the three layouts and the whole selection machinery need to know
/// nothing about where they came from. What is left over is exactly this — the
/// handful of questions a pane has to answer differently while it is showing
/// one, and the sentence each puts above the listing.
///
/// **This block grows by one every time a listing is added**, which is the
/// argument for it having a file. The bin, both Recent listings and a search
/// were here; space usage joined them, then duplicates, and each arrival added
/// its flag, its band line and another arm to CanGoToLocation in the middle of
/// a five-thousand-line file. Split out under roadmap 22; nothing changed.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>
    /// The path as a person should see it.
    ///
    /// **A virtual listing's path is an internal scheme**, and showing it is a
    /// leak: hovering the location bar in This PC read "vaktari:computer",
    /// which is a name for the code's benefit and nobody else's.
    /// </summary>
    public string DisplayPath => VirtualPaths.IsVirtual(CurrentPath)
        ? VirtualPaths.Label(CurrentPath)
        : CurrentPath;

    /// <summary>
    /// True only in the two recent listings, where the rows come from a store
    /// rather than a directory.
    /// </summary>
    public bool IsRecentListing => VirtualPaths.IsRecent(CurrentPath);

    /// <summary>True in the trash listing, which gates restore and empty.</summary>
    public bool IsTrashListing => CurrentPath == VirtualPaths.Trash;

    /// <summary>
    /// True while this pane is showing a search, which is what puts the band
    /// above the listing.
    /// </summary>
    public bool IsSearchListing => VirtualPaths.IsSearch(CurrentPath);

    /// <summary>
    /// True while this pane is showing what is using the space in a folder.
    /// </summary>
    public bool IsUsageListing => VirtualPaths.IsUsage(CurrentPath);

    /// <summary>The folder that was measured, which is where this listing came
    /// from and the way back out of it.</summary>
    public string UsageFolder => VirtualPaths.FolderOf(CurrentPath);

    /// <summary>
    /// What the measured folder came to. Cleared when a listing starts and set
    /// when one finishes, so the band never carries the previous folder's
    /// figure while the next is still being walked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageTotalLine))]
    private Usage _usageTotal;

    /// <summary>
    /// The band's sentence: what the folder holds, and what could not be read
    /// on the way.
    ///
    /// **The unreadable count has nowhere else to go.** No row can carry it — a
    /// folder nobody was allowed to open contributes none — so without this the
    /// measurement works out its honest half and throws it away, leaving a
    /// total short by whatever was behind a denied folder to read as exact.
    /// </summary>
    public string UsageTotalLine
    {
        get
        {
            var line = $"{ByteSize.Format(UsageTotal.Bytes)} in "
                       + Count(UsageTotal.Folders, UsageTotal.Files);

            if (UsageTotal.Unreadable == 0) return line;

            return $"{line}, and {UsageTotal.Unreadable:N0} "
                   + $"folder{(UsageTotal.Unreadable == 1 ? "" : "s")} could not be read";
        }
    }

    /// <summary>
    /// True while this pane is showing the files under a folder that are copies
    /// of each other.
    /// </summary>
    public bool IsDuplicatesListing => VirtualPaths.IsDuplicates(CurrentPath);

    /// <summary>
    /// What the scan came to. Cleared when a listing starts and set when one
    /// finishes, for the reason <see cref="UsageTotal"/> is.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DuplicatesLine))]
    private Copies _duplicateTotal;

    /// <summary>
    /// Every copy but one of each set — what <see cref="SelectExtraCopies"/>
    /// picks.
    ///
    /// **Not a property anything binds to**, deliberately: it is a list of
    /// paths that would mean nothing on screen, and the one thing it is for is
    /// the command below. Replaced wholesale when a scan finishes and emptied
    /// when one starts, so a command pressed against the previous folder's
    /// listing selects nothing rather than rows that are no longer there.
    /// </summary>
    private IReadOnlyList<string> _extraCopies = [];

    /// <summary>
    /// Selects every copy but one of each set.
    ///
    /// **This is the safe route to freeing the space, and the only bulk one.**
    /// Select-all would offer every member of every set, so one Delete would
    /// take both copies of everything; this leaves one of each behind by
    /// construction — see <see cref="CopyRows.Extras"/>, which is where the
    /// property is kept and tested.
    /// </summary>
    [RelayCommand]
    private void SelectExtraCopies() => ReselectPaths([.. _extraCopies]);

    /// <summary>
    /// The band's sentence: how many sets there are, what deleting the spare
    /// copies would give back, and what could not be read on the way.
    ///
    /// **The unreadable count has nowhere else to go**, as the measured
    /// folder's has nowhere but its own band — and here it changes what the
    /// figure MEANS: a scan that could not read part of the tree may have
    /// missed copies, so "nothing here is a copy of anything else" would
    /// otherwise be said about a tree it did not finish reading.
    /// </summary>
    public string DuplicatesLine
    {
        get
        {
            var line = DuplicateTotal.Sets == 0
                ? "nothing here is a copy of anything else"
                : $"{DuplicateTotal.Files:N0} copies in {DuplicateTotal.Sets:N0} "
                  + $"set{(DuplicateTotal.Sets == 1 ? "" : "s")}, "
                  + $"{ByteSize.Format(DuplicateTotal.Reclaimable)} can be freed";

            if (DuplicateTotal.Unreadable == 0) return line;

            return $"{line}, and {DuplicateTotal.Unreadable:N0} "
                   + $"thing{(DuplicateTotal.Unreadable == 1 ? "" : "s")} could not be read";
        }
    }

    /// <summary>
    /// Whether "where does this row actually live" is a question this listing
    /// can answer.
    ///
    /// **Recent asked it and offered no answer.** A search and both Recent
    /// listings gather rows from the whole machine, and all three show the
    /// parent-path column for that one reason — a bare `config.toml` says
    /// nothing about which of four it is — but only a search carried the row
    /// that takes you there. Recent's only addition to the menu was Forget,
    /// which made "stop showing me this" easier to reach than "show me this".
    ///
    /// The bin is deliberately not included: a bin row's <c>FullPath</c> is the
    /// ORIGINAL path the file occupied before it was deleted, so going there
    /// lands on a folder that does not contain the row and may well contain
    /// something else wearing its name.
    /// </summary>
    /// <remarks>
    /// A usage listing is in it because **Up and the breadcrumb cannot get out
    /// of one**: Up is refused for every virtual path, and a virtual listing
    /// draws a single crumb whose command does nothing. Without this row, Back
    /// is the only way back to the folder that was measured — and Back is gone
    /// as soon as the person goes anywhere else first.
    /// </remarks>
    public bool CanGoToLocation =>
        IsSearchListing || IsRecentListing || IsUsageListing || IsDuplicatesListing;
    /// <summary>
    /// Go to where a result actually lives.
    ///
    /// **A search spans the whole filesystem, and a row is a filename.** The
    /// parent-path column answers "which of four config.toml is this"; this
    /// answers "take me there", which is the other half and the one Explorer
    /// calls Open file location. It is also all the popup could do with a
    /// result — choosing one WAS this — so a listing without it would have
    /// taken something away while adding everything else.
    /// </summary>
    [RelayCommand]
    private async Task GoToLocation()
    {
        if (SelectedEntry is not { } entry) return;

        // **In Recent Locations every row is a folder, and revealing a folder
        // ENTERS it** — which is what double-clicking the row already does, so
        // offered there unchanged this would have been a second name for Open
        // rather than an answer to "where is this". Shown in its parent with
        // the row lit instead, which is ShowAsync's whole distinction from
        // RevealAsync. A search keeps the other behaviour: a hit you asked to
        // be taken to is somewhere you want to BE.
        if (entry.IsDirectory && IsRecentListing)
        {
            // **A drive root has no parent directory, and GetParent says so on
            // both platforms — but for two different reasons, so neither is
            // worth leaning on.** The Windows provider returns
            // PathRules.Parent, which answers null for anything IsRoot accepts;
            // the Linux one returns Path.GetDirectoryName, which answers null
            // for `/`. Asking IsRoot here is what turns that null into an
            // ANSWER: the machine, which is where Up already goes from the top
            // of a drive. Recent Locations collects drive roots like any other
            // folder you visit, and without this line the entry would sit on
            // one doing nothing.
            var parent = PathRules.IsRoot(entry.FullPath)
                ? VirtualPaths.Computer
                : _fs.GetParent(entry.FullPath);

            if (!string.IsNullOrEmpty(parent)) await ShowAsync(parent, [entry.FullPath]);

            return;
        }

        await RevealAsync(entry);
    }
}
