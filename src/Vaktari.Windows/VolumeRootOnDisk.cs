using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Whether the file system says a path is the root of a volume — the engine's
/// own question, asked beside <see cref="VolumeRoots"/> rather than instead of
/// it.
///
/// **Asked of a handle, not of the text.** The path is opened the way
/// <see cref="ReparseTags"/> opens one — FILE_READ_ATTRIBUTES, backup
/// semantics, and FILE_FLAG_OPEN_REPARSE_POINT so a junction or a volume
/// mounted in a folder is the link itself, which removing only unlinks — and
/// GetFinalPathNameByHandle is asked what the handle reached, twice:
///
/// - **Without the volume** (VOLUME_NAME_NONE): a local volume's root answers
///   "\" however it was spelled — "C:\", "\\?\GLOBALROOT\Device\HarddiskVolume3\",
///   "\\?\Volume{…}\".
/// - **With the drive or share** (VOLUME_NAME_DOS), whose answer the text
///   guard is asked of. A share's root does NOT answer "\" without the volume
///   — measured: "\\localhost\c$\" answered "\localhost\c$" — so the first
///   question alone never refused a share root or a drive mapped to one; the
///   second reads "\\?\UNC\localhost\c$\", which is one.
///
/// **For a volume with a drive letter the two answer alike**, "\" and
/// "\\?\C:\", so each hides the other's absence and they are revert-checked
/// as a pair. The first stays for a volume with no letter — a recovery
/// partition by its "\\?\Volume{…}\" name — which has no DOS name to give.
///
/// **Not GetVolumePathName**, which was measured first: on a subst drive it
/// failed for "Z:\" and answered "Z:\x\" for the folder "Z:\x", so comparing a
/// path with its answer would have refused every folder on a subst drive.
///
/// A subst drive's root, and a drive mapped to a folder INSIDE a share, are
/// folders to this question — each IS a folder on another volume — and the
/// text guard is what refuses them. A path that cannot be opened (gone,
/// refused, a spelling the file system rejects) is not a root here; whatever
/// it is, the operation will fail on it with its own words.
/// </summary>
internal static class VolumeRootOnDisk
{
    private const uint VolumeNameDos = 0x0;
    private const uint VolumeNameNone = 0x4;

    public static bool Is(string path)
    {
        var handle = Native.CreateFile(
            path, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING,
            Native.FILE_FLAG_OPEN_REPARSE_POINT | Native.FILE_FLAG_BACKUP_SEMANTICS, 0);

        if (handle == Native.INVALID_HANDLE_VALUE) return false;

        try
        {
            if (Reached(handle, VolumeNameNone) == @"\") return true;

            return Reached(handle, VolumeNameDos) is { } dos && VolumeRoots.IsRootSpelling(dos);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>The path a handle reached, in the form asked for, or null when
    /// the file system will not say.</summary>
    private static unsafe string? Reached(nint handle, uint form)
    {
        var size = 260u;

        // Twice at most: a longer answer comes back as the size it needs.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new char[size];

            fixed (char* at = buffer)
            {
                var length = Native.GetFinalPathNameByHandle(handle, at, size, form);

                if (length == 0) return null;

                if (length < size) return new string(buffer, 0, (int)length);

                size = length + 1;
            }
        }

        return null;
    }
}
