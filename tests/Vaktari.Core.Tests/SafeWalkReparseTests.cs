using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The walk with no reader of reparse tags, which is how this assembly runs it.
///
/// <see cref="SafeWalk.IsLink"/> tells a link from the other reparse points by
/// the tag, and reading a tag is a call into Windows that Core does not make:
/// the Windows platform adopts a reader. What the walk does WITH that reader is
/// tested beside the reader, in Vaktari.Windows.Tests' SafeWalkLinkTests. This
/// pins what it does without one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SafeWalkReparseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-reparse").FullName;

    private readonly List<string> _tagged = [];

    /// <summary>Untagged first, so what is left is an ordinary file; then only
    /// the folder this class made.</summary>
    public void Dispose()
    {
        foreach (var path in _tagged)
        {
            try { Reparse.Remove(path); }
            catch (Exception) { /* the delete below says what it cannot take */ }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }

    /// <summary>
    /// GUARD, and it says so: it passed before the walk had a reader, when every
    /// reparse point was a link. It is here for the hosts that adopt no reader —
    /// this test assembly among them — which must go on never following an entry
    /// they cannot tell apart.
    ///
    /// The fixture is a 1,234-byte file carrying a third-party reparse point,
    /// which an unprivileged process may set and no filter owns. Read with the
    /// platform's reader, it is a file.
    /// </summary>
    [WindowsFact]
    public void Without_a_reader_every_reparse_point_is_a_link()
    {
        // The premise: nothing in this assembly adopts one.
        Assert.Null(SafeWalk.ReparseTag);

        var path = Path.Combine(_root, "sized.bin");

        File.WriteAllBytes(path, new byte[1_234]);
        Reparse.Set(path);
        _tagged.Add(path);

        Assert.True((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0, "the reparse point did not take");

        var found = Assert.Single(SafeWalk.Descend(_root).ToList());

        Assert.True(found.IsLink, "with no reader, a reparse point was walked as something other than a link");
        Assert.Equal(0, found.Length);
    }

    /// <summary>
    /// Sets and removes a third-party reparse point: a REPARSE_GUID_DATA_BUFFER
    /// with bit 31 of the tag clear, so no Microsoft filter owns it.
    ///
    /// DllImport rather than the application's LibraryImport. That rule exists
    /// because the application ships as a NativeAOT binary, and a test project
    /// is never published.
    /// </summary>
    private static class Reparse
    {
        private const uint Tag = 0x00001234;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareAll = 0x7;
        private const uint OpenExisting = 3;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint FsctlSetReparsePoint = 0x000900A4;
        private const uint FsctlDeleteReparsePoint = 0x000900AC;

        private static readonly Guid Owner = Guid.Parse("6a6b8f1e-3c2d-4e5f-9a0b-1c2d3e4f5a6b");

        public static void Set(string path) => Control(path, FsctlSetReparsePoint, [1, 2, 3, 4]);

        public static void Remove(string path) => Control(path, FsctlDeleteReparsePoint, []);

        private static void Control(string path, uint code, byte[] data)
        {
            using var handle = CreateFileW(path, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);

            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

            var buffer = new byte[24 + data.Length];

            BitConverter.GetBytes(Tag).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)data.Length).CopyTo(buffer, 4);
            Owner.ToByteArray().CopyTo(buffer, 8);
            data.CopyTo(buffer, 24);

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
}
