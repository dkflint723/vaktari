using System.Text.RegularExpressions;
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
}
