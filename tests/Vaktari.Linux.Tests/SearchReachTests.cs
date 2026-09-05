using Vaktari.Core.Search;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What an unscoped search covers on Linux, and what the box says it covers.
///
/// **It was the home folder and nothing else.** A machine with a second disk,
/// or a stick plugged in, answered a search of "everywhere" — asked from
/// somewhere the box cannot scope to, such as This PC or another search
/// listing — with results from one directory tree, while saying "everywhere"
/// beside it.
///
/// Read through the same /proc/mounts seam LinuxPlacesProvider uses, so these
/// describe a machine rather than the machine they run on.
/// </summary>
public sealed class SearchReachTests : IDisposable
{
    private readonly Func<IEnumerable<string>>? _before = LinuxSearchProvider.MountLines;

    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public void Dispose()
    {
        LinuxSearchProvider.MountLines = _before;
        GC.SuppressFinalize(this);
    }

    private static List<string> Roots(params string[] mounts)
    {
        LinuxSearchProvider.MountLines = () => mounts;

        return LinuxSearchProvider.Roots(new SearchQuery { Text = "report" });
    }

    /// <summary>One /proc/mounts line: source, mount point, type, then flags.</summary>
    private static string Line(string source, string mountPoint, string type)
        => $"{source} {mountPoint} {type} rw,relatime 0 0";

    // ---- which roots ---------------------------------------------------------

    /// <summary>Home is always searched, with or without anything else.</summary>
    [Fact]
    public void Home_is_searched() => Assert.Contains(Home, Roots());

    /// <summary>**The whole finding**: a second disk is searched too.</summary>
    [Fact]
    public void A_mounted_drive_is_searched_as_well()
        => Assert.Contains("/mnt/data", Roots(Line("/dev/sdb1", "/mnt/data", "ext4")));

    /// <summary>And a stick, which is what the box is most often asked about.</summary>
    [Fact]
    public void And_so_is_something_plugged_in()
        => Assert.Contains("/run/media/me/STICK", Roots(Line("/dev/sdc1", "/run/media/me/STICK", "vfat")));

    /// <summary>
    /// **A network mount is not**, and MountTable.IsRealVolume settles that
    /// without a second rule: it requires a source under /dev, and a cifs
    /// mount's source is //server/share. Which matters because a stale network
    /// mount blocks rather than failing, and a walk cannot time out of it.
    /// </summary>
    [Theory]
    [InlineData("//server/share", "/mnt/share", "cifs")]
    [InlineData("server:/export", "/mnt/nfs", "nfs4")]
    public void A_network_mount_is_not(string source, string mountPoint, string type)
        => Assert.DoesNotContain(mountPoint, Roots(Line(source, mountPoint, type)));

    /// <summary>
    /// Nor is the machinery every desktop mounts by the dozen. Without this a
    /// search of "everywhere" walks every snap image on the machine.
    /// </summary>
    [Theory]
    [InlineData("/dev/loop0", "/snap/core/1", "squashfs")]
    [InlineData("tmpfs", "/run/user/1000", "tmpfs")]
    [InlineData("/dev/sda1", "/boot/efi", "vfat")]
    public void And_neither_is_the_machinery(string source, string mountPoint, string type)
        => Assert.DoesNotContain(mountPoint, Roots(Line(source, mountPoint, type)));

    /// <summary>
    /// The root filesystem is skipped: walking "/" reaches everything mounted
    /// under it, including the network mounts the rule above just excluded, and
    /// including home a second time.
    /// </summary>
    [Fact]
    public void The_root_filesystem_itself_is_skipped()
        => Assert.DoesNotContain("/", Roots(Line("/dev/sda2", "/", "ext4")));

    /// <summary>
    /// A mount inside home is not a second root. It would be walked twice
    /// otherwise — once as itself and once on the way down — and every result
    /// on it reported twice.
    /// </summary>
    [Fact]
    public void A_mount_inside_home_is_not_walked_twice()
    {
        var inside = Path.Combine(Home, "vault");

        Assert.DoesNotContain(inside, Roots(Line("/dev/sdd1", inside, "ext4")));
    }

    // ---- and scoping still wins ---------------------------------------------

    /// <summary>
    /// A scoped search is exactly one root, whatever is mounted. The box that
    /// says "Only in Documents" has to mean it.
    /// </summary>
    [Fact]
    public void A_scoped_search_is_still_only_that_folder()
    {
        LinuxSearchProvider.MountLines = () => [Line("/dev/sdb1", "/mnt/data", "ext4")];

        var roots = LinuxSearchProvider.Roots(
            new SearchQuery { Text = "report", ScopePath = "/home/me/Documents" });

        Assert.Equal(["/home/me/Documents"], roots);
    }

    /// <summary>
    /// And the box says what the unscoped case adds up to, rather than
    /// "everywhere".
    /// </summary>
    [Fact]
    public void The_box_says_what_is_actually_covered()
    {
        var said = new LinuxSearchProvider().Everywhere;

        Assert.Equal("your home folder and any mounted drives", said);
        Assert.NotEqual("everywhere", said);
    }
}
