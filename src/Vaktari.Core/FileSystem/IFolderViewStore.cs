using Vaktari.Core.Session;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// How one folder should be displayed, when it has an opinion of its own.
///
/// A subset of <see cref="TabState"/>: only the things that are genuinely
/// properties of a *folder* rather than of a tab. Scroll position and history
/// belong to the tab; whether a photo directory shows as a grid belongs to the
/// directory.
/// </summary>
public sealed record FolderViewState
{
    public ViewMode View { get; init; } = ViewMode.Details;
    public SortField Sort { get; init; } = SortField.Name;
    public bool SortDescending { get; init; }
    public GroupMode GroupBy { get; init; } = GroupMode.None;

    /// <summary>Zero means "no opinion" and the pane keeps its current scale.</summary>
    public double FontScale { get; init; }
    public double IconScale { get; init; }
}

/// <summary>
/// Per-folder view state.
///
/// **Written to a central store, never into the folder itself.** Dolphin keeps
/// these in a <c>.directory</c> file inside each directory, which is how its
/// settings interoperate — and also means merely *looking* at a folder can
/// write to it. That fails on read-only mounts and on other people's
/// directories, and it litters network shares, which are exactly the places
/// this application is meant to be good at.
///
/// **But a <c>.directory</c> that already exists is still read**, so a folder
/// Dolphin has been configured for opens the way its owner intended. Same shape
/// as Places, which imports Dolphin and GTK bookmarks while keeping its own
/// store: consume the desktop's data, do not scribble on it.
/// </summary>
public interface IFolderViewStore
{
    /// <summary>Null when this folder has no opinion; the pane keeps its own.</summary>
    FolderViewState? Read(string path);

    void Write(string path, FolderViewState state);

    /// <summary>Forgets one folder's overrides, so it follows the default again.</summary>
    void Forget(string path);

    /// <summary>
    /// How many folders are being remembered.
    ///
    /// Here so the settings dialog can say whether there is anything to forget
    /// — an offer to clear a list that is already empty is a button that does
    /// nothing, and this store is otherwise invisible: it is written to by
    /// merely looking at a folder, and there was no way to see it at all.
    /// </summary>
    int Remembered { get; }

    /// <summary>
    /// Forgets every folder's overrides, and answers how many that was.
    ///
    /// **Forget(path) existed and nothing ever called it.** Turning the
    /// remember-per-folder setting off stops new folders being recorded and
    /// leaves every folder already recorded exactly as it was, so a listing
    /// that had been given a layout kept it with the feature switched off and
    /// no way to say otherwise short of deleting the file by hand.
    /// </summary>
    int ForgetAll();
}
