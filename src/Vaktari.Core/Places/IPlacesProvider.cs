namespace Vaktari.Core.Places;

public enum PlaceKind { UserFolder, Bookmark, Device, RemovableDevice, Network, Virtual }

public sealed record Place
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Path { get; init; }
    public required PlaceKind Kind { get; init; }

    /// <summary>Icon token, resolved to our own icon set by the UI.</summary>
    public required string Icon { get; init; }

    /// <summary>Populated lazily and only for devices — never during listing.</summary>
    public long? CapacityBytes { get; init; }
    public long? FreeBytes { get; init; }

    /// <summary>
    /// False for an unmounted volume or an unreachable share. Rendered dimmed
    /// and in place — never hidden, never silently dropped.
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    public bool CanEject { get; init; }

    /// <summary>
    /// Whether this place can be disconnected — a mapped network drive.
    ///
    /// **A mapped drive could not be got rid of.** Its row offered Open, Open
    /// in a new tab, Pin and Properties, and Eject is for removable media, so
    /// the only way to take Z: off the sidebar was `net use /delete` in a
    /// console. Explorer has Disconnect on exactly this row.
    ///
    /// Set by the provider rather than inferred from Kind in the view: what can
    /// be disconnected is a fact about the platform's own connection table, and
    /// a Network place is not always one of them — Linux's are cifs and nfs
    /// mounts read out of /proc/mounts, and the only unmounter that side is
    /// `gio mount -u`, which has no authority over a kernel mount. That is the
    /// same reason LinuxRemoteMounts refuses a kio-fuse path rather than
    /// failing confusingly.
    /// </summary>
    public bool CanDisconnect { get; init; }
    public bool IsUserPinned { get; init; }
}

public sealed record PlaceGroup(string Label, IReadOnlyList<Place> Places);

/// <summary>
/// The sidebar's data source, and the one place where the Windows/Linux root
/// difference is expressed honestly: drive letters on one side, mount points on
/// the other, rather than a fabricated common root.
/// </summary>
public interface IPlacesProvider
{
    ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct);

    /// <summary>
    /// Fires on mount, unmount, and device arrival or removal — and on a pin
    /// being added or removed.
    ///
    /// **Raised on a background thread.** Handlers marshal to their own if they
    /// need one; the sidebar posts to the dispatcher. This was always true of
    /// the pin path and never written down, and it becomes load-bearing now
    /// that a watcher raises it from a timer with no thread affinity at all.
    /// </summary>
    event EventHandler? PlacesChanged;

    ValueTask PinAsync(string path, string? label, CancellationToken ct);
    ValueTask UnpinAsync(string id, CancellationToken ct);

    /// <summary>
    /// Renames a pinned place.
    ///
    /// **Both providers have persisted a per-pin label since they were written,
    /// and nothing could ever change it.** The import paths already set a
    /// custom one — a shortcut's own filename on Windows, an xbel title or a
    /// GTK bookmark's trailing label on Linux — so the field was read, written
    /// and honoured everywhere except by the person whose sidebar it is. Two
    /// folders both called "src" pinned as two rows called "src", and the only
    /// way to tell them apart was to edit places.json by hand.
    ///
    /// The id is untouched: it is the path, so a rename disturbs neither the
    /// ordering, nor the highlight on the row being viewed, nor a reorder in
    /// flight.
    ///
    /// A blank label is a no-op rather than a blank row.
    /// </summary>
    ValueTask RenameAsync(string id, string label, CancellationToken ct);
    ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct);

    /// <summary>
    /// The name this machine gives a path, when it has a better one than the
    /// path's own last segment. Null when it has not.
    ///
    /// **A drive root was titled "C:".** The tab, the crumb and the window
    /// title all fall back to the last path segment, which for a root is the
    /// root — while the sidebar three inches away called the same drive
    /// "Windows (C:)", because building THAT list is where the volume label is
    /// read. One machine, two names for one drive, and the useless one in the
    /// two places you look most.
    ///
    /// Answered from what the last listing already worked out, never by asking
    /// the disk. This is called on every navigation, and reading a volume label
    /// is a call that blocks for the whole SMB timeout on a mapped drive that
    /// has gone away — which is exactly the kind of thing a tab title must
    /// never wait for. An empty cache answers null and the caller falls back,
    /// which is what it did before.
    ///
    /// Defaulted to "no better name", the way HasAny defaults to the expensive
    /// answer: a provider with nothing to add is correct rather than obliged to
    /// say so, and every caller has a fallback already.
    /// </summary>
    string? NameFor(string path) => null;

    ValueTask MountAsync(string id, CancellationToken ct);

    /// <summary>
    /// Safely removes the volume behind a place id.
    ///
    /// Returns rather than throws, because "something has a file open" is an
    /// ordinary answer and not an exceptional one — and the caller has to tell
    /// the person which of several ordinary answers happened. An id that names
    /// nothing, or names something not removable, comes back as
    /// <see cref="EjectOutcome.NotRemovable"/> without the platform being asked
    /// to do anything.
    /// </summary>
    ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct);

    /// <summary>
    /// First-run import of the user's existing bookmarks — Dolphin's
    /// user-places.xbel, GTK bookmarks, Windows Quick Access. Starting with
    /// their real shortcuts already present matters more for adoption than any
    /// individual feature.
    /// </summary>
    ValueTask<int> ImportExistingAsync(CancellationToken ct);
}
