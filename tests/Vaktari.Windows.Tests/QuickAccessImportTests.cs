using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Importing the folders somebody pinned to Quick access.
///
/// **Nothing read them.** The importer took the two lists that are FILES — the
/// Links folder, which is where Explorer kept Favorites before Quick access
/// replaced it, and Network Shortcuts — and the comment beside it said Quick
/// access was "waiting on the same COM decision as the Trash view and the
/// open-with list". That decision was made a release earlier and recorded in
/// WINDOWS.md §7c; four files in the Windows assembly have used
/// source-generated COM since. The reason outlived itself, and the feature it
/// was blocking stayed blocked because nobody re-read the comment.
///
/// Through the same seam LinuxPlacesProvider gives its mount table, and for the
/// same reason: what these rules do with a list is worth testing on any
/// machine, and the reading of the list is worth testing on exactly one.
///
/// This assembly does not disable test parallelization and the seam is static,
/// so the override would be a race if anything else touched it. Nothing does:
/// this is the only class in the assembly that sets it, and the only one that
/// calls ImportExistingAsync on a real provider. xUnit runs the tests WITHIN a
/// class one at a time, which is what makes that enough.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QuickAccessImportTests : IDisposable
{
    private readonly Func<IReadOnlyList<string>>? _before = QuickAccess.Override;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-quickaccess-" + Guid.NewGuid().ToString("N")[..8]);

    public QuickAccessImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        QuickAccess.Override = _before;

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>A folder that really exists, since the import will check.</summary>
    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private WindowsPlacesProvider Provider(params string[] pinned)
    {
        QuickAccess.Override = () => pinned;

        return new WindowsPlacesProvider(Path.Combine(_root, "state"));
    }

    private static async Task<List<Place>> PinsOf(WindowsPlacesProvider provider)
    {
        await provider.ImportExistingAsync(CancellationToken.None);

        return await PlacesOf(provider);
    }

    /// <summary>The sidebar's own group, without importing anything first.</summary>
    private static async Task<List<Place>> PlacesOf(WindowsPlacesProvider provider)
    {
        var groups = await provider.GetPlacesAsync(CancellationToken.None);

        return [.. groups.Where(g => g.Label == PlaceGroups.Places).SelectMany(g => g.Places)];
    }

    // ---- what it does with the list ------------------------------------------

    /// <summary>The whole finding: a pinned folder becomes a place.</summary>
    [WindowsFact]
    public async Task A_folder_pinned_to_quick_access_is_imported()
    {
        var pinned = Folder("Screenshots");

        var places = await PinsOf(Provider(pinned));

        Assert.Contains(places, p => PathRules.Same(p.Path, pinned));
    }

    /// <summary>Under its own name, which is all Quick access has to offer.</summary>
    [WindowsFact]
    public async Task Under_the_folders_own_name()
    {
        var pinned = Folder("Screenshots");

        var places = await PinsOf(Provider(pinned));

        Assert.Contains(places, p => p.Label == "Screenshots");
    }

    /// <summary>
    /// **A folder the sidebar already shows is not written into places.json.**
    /// Every pinned item on a default profile is one of these — measured on the
    /// machine this was written on, all six pinned folders with a real path
    /// were built-ins.
    ///
    /// **Asserted on the COUNT, not on the sidebar, and that distinction cost
    /// two revert-checks.** BuildUserPlaces already drops a pin whose path a
    /// built-in occupies, so the rendered sidebar shows Documents once however
    /// this behaves — a test that read the sidebar passed with the rule
    /// inverted. What the rule protects is the FILE: without it every launch
    /// writes six redundant pins that the sidebar then silently hides, and the
    /// number returned is where that is visible.
    ///
    /// The rule lives in the repair pass at the end of ImportExistingAsync, not
    /// in the importer. A built-in check inside the importer was written first
    /// and removed: inverting it changed nothing this or anything else could
    /// see, because the repair pass ran immediately afterwards and undid it
    /// either way.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_the_sidebar_already_shows_is_not_imported_again()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        Assert.False(string.IsNullOrEmpty(documents), "this profile has no Documents folder");

        var provider = Provider(documents);

        Assert.Equal(0, await provider.ImportExistingAsync(CancellationToken.None));

        var places = await PlacesOf(provider);

        Assert.Single(places, p => PathRules.Same(p.Path, documents));
    }

    /// <summary>
    /// And one already pinned from a .lnk keeps the name the .lnk gave it: that
    /// name was typed by a person, and Quick access has only the folder's own.
    ///
    /// Counted the same way and for the same reason as above. The label is
    /// checked on the ONE entry the path has rather than inside the predicate:
    /// "the single place matching this path AND this label" is satisfied by one
    /// good entry standing beside a bad one, which is how the first version of
    /// this test passed with the guard removed.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_already_pinned_keeps_the_name_it_had()
    {
        var pinned = Folder("Screenshots");

        var provider = Provider(pinned);

        await provider.PinAsync(pinned, "My shots", CancellationToken.None);

        Assert.Equal(0, await provider.ImportExistingAsync(CancellationToken.None));

        var mine = Assert.Single(
            await PlacesOf(provider), p => PathRules.Same(p.Path, pinned));

        Assert.Equal("My shots", mine.Label);
    }

    /// <summary>
    /// A path that is not there is skipped rather than pinned. Quick access
    /// keeps entries for folders that have been deleted or that live on a drive
    /// which is not mounted, and a place that cannot be listed is worse than an
    /// absent one.
    /// </summary>
    [WindowsFact]
    public async Task A_pin_pointing_at_nothing_is_skipped()
    {
        var places = await PinsOf(Provider(Path.Combine(_root, "went-away")));

        Assert.DoesNotContain(places, p => p.Path.Contains("went-away", StringComparison.Ordinal));
    }

    /// <summary>An empty Quick access changes nothing and does not throw.</summary>
    [WindowsFact]
    public async Task No_pins_at_all_is_not_a_failure()
    {
        var provider = Provider();

        Assert.Equal(0, await provider.ImportExistingAsync(CancellationToken.None));
    }

    // ---- and the reading of it, on this machine ------------------------------

    /// <summary>
    /// **A GUARD, not a regression test.** It asserts what a correct answer
    /// looks like rather than what the answer is, because the answer is
    /// whatever the person at this keyboard has pinned — nothing here can make
    /// it wrong on purpose.
    ///
    /// It is worth having anyway, and it is the only thing that is: it runs the
    /// real COM walk, so a PROPERTYKEY declared the wrong width, an interface
    /// method in the wrong vtable slot or a GUID typed wrong shows up here as a
    /// throw or as garbage, and in no fake anywhere.
    ///
    /// Measured when written, on Windows 11 26200: ten items in Quick access,
    /// seven with System.Home.IsPinned true, and six returned — the seventh
    /// being the Recycle Bin, which is pinned, has no filesystem path, and is
    /// dropped for exactly that reason.
    /// </summary>
    [WindowsFact]
    public void The_real_shell_walk_answers_with_rooted_paths()
    {
        QuickAccess.Override = null;

        foreach (var path in QuickAccess.Pinned())
        {
            Assert.False(string.IsNullOrWhiteSpace(path), "the shell returned a blank path");

            Assert.True(
                Path.IsPathFullyQualified(path),
                $"the shell returned {path}, which is not a path anything could list");
        }
    }
}
