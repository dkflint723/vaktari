using System.Runtime.InteropServices;

namespace Vaktari.Linux;

/// <summary>
/// Whether the kernel says a path is the root of a mount — the engine's own
/// question, asked beside <see cref="Vaktari.Core.FileSystem.VolumeRoots"/>
/// rather than instead of it.
///
/// **From the filesystem, not the path's text.** A mount reached through a
/// linked parent ("~/media/STICK" where ~/media leads to /media — the layout
/// Fedora Silverblue ships for /mnt, /media and /home) and "toproc/." were
/// folders to the text and the table, and the review's probe had the engine
/// delete a stick through the first. The kernel resolves the path itself.
///
/// **statx's STATX_ATTR_MOUNT_ROOT first**, which the kernel sets on the root
/// of every mount (5.8 and later) — a bind mount of a folder on the same disk
/// included — and which is NOT set on a btrfs subvolume that is not mounted,
/// whose different st_dev would otherwise read as a mount. Where the kernel
/// does not report that attribute, st_dev against the parent's: a different
/// device, or the same inode as the parent (which only "/" has).
///
/// **The last name as itself** (AT_SYMLINK_NOFOLLOW): a link to a mount is a
/// link, and removing it removes only the link. "link/." and "link/" ask for
/// what the link leads to, and the kernel follows them on its own. A path the
/// kernel cannot answer for is not a root here.
/// </summary>
internal static partial class MountRootOnDisk
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const uint StatxIno = 0x100;
    private const ulong AttrMountRoot = 0x2000;
    private const uint TypeMask = 0xF000;
    private const uint TypeLink = 0xA000;

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, ref byte buffer);

    /// <summary>False once the symbol has turned out not to be there.</summary>
    private static bool _available = true;

    public static bool Is(string path) => Is(path, askAttribute: true);

    /// <summary>
    /// The question, with the kernel's attribute ignored when
    /// <paramref name="askAttribute"/> is false — so the tests reach the st_dev
    /// answer on a kernel that has the attribute.
    ///
    /// **Asked of the path as written and as .NET resolves it**, because an
    /// engine hands the second to the kernel: GetFullPath folds "." and ".."
    /// as text, so "a/link/../STICK" is acted on as "a/STICK" while the kernel
    /// would read it through the link. Either being a mount refuses.
    /// </summary>
    internal static bool Is(string path, bool askAttribute)
    {
        if (!OperatingSystem.IsLinux() || path.Contains('\0')) return false;

        if (IsAsWritten(path, askAttribute)) return true;

        string resolved;

        try
        {
            resolved = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException
                                      or System.Security.SecurityException)
        {
            return false;
        }

        return resolved != path && IsAsWritten(resolved, askAttribute);
    }

    private static bool IsAsWritten(string path, bool askAttribute)
    {

        if (Read(path) is not { } self) return false;

        if ((self.Mode & TypeMask) == TypeLink) return false;

        if (askAttribute && (self.AttributesMask & AttrMountRoot) != 0)
            return (self.Attributes & AttrMountRoot) != 0;

        if (Read(path.TrimEnd('/') + "/..") is not { } parent) return false;

        return self.Device != parent.Device || self.Inode == parent.Inode;
    }

    private static (ulong Attributes, ulong AttributesMask, uint Mode, ulong Inode, (uint, uint) Device)? Read(string path)
    {
        if (!_available) return null;

        // struct statx is 256 bytes with the same layout on every architecture.
        var buffer = new byte[256];

        try
        {
            if (Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType | StatxIno, ref buffer[0]) != 0) return null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            _available = false;
            return null;
        }

        return (BitConverter.ToUInt64(buffer, 8),
                BitConverter.ToUInt64(buffer, 56),
                BitConverter.ToUInt16(buffer, 28),
                BitConverter.ToUInt64(buffer, 32),
                (BitConverter.ToUInt32(buffer, 136), BitConverter.ToUInt32(buffer, 140)));
    }
}
