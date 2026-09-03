using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The search panel. Results live in the sidebar rather than a modal dialog, so
/// they survive navigation — you can walk through hits one at a time with the
/// list still on screen, which is the whole reason the rail exists.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly ISearchProvider? _search;
    private readonly Func<string?> _currentPath;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Incremented per query. A cancelled search can still have a batch in
    /// flight to the dispatcher; without this it lands after the list was
    /// cleared and the previous query's hits reappear under the new one.
    /// </summary>
    private int _generation;

    public SearchViewModel(ISearchProvider? search, Func<string?> currentPath)
    {
        _search = search;
        _currentPath = currentPath;
    }

    public BulkObservableCollection<FileEntry> Results { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _scopeToCurrentFolder = true;
    [ObservableProperty] private string _status = "";

    /// <summary>Drives whether the sidebar shows results instead of its sections.</summary>
    public bool HasQuery => Query.Length > 0;

    public string BackendName => _search?.BackendName ?? "none";

    /// <summary>Shown when falling back to a walk, so slow results are explained.</summary>
    public bool IsIndexed => _search?.BackendName == "baloo";

    /// <summary>Raised when a result is chosen; the shell navigates to it.</summary>
    public event EventHandler<FileEntry>? ResultChosen;

    /// <summary>
    /// Opens the top result, which is what Enter means in a search box.
    ///
    /// **Enter did nothing at all.** The results were a list of buttons with no
    /// selection and no keyboard route, so type-then-Enter — the reflex in both
    /// Explorer and Dolphin — dead-ended, and a result could only be reached
    /// with the mouse. That is an accessibility blocker as much as an
    /// inconvenience.
    /// </summary>
    [RelayCommand]
    public void OpenFirst()
    {
        if (Results.Count > 0) Open(Results[0]);
    }

    [RelayCommand]
    public void Open(FileEntry? entry)
    {
        if (entry is { } value) ResultChosen?.Invoke(this, value);
    }

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        Restart();
    }
    partial void OnScopeToCurrentFolderChanged(bool value) => Restart();

    /// <summary>
    /// The folder a scoped search runs in, or null for "everywhere".
    ///
    /// **"This folder only" over This PC, the bin or either Recent listing
    /// searched for a folder called "vaktari:computer".** The box is ticked by
    /// default and the scope was the pane's raw path, which for a virtual
    /// listing is an internal scheme rather than a directory.
    ///
    /// On Windows the walk pushes it as a root, the directory read throws, the
    /// per-directory catch swallows it, and the panel reports "no results" — a
    /// definite negative about the whole machine. On Linux the enumerator
    /// throws before yielding anything and the panel says the folder is not
    /// there any more, about a place you are standing in.
    ///
    /// Explorer searches every drive from This PC. The honest scope for a
    /// listing that is not a folder is none.
    /// </summary>
    /// <summary>Whether "this folder only" has a folder to mean.</summary>
    public bool CanScopeToCurrentFolder => !VirtualPaths.IsVirtual(_currentPath());

    /// <summary>
    /// The scope box's own words. A box still labelled "This folder only" over
    /// This PC claims a scope the search does not have, so the label carries
    /// the truth instead of the box quietly being ignored.
    /// </summary>
    public string ScopeLabel
    {
        get
        {
            var path = _currentPath();

            // Only when it IS virtual: Label falls back to "Recent locations"
            // for anything it does not recognise.
            return VirtualPaths.IsVirtual(path)
                ? $"{VirtualPaths.Label(path!)} is not a folder — searching everywhere"
                : "This folder only";
        }
    }

    internal static string? ScopeFor(string? currentPath, bool scopeToCurrentFolder)
        => scopeToCurrentFolder && !VirtualPaths.IsVirtual(currentPath) ? currentPath : null;

    private void Restart()
    {
        // Both derive from where the pane is, which changes underneath this —
        // and Restart runs on every keystroke and every toggle, which is
        // whenever the popup is on screen.
        OnPropertyChanged(nameof(CanScopeToCurrentFolder));
        OnPropertyChanged(nameof(ScopeLabel));

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        Results.Reset();

        // **One character was refused with "keep typing…".** A single letter is
        // a real query -- "b" for a folder of build outputs, "~" for the editor
        // backups -- and every other file manager runs it. The debounce below
        // and the result cap already bound what a broad query costs, so the
        // length was buying nothing that those two do not.
        if (Query.Length == 0)
        {
            IsSearching = false;
            Status = "";
            return;
        }

        _cts = new CancellationTokenSource();
        _ = RunAsync(Query, ++_generation, _cts.Token);
    }

    private async Task RunAsync(string text, int generation, CancellationToken ct)
    {
        if (_search is null) { Status = "no search backend"; return; }

        // Debounce. Every keystroke past the second used to start a full walk
        // immediately, so typing "claude" launched six of them — each one
        // cancelled by the next, but only after it had already begun reading
        // directories. Measured on Windows, an unindexed walk of a profile
        // directory is about five seconds, so the wasted work was most of it.
        //
        // NOT ConfigureAwait(false): this resumes on the dispatcher, and
        // everything after it touches observable properties the UI is bound to.
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IsSearching = true;
        Status = $"searching ({BackendName})…";

        var query = new SearchQuery
        {
            Text = text,
            ScopePath = ScopeFor(_currentPath(), ScopeToCurrentFolder),
            MaxResults = 500,
        };

        var found = 0;
        var batch = new List<FileEntry>(64);

        try
        {
            // Results stream in as the backend produces them, so a slow walk
            // fills the panel progressively instead of showing nothing for
            // twenty seconds and then everything at once.
            await foreach (var entry in _search.SearchAsync(query, ct).ConfigureAwait(false))
            {
                batch.Add(entry);
                found++;

                if (batch.Count < 32) continue;

                var flush = batch;
                batch = new List<FileEntry>(64);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _generation) return;
                    Results.AddRange(flush);
                    Status = $"{found} results…";
                });
            }

            if (batch.Count > 0)
            {
                var tail = batch;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation == _generation) Results.AddRange(tail);
                });
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _generation) return;

                Status = found == 0
                    ? $"no results ({BackendName})"
                    : $"{found} results ({BackendName})";
                IsSearching = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _generation) return;
                Status = Vaktari.Core.FileSystem.Failures.Describe(ex, "search there");
                IsSearching = false;
            });
        }
    }
}
