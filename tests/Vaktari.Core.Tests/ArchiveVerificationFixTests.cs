using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What the independent verification of the Stage A review fixes found,
/// beside the verifier's own ArchiveVerifyTests,
/// one test per finding.
/// </summary>
public sealed class ArchiveVerificationFixTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-verifyfix").FullName;

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

    private static Archives.Extraction Extract(string archive, string into, ArchiveRoom? room = null)
        => Archives.Extract(archive, into, null, default, room ?? ArchiveRoom.Real, observer: null);

    // ---- 1. a sweep never reaches through a link -------------------------

    /// <summary>
    /// **Clearing ReadOnly followed the link.** A symbolic link in an
    /// abandoned working folder, pointing at a read-only file somewhere
    /// else: the sweep removes the link, and the file it pointed at keeps
    /// its mode and its existence.
    /// </summary>
    [PosixFact]
    [SupportedOSPlatform("linux")]
    public void Sweeping_a_link_leaves_what_it_points_at_alone()
    {
        var outside = Dir("outside");
        var target = Path.Combine(outside, "precious.txt");

        File.WriteAllText(target, "not the sweep's");
        File.SetUnixFileMode(target, (UnixFileMode)0x124);

        var into = Dir("out");
        var abandoned = Directory.CreateDirectory(Path.Combine(into, ".vaktari-extracting-0000feedbeef")).FullName;

        File.CreateSymbolicLink(Path.Combine(abandoned, "link.txt"), target);
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));

        Assert.Equal(1, Archives.Sweep(into));

        Assert.False(Directory.Exists(abandoned));
        Assert.True(File.Exists(target));
        Assert.Equal((UnixFileMode)0x124, File.GetUnixFileMode(target));
    }

    // ---- 2. a declared size that wraps ------------------------------------

    [Fact]
    public void A_need_past_the_largest_long_saturates_rather_than_wrapping()
        => Assert.NotNull(new ArchiveRoom(_ => 500 * MiB, _ => null).RefuseUpFront("x", long.MaxValue, 3, "a.zip"));

    /// <summary>
    /// A Zip64 entry declaring exactly long.MaxValue bytes: refused before
    /// anything is written, where the wrapped sum let the run decode until
    /// the disk filled.
    /// </summary>
    [Fact]
    public void An_entry_declaring_the_largest_long_is_refused_up_front()
    {
        File.WriteAllBytes(At("max.zip"), ZipBytes.Build(
            new ZipBytes.Entry("huge.bin") { Data = "x"u8.ToArray(), Size64 = long.MaxValue }));

        var refused = Assert.Throws<ArchiveRefusedException>(
            () => Extract(At("max.zip"), Dir("out"), new ArchiveRoom(_ => 10L << 30, _ => null)));

        Assert.StartsWith("there is not enough room on ", refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    // ---- 3. the sentence names the place, not the working folder ----------

    [Fact]
    public void The_floor_sentence_names_the_destination_not_the_working_folder()
    {
        var archive = ArchiveTestData.Tar(At("big.tar.gz"), t => t.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "big.bin")
        {
            DataStream = new MemoryStream(new byte[8 * MiB]),
        }), ArchiveTestData.Compressor(ArchiveFormat.TarGz));

        var into = Dir("out");

        var refused = Assert.Throws<ArchiveRefusedException>(
            () => Extract(archive, into, new ArchiveRoom(_ => 200 * MiB, _ => null)));

        Assert.Equal($"stopped before big.tar.gz filled {ArchiveRoom.Drive(into)} — nothing was extracted", refused.Message);
        Assert.DoesNotContain(".vaktari", refused.Message);
    }

    // ---- 4. an archive's sentence survives Describe -----------------------

    /// <summary>
    /// **Describe answered by HResult and lost the archive's words**: a
    /// lock on the archive read "something else has that file open", and a
    /// pulled stick would read "that drive is not ready" — either drive.
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0x80070021))]
    [InlineData(unchecked((int)0x80070015))]
    [InlineData(unchecked((int)0x80070070))]
    public void An_unreadable_archive_keeps_its_own_sentence(int hresult)
    {
        var e = new ArchiveUnreadableException("a.zip could not be read — nothing was extracted") { HResult = hresult };

        Assert.Equal("a.zip could not be read — nothing was extracted", Failures.Describe(e));
    }

    // ---- 5. both readers see the same bytes ----------------------------------

    /// <summary>
    /// **Two readings of one prefixed zip.** The directory's offsets were
    /// written without the bytes in front, and those bytes are themselves a
    /// valid local entry of the same name holding different data. Our
    /// directory adds the shift and points at the real entry; SharpCompress,
    /// reading offsets from the start of the file, pointed at the planted
    /// one. It is now handed the file from where the zip starts, so both read
    /// the same entry — the real one, whose CRC the directory holds.
    /// </summary>
    [Fact]
    public void A_zip_behind_a_prefix_is_read_the_same_way_by_both_readers()
    {
        var real = ZipBytes.Build(
            new ZipBytes.Entry("a.txt") { Data = "good1"u8.ToArray() },
            new ZipBytes.Entry("b.txt") { Data = "good2"u8.ToArray() });
        var planted = ZipBytes.Build(new ZipBytes.Entry("a.txt") { Data = "EVIL!"u8.ToArray() });

        // The planted file's own local record: header, name and data, the
        // same length as the real a.txt's.
        var local = 30 + "a.txt".Length + 5;

        File.WriteAllBytes(At("prefixed.zip"), [.. planted.AsSpan(0, local).ToArray(), .. real]);

        var done = Extract(At("prefixed.zip"), Dir("out"));

        Assert.Equal("good1", File.ReadAllText(Path.Combine(done.Landed, "a.txt")));
        Assert.Equal("good2", File.ReadAllText(Path.Combine(done.Landed, "b.txt")));
    }

    /// <summary>The decoder is bounded by the local header and every rule
    /// by the directory: when their compressed sizes disagree, that is two
    /// archives in one file, and it is damage.</summary>
    [Fact]
    public void A_local_header_whose_compressed_size_disagrees_is_damage()
    {
        var zip = ZipBytes.Build(new ZipBytes.Entry("a.txt") { Data = "hello"u8.ToArray() });

        BitConverter.GetBytes(3u).CopyTo(zip, 18);

        Assert.Throws<InvalidDataException>(() => ZipDirectory.Read(new MemoryStream(zip)));
    }

    // ---- 6. nothing written lands nothing -------------------------------------

    [Fact]
    public void An_archive_whose_every_entry_is_left_out_lands_nothing_and_says_so()
    {
        var archive = ArchiveTestData.Zip(At("only.zip"), ("../escape.txt", "never"));
        var into = Dir("out");

        File.Move(archive, Path.Combine(into, "only.zip"));

        var refused = Assert.Throws<ArchiveRefusedException>(() => Extract(Path.Combine(into, "only.zip"), into));

        Assert.Equal("nothing in only.zip could be written: 1 left out (1 unsafe name)", refused.Message);
        Assert.Equal(["only.zip"], Directory.EnumerateFileSystemEntries(into).Select(Path.GetFileName));
    }

    /// <summary>An archive with no entries at all refused nothing, and still
    /// lands as the empty folder it is.</summary>
    [Fact]
    public void An_empty_archive_still_lands_as_an_empty_folder()
    {
        using (var file = File.Create(At("empty.zip")))
        using (new ZipArchive(file, ZipArchiveMode.Create)) { }

        var done = Extract(At("empty.zip"), Dir("out"));

        Assert.Equal(At("out", "empty"), done.Landed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(done.Landed));
    }
}
