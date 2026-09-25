using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Asking a question of the filesystem, and everything a pane says while it
/// waits for the answer.
///
/// **A search is a PLACE here, not a panel** — see VirtualPaths — so the rows
/// arrive in Entries like any other listing and this file is only what is left
/// over: the box and its draft, the scope, the case and the contents, what the
/// backend can and cannot promise, what it would not read, and the cap with
/// the offer to go past it.
///
/// Sits beside PaneViewModel.SearchHistory, which remembers what was asked
/// before. Split out under roadmap 22; nothing moved changed.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>What was asked, drawn in the band and in the empty state.</summary>
    public string SearchQueryText => VirtualPaths.QueryOf(CurrentPath);

    /// <summary>
    /// Whether "this folder only" has a folder to mean.
    ///
    /// **"This folder only" over This PC searched for a folder called
    /// "vaktari:computer".** A search started somewhere that is not a folder
    /// has no origin to scope to, so the box is disabled rather than ticked and
    /// quietly ignored.
    /// </summary>
    public bool CanScopeSearch =>
        VirtualPaths.OriginOf(CurrentPath) is { } origin && !VirtualPaths.IsVirtual(origin);

    /// <summary>
    /// The scope, as a place rather than a flag.
    ///
    /// **Ticking it navigates**, which is the whole difference from the popup's
    /// version: the scope is part of where you are, so changing it is going
    /// somewhere else and Back returns to the answer you had. The getter reads
    /// the path, so nothing has to keep the two in step.
    /// </summary>
    public bool SearchScopedHere
    {
        get => VirtualPaths.IsScoped(CurrentPath);
        set
        {
            // The navigation below raises this property again as the path
            // lands; without the guard that write starts a second navigation.
            if (!IsSearchListing || value == SearchScopedHere) return;

            // The case and contents flags are carried, not defaulted: narrowing
            // a search you had asked to match capitals must not quietly widen
            // it back.
            _ = NavigateAsync(VirtualPaths.Search(
                SearchQueryText, VirtualPaths.OriginOf(CurrentPath), value,
                SearchMatchesCase, SearchesContents));
        }
    }

    /// <summary>
    /// Whether this backend can be asked to mind the capitals, which is what
    /// decides whether the box is drawn at all.
    ///
    /// **A box that cannot be honoured is the bug this came from wearing a
    /// tick.** SearchQuery.CaseSensitive had two readers and no writer; a
    /// control offered over a backend that ignores it — an index answering its
    /// own way — would be the same silence one layer up. Drawn rather than
    /// disabled, because unlike the scope box there is nothing search-specific
    /// to explain: the answer is a property of the backend, not of where you
    /// are.
    ///
    /// **No change notification, and that is an ordering rather than a hope.**
    /// <see cref="Search"/> is assigned by <c>WindowServices.Create</c>, which
    /// the first MainWindow's constructor calls BEFORE it builds the
    /// ShellViewModel whose panes this band binds to and before it assigns
    /// DataContext — three statements of one constructor, in that order, and a
    /// binding is not read until its DataContext attaches. Every later window
    /// is handed those same services, so by then it is already assigned.
    /// </summary>
    public bool CanMatchCase => Search?.SupportsCaseSensitivity ?? false;

    /// <summary>
    /// Whether the capitals in the question are part of it.
    ///
    /// Shaped exactly like <see cref="SearchScopedHere"/> — read off the path,
    /// written by navigating — so asking the same words two ways is being in
    /// two places and Back returns to the previous answer rather than re-running
    /// it.
    /// </summary>
    public bool SearchMatchesCase
    {
        get => VirtualPaths.MatchesCase(CurrentPath);
        set
        {
            // The navigation below raises this property again as the path
            // lands; without the guard that write starts a second navigation.
            if (!IsSearchListing || value == SearchMatchesCase) return;

            _ = NavigateAsync(VirtualPaths.Search(
                SearchQueryText, VirtualPaths.OriginOf(CurrentPath), SearchScopedHere,
                value, SearchesContents));
        }
    }

    /// <summary>
    /// Whether the "Search contents" box is drawn: the backend can look inside
    /// files, and the question is one a file's contents could answer.
    ///
    /// **Not for a pattern.** "*.pdf" is a question about names, and both
    /// walks and the band read it that way whatever the box says — so a box
    /// offered beside one would navigate, walk again and come back with the
    /// same answer. Hidden rather than disabled, which is this band's rule
    /// for a tick that could change nothing (see the Match case box).
    ///
    /// The backend half needs no change notification, on the ordering
    /// <see cref="CanMatchCase"/> stands on. The question half does, because
    /// it moves with the path, and OnCurrentPathChanged raises it.
    /// </summary>
    public bool CanSearchContents =>
        (Search?.SupportsContentSearch ?? false) && !SearchListing.QueryFor(CurrentPath).IsPattern;

    /// <summary>
    /// Whether a file's contents can answer, as well as its name.
    ///
    /// **SearchQuery.MatchContent had no writer, like CaseSensitive before
    /// it**, and on Windows no reader either: the one backend that could look
    /// inside files was Baloo, which did so whatever it was asked. Shaped
    /// exactly like <see cref="SearchMatchesCase"/> — read off the path,
    /// written by navigating — so the question with contents and the question
    /// without are two places, and Back goes from one to the other. It asks
    /// again when it gets there: nothing is cached, and a content search is
    /// read afresh.
    ///
    /// Off by default, and a fresh search from a folder starts off, because
    /// reading every text file is a different order of cost from reading
    /// names, and the person asking is the one who should decide to pay it.
    /// </summary>
    public bool SearchesContents
    {
        get => VirtualPaths.MatchesContent(CurrentPath);
        set
        {
            // The navigation below raises this property again as the path
            // lands; without the guard that write starts a second navigation.
            if (!IsSearchListing || value == SearchesContents) return;

            _ = NavigateAsync(VirtualPaths.Search(
                SearchQueryText, VirtualPaths.OriginOf(CurrentPath), SearchScopedHere,
                SearchMatchesCase, value));
        }
    }

    /// <summary>
    /// The box's tooltip, which has to say what ticking it costs — and that
    /// depends on who is answering.
    ///
    /// **An index has already read the files; a walk has to.** Where Baloo
    /// answers, the tick costs nothing and reaches whatever Baloo extracts text
    /// from, PDFs and office documents included. Over a walk — including the
    /// one Baloo's provider falls back to when the index has nothing — it opens
    /// every file in reach, one after another, and finds text only in the
    /// plain-text ones. So the same words on both would be a promise one of
    /// them does not keep, and this follows <see cref="SearchUnindexed"/>,
    /// which learns of that fallback while it runs.
    /// </summary>
    public string SearchContentsHint =>
        !SearchUnindexed
            ? "Also find files by the words in them, as the index has read them"
            : "Also find plain-text files by what is in them. Every file is opened to look, "
              + "so it is slower";

    /// <summary>
    /// The box's own words, which carry the truth when it is disabled — a box
    /// still reading "This folder only" while being ignored claims a scope the
    /// search does not have.
    /// </summary>
    public string SearchScopeLabel
    {
        get
        {
            var origin = VirtualPaths.OriginOf(CurrentPath);

            // **"Everywhere" was not true on either platform.** Windows walked
            // the fixed drives, so the stick just plugged in was skipped;
            // Linux walked the home folder alone, so every other disk was. The
            // provider says what it actually covers now, because the provider
            // is what decides the roots — and it still says "everywhere" when
            // there is no provider, which is the one case where nothing is
            // being covered and no narrower claim would be truer.
            var reach = Search?.Everywhere ?? "everywhere";

            if (origin is null) return $"searching {reach}";

            return VirtualPaths.IsVirtual(origin)
                ? $"{VirtualPaths.Label(origin)} is not a folder — searching {reach}"
                : $"Only in {FolderName(origin)}";
        }
    }

    /// <summary>
    /// A folder path as the one word a person would call it.
    ///
    /// Shared rather than written twice: the box says "Only in Documents" and
    /// the line under it says "every folder in Documents is read in turn", and
    /// two copies of the trimming would name the same folder differently the
    /// first time either moved. The trailing separator has to go first — a
    /// GetFileName of "C:\Users\me\" is the empty string.
    /// </summary>
    private static string FolderName(string path)
        => System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>
    /// What is being typed into the search field, which is NOT what is being
    /// searched for.
    ///
    /// **The two used to be one string**, so every keystroke was a query: typing
    /// "claude" launched six walks, each cancelled by the next but only after it
    /// had begun reading directories. A draft that becomes a search on Enter
    /// needs no debounce, no minimum length and no cancellation — the three
    /// things the live version spent its complexity on.
    /// </summary>
    [ObservableProperty] private string _searchDraft = "";

    /// <summary>Whether the field has anything in it, which is what hides the
    /// "Ctrl+F" hint sitting at its right-hand end.</summary>
    public bool HasSearchDraft => SearchDraft.Length > 0;

    partial void OnSearchDraftChanged(string value) => OnPropertyChanged(nameof(HasSearchDraft));

    /// <summary>
    /// Whether the path bar shows the search FIELD or just its icon. The field
    /// is 230px that never yielded, and on the active side of a split it plus
    /// the filter button took the whole bar — leaving the crumbs reading "C:".
    /// </summary>
    [ObservableProperty] private bool _isSearchOpen;

    /// <summary>
    /// A one-shot trigger for the focus behaviour, not a state: set false then
    /// true to re-fire it, because after the first Ctrl+F it is true for ever
    /// and a second one would otherwise leave the caret where it was.
    /// </summary>
    [ObservableProperty] private bool _isSearchFocused;

    /// <summary>
    /// Ctrl+F: open the field and put the caret in it.
    ///
    /// Seeded from the search you are already looking at, so refining a
    /// question means editing it rather than retyping it — and abandoning the
    /// edit leaves the results you had, because they are a listing rather than
    /// a popup keyed on the box's contents.
    /// </summary>
    [RelayCommand]
    private void BeginSearch()
    {
        SearchDraft = IsSearchListing ? SearchQueryText : "";

        IsSearchOpen = true;

        IsSearchFocused = false;
        IsSearchFocused = true;
    }

    /// <summary>
    /// Enter: go to the results.
    ///
    /// **Enter used to do nothing at all**, so type-then-Enter — the reflex in
    /// both Explorer and Dolphin — dead-ended, and a result could only be
    /// reached with the mouse.
    ///
    /// The scope carries: retyping over a search you have already narrowed
    /// keeps it narrowed, and the origin stays the folder it started from
    /// rather than becoming the search path itself.
    /// </summary>
    [RelayCommand]
    private void RunSearch()
    {
        var text = SearchDraft.Trim();

        // Nothing typed is not a question. Whitespace is the same nothing: a
        // path built from it looks like a real search and asks the index to
        // match every file on the machine.
        if (text.Length == 0) return;

        var origin = IsSearchListing ? VirtualPaths.OriginOf(CurrentPath) : CurrentPath;

        // A fresh search means the folder you are standing in, the way
        // Explorer's box does. Asking for it from This PC is not refused here:
        // "a place that is not a folder cannot be the scope" is a fact about
        // the path, answered once by VirtualPaths.IsScoped so that this road
        // and a hand-edited session file get the same answer.
        var scoped = !IsSearchListing || SearchScopedHere;

        // The band above the results says what was asked, so the field has
        // nothing left to show and the crumbs can have their width back.
        IsSearchOpen = false;

        // Case and contents carry the same way the scope does, and need no
        // IsSearchListing clause of their own: both answer false for a folder
        // path, so a search begun from a folder starts case-insensitive and
        // names-only, and one refined from a search keeps what it was set to.
        _ = NavigateAsync(VirtualPaths.Search(
            text, origin, scoped, SearchMatchesCase, SearchesContents));
    }

    /// <summary>
    /// Escape: put the field away.
    ///
    /// **It no longer takes the results with it**, which is the change that
    /// makes this safe. The popup was keyed on the box's contents, so clearing
    /// the box was the only way to close it and the only way out of a running
    /// walk — one gesture for three different intentions.
    /// </summary>
    [RelayCommand]
    private void DismissSearch()
    {
        SearchDraft = "";
        IsSearchFocused = false;
        IsSearchOpen = false;
    }

    /// <summary>
    /// Collapses the field back to its icon when you click away from it, but
    /// only when nothing is half-typed in it.
    /// </summary>
    [RelayCommand]
    private void CloseSearchIfEmpty()
    {
        if (SearchDraft.Length == 0) IsSearchOpen = false;
    }
    /// <summary>
    /// The line under a search that no index is answering, which is the whole
    /// explanation for the wait.
    ///
    /// **The sentence was written here and no .axaml ever named the property,
    /// so it appeared nowhere** — and nothing could have reached it in any
    /// case. The getter chose between this and the backend's own name on
    /// <c>ISearchProvider.IsAvailable</c>, which both shipped providers
    /// hardcoded true, so the arm that ran was always the other one: "searching
    /// with directory walk" on Windows and "searching with baloo" on Fedora, in
    /// front of somebody who was looking for a file. There is no branch naming
    /// the implementation now.
    ///
    /// **It follows the scope, because the box directly above it does.** Said
    /// unconditionally, a search narrowed to one folder read "Only in
    /// Documents" with "reading every folder" underneath — the two halves of
    /// one band contradicting each other, which on Windows was every scoped
    /// search there has ever been. The scope comes from
    /// <c>VirtualPaths.ScopeOf</c>, which is the same string handed to the
    /// backend, so the sentence cannot claim a reach the query does not have.
    ///
    /// **And it is not in the progressive, because it outlives the walk.** The
    /// bar and the Stop beside it are gated on <see cref="IsLoading"/> and go
    /// when the search settles; this one stays for as long as the results are
    /// on screen, so "searching by reading every folder" would have gone on
    /// claiming a search was running for ever. Read in turn is how this search
    /// works, during and after.
    ///
    /// **And it says what is read, which the contents box changes.** Names
    /// come from the folders; contents come from opening every file in them —
    /// every file, not every text file, because a file is opened before it can
    /// be told to be binary — which is the slow part and the part worth being
    /// told about. Asked of the same query the walk runs, so a pattern, which
    /// reads names only, is not claimed to read files.
    /// </summary>
    public string SearchBackendLine =>
        (VirtualPaths.ScopeOf(CurrentPath) is { Length: > 0 } scope
            ? $"every {WhatIsRead} in {FolderName(scope)} is read in turn — {WhyWalked}"
            : $"every {WhatIsRead} is read in turn — {WhyWalked}")
        + SearchCaveat;

    /// <summary>
    /// Why no index is answering. There may BE one: on a KDE desktop Baloo is
    /// asked first and the walk runs only when it has nothing to say, and
    /// "there is no index on this machine" would then be false on the one
    /// machine where somebody is most likely to know better.
    /// </summary>
    private string WhyWalked => _walkingInstead
        ? "the index had nothing for this"
        : "there is no index on this machine";

    /// <summary>
    /// Set when the backend reports that it meant to answer from its index and
    /// is walking instead — see <see cref="Core.Search.SearchQuery.WalkingInstead"/>.
    /// Cleared as each load begins, like the other things a load learns.
    /// </summary>
    private bool _walkingInstead;

    /// <summary>
    /// What the backend said about walking, arriving from the pool. Posted to
    /// the dispatcher, and dropped if a newer load has started meanwhile.
    /// </summary>
    private void OnWalkingInstead(int generation)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation) return;

            _walkingInstead = true;

            OnPropertyChanged(nameof(SearchUnindexed));
            OnPropertyChanged(nameof(SearchBackendLine));
            OnPropertyChanged(nameof(SearchContentsHint));
        });

    private string WhatIsRead =>
        SearchListing.QueryFor(CurrentPath).ReadsContents ? "folder and file" : "folder";

    /// <summary>
    /// What the backend leaves out, appended to the line above. **A walk that
    /// skips something and does not say so is a search that lies by omission**
    /// — on Windows it skipped every System-attributed file, and the only
    /// place a person could have learned that was the source. Empty when the
    /// provider has nothing to confess, so the line reads exactly as before.
    /// </summary>
    private string SearchCaveat =>
        Search?.Caveat is { Length: > 0 } caveat ? $"; {caveat}" : "";

    /// <summary>
    /// Whether that line is drawn at all: an index answering has nothing to
    /// explain, and a sentence shown over every search would be read as
    /// furniture within a day.
    ///
    /// **Asked of the question, not of the backend.** A provider decides this
    /// per query — LinuxSearchProvider sends a glob past Baloo to the walk,
    /// because the index stores words rather than filename patterns — so a flag
    /// read off the provider alone would have hidden the warning for "*.pdf" on
    /// every KDE box while home and every mounted drive were being read. The
    /// query is built by <c>SearchListing.QueryFor</c>, which is what the
    /// search itself runs on.
    ///
    /// A missing backend takes the warning too. Nothing is indexing then
    /// either, which is what the sentence says, and it is the arm the
    /// interface's own <c>AnswersFromIndex => false</c> default was written
    /// for.
    ///
    /// **No change notification for <see cref="Search"/>, and that is an
    /// ordering rather than a hope** — the same one <see cref="CanMatchCase"/>
    /// stands on. It is assigned by <c>WindowServices.Create</c> BEFORE the
    /// first MainWindow's constructor builds the ShellViewModel whose panes
    /// this band binds to, and a binding is not read until its DataContext
    /// attaches. The PATH half does change, and OnCurrentPathChanged raises it.
    /// </summary>
    public bool SearchUnindexed =>
        _walkingInstead
        || Search is not { } backend
        || !backend.AnswersFromIndex(SearchListing.QueryFor(CurrentPath, SearchLimit));

    /// <summary>
    /// Ends a running search where it stands, keeping the hits already found.
    ///
    /// **An unindexed walk is unbounded**, and the only other way out of one is
    /// to navigate away, which takes the results with it. Stop keeps them, and
    /// that is the whole difference between the two.
    ///
    /// The load's own cancellation path deliberately clears nothing, because it
    /// assumes a newer navigation is following and owns the state. Nothing
    /// follows this one, so it finishes the listing itself.
    /// </summary>
    [RelayCommand]
    private void StopSearch()
    {
        if (!IsSearchListing || !IsLoading) return;

        _cts?.Cancel();

        IsLoading = false;
        IsLoaded = true;

        // The completion block that says this for a finished search is never
        // reached by a stopped one, and what was skipped before the Stop is
        // still missing from the results being kept.
        SearchSkippedLine = SkippedLine(_searchSkips);

        Status = Entries.Count == 0
            ? "stopped"
            : $"stopped — {Entries.Count:N0} results so far";
    }

    /// <summary>
    /// Where the running search counts the files it would not read. A fresh
    /// one per load, made where the load starts, so a count can never carry
    /// from one question into the next.
    /// </summary>
    private ContentSkips? _searchSkips;

    /// <summary>
    /// What a content search left unread, once it has stopped. Empty when
    /// nothing was.
    ///
    /// **A file the search would not read is one it cannot say the text is not
    /// in.** Refusing a 2 GB log is right, and so is refusing to download
    /// every file in a synced folder to look inside it — but either, unsaid,
    /// turns "I did not look" into "it is not there".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchSkipped))]
    private string _searchSkippedLine = "";

    public bool HasSearchSkipped => SearchSkippedLine.Length > 0;

    /// <summary>The sentence itself, apart from the pane so its grammar can be pinned.</summary>
    internal static string SkippedLine(ContentSkips? skips)
    {
        if (skips is null) return "";

        var parts = new List<string>(2);

        // In the unit the Size column uses. The limit is 64 × 1024 × 1024
        // bytes, and calling that "64 MB" is the decimal name on a binary
        // quantity that ByteSize was written to stop.
        if (skips.TooLarge is var large and > 0)
            parts.Add(large == 1
                ? $"1 file over {ByteSize.Format(ContentMatcher.MaxBytes)} was not read"
                : $"{large:N0} files over {ByteSize.Format(ContentMatcher.MaxBytes)} were not read");

        if (skips.Online is var online and > 0)
            parts.Add(online == 1
                ? "1 file kept online was not downloaded to be read"
                : $"{online:N0} files kept online were not downloaded to be read");

        return string.Join("; ", parts);
    }

    /// <summary>
    /// How many matches this search may return before it gives up.
    ///
    /// Per pane rather than a constant, because <see cref="SearchMoreAsync"/>
    /// raises it: the cap exists to stop an unindexed walk running for ever,
    /// not to decide how many answers a person is allowed to have.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchLimitLine))]
    private int _searchLimit = SearchListing.Limit;

    /// <summary>
    /// **A search that ran out of budget looked exactly like one that ran out
    /// of tree.** Both end with the bar gone, the Stop gone and the listing
    /// settled; only one of them is an answer. This is the difference, and
    /// nothing carried it before — the walk broke out of its loop and returned.
    /// </summary>
    [ObservableProperty] private bool _searchHitLimit;

    /// <summary>What the band says when the walk was cut off.</summary>
    public string SearchLimitLine =>
        $"stopped after the first {SearchLimit:N0} matches — there are more";

    /// <summary>
    /// Honest about the cost. There is no cursor to resume from — the walk
    /// keeps no state between runs — so this is the same walk again with a
    /// bigger budget, not a continuation, and the hint says so rather than
    /// letting "Keep looking" imply otherwise.
    /// </summary>
    public string SearchMoreHint =>
        $"search again for another {SearchListing.Limit:N0} — the walk starts from the beginning";

    /// <summary>
    /// The way on from a truncated answer.
    ///
    /// **A message with no next step is a better-worded dead end.** The two
    /// things a person could already do about a cut-off search — narrow the
    /// words, tick "this folder only" — both throw away the answer in front of
    /// them. This keeps the question and raises the cap.
    ///
    /// Guarded as well as hidden: the button is bound to
    /// <see cref="SearchHitLimit"/>, but a command is reachable without its
    /// button, and re-reading every fixed drive to change nothing is an
    /// expensive way to do nothing.
    /// </summary>
    [RelayCommand]
    private async Task SearchMoreAsync()
    {
        if (!SearchHitLimit) return;

        SearchLimit += SearchListing.Limit;

        await RefreshAsync();
    }
}
