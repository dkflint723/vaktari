using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Vaktari.Ui;
using Vaktari.Ui.Session;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A copy that carries its state with it.
///
/// **Every copy wrote to the one place per user**, so a copy on a stick left
/// its tabs, places and settings behind on every machine it was run on, and
/// found none of its own on the next. A folder named <c>portable</c> beside
/// the executable is the marker and the place, and the single-instance lock
/// is the one thing that stays per-user — named for the folder, so the stick
/// and an installed copy do not hand folders to each other.
///
/// Both seams the store reads are put back afterwards: the suite redirects
/// every store to a directory of its own through one of them, and a test
/// that left that lifted would send the next class's windows to the
/// developer's real state.
/// </summary>
public sealed class PortableModeTests : IDisposable
{
    private readonly Func<string>? _directoryBefore = JsonSessionStore.DirectoryOverride;
    private readonly Func<string>? _binaryBefore = JsonSessionStore.BinaryDirectoryOverride;
    private readonly string _binary = Directory.CreateTempSubdirectory("vaktari-portable").FullName;

    public void Dispose()
    {
        JsonSessionStore.DirectoryOverride = _directoryBefore;
        JsonSessionStore.BinaryDirectoryOverride = _binaryBefore;

        try { Directory.Delete(_binary, recursive: true); }
        catch (Exception) { /* a temp folder left behind is not worth failing over */ }
    }

    private string MakePortable() => Directory.CreateDirectory(Path.Combine(_binary, "portable")).FullName;

    /// <summary>The folder is the marker: present, it is the answer; absent,
    /// there is no answer and the per-user place is used.</summary>
    [Fact]
    public void The_portable_folder_beside_the_binary_is_the_state_folder()
    {
        Assert.Null(JsonSessionStore.PortableRootBeside(_binary));

        var portable = MakePortable();

        Assert.Equal(portable, JsonSessionStore.PortableRootBeside(_binary));
    }

    /// <summary>
    /// The store's default — the one directory every store, the log and the
    /// platform are built from — is the portable folder when there is one.
    /// The suite's own redirection is lifted for this one call, because it
    /// answers first by design and would otherwise be what this measures.
    /// </summary>
    [Fact]
    public void Every_store_is_built_in_the_portable_folder()
    {
        var portable = MakePortable();

        JsonSessionStore.BinaryDirectoryOverride = () => _binary;
        JsonSessionStore.DirectoryOverride = null;

        Assert.Equal(portable, JsonSessionStore.DefaultDirectory());
    }

    /// <summary>
    /// **Without this a portable copy and the installed copy shared one
    /// lock**: the second to start handed its folder to the first and
    /// exited. A portable copy's lock and socket carry a digest of its
    /// folder; an installed copy's are exactly what they were.
    /// </summary>
    [Fact]
    public void A_portable_copy_has_a_lock_and_a_socket_of_its_own()
    {
        JsonSessionStore.BinaryDirectoryOverride = () => _binary;

        var installedLock = SingleInstance.LockPath;
        var installedSocket = SingleInstance.SocketPath;

        Assert.EndsWith("vaktari.lock", installedLock, StringComparison.Ordinal);
        Assert.EndsWith("vaktari.sock", installedSocket, StringComparison.Ordinal);

        MakePortable();

        var portableLock = SingleInstance.LockPath;
        var portableSocket = SingleInstance.SocketPath;

        Assert.Matches(new Regex(@"vaktari-[0-9a-f]{8}\.lock$"), portableLock);
        Assert.Matches(new Regex(@"vaktari-[0-9a-f]{8}\.sock$"), portableSocket);

        // In the same per-user folder: the stick may be read-only, and a
        // socket has to be somewhere a socket can be.
        Assert.Equal(Path.GetDirectoryName(installedLock), Path.GetDirectoryName(portableLock));

        // And the same copy gets the same name every time.
        Assert.Equal(portableLock, SingleInstance.LockPath);
    }

    /// <summary>
    /// **A portable copy answered org.freedesktop.FileManager1 for the
    /// desktop.** Owning a lock of its own, it passed the founder's one test,
    /// and reconciling the role rewrote the per-user D-Bus activation file to
    /// start the binary on the stick — or deleted it — which is a write
    /// outside the folder the copy promises to stay in.
    /// </summary>
    [AvaloniaFact]
    public void A_portable_copy_does_not_answer_for_the_desktop()
    {
        JsonSessionStore.BinaryDirectoryOverride = () => _binary;

        Assert.True(MainWindow.AnswersForTheDesktop(ownsLock: true));
        Assert.False(MainWindow.AnswersForTheDesktop(ownsLock: false));

        MakePortable();

        Assert.False(MainWindow.AnswersForTheDesktop(ownsLock: true),
                     "a portable copy took the desktop's file-manager role");
    }

    /// <summary>
    /// **Nothing held the lines that send a portable copy's downloads to its
    /// own folder.** The icon catalogue, the Proton Drive tool and the Linux
    /// scripts each honour a portable folder when they are given one, and
    /// their tests give it by hand — so dropping any of these three lines in
    /// the composition root left every test green while a stick went back to
    /// writing themes, the CLI and scripts onto each machine it visited.
    /// Read from the source, because building the real services for a test
    /// builds the whole platform with them.
    /// </summary>
    [Fact]
    public void The_services_hand_the_portable_folder_to_everything_that_downloads()
    {
        var source = RepoSource.Ui("WindowServices.cs");

        Assert.Contains("new LinuxPlatform(JsonSessionStore.DefaultDirectory(), JsonSessionStore.PortableRoot)",
                        source, StringComparison.Ordinal);
        Assert.Contains("PortableRoot = JsonSessionStore.PortableRoot,", source, StringComparison.Ordinal);
        Assert.Contains("IconThemeCatalogue.PortableRoot = JsonSessionStore.PortableRoot;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A theme chosen on a stick was missing wherever the stick mounted
    /// elsewhere.** The saved choice is a full path, and it is found again in
    /// the portable folder — which needs the catalogue to know that folder
    /// before the choice is looked at, and the choice looked at before the
    /// theme is loaded from it.
    /// </summary>
    [Fact]
    public void A_saved_theme_is_found_again_before_it_is_loaded()
    {
        var source = RepoSource.Ui("WindowServices.cs");

        var told = source.IndexOf("IconThemeCatalogue.PortableRoot = JsonSessionStore.PortableRoot;", StringComparison.Ordinal);
        var found = source.IndexOf("IconThemeCatalogue.Relocated(settings.General.IconThemeFolder)", StringComparison.Ordinal);
        var loaded = source.IndexOf("InstallIconTheme(platform);", StringComparison.Ordinal);

        Assert.True(found > 0, "the saved theme is not looked for in the portable folder");
        Assert.True(told > 0 && told < found, "the theme is looked for before the catalogue knows the portable folder");
        Assert.True(found < loaded, "the theme is loaded before it has been looked for");
    }
}
