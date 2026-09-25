using System.Runtime.InteropServices;
using System.Text;

namespace Vaktari.Windows;

/// <summary>
/// Reads the target path out of a .lnk file.
///
/// **By the format rather than through the shell.** IShellLink would do this in
/// three calls and is COM, which is the decision this port has been deferring;
/// the Shell Link Binary File Format (MS-SHLLINK) is public, stable since
/// Windows 95, and the one field wanted here sits in a fixed place near the
/// front. Reading it costs a header parse and no dependency.
///
/// Deliberately partial. It reads LinkInfo, which is where a shortcut to a
/// local or mapped path keeps its target, and does not attempt the
/// LinkTargetIDList — the shell's own item-ID chain, which encodes virtual
/// locations like Control Panel and needs the namespace to resolve. A shortcut
/// with no LinkInfo returns null and is skipped, which is the right answer:
/// this is used to import folder bookmarks, and a bookmark to a virtual
/// location is not a folder Vaktari could list anyway.
/// </summary>
internal static class ShellLink
{
    private const int HeaderSize = 0x4C;

    private const uint HasLinkTargetIdList = 0x00000001;
    private const uint HasLinkInfo = 0x00000002;
    private const uint ForceNoLinkInfo = 0x00000100;

    private const uint VolumeIdAndLocalBasePath = 0x00000001;

    /// <summary>The path a shortcut points at, or null if it cannot be read.</summary>
    internal static string? TargetOf(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return Parse(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? Parse(byte[] bytes)
    {
        // Header, and the signature that says this is a shell link at all.
        if (bytes.Length < HeaderSize) return null;
        if (BitConverter.ToUInt32(bytes, 0) != HeaderSize) return null;

        var flags = BitConverter.ToUInt32(bytes, 0x14);

        if ((flags & HasLinkInfo) == 0 || (flags & ForceNoLinkInfo) != 0) return null;

        var offset = HeaderSize;

        // The target ID list is variable length and is skipped wholesale: its
        // own size prefix is the only part of it this needs.
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (offset + 2 > bytes.Length) return null;

            var idListSize = BitConverter.ToUInt16(bytes, offset);
            offset += 2 + idListSize;
        }

        return ReadLinkInfo(bytes, offset);
    }

    private static string? ReadLinkInfo(byte[] bytes, int start)
    {
        // LinkInfoSize, LinkInfoHeaderSize, LinkInfoFlags, then four offsets.
        if (start + 24 > bytes.Length) return null;

        var headerSize = BitConverter.ToUInt32(bytes, start + 4);
        var linkInfoFlags = BitConverter.ToUInt32(bytes, start + 8);

        // Only the local-path arm is handled; a purely network-relative link
        // keeps its path somewhere this does not read.
        if ((linkInfoFlags & VolumeIdAndLocalBasePath) == 0) return null;

        var basePathOffset = BitConverter.ToUInt32(bytes, start + 16);
        var suffixOffset = BitConverter.ToUInt32(bytes, start + 24);

        // A header of 0x24 or more carries the Unicode offsets as well, and
        // those are preferred: the ANSI fields are in the machine's code page
        // and mangle any character outside it, which is most of the ways a
        // person names a folder.
        if (headerSize >= 0x24 && start + 36 <= bytes.Length)
        {
            var unicodeBase = BitConverter.ToUInt32(bytes, start + 28);
            var unicodeSuffix = BitConverter.ToUInt32(bytes, start + 32);

            if (unicodeBase != 0)
            {
                var basePath = ReadUnicode(bytes, start + (int)unicodeBase);
                var suffix = unicodeSuffix == 0 ? "" : ReadUnicode(bytes, start + (int)unicodeSuffix);

                return Join(basePath, suffix);
            }
        }

        if (basePathOffset == 0) return null;

        return Join(
            ReadAnsi(bytes, start + (int)basePathOffset),
            suffixOffset == 0 ? "" : ReadAnsi(bytes, start + (int)suffixOffset));
    }

    private static string? Join(string? basePath, string? suffix)
    {
        if (string.IsNullOrEmpty(basePath)) return null;

        return string.IsNullOrEmpty(suffix) ? basePath : basePath + suffix;
    }

    private static string? ReadAnsi(byte[] bytes, int offset)
    {
        if (offset < 0 || offset >= bytes.Length) return null;

        var end = Array.IndexOf<byte>(bytes, 0, offset);
        if (end < 0) end = bytes.Length;

        // **In the machine's code page, which is what wrote it.**
        // Encoding.Default is UTF-8 on .NET, so "Música", stored as 0xFA for
        // the ú, came back as "M�sica" — a folder that does not exist, so
        // double-clicking the shortcut opened Explorer instead of the folder.
        // The ANSI conversion the system itself uses is the one that reverses
        // what it did.
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        try
        {
            return Marshal.PtrToStringAnsi(handle.AddrOfPinnedObject() + offset, end - offset);
        }
        finally
        {
            handle.Free();
        }
    }

    private static string? ReadUnicode(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 1 >= bytes.Length) return null;

        var end = offset;

        while (end + 1 < bytes.Length && !(bytes[end] == 0 && bytes[end + 1] == 0))
            end += 2;

        return Encoding.Unicode.GetString(bytes, offset, end - offset);
    }
}
