using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A recursive permission change stays inside the folder it was asked of.
///
/// **A folder swapped for a link mid-walk took the change with it.** The walk
/// listed a folder, then opened each subfolder and changed each mode by path
/// later on, and a path follows a link. Another user able to write into a
/// shared tree could swap a subfolder not yet visited for a link to somebody
/// else's home, and "others can read" went on into it.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class AccessWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-accesswalk-" + Guid.NewGuid().ToString("N")[..8]);

    public AccessWalkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        AccessWalk.Listed = null;

        if (OperatingSystem.IsLinux())
        {
            foreach (var folder in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (Exception) { }
            }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private static AccessToggle[] Toggles(bool ownerRead, bool othersRead, bool ownerExecute = false) =>
    [
        new("ur", "owner",  "read",    ownerRead),
        new("uw", "owner",  "write",   true),
        new("ux", "owner",  "execute", ownerExecute),
        new("gr", "group",  "read",    false),
        new("gw", "group",  "write",   false),
        new("gx", "group",  "execute", false),
        new("or", "others", "read",    othersRead),
        new("ow", "others", "write",   false),
        new("ox", "others", "execute", false),
    ];

    private string Write(string relative, UnixFileMode mode)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
        File.SetUnixFileMode(path, mode);
        return path;
    }

    private static Task<AccessOutcome> ApplyAsync(string path, AccessToggle[] toggles)
        => new LinuxPropertiesProvider().SetAccessAsync(path, toggles, recursive: true, null, default).AsTask();

    /// <summary>The whole finding, with the swap made at the one moment it
    /// has to land in: after "top" is listed, before "a" is visited.</summary>
    [PosixFact]
    public async Task A_folder_swapped_for_a_link_mid_walk_is_not_followed()
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var secret = Write("victim/id_ed25519", Private);
        Write("top/a/inner.txt", Private);
        var top = Path.Combine(_root, "top");

        AccessWalk.Listed = listed =>
        {
            if (listed != top) return;

            Directory.Move(Path.Combine(top, "a"), Path.Combine(_root, "a-moved"));
            Directory.CreateSymbolicLink(Path.Combine(top, "a"), Path.Combine(_root, "victim"));
        };

        var outcome = await ApplyAsync(top, Toggles(ownerRead: true, othersRead: true));

        Assert.Equal(Private, File.GetUnixFileMode(secret));
        Assert.True(outcome.Skipped >= 1, "the link was not reported as skipped");
    }

    /// <summary>What the walk is for still happens: files and folders below,
    /// folders given search wherever read is granted.</summary>
    [PosixFact]
    public async Task Everything_below_is_changed_and_folders_stay_searchable()
    {
        var deep = Write("top/one/two/deep.txt", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var top = Path.Combine(_root, "top");

        var outcome = await ApplyAsync(top, Toggles(ownerRead: true, othersRead: true));

        Assert.Equal(0, outcome.Skipped);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead,
            File.GetUnixFileMode(deep));
        Assert.True(File.GetUnixFileMode(Path.Combine(top, "one", "two")).HasFlag(UnixFileMode.OtherExecute));
    }

    /// <summary>A link already in the tree is skipped and its target left
    /// alone, as the path-based walk did.</summary>
    [PosixFact]
    public async Task A_link_in_the_tree_is_skipped()
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var outside = Write("outside/notes.txt", Private);
        Directory.CreateDirectory(Path.Combine(_root, "top"));
        File.CreateSymbolicLink(Path.Combine(_root, "top", "to-notes"), outside);

        var outcome = await ApplyAsync(Path.Combine(_root, "top"), Toggles(ownerRead: true, othersRead: true));

        Assert.Equal(Private, File.GetUnixFileMode(outside));
        Assert.Equal(1, outcome.Skipped);
    }

    /// <summary>
    /// **Taking away the owner's own read reaches the whole tree.** Each folder
    /// is opened before its mode changes, so the change does not lock the walk
    /// out of what it was about to list. (Taking away search as well is another
    /// matter: nothing, a descriptor included, looks inside a folder its owner
    /// may not search — chmod -R stops there too.)
    /// </summary>
    [PosixFact]
    public async Task Taking_away_the_owners_read_still_reaches_what_is_inside()
    {
        var deep = Write("top/one/deep.txt", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var outcome = await ApplyAsync(
            Path.Combine(_root, "top"), Toggles(ownerRead: false, othersRead: false, ownerExecute: true));

        Assert.Equal(0, outcome.Skipped);
        Assert.Equal(UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(deep));
    }
}
