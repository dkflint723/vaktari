using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Which application "Open with" offers first.
///
/// **The group heading was not read at all.** Any line beginning with the type
/// was taken as a preference, wherever in the file it appeared — so an
/// application listed under [Removed Associations], which is that file's way of
/// saying "never this one for this type", was read as the FIRST choice for it.
/// Un-choosing an application in the desktop's settings made it the default
/// here.
///
/// And only two of the six kinds of mimeapps.list were consulted. The
/// desktop-specific ones are where Plasma and GNOME write what their own
/// settings pages are told, and the ones under the system data directories are
/// where a distribution puts its defaults.
/// </summary>
public sealed class MimeAppsListTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-mimeapps-" + Guid.NewGuid().ToString("N")[..8]);

    public MimeAppsListTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_root, name);

        File.WriteAllText(path, contents);

        return path;
    }

    private static (List<string> Preferred, List<string> Removed) Read(string path)
        => DesktopEntries.ReadMimeApps(path, "image/png");

    /// <summary>The ordinary case: a file naming a default.</summary>
    [Fact]
    public void A_default_is_read_from_its_own_group()
    {
        var path = Write("a.list", """
            [Default Applications]
            image/png=gimp.desktop;
            """);

        Assert.Equal(["gimp.desktop"], Read(path).Preferred);
    }

    /// <summary>
    /// **The finding, in the smallest shape that shows it.** Read without
    /// regard to the group, this file says the default for a PNG is the very
    /// application somebody went into their settings to un-choose.
    /// </summary>
    [Fact]
    public void A_removed_association_is_not_a_preference()
    {
        var path = Write("b.list", """
            [Removed Associations]
            image/png=eog.desktop;
            """);

        var (preferred, removed) = Read(path);

        Assert.Empty(preferred);
        Assert.Equal(["eog.desktop"], removed);
    }

    /// <summary>Added and Default both name applications to offer.</summary>
    [Fact]
    public void An_added_association_is_offered_too()
    {
        var path = Write("c.list", """
            [Default Applications]
            image/png=gimp.desktop;

            [Added Associations]
            image/png=krita.desktop;
            """);

        Assert.Equal(["gimp.desktop", "krita.desktop"], Read(path).Preferred);
    }

    /// <summary>
    /// A group this does not know about names nothing. These files carry other
    /// sections, and reading them all was the fault above.
    /// </summary>
    [Fact]
    public void A_group_that_means_something_else_is_ignored()
    {
        var path = Write("d.list", """
            [Some Other Group]
            image/png=nothing.desktop;
            """);

        var (preferred, removed) = Read(path);

        Assert.Empty(preferred);
        Assert.Empty(removed);
    }

    /// <summary>A line before any group heading belongs to no group.</summary>
    [Fact]
    public void A_line_above_the_first_heading_names_nothing()
        => Assert.Empty(Read(Write("e.list", "image/png=stray.desktop;\n")).Preferred);

    /// <summary>Several applications on one line keep their order.</summary>
    [Fact]
    public void Several_on_one_line_keep_their_order()
    {
        var path = Write("f.list", """
            [Default Applications]
            image/png=first.desktop;second.desktop;third.desktop;
            """);

        Assert.Equal(
            ["first.desktop", "second.desktop", "third.desktop"], Read(path).Preferred);
    }

    /// <summary>Another type's line is not this type's.</summary>
    [Fact]
    public void Another_types_preference_is_left_alone()
    {
        var path = Write("g.list", """
            [Default Applications]
            image/jpeg=other.desktop;
            image/png=mine.desktop;
            """);

        Assert.Equal(["mine.desktop"], Read(path).Preferred);
    }

    /// <summary>A file that is not there says nothing, rather than throwing.</summary>
    [Fact]
    public void A_file_that_is_not_there_says_nothing()
    {
        var (preferred, removed) = Read(Path.Combine(_root, "absent.list"));

        Assert.Empty(preferred);
        Assert.Empty(removed);
    }

    // ---- folding the files together ------------------------------------------

    private static List<string> Resolved(params (string[] Preferred, string[] Removed)[] files)
        => [.. DesktopEntries.Resolve(
            files.Select(f => (f.Preferred.ToList(), f.Removed.ToList())))];

    /// <summary>Nearest file first, and each application named once.</summary>
    [Fact]
    public void The_nearest_files_preference_comes_first()
        => Assert.Equal(
            ["mine.desktop", "theirs.desktop"],
            Resolved((["mine.desktop"], []), (["theirs.desktop", "mine.desktop"], [])));

    /// <summary>
    /// **A removal beats a preference in a file further away**, which is what
    /// un-choosing an application in the desktop's settings has to mean: the
    /// distribution offers it, and the person says no.
    /// </summary>
    [Fact]
    public void A_removal_takes_away_what_a_further_file_offers()
        => Assert.Equal(
            ["keep.desktop"],
            Resolved((["keep.desktop"], ["gone.desktop"]), (["gone.desktop"], [])));

    /// <summary>
    /// **And not the other way round.** A removal written by a system file
    /// cannot veto a choice the person made above it — gathering every removal
    /// before walking would do exactly that, and would look identical in every
    /// test that puts the removal first.
    /// </summary>
    [Fact]
    public void A_further_files_removal_cannot_veto_a_nearer_choice()
        => Assert.Equal(
            ["chosen.desktop"],
            Resolved((["chosen.desktop"], []), ([], ["chosen.desktop"])));

    /// <summary>
    /// **A file's own default is not vetoed by its own removal line**, which is
    /// what the statement order in the fold decides. Such a file contradicts
    /// itself — the removal is meant for the files BELOW it — and reading it
    /// the other way would let one stray line in a desktop's own list take away
    /// the default written two lines above it.
    /// </summary>
    [Fact]
    public void A_files_own_preference_survives_its_own_removal_line()
        => Assert.Equal(
            ["both.desktop"],
            Resolved((["both.desktop"], ["both.desktop"])));

    /// <summary>Nothing anywhere is nothing.</summary>
    [Fact]
    public void No_files_offer_nothing()
        => Assert.Empty(Resolved());

    // ---- and where they are looked for ---------------------------------------

    /// <summary>
    /// **The desktop's own file comes first**, and there is one per name in
    /// XDG_CURRENT_DESKTOP — which may list several, most specific first. This
    /// is where Plasma and GNOME write what their settings pages are told, so
    /// missing it means disagreeing with the desktop about its own default.
    /// </summary>
    [Fact]
    public void The_desktops_own_list_is_looked_for_before_the_plain_one()
    {
        var lists = Names();

        var plasma = lists.IndexOf("kde-mimeapps.list");
        var plain = lists.IndexOf("mimeapps.list");

        Assert.True(plasma >= 0, "the desktop-specific file is not looked for at all");
        Assert.True(plasma < plain, "the plain file is consulted before the desktop's own");
    }

    /// <summary>
    /// And the config directories come before the data ones, which is the
    /// spec's order: a choice beats an installation's suggestion.
    /// </summary>
    [Fact]
    public void The_configured_choice_beats_what_was_installed()
    {
        var paths = Paths();

        var config = paths.FindIndex(p => p.Contains(".config", StringComparison.Ordinal));
        var data = paths.FindIndex(p => p.Contains("applications", StringComparison.Ordinal));

        Assert.True(config >= 0 && data >= 0);
        Assert.True(config < data, "an installed default is consulted before the person's own");
    }

    /// <summary>
    /// The system-wide ones are looked for as well — a distribution's defaults
    /// live under the data directories, and skipping them left Vaktari with no
    /// answer where the desktop had one.
    ///
    /// The two roots are asserted SEPARATELY. Asked as "one or the other", the
    /// data half could be deleted entirely and the config half would answer for
    /// it — which is precisely what happened to the first version of this test.
    ///
    /// Compared with one separator: this suite also runs on Windows agents,
    /// where Path.Combine writes the other one.
    /// </summary>
    [Fact]
    public void The_system_wide_lists_are_looked_for_too()
    {
        var paths = Paths()
            .Select(p => p.Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        Assert.Contains(paths, p => p.Contains("/etc/xdg/", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.Contains("/usr/share/applications/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Sets the variable and puts it back. Safe because this assembly runs as
    /// one serial collection — see Parallelism.cs, which is there because a
    /// borrowed static leaked between classes once already.
    /// </summary>
    private static List<string> Paths()
    {
        var before = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");

        try
        {
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "KDE");

            return [.. DesktopEntries.MimeAppsLists()];
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", before);
        }
    }

    private static List<string> Names()
        => [.. Paths().Select(p => Path.GetFileName(p) ?? "")];
}
