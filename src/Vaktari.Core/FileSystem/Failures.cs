namespace Vaktari.Core.FileSystem;

/// <summary>
/// Says what went wrong in words somebody who is not a programmer can act on.
///
/// **The application spoke .NET.** A folder it could not open reported
/// "UnauthorizedAccessException: Access to the path 'D:\x' is denied." in the
/// status bar — while the listing behind it, from the very same catch block,
/// said "you do not have permission to open this folder". The plain sentence
/// already existed and only one of the two places used it.
///
/// **The exception's own message is the fallback, not the enemy.** Some of them
/// are perfectly clear — "The process cannot access the file because it is being
/// used by another process" tells you what to do about it. What is never worth
/// showing is the type name, which is a fact about the code rather than about
/// the file.
/// </summary>
public static class Failures
{
    /// <summary>ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION, as HRESULTs.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL.</summary>
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    private const int PathTooLong = unchecked((int)0x800700CE);

    /// <summary>ERROR_NOT_READY: a drive letter with nothing behind it — an
    /// empty optical drive, a card reader with no card.</summary>
    private const int NotReady = unchecked((int)0x80070015);

    /// <summary>ERROR_BAD_NETPATH and ERROR_BAD_NET_NAME: a mapped drive or a
    /// UNC share whose server is not answering.</summary>
    private const int BadNetPath = unchecked((int)0x80070035);
    private const int BadNetName = unchecked((int)0x80070043);

    /// <summary>ERROR_NETWORK_UNREACHABLE: the server behind the share is off,
    /// or off the network — as opposed to a name that does not resolve
    /// (BadNetPath) or a share that is not there on a server that is
    /// (BadNetName). Measured on Windows 11: enumerating \\127.0.0.2\nosuchshare
    /// raises IOException 0x800704CF, "The network location cannot be reached.
    /// For information about network troubleshooting, see Windows Help. :
    /// '\\127.0.0.2\nosuchshare'."</summary>
    private const int NetworkUnreachable = unchecked((int)0x800704CF);

    /// <summary>
    /// What to tell somebody, given what they were trying to do.
    /// </summary>
    /// <param name="doing">A phrase completing "could not …" — "open that
    /// folder", "copy those". Used only where the exception alone does not say
    /// enough, so it need not be repeated in every message.</param>
    public static string Describe(Exception e, string doing = "do that") => e switch
    {
        OperationCanceledException => "cancelled",

        DirectoryNotFoundException => "that folder is not there any more",
        FileNotFoundException => "that file is not there any more",

        UnauthorizedAccessException => $"you do not have permission to {doing}",

        IOException io when io.HResult is SharingViolation or LockViolation =>
            "something else has that file open",

        IOException io when io.HResult is DiskFull or HandleDiskFull =>
            "there is not enough room on the disk",

        IOException io when io.HResult == PathTooLong =>
            "that path is too long for this filesystem",

        // **A drive that was not there answered in Win32's words.** A server
        // that has gone away says "The network location cannot be reached", a
        // name that does not resolve says "The network path was not found",
        // and a share that is not there says "The network name cannot be
        // found" — each with the path handed back after a colon dropped into
        // the middle of it. An optical drive with no disc in it said "The
        // device is not ready." All of them arrive as a plain IOException
        // carrying the Win32 code in the HRESULT: the BCL promotes only a
        // handful of codes to exception types of their own and none of these
        // is one of them, so they fell straight through to the message below.
        IOException io when io.HResult == NotReady =>
            "that drive is not ready",

        IOException io when io.HResult is BadNetPath or BadNetName
                                        or NetworkUnreachable =>
            "that network drive is not connected",

        // The rest of IOException is written for people and usually says
        // something useful — "already exists here", for one, which this
        // application raises itself.
        IOException io => io.Message,

        ArgumentException a => a.Message,

        // Anything unforeseen: its message, never its type. A sentence nobody
        // wrote for this situation still beats a class name.
        _ => e.Message,
    };
}
