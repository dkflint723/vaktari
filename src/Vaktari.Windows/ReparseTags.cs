using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The reparse tag of an entry, read without following it: the answer
/// <see cref="Vaktari.Core.FileSystem.SafeWalk.IsLink"/> needs on Windows and
/// cannot get for itself, because Core makes no calls into the operating system.
///
/// **FileAttributeTagInfo, on a handle opened with FILE_FLAG_OPEN_REPARSE_POINT
/// and nothing but FILE_READ_ATTRIBUTES.** The entry is not followed, so a link
/// Windows cannot follow at all is still read, and nothing behind a placeholder
/// is fetched. Measured on each kind: a junction answered 0xA0000003; a link
/// made by WSL answered 0xA000001D, both on NTFS and through \\wsl.localhost,
/// where fsutil could not read it; a third-party tag answered its own
/// 0x00001234; and an app execution alias 0x8000001B.
/// </summary>
internal static class ReparseTags
{
    /// <summary>
    /// The tag, or null when the entry could not be opened or asked — gone,
    /// refused, or on a filesystem that does not answer.
    ///
    /// **Asked of the entry itself, through <see cref="ReachablePath.Exact"/>.**
    /// Every caller hands over a path in the spelling it found it under, and a
    /// plain "…\x " is opened as "…\x": the listing, the search walk and the
    /// disk-usage walk all read the neighbour's tag, so a row under a tag that
    /// is no link wore its neighbour's link emblem and a WSL link beside an
    /// ordinary file lost its own (0.11.1 path-safety check). An entry with no
    /// spelling that reaches it — a folded name in a "\\.\" path — is not
    /// asked, and reads as a tag that could not be read.
    /// </summary>
    public static uint? Of(string path)
    {
        if (ReachablePath.Exact(path) is not { } exact) return null;

        var handle = Native.CreateFile(
            exact, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING,
            Native.FILE_FLAG_OPEN_REPARSE_POINT | Native.FILE_FLAG_BACKUP_SEMANTICS, 0);

        if (handle == Native.INVALID_HANDLE_VALUE) return null;

        try
        {
            if (!Native.GetFileInformationByHandleEx(handle, Native.FileAttributeTagInfo, out var info, 8))
                return null;

            return info.ReparseTag;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
