using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The name this machine gives a drive, remembered for the places that need it
/// without asking the disk.
///
/// **A drive root was titled "C:".** The tab, the crumb and the window title
/// all fall back to the path's last segment, which for a root is the root —
/// while the sidebar three inches away called the same drive "Windows (C:)",
/// because building THAT list is where the volume label is read.
///
/// It is read from the last listing rather than on demand, and that is not an
/// optimisation: reading a volume label blocks for the whole SMB timeout on a
/// mapped drive that has gone away, and a tab title must never wait for one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriveNameCacheTests : IDisposable
{
    /// <summary>Its own state directory, so a test cannot read or write the
    /// pins of whoever is running it.</summary>
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "vaktari-drivenames-" + Guid.NewGuid().ToString("N")[..8]);

    private WindowsPlacesProvider Provider() => new(_state);

    public void Dispose()
    {
        try { Directory.Delete(_state, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// **Nothing is known until the places have been listed once.** That is the
    /// whole shape of this: the answer is a by-product of building the sidebar,
    /// so before that there is nothing to give and the caller falls back — which
    /// is what it did before there was an answer at all.
    /// </summary>
    [WindowsFact]
    public void Before_any_listing_it_names_nothing()
    {
        using var places = Provider();

        Assert.Null(places.NameFor(@"C:\"));
    }

    /// <summary>
    /// And after one, every drive it listed can be named — with the label and
    /// the letter, the way the sidebar row reads.
    /// </summary>
    [WindowsFact]
    public async Task After_a_listing_every_drive_it_found_has_a_name()
    {
        using var places = Provider();

        var groups = await places.GetPlacesAsync(CancellationToken.None);

        var drives = groups
            .SelectMany(g => g.Places)
            .Where(p => p.Kind is Vaktari.Core.Places.PlaceKind.Device)
            .ToList();

        Assert.NotEmpty(drives);

        foreach (var drive in drives)
        {
            var name = places.NameFor(drive.Path);

            Assert.Equal(drive.Label, name);

            // The point of the whole thing: something better than the path.
            Assert.NotEqual(PathRules.LeafName(drive.Path), name);
        }
    }

    /// <summary>
    /// A path that is not a drive gets no name, so this cannot start renaming
    /// ordinary folders after whatever the sidebar happens to hold.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_is_not_given_a_drives_name()
    {
        using var places = Provider();

        await places.GetPlacesAsync(CancellationToken.None);

        Assert.Null(places.NameFor(Path.Combine(Path.GetTempPath(), "not-a-drive")));
    }

    /// <summary>
    /// **Case-insensitively, because these are Windows paths.** The sidebar
    /// records a root as the drive hands it over and a navigation can spell it
    /// either way; an ordinal lookup would answer for "C:\" and not for "c:\".
    /// </summary>
    [WindowsFact]
    public async Task The_letter_is_matched_however_it_is_spelled()
    {
        using var places = Provider();

        var groups = await places.GetPlacesAsync(CancellationToken.None);

        var drive = groups
            .SelectMany(g => g.Places)
            .First(p => p.Kind is Vaktari.Core.Places.PlaceKind.Device);

        Assert.Equal(
            places.NameFor(drive.Path),
            places.NameFor(drive.Path.ToLowerInvariant()));
    }
}
