using Vaktari.Core.Session;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// How one folder should be displayed, when it has an opinion of its own.
///
/// A subset of <see cref="TabState"/>: only the things that are genuinely
/// properties of a *folder* rather than of a tab. Scroll position and history
/// belong to the tab; whether a photo directory shows as a grid belongs to the
/// directory.
///
/// **The subset used to stop at layout, sort and grouping**, so "remember the
/// view for each folder" remembered three of the six things the view options
/// menu offers: hidden files and the four column ticks were not in this record
/// at all, and were therefore neither written nor restored.
/// </summary>
public sealed record FolderViewState
{
    /// <summary>
    /// Null is "no opinion" throughout, and the pane keeps what it had.
    ///
    /// **These four were <c>ViewMode.Details</c>, <c>SortField.Name</c>, false
    /// and <c>GroupMode.None</c>, and a record has no way to say it did not
    /// mean them.** The application's own writer fills all four every time, so
    /// this only ever showed on the Dolphin path: a <c>.directory</c> naming
    /// nothing but <c>SortRole=size</c> came back carrying Details and
    /// ungrouped as well, and arriving in that folder pulled a pane out of the
    /// grid it was in. The value that means "leave it alone" now exists.
    /// </summary>
    public ViewMode? View { get; init; }
    public SortField? Sort { get; init; }
    public bool? SortDescending { get; init; }
    public GroupMode? GroupBy { get; init; }

    /// <summary>Zero means "no opinion" and the pane keeps its current scale.</summary>
    public double FontScale { get; init; }
    public double IconScale { get; init; }

    /// <summary>
    /// Whether this folder shows hidden files. Null means "no opinion".
    ///
    /// **A plain bool could not be added here.** Every entry already in
    /// somebody's folder-views.json was written before this key existed, and an
    /// absent key deserialises as <c>default(bool)</c> — so on a plain bool
    /// every one of those folders would have come back saying "hide them", and
    /// arriving at one with Ctrl+H on would have put them away. Nullable is the
    /// same trick the scales above play with zero, spelled the way a bool can
    /// spell it.
    /// </summary>
    public bool? ShowHidden { get; init; }

    /// <summary>
    /// Which columns this folder shows, or null for "no opinion" — nested for
    /// the reason <see cref="ShowHidden"/> is nullable, and as one object
    /// because the four travel together: a folder has been given a set of
    /// columns or it has not, and four independent nullables could express
    /// three-quarters of an answer that nothing produces.
    /// </summary>
    public FolderColumns? Columns { get; init; }
}

/// <summary>
/// The four column ticks, phrased exactly as the pane and the session phrase
/// them so the mapping stays a copy rather than a translation: false is what
/// the pane showed before there was a chooser.
/// </summary>
public sealed record FolderColumns
{
    public bool HideSize { get; init; }
    public bool HideModified { get; init; }
    public bool ShowType { get; init; }
    public bool ShowCreated { get; init; }
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
