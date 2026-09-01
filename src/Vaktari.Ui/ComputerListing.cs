using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;

namespace Vaktari.Ui;

/// <summary>
/// Every drive on the machine, as a listing — Explorer's This PC.
///
/// **Built from <see cref="IPlacesProvider"/> rather than from DriveInfo.** The
/// providers already answer this question per platform, with the labels, the
/// capacities and the availability the sidebar shows: drive letters on Windows,
/// mount points on Linux. Enumerating drives a second time here would be a
/// second answer to one question, and the two would eventually disagree —
/// exactly the fault this codebase has been finding all week.
///
/// Shaped as the same <see cref="IAsyncEnumerable{T}"/> the filesystem provider
/// returns, so everything downstream — sorting, filtering, the three layouts,
/// selection — runs unchanged and knows nothing about where the rows came from.
/// </summary>
public static class ComputerListing
{
    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
        IPlacesProvider? places,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (places is null)
        {
            yield return [];
            yield break;
        }

        var groups = await places.GetPlacesAsync(ct).ConfigureAwait(false);

        yield return Build(groups);
    }

    /// <summary>
    /// The rows, from the groups the sidebar shows.
    ///
    /// Separated from the enumeration so it can be tested without a provider
    /// standing behind it — the interesting part is which places become rows
    /// and what they are called, not the plumbing.
    /// </summary>
    internal static List<FileEntry> Build(IReadOnlyList<PlaceGroup> groups)
    {
        var rows = new List<FileEntry>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var group in groups)
        foreach (var place in group.Places)
        {
            // **Drives and shares only.** The user folders and the pins are
            // places to go, not hardware — Documents is not a drive, and
            // listing it here would make This PC a second copy of the sidebar
            // rather than an answer to "what is attached to this machine".
            if (place.Kind is not (PlaceKind.Device or PlaceKind.RemovableDevice
                                   or PlaceKind.Network))
                continue;

            if (place.Path.Length == 0) continue;

            // One row per volume. A place can legitimately appear in two
            // groups — a removable drive that is also pinned — and two rows for
            // one drive is the sort of thing nobody notices until they select
            // both and act on them.
            if (!seen.Add(place.Path)) continue;

            rows.Add(new FileEntry(
                place.Label,
                place.Path,

                // The capacity, so the size column says something true about a
                // drive rather than zero. Free space is what the sidebar shows,
                // but a listing's size column means "how big is this".
                place.CapacityBytes ?? 0,

                // Drives have no meaningful modified time, and inventing one
                // would sort them by a lie. The epoch reads as "no date".
                DateTimeOffset.UnixEpoch,

                // A directory, because that is what opening one does: navigate
                // into it. Everything downstream keys on this flag.
                EntryFlags.Directory));
        }

        return rows;
    }
}
