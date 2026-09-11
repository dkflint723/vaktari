using Vaktari.Core.Diagnostics;

namespace Vaktari.Core;

/// <summary>
/// A folder only this user can enter, and the files that have to live in one.
///
/// **The instance lock and its socket went to /tmp** whenever a Linux session
/// set no XDG_RUNTIME_DIR — a login over SSH, a container, some display
/// managers — and /tmp is shared by every account on the machine. Another
/// user could take the lock's name first, and every start of Vaktari would
/// then be told a copy was already running and hand its folders to nobody;
/// or bind the socket's name and be handed the folders instead. XDG_RUNTIME_DIR
/// is 0700 by specification and stays the first choice; this is what stands
/// in when a session has none.
/// </summary>
public static class PrivateDirectory
{
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Where this session's private working files go.</summary>
    public static string Runtime()
        => Runtime(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());

    /// <summary>
    /// Windows: %TEMP%, which sits under the profile and is readable by
    /// nobody else. Elsewhere: XDG_RUNTIME_DIR when the session has one — per
    /// session, 0700, cleared at logout — and otherwise a folder of our own
    /// under the cache home, made 0700 here. Never /tmp.
    /// </summary>
    internal static string Runtime(Func<string, string?> env, bool windows)
    {
        if (windows) return Path.GetTempPath();

        var runtime = env("XDG_RUNTIME_DIR");

        if (!string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime)) return runtime;

        var home = env("HOME") is { Length: > 0 } set
            ? set
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var cache = env("XDG_CACHE_HOME") is { Length: > 0 } chosen
            ? chosen
            : Path.Combine(home, ".cache");

        return Ensure(Path.Combine(cache, "vaktari", "run"));
    }

    /// <summary>
    /// The folder, created 0700 if it was missing and tightened to 0700 if it
    /// was not — CreateDirectory leaves an existing folder as it found it. A
    /// link standing where the folder should be is refused rather than
    /// followed: whatever it points at is not ours to open up or close down.
    /// </summary>
    public static string Ensure(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is { } target)
            throw new IOException($"{path} is a link to {target}, not a folder of this user's own");

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return path;
        }

        if (Directory.Exists(path))
        {
            // One made 0755 by hand, or by the version before this one.
            if (File.GetUnixFileMode(path) != OwnerOnly) File.SetUnixFileMode(path, OwnerOnly);

            return path;
        }

        // The mode goes in with the creation, so there is no moment between
        // the folder appearing and its being closed to everyone else. Not
        // followed by a chmod: measured, the chmod made the creation mode
        // unobservable — a folder created open and tightened a line later
        // passed every test this has — so each case has exactly one line.
        Directory.CreateDirectory(path, OwnerOnly);

        return path;
    }

    /// <summary>
    /// Opens a lock file for this process alone.
    ///
    /// **A link at the lock's name is not followed.** Opening it would take
    /// the lock on whatever the link pointed at, and hold a writable handle on
    /// it for the life of the window. The link is unlinked, which never
    /// touches its target, and a plain file is made in its place.
    /// </summary>
    public static FileStream OpenLock(string path)
    {
        if (new FileInfo(path).LinkTarget is { } target)
        {
            Log.Warn("instance", $"the lock at {path} was a link to {target}; replaced with a file");
            File.Delete(path);
        }

        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
