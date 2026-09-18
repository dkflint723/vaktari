using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Two kinds of reparse point these tests need and no tool on a Windows runner
/// makes: the tag WSL leaves on NTFS for a symbolic link, and a third-party tag
/// that no filter owns.
///
/// An unprivileged process may set either on a file of its own or on an EMPTY
/// folder — measured — and they then read, to .NET and to the tag query,
/// exactly as the links WSL made through /mnt/c read: tag 0xA000001D, the
/// ReparsePoint attribute, no LinkTarget, and "the file cannot be accessed by
/// the system" to anything that tries to open or enter one.
///
/// DllImport rather than the application's LibraryImport. That rule exists
/// because the application ships as a NativeAOT binary, and a test project is
/// never published.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ReparseFixture
{
    private const uint WslTag = 0xA000001D;
    private const uint ThirdPartyTag = 0x00001234;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint FsctlSet = 0x000900A4;
    private const uint FsctlDelete = 0x000900AC;

    private static readonly Guid Owner = Guid.Parse("6a6b8f1e-3c2d-4e5f-9a0b-1c2d3e4f5a6b");

    /// <summary>A link as WSL writes one: version 2, then the target's text.</summary>
    public static void SetWsl(string path, bool directory)
        => Control(path, directory, FsctlSet, Microsoft(WslTag, [.. BitConverter.GetBytes(2u), .. Encoding.UTF8.GetBytes("elsewhere")]));

    public static void RemoveWsl(string path, bool directory)
        => Control(path, directory, FsctlDelete, Microsoft(WslTag, []));

    public static void SetThirdParty(string path, bool directory)
        => Control(path, directory, FsctlSet, Guided(ThirdPartyTag, [1, 2, 3, 4]));

    public static void RemoveThirdParty(string path, bool directory)
        => Control(path, directory, FsctlDelete, Guided(ThirdPartyTag, []));

    /// <summary>REPARSE_DATA_BUFFER, the shape a Microsoft tag takes.</summary>
    private static byte[] Microsoft(uint tag, byte[] data)
    {
        var buffer = new byte[8 + data.Length];

        BitConverter.GetBytes(tag).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)data.Length).CopyTo(buffer, 4);
        data.CopyTo(buffer, 8);

        return buffer;
    }

    /// <summary>REPARSE_GUID_DATA_BUFFER, the shape any other tag takes.</summary>
    private static byte[] Guided(uint tag, byte[] data)
    {
        var buffer = new byte[24 + data.Length];

        BitConverter.GetBytes(tag).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)data.Length).CopyTo(buffer, 4);
        Owner.ToByteArray().CopyTo(buffer, 8);
        data.CopyTo(buffer, 24);

        return buffer;
    }

    private static void Control(string path, bool directory, uint code, byte[] buffer)
    {
        using var handle = CreateFileW(
            path, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting,
            OpenReparsePoint | (directory ? BackupSemantics : 0), IntPtr.Zero);

        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        if (!DeviceIoControl(handle, code, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize,
        out int returned, IntPtr overlapped);
}
