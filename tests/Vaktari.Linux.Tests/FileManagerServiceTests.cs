using System.Text;
using System.Xml.Linq;
using Vaktari.Core;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Answering the desktop's "show me this file where it lives".
///
/// **This is the other half of being the default file manager, and Vaktari had
/// only the first.** The MIME association covers opening a folder. It does not
/// cover showing a FILE inside one, because nothing in the MIME system can
/// express "and select this" — so a browser's "Open Containing Folder", a chat
/// client's download button and Plasma's own menu entry all go to
/// org.freedesktop.FileManager1 instead, and found nobody home.
/// </summary>
public sealed class FileManagerServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "vaktari-fm1-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// The service file goes under the user's data directory, and the
    /// NotDefault branch DELETES it — so these must not run against the real
    /// one.
    ///
    /// **Redirected through the seam rather than by moving XDG_DATA_HOME**,
    /// which is process-global: xUnit runs test classes in parallel, and
    /// setting it here took the terminal-entry tests' data directory out from
    /// under them. That failed only on the Linux job, where those tests have
    /// something to find.
    /// </summary>
    public FileManagerServiceTests()
    {
        Directory.CreateDirectory(_temp);
        FileManager1ServiceFile.DataHomeOverride = _temp;
    }

    public void Dispose()
    {
        FileManager1ServiceFile.DataHomeOverride = null;

        try { Directory.Delete(_temp, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private sealed class Chosen(bool isDefault) : IDefaultFileManager
    {
        public bool IsDefault() => isDefault;
        public DefaultChange MakeDefault() => new(true, "");
        public DefaultChange Restore() => new(true, "");
        public string Caveat => "";
    }

    /// <summary>
    /// The whole policy, and the reason this cannot simply claim the name on
    /// startup: org.freedesktop.FileManager1 is a singleton that Dolphin,
    /// Nautilus, Nemo and Thunar all want, and the user has already assigned it
    /// by choosing a default file manager. An application that grabbed it
    /// merely because it happened to be running would make "show in folder"
    /// land wherever the last-started file manager was.
    ///
    /// Deterministic on a runner with no session bus, because the gate returns
    /// before any bus work: an implementation with it inverted answers
    /// Unavailable there and Serving or Taken on a desktop, and all three fail
    /// this.
    /// </summary>
    [Fact]
    public async Task A_desktop_that_has_chosen_another_file_manager_is_not_answered_for()
        => Assert.Equal(
            FileManagerServiceState.NotDefault,
            await new FreedesktopFileManager(new Chosen(false), address: null).ReconcileAsync());

    /// <summary>And with no bus to reach, that is said as its own thing rather
    /// than folded into a shared "unavailable".</summary>
    [Fact]
    public async Task A_session_with_no_message_bus_says_so()
        => Assert.Equal(
            FileManagerServiceState.Unavailable,
            await new FreedesktopFileManager(new Chosen(true), address: null).ReconcileAsync());

    /// <summary>
    /// The XML and the dispatch table are two lists of the same three names
    /// written twice, which is exactly the shape that drifts. Neither is the
    /// authority; they have to agree, in both directions.
    ///
    /// Parsed rather than grepped: a blob that merely CONTAINS "ShowItems" says
    /// nothing about whether it declares the right argument types.
    /// </summary>
    [Fact]
    public void Every_method_it_advertises_is_one_it_implements()
    {
        var methods = XDocument
            .Parse(Encoding.UTF8.GetString(FreedesktopFileManager.InterfaceXml))
            .Root!.Elements("method")
            .Select(m => (string)m.Attribute("name")!)
            .ToList();

        Assert.Equal(3, methods.Count);

        foreach (var name in methods)
            Assert.NotNull(FreedesktopFileManager.KindOf(name));

        foreach (var kind in Enum.GetValues<ShowKind>())
            Assert.Contains(methods, m => FreedesktopFileManager.KindOf(m) == kind);
    }

    /// <summary>
    /// The signature guard and the declared arguments, likewise. A guard on the
    /// wrong string rejects every real call with "invalid arguments", which
    /// reads from outside as the file manager refusing to work.
    /// </summary>
    [Fact]
    public void Every_method_takes_the_arguments_the_guard_expects()
    {
        var xml = XDocument.Parse(
            Encoding.UTF8.GetString(FreedesktopFileManager.InterfaceXml));

        foreach (var method in xml.Root!.Elements("method"))
            Assert.Equal(
                "ass",
                string.Concat(method.Elements("arg").Select(a => (string)a.Attribute("type")!)));
    }

    [Theory]
    [InlineData("ShowItem")]
    [InlineData("ShowFolder")]
    [InlineData("Introspect")]
    [InlineData("")]
    public void Anything_else_is_not_one_of_ours(string member)
        => Assert.Null(FreedesktopFileManager.KindOf(member));

    /// <summary>
    /// **The trap in that file, and invisible at the call site.**
    /// RequestNameOptions.Default is AllowReplacement|ReplaceExisting — 3, not
    /// 0 — so the member that reads like "the sensible default" takes the name
    /// off a running Dolphin without asking, and a queued request moves "show
    /// in folder" mid-session when that Dolphin exits. Nothing about either
    /// fails, compiles wrong, or looks wrong.
    /// </summary>
    [Fact]
    public void The_bus_name_is_asked_for_and_never_taken_or_queued_for()
    {
        var source = RepoSource.Read("src", "Vaktari.Linux", "FreedesktopFileManager.cs");

        Assert.Contains("TryRequestNameAsync(BusName, RequestNameOptions.None)",
                        source, StringComparison.Ordinal);

        Assert.DoesNotContain("RequestNameOptions.Default", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestNameOptions.ReplaceExisting", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueNameRequestAsync", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The handler is registered before the name is claimed. dbus-daemon
    /// delivers a queued activation call the instant the name is acquired, and
    /// a name owned by a path with nothing behind it answers UnknownObject to
    /// the very call it was claimed for — the one case this feature exists for.
    /// </summary>
    [Fact]
    public void And_something_is_listening_before_the_name_is_taken()
    {
        var source = RepoSource.Read("src", "Vaktari.Linux", "FreedesktopFileManager.cs");

        var listening = source.IndexOf("AddMethodHandler(this)", StringComparison.Ordinal);
        var claiming = source.IndexOf("TryRequestNameAsync", StringComparison.Ordinal);

        Assert.True(listening > 0, "nothing is registered to answer calls");
        Assert.True(listening < claiming,
                    "the name is claimed before anything can answer for it");
    }

    // ---- the activation file ----------------------------------------------

    /// <summary>
    /// **Under XDG_DATA_HOME, never /usr/share.** Nautilus and Dolphin each
    /// ship that path byte for byte, so a package of ours shipping one could
    /// not be installed beside either.
    /// </summary>
    [Fact]
    public void The_activation_file_goes_in_the_users_own_directory()
    {
        Assert.StartsWith(_temp, FileManager1ServiceFile.FilePath, StringComparison.Ordinal);
        Assert.Contains("dbus-1", FileManager1ServiceFile.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("org.freedesktop.FileManager1.service",
                        FileManager1ServiceFile.FilePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Exec must be an absolute path.** dbus-daemon does not consult PATH; a
    /// bare "vaktari" parses, installs, and then fails to activate — which from
    /// outside is indistinguishable from the name never having been claimed.
    /// </summary>
    [Fact]
    public void It_names_the_binary_to_start()
    {
        FileManager1ServiceFile.Install("/opt/vaktari/vaktari");

        Assert.Equal(
            "/opt/vaktari/vaktari",
            FileManager1ServiceFile.ExecIn(File.ReadAllLines(FileManager1ServiceFile.FilePath)));

        Assert.Contains(FreedesktopFileManager.BusName,
                        File.ReadAllText(FileManager1ServiceFile.FilePath),
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// A binary that has moved repoints itself on the next launch — which is
    /// the whole answer to the stale-Exec wound, with no separate healing pass.
    /// </summary>
    [Fact]
    public void A_binary_that_moved_is_pointed_at_again()
    {
        FileManager1ServiceFile.Install("/opt/old/vaktari");
        FileManager1ServiceFile.Install("/opt/new/vaktari");

        Assert.Equal(
            "/opt/new/vaktari",
            FileManager1ServiceFile.ExecIn(File.ReadAllLines(FileManager1ServiceFile.FilePath)));
    }

    /// <summary>
    /// And an unchanged one is left alone. This runs on every start, and
    /// churning a file's mtime for no change is how a backup tool or a file
    /// watcher ends up with something to say every time the application opens.
    /// </summary>
    [Fact]
    public void An_unchanged_one_is_not_rewritten()
    {
        FileManager1ServiceFile.Install("/opt/vaktari/vaktari");

        var written = File.GetLastWriteTimeUtc(FileManager1ServiceFile.FilePath);

        File.SetLastWriteTimeUtc(FileManager1ServiceFile.FilePath, written.AddDays(-1));
        var moved = File.GetLastWriteTimeUtc(FileManager1ServiceFile.FilePath);

        FileManager1ServiceFile.Install("/opt/vaktari/vaktari");

        Assert.Equal(moved, File.GetLastWriteTimeUtc(FileManager1ServiceFile.FilePath));
    }

    /// <summary>
    /// Choosing another file manager takes it away, so the bus does not start
    /// Vaktari for a role it will decline the moment it is up.
    /// </summary>
    [Fact]
    public async Task Giving_up_the_role_takes_the_activation_file_with_it()
    {
        FileManager1ServiceFile.Install("/opt/vaktari/vaktari");

        Assert.True(File.Exists(FileManager1ServiceFile.FilePath));

        await new FreedesktopFileManager(new Chosen(false), address: null).ReconcileAsync();

        Assert.False(File.Exists(FileManager1ServiceFile.FilePath));
    }

    /// <summary>Nothing to remove is not a failure.</summary>
    [Fact]
    public void Removing_one_that_is_not_there_is_quiet()
        => FileManager1ServiceFile.Remove();

    [Fact]
    public void An_unknown_binary_writes_nothing()
    {
        FileManager1ServiceFile.Install(null);

        Assert.False(File.Exists(FileManager1ServiceFile.FilePath));
    }
}
