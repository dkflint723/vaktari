using System.Runtime.InteropServices;

namespace Vaktari.Windows;

/// <summary>
/// Which file or folder a path reaches, as the file system names it: the
/// volume's serial number and the entry's id on that volume.
///
/// **Two spellings of one folder are one folder, and text cannot always say
/// so.** A mapped drive and the share it maps, a subst drive and the folder it
/// stands for, a path with the \\?\ prefix and the same path without, \\wsl$
/// and \\wsl.localhost — none of these is a link, so resolving links along the
/// path leaves them different strings. Moving a file into the same folder by
/// its second name then took the copy route: the copy landed over the file,
/// which was itself, and the delete of the source that follows every move
/// deleted what had just landed. The file system knows they are one entry, so
/// it is asked.
///
/// Opened with nothing but FILE_READ_ATTRIBUTES and backup semantics: a folder
/// can be asked, nothing behind a placeholder is fetched, and every other
/// program keeps its access. A link is followed, because the question is which
/// file it reaches.
/// </summary>
internal static class FileIdentity
{
    /// <summary>
    /// The entry's identity, or null when it cannot be opened or asked. The
    /// 128-bit id first; the older 64-bit index where a file system — an SMB
    /// server, typically, which is where two spellings are commonest — does
    /// not answer the newer question.
    /// </summary>
    public static (ulong Volume, ulong Low, ulong High)? Of(string path)
    {
        var handle = Native.CreateFile(
            path, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING,
            Native.FILE_FLAG_BACKUP_SEMANTICS, 0);

        if (handle == Native.INVALID_HANDLE_VALUE) return null;

        try
        {
            if (Native.GetFileIdInfo(
                    handle, Native.FileIdInfo, out Native.FILE_ID_INFO id,
                    (uint)Marshal.SizeOf<Native.FILE_ID_INFO>()))
                return Checked((id.VolumeSerialNumber, id.FileIdLow, id.FileIdHigh));

            if (Native.GetFileInformationByHandle(handle, out var info))
                return Checked((info.VolumeSerialNumber,
                                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                                0));

            return null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    /// An identity only when it is one.
    ///
    /// **Some file systems answer every file with the same id** — zero, from
    /// the WebDAV redirector, many cloud drives mounted through a user-mode
    /// file system, and SMB servers without persistent ids; all ones from
    /// others. Taken at its word, every file on such a drive was one file: a
    /// duplicates scan kept one name per size and found nothing, and two
    /// different folders were "the same folder". What is not an id says
    /// nothing, and every caller then keeps the rule it had before.
    /// </summary>
    internal static (ulong Volume, ulong Low, ulong High)? Checked((ulong Volume, ulong Low, ulong High) id)
        => id is { Low: 0, High: 0 }
           || id is { Low: ulong.MaxValue, High: ulong.MaxValue or 0 }
            ? null
            : id;

    /// <summary>Whether two paths reach one entry. False when either cannot be asked.</summary>
    public static bool Same(string a, string b)
        => Of(a) is { } left && Of(b) is { } right && left == right;
}
