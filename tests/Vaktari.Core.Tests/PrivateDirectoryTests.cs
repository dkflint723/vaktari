using System.Runtime.Versioning;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Where the instance lock and its socket go on a session that sets no
/// XDG_RUNTIME_DIR, and what stands between them and the other accounts on
/// the machine.
///
/// **They went to /tmp**, which every account shares: another user could hold
/// the lock's name and every start of Vaktari would be told a copy was already
/// running, or bind the socket's name and be handed the folders each launch
/// meant for the running window. The fallback is a folder of the user's own
/// now, made 0700 on creation and tightened if found otherwise, and a link
/// standing where the folder or the lock should be is not followed.
///
/// The resolution is driven through an environment of the test's making, so
/// every platform can check it; the modes and links are Posix-only facts.
/// </summary>
public sealed class PrivateDirectoryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-private-" + Guid.NewGuid().ToString("N")[..12]);

    public PrivateDirectoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value);
        return key => map.GetValueOrDefault(key);
    }

    private string In(params string[] parts) => Path.Combine([_root, .. parts]);

    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // ---- choosing the folder ---------------------------------------------------

    [Fact]
    public void Without_a_session_runtime_directory_the_files_go_under_the_users_cache()
    {
        var got = PrivateDirectory.Runtime(Env(("XDG_CACHE_HOME", _root)), windows: false);

        Assert.Equal(In("vaktari", "run"), got);
        Assert.True(Directory.Exists(got));
    }

    [Fact]
    public void With_no_cache_home_either_it_is_dot_cache_under_HOME()
    {
        var got = PrivateDirectory.Runtime(Env(("HOME", _root)), windows: false);

        Assert.Equal(In(".cache", "vaktari", "run"), got);
        Assert.True(Directory.Exists(got));
    }

    [Fact]
    public void A_session_runtime_directory_is_the_first_choice()
    {
        var xdg = Directory.CreateDirectory(In("xdg")).FullName;

        var got = PrivateDirectory.Runtime(
            Env(("XDG_RUNTIME_DIR", xdg), ("XDG_CACHE_HOME", _root)), windows: false);

        Assert.Equal(xdg, got);
        Assert.False(Directory.Exists(In("vaktari")), "the fallback was made although the session had a runtime directory");
    }

    /// <summary>A variable pointing at nothing is a variable to ignore, not a
    /// folder to create: XDG_RUNTIME_DIR is the session's to make.</summary>
    [Fact]
    public void A_runtime_directory_that_is_not_there_is_not_used()
    {
        var got = PrivateDirectory.Runtime(
            Env(("XDG_RUNTIME_DIR", In("gone")), ("XDG_CACHE_HOME", _root)), windows: false);

        Assert.Equal(In("vaktari", "run"), got);
        Assert.False(Directory.Exists(In("gone")));
    }

    /// <summary>%TEMP% is per user on Windows; nothing there needed changing.</summary>
    [Fact]
    public void On_Windows_the_per_user_temp_folder_is_kept()
        => Assert.Equal(Path.GetTempPath(), PrivateDirectory.Runtime(Env(), windows: true));

    // ---- what the folder is ----------------------------------------------------

    [PosixFact, SupportedOSPlatform("linux")]
    public void The_fallback_is_made_for_this_user_alone()
    {
        var made = PrivateDirectory.Ensure(In("run"));

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(made));
    }

    /// <summary>CreateDirectory leaves an existing folder as it found it, so a
    /// folder made 0755 by an earlier version — or by hand — is tightened.</summary>
    [PosixFact, SupportedOSPlatform("linux")]
    public void A_folder_that_is_already_there_is_tightened()
    {
        var loose = Directory.CreateDirectory(In("run")).FullName;
        File.SetUnixFileMode(loose,
            OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        PrivateDirectory.Ensure(loose);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(loose));
    }

    [PosixFact, SupportedOSPlatform("linux")]
    public void A_link_standing_where_the_folder_should_be_is_refused()
    {
        var elsewhere = Directory.CreateDirectory(In("elsewhere")).FullName;
        var before = File.GetUnixFileMode(elsewhere);
        Directory.CreateSymbolicLink(In("run"), elsewhere);

        Assert.Throws<IOException>(() => PrivateDirectory.Ensure(In("run")));

        Assert.Equal(before, File.GetUnixFileMode(elsewhere));
    }

    // ---- the lock --------------------------------------------------------------

    [Fact]
    public void A_lock_is_held_by_one_process_at_a_time()
    {
        using var held = PrivateDirectory.OpenLock(In("vaktari.lock"));

        Assert.Throws<IOException>(() => PrivateDirectory.OpenLock(In("vaktari.lock")));
    }

    /// <summary>
    /// Under the old code the lock was taken on the link's target, with a
    /// writable handle held on it for the life of the window — which is what
    /// the exclusive open of the target at the end would have collided with.
    /// </summary>
    [PosixFact, SupportedOSPlatform("linux")]
    public void A_link_at_the_locks_name_is_not_followed()
    {
        var precious = In("precious.txt");
        File.WriteAllText(precious, "not a lock");
        File.CreateSymbolicLink(In("vaktari.lock"), precious);

        using var held = PrivateDirectory.OpenLock(In("vaktari.lock"));

        Assert.Null(new FileInfo(In("vaktari.lock")).LinkTarget);
        Assert.Equal("not a lock", File.ReadAllText(precious));

        using var untouched = new FileStream(precious, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
