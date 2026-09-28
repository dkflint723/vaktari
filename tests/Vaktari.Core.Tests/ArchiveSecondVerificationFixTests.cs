using System.Runtime.Versioning;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The rest of what the second verification of Stage A found, beside its
/// own paste-ready ArchiveSecondVerificationBugTests.
/// </summary>
public sealed class ArchiveSecondVerificationFixTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-verify2fix").FullName;

    public void Dispose()
    {
        try { Archives.DeleteTree(_root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Dir(string name) => Directory.CreateDirectory(At(name)).FullName;

    private sealed class Hooks : IExtractionObserver
    {
        public Action<string, string>? While { get; init; }

        public void BeforeCreate(string path) { }
        public void WhileWriting(string temporary, string final) => While?.Invoke(temporary, final);
        public void BeforeLanding(string target) { }
    }

    // ---- 1. the working folder, re-opened by name ---------------------------

    /// <summary>
    /// The working folder is renamed away and a link to somebody's folder put
    /// in its place while the run writes. Nothing is written through it (the
    /// chain check), and nothing it points at is moved into the destination
    /// or deleted: the landing refuses, and the discard removes the link.
    /// </summary>
    [Fact]
    public void A_working_folder_swapped_for_a_link_mid_run_moves_and_deletes_nothing_it_points_at()
    {
        var victim = Dir("victim");

        Directory.CreateDirectory(Path.Combine(victim, "photos"));
        File.WriteAllText(Path.Combine(victim, "photos", "one.jpg"), "precious");

        var archive = ArchiveTestData.Zip(At("a.zip"), ("a.txt", "a"), ("b.txt", "b"));
        var into = Dir("out");
        var swapped = false;

        Assert.ThrowsAny<Exception>(() => Archives.Extract(archive, into, null, default, ArchiveRoom.Real, new Hooks
        {
            While = (_, final) =>
            {
                if (swapped || !final.EndsWith("b.txt", StringComparison.Ordinal)) return;

                swapped = true;

                var working = Path.GetDirectoryName(final)!;

                Directory.Move(working, working + "-moved");
                TestLinks.FolderLink(working, victim);
            },
        }));

        Assert.True(swapped);
        Assert.Equal("precious", File.ReadAllText(Path.Combine(victim, "photos", "one.jpg")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(into), p => Path.GetFileName(p) == "photos");
    }

    // ---- 3. read-only folders inside a swept working folder ------------------

    [Fact]
    public void A_read_only_folder_inside_an_abandoned_working_folder_is_swept_too()
    {
        var into = Dir("out");
        var abandoned = Directory.CreateDirectory(Path.Combine(into, ".vaktari-extracting-0123456789ab")).FullName;
        var locked = Directory.CreateDirectory(Path.Combine(abandoned, "locked")).FullName;

        File.WriteAllText(Path.Combine(locked, "inside.txt"), "x");
        MakeReadOnly(locked);
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));

        Assert.Equal(1, Archives.Sweep(into));
        Assert.False(Directory.Exists(abandoned));
    }

    private static void MakeReadOnly(string folder)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.ReadOnly);
        else ReadOnlyOnUnix(folder);
    }

    [UnsupportedOSPlatform("windows")]
    private static void ReadOnlyOnUnix(string folder) => File.SetUnixFileMode(folder, (UnixFileMode)0x16D);

    // ---- 4. a declared size the floor check could wrap ------------------------

    /// <summary>
    /// A PAX tar entry declaring long.MaxValue bytes: the per-entry floor
    /// check added the floor to it and wrapped negative. It is refused before
    /// a byte is written, in the floor's words.
    ///
    /// Inside a gzip, because a plain tar cannot hold more than itself and
    /// such an entry is now damage before the room is asked about (see
    /// <see cref="A_plain_tar_entry_declaring_more_than_the_archive_holds_is_damage_before_anything_is_written"/>);
    /// a compressed one can, so only the room check stands in its way.
    /// </summary>
    [Fact]
    public void A_tar_entry_declaring_the_largest_long_is_refused_by_the_floor()
    {
        using (var file = File.Create(At("huge.tar.gz")))
        using (var gzip = ArchiveTestData.Compressor(ArchiveFormat.TarGz)(file))
            gzip.Write(PaxTarDeclaring(long.MaxValue));

        var refused = Assert.Throws<ArchiveRefusedException>(() => Archives.Extract(
            At("huge.tar.gz"), Dir("out"), null, default, new ArchiveRoom(_ => 1L << 40, _ => null), observer: null));

        Assert.StartsWith("stopped before huge.tar.gz filled ", refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    /// <summary>
    /// **A plain tar entry larger than the whole archive is a header that
    /// lies**, and the room check would be asked in its terms: 500 MiB
    /// declared in a 3 KB tar, onto a drive with room for it, is damage said
    /// before anything is created.
    /// </summary>
    [Fact]
    public void A_plain_tar_entry_declaring_more_than_the_archive_holds_is_damage_before_anything_is_written()
    {
        File.WriteAllBytes(At("liar.tar"), PaxTarDeclaring(500L * 1024 * 1024));

        var created = new Creations();

        Assert.Throws<ArchiveDamagedException>(() => Archives.Extract(
            At("liar.tar"), Dir("out"), null, default, new ArchiveRoom(_ => 1L << 40, _ => null), created));

        Assert.Empty(created.Paths);
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    private sealed class Creations : IExtractionObserver
    {
        public List<string> Paths { get; } = [];

        public void BeforeCreate(string path) => Paths.Add(path);
        public void WhileWriting(string temporary, string final) { }
        public void BeforeLanding(string target) { }
    }

    /// <summary>A PAX header setting the size, then a file header of size 0,
    /// then the end of the archive — written by hand, because no writer will
    /// declare a size its data does not have.</summary>
    private static byte[] PaxTarDeclaring(long size)
    {
        var record = $"size={size}\n";
        var line = $"{record.Length + 3} {record}";

        if (Encoding.ASCII.GetByteCount(line) != record.Length + 3) throw new InvalidOperationException("record length");

        var pax = Encoding.ASCII.GetBytes(line);
        var ms = new MemoryStream();

        ms.Write(Header("PaxHeaders/huge.bin", 'x', pax.Length));
        ms.Write(pax);
        ms.Write(new byte[512 - pax.Length]);
        ms.Write(Header("huge.bin", '0', 0));
        ms.Write(new byte[1024]);

        return ms.ToArray();
    }

    private static byte[] Header(string name, char type, long size)
    {
        var h = new byte[512];

        Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(h, 100);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(h, 108);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(h, 116);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(h, 124);
        Encoding.ASCII.GetBytes("00000000000\0").CopyTo(h, 136);
        h[156] = (byte)type;
        Encoding.ASCII.GetBytes("ustar\0" + "00").CopyTo(h, 257);

        for (var i = 148; i < 156; i++) h[i] = (byte)' ';

        var sum = h.Sum(b => (int)b);

        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(h, 148);

        return h;
    }

    // ---- 5. sizes no archive can hold, said as damage ---------------------------

    /// <summary>
    /// Two entries each declaring long.MaxValue: the sum cannot be taken.
    /// Said as what it is — damage — not "not a zip file".
    /// </summary>
    [Fact]
    public void Sizes_that_cannot_be_added_up_are_called_damage()
    {
        File.WriteAllBytes(At("impossible.zip"), ZipBytes.Build(
            new ZipBytes.Entry("a.bin") { Data = "a"u8.ToArray(), Size64 = long.MaxValue },
            new ZipBytes.Entry("b.bin") { Data = "b"u8.ToArray(), Size64 = long.MaxValue }));

        var damaged = Assert.Throws<ArchiveDamagedException>(() => Archives.Extract(At("impossible.zip"), Dir("out")));

        Assert.Equal("impossible.zip is damaged — it declares sizes no archive can hold", damaged.Message);
    }
}
