using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Two product problems found by the second verification of Stage A. Both
/// tests FAIL on b995890 and are NOT committed; they are here for whoever
/// fixes the code. Proven red on Windows and in WSL Fedora 44.
/// </summary>
public sealed class ArchiveSecondVerificationBugTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-verify2bug").FullName;

    public void Dispose()
    {
        try { Archives.DeleteTree(_root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    /// <summary>
    /// **The working folder ITSELF is a link.** Sweep lists
    /// .vaktari-extracting-* with EnumerateDirectories, which returns a
    /// junction / a symlink to a folder, and DeleteTree pushes its root
    /// without asking whether it is a link: every file in the target is
    /// deleted. Anybody who can write into a destination (a shared folder,
    /// /tmp) can plant one and wait for the next Extract all there.
    /// Fix: in Sweep (or at the top of DeleteTree) a root that is a link
    /// (SafeWalk.IsLink / LinkTarget / ReparsePoint) is deleted as a link
    /// and never entered.
    /// </summary>
    [Fact]
    public void A_working_folder_that_is_itself_a_link_is_never_entered()
    {
        var outside = Directory.CreateDirectory(At("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "not the sweep's");
        Directory.SetLastWriteTimeUtc(outside, DateTime.UtcNow.AddHours(-2));

        var into = Directory.CreateDirectory(At("out")).FullName;
        TestLinks.FolderLink(Path.Combine(into, ".vaktari-extracting-0000feedbeef"), outside);

        // Aged as an attacker would simply wait out: the LINK's own time.
        AgeLinkItself(Path.Combine(into, ".vaktari-extracting-0000feedbeef"));

        Archives.Sweep(into);

        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    private static void AgeLinkItself(string link)
    {
        if (OperatingSystem.IsWindows())
        {
            var h = CreateFileW(link, 0x100, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            Assert.NotEqual(new IntPtr(-1), h);
            var t = DateTime.UtcNow.AddHours(-2).ToFileTimeUtc();
            try { Assert.True(SetFileTime(h, ref t, ref t, ref t)); }
            finally { CloseHandle(h); }
        }
        else
        {
            using var touch = System.Diagnostics.Process.Start("touch", ["-h", "-d", "2 hours ago", link])!;
            touch.WaitForExit();
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(IntPtr h, ref long created, ref long accessed, ref long written);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>
    /// **A Zip64 locator read two ways.** ZipDirectory reads the Zip64 end
    /// record at the locator's offset as written (unshifted), computes the
    /// shift from it, and hands SharpCompress the file from that shift on;
    /// SharpCompress then reads the SAME locator offset inside the view,
    /// i.e. shift bytes further on. With a second Zip64 end record planted
    /// there, the directory validated (names, overlap, sizes, CRCs) is not
    /// the one whose data is decoded. Both entries here have one name, one
    /// size and one CRC, so nothing else notices.
    /// Fix: read the Zip64 end record at locatorOffset + shift, where the
    /// shift comes from the locator's own position (end - 20 - 56), or
    /// refuse a Zip64 archive whose shift is not zero.
    /// </summary>
    [Fact]
    public void A_zip64_locator_names_one_end_record_for_both_readers()
    {
        // Two 8-byte contents with one CRC (the last four bytes forced).
        byte[] good = [.. "good"u8, 0x00, 0x00, 0x00, 0x00];
        byte[] evil = [.. "EVIL"u8, 0x00, 0x00, 0x00, 0x00];
        Forge(good, 0x1234ABCD);
        Forge(evil, 0x1234ABCD);

        var bytes = TwoZip64EndRecords(decoy: evil, real: good, crc: 0x1234ABCD);

        File.WriteAllBytes(At("z64.zip"), bytes);

        var directory = ZipDirectory.Read(new MemoryStream(bytes));
        var record = directory.Records.Single();
        var pointedAt = bytes.AsSpan((int)record.DataOffset, 8).ToArray();

        var done = Archives.Extract(At("z64.zip"), Directory.CreateDirectory(At("out")).FullName);

        Assert.Equal(pointedAt, File.ReadAllBytes(Path.Combine(done.Landed, "a.txt")));
    }

    // Layout: [pad s][decoy local][decoy CD][Z64 #1 at X][real local][real CD][Z64 #2 at X+s][locator -> X][EOCD]
    private static byte[] TwoZip64EndRecords(byte[] decoy, byte[] real, uint crc)
    {
        static byte[] Local(byte[] data, uint crc)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u);
            w.Write(crc); w.Write((uint)data.Length); w.Write((uint)data.Length); w.Write((ushort)5); w.Write((ushort)0);
            w.Write("a.txt"u8); w.Write(data);
            return ms.ToArray();
        }

        static byte[] Central(int size, uint crc, long local)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u);
            w.Write(crc); w.Write((uint)size); w.Write((uint)size); w.Write((ushort)5); w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write((uint)local); w.Write("a.txt"u8);
            return ms.ToArray();
        }

        static byte[] Z64(long cdSize, long cdOffset)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(0x06064b50u); w.Write(44L); w.Write((ushort)45); w.Write((ushort)45); w.Write(0u); w.Write(0u);
            w.Write(1L); w.Write(1L); w.Write(cdSize); w.Write(cdOffset);
            return ms.ToArray();
        }

        var decoyLocal = Local(decoy, crc);
        var realLocal = Local(real, crc);
        const int cd = 46 + 5;
        long s = 56 + realLocal.Length + cd;
        long decoyCdAt = s + decoyLocal.Length;
        long x = decoyCdAt + cd;
        long realLocalAt = x + 56;
        long realCdAt = realLocalAt + realLocal.Length;

        var tail = new MemoryStream(); var t = new BinaryWriter(tail);
        t.Write(0x07064b50u); t.Write(0u); t.Write(x); t.Write(1u);
        t.Write(0x06054b50u); t.Write((ushort)0); t.Write((ushort)0); t.Write((ushort)0xFFFF); t.Write((ushort)0xFFFF);
        t.Write(0xFFFFFFFFu); t.Write(0xFFFFFFFFu); t.Write((ushort)0);

        return
        [
            .. new byte[s], .. decoyLocal, .. Central(decoy.Length, crc, 0), .. Z64(cd, decoyCdAt - s),
            .. realLocal, .. Central(real.Length, crc, realLocalAt - s), .. Z64(cd, realCdAt - s),
            .. tail.ToArray(),
        ];
    }

    /// <summary>Forces the last four bytes so the CRC-32 of the whole is target.</summary>
    private static void Forge(byte[] data, uint target)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++) { var c = i; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; table[i] = c; }
        var rev = new int[256];
        for (var i = 0; i < 256; i++) rev[table[i] >> 24] = i;
        var r = ~target;
        var idx = new int[4];
        for (var k = 3; k >= 0; k--) { idx[k] = rev[r >> 24]; r = (r ^ table[idx[k]]) << 8; }
        var reg = ~ZipDirectory.Crc32(data.AsSpan(0, data.Length - 4));
        for (var k = 0; k < 4; k++) { data[data.Length - 4 + k] = (byte)((reg ^ (uint)idx[k]) & 0xFF); reg = (reg >> 8) ^ table[idx[k]]; }
        Assert.Equal(target, ZipDirectory.Crc32(data));
    }
}
