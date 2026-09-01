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
    ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct);

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
