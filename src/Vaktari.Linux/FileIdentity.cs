using System.Runtime.InteropServices;

namespace Vaktari.Linux;

/// <summary>
/// Which file or folder a path reaches: its device and inode.
///
/// **Two names for one folder are not always links.** A bind mount, or one
/// export mounted twice, makes a folder reachable by two paths neither of which
/// is a link — so resolving links leaves them two strings, and a move into the
/// same folder by its second name took the copy route: landed the copy over the
/// file, which was itself, then deleted the source, which was what had landed.
/// stat answers the question the text cannot.
///
/// <c>stat</c> rather than <c>lstat</c>: the question is which file a path
/// reaches, so a link along it is followed. Only the first two fields are read
/// — st_dev and st_ino, eight bytes each and first in the structure on both
/// 64-bit layouts Vaktari ships for (x86-64 and aarch64). A 32-bit process, or
/// a C library too old to export the symbol, answers nothing, and every caller
/// then keeps the rule it had before.
/// </summary>
internal static partial class FileIdentity
{
    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8,
                   SetLastError = true)]
    private static partial int Stat(string path, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8,
                   SetLastError = true)]
    private static partial int LStat(string path, ref byte buffer);

    /// <summary>
    /// The owner of an entry itself — a link as the link — or null when that
    /// cannot be told. st_uid sits just after st_mode on x86-64, four bytes
    /// on; on aarch64 a 4-byte st_nlink comes between, so eight.
    /// </summary>
    public static uint? OwnerOf(string path)
    {
        var at = ModeOffset;

        if (at < 0 || !_available || !OperatingSystem.IsLinux()) return null;

        var owner = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? at + 8 : at + 4;
        var buffer = new byte[256];

        try
        {
            return LStat(path, ref buffer[0]) == 0 ? BitConverter.ToUInt32(buffer, owner) : null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8,
                   SetLastError = true)]
    private static partial nint RealPathOf(string path, ref byte resolved);

    /// <summary>False once the symbol has turned out not to be there.</summary>
    private static bool _available = true;

    /// <summary>
    /// A path with every link along it followed, the way the kernel reaches
    /// it — or null when it cannot be told.
    /// </summary>
    public static string? RealPath(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;

        // PATH_MAX, which realpath writes no more than.
        var buffer = new byte[4097];

        try
        {
            if (RealPathOf(path, ref buffer[0]) == 0) return null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }

        var length = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }

    /// <summary>
    /// Device, inode and the whole st_mode — type and permission bits — of what
    /// a path reaches, or null when that cannot be told.
    /// </summary>
    public static (ulong Device, ulong Inode, uint Mode)? Full(string path)
    {
        var at = ModeOffset;

        if (at < 0 || Read(path) is not { } buffer) return null;

        return (BitConverter.ToUInt64(buffer, 0), BitConverter.ToUInt64(buffer, 8), BitConverter.ToUInt32(buffer, at));
    }

    /// <summary>
    /// Where st_mode sits: the one field whose place differs between the two
    /// layouts — after st_nlink on x86-64, before it on aarch64.
    /// </summary>
    private static int ModeOffset => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 24,
        Architecture.Arm64 => 16,
        _ => -1,
    };

    public static (ulong Device, ulong Inode)? Of(string path)
        => Read(path) is { } buffer
            ? (BitConverter.ToUInt64(buffer, 0), BitConverter.ToUInt64(buffer, 8))
            : null;

    /// <summary>
    /// Whether what a path reaches is something other than a regular file or a
    /// folder — a FIFO, a socket, a device — or null when that cannot be told.
    ///
    /// **Opening a FIFO to read waits until something writes, and nothing
    /// may.** The checksum button opened whatever properties had been asked
    /// about, on the thread drawing the window, so a named pipe's properties
    /// and one click froze the application for good. Asked through the link, so
    /// a link to a FIFO is a FIFO.
    /// </summary>
    public static bool? IsSpecial(string path)
        => Full(path) is { } found ? (found.Mode & 0xF000) is not (0x8000 or 0x4000) : null;

    private static byte[]? Read(string path)
    {
        if (!_available || !OperatingSystem.IsLinux() || !Environment.Is64BitProcess) return null;

        // Larger than struct stat on either layout (144 and 128 bytes).
        var buffer = new byte[256];

        try
        {
            return Stat(path, ref buffer[0]) == 0 ? buffer : null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            _available = false;
            return null;
        }
    }

    /// <summary>Whether two paths reach one entry. False when either cannot be asked.</summary>
    public static bool Same(string a, string b)
        => Of(a) is { } left && Of(b) is { } right && left == right;
}
