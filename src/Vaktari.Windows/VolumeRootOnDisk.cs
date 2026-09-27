namespace Vaktari.Windows;

/// <summary>
/// Whether the file system says a path is the root of a volume — the engine's
/// own question, asked beside <see cref="Vaktari.Core.FileSystem.VolumeRoots"/>
/// rather than instead of it.
///
/// **Asked of a handle, not of the text.** The path is opened the way
/// <see cref="ReparseTags"/> opens one — FILE_READ_ATTRIBUTES, backup
/// semantics, and FILE_FLAG_OPEN_REPARSE_POINT so a junction or a volume
/// mounted in a folder is the link itself, which removing only unlinks — and
/// GetFinalPathNameByHandle is asked for its path WITHOUT the volume. A
/// volume's root answers "\" however it was spelled: "C:\",
/// "\\?\GLOBALROOT\Device\HarddiskVolume3\", "\\?\Volume{…}\", a share's root.
///
/// **Not GetVolumePathName**, which was measured first: on a subst drive it
/// failed for "Z:\" and answered "Z:\x\" for the folder "Z:\x", so comparing a
/// path with its answer would have refused every folder on a subst drive.
///
/// A subst drive's root is a folder to this question — it IS a folder on
/// another volume — and the text guard is what refuses it. A path that cannot
/// be opened (gone, refused, a spelling the file system rejects) is not a root
/// here; whatever it is, the operation will fail on it with its own words.
/// </summary>
internal static class VolumeRootOnDisk
{
    private const uint VolumeNameNone = 0x4;

    public static bool Is(string path)
    {
        var handle = Native.CreateFile(
            path, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING,
            Native.FILE_FLAG_OPEN_REPARSE_POINT | Native.FILE_FLAG_BACKUP_SEMANTICS, 0);

        if (handle == Native.INVALID_HANDLE_VALUE) return false;

        try
        {
            // Room for a root's "\" and nothing much else: a longer answer
            // comes back as the size it needs, which is not 1.
            unsafe
            {
                var buffer = stackalloc char[8];

                var length = Native.GetFinalPathNameByHandle(handle, buffer, 8, VolumeNameNone);

                return length == 1 && buffer[0] == '\\';
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
