using System.IO.Compression;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **Reading a compressed tar to the end of its compression must not refuse
/// an honest one** (fix-12 verification). The pass now drains the decoder
/// after the tar's last entry so the trailer's check is read; what lies past
/// the tar's end blocks is therefore read too, and every shape an honest
/// compressor leaves there has to decode as it did before: a stream split into
/// several members (pigz, plzip, pbzip2, a zstd writer that flushes frames), a
/// zstd skippable frame after the data (the seekable format keeps its table
/// there), BGZF's empty end-of-file member, and a tar with more NUL blocks
/// after its end marker than the two it needs.
///
/// And the other half of the same rule: a check that fails in a member other
/// than the last is damage too, and a run cancelled while the drain is reading
/// lands nothing.
/// </summary>
public sealed class ArchiveDrainHonestyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-drainhonest").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(string name) => Path.Combine(_root, name);

    /// <summary>A plain tar of three files whose bytes all differ, with
    /// <paramref name="extraNulBlocks"/> NUL blocks after its end marker.</summary>
    private byte[] Tar(int extraNulBlocks = 0)
    {
        var path = ArchiveTestData.Tar(At("src" + Guid.NewGuid().ToString("N")[..6] + ".tar"), t =>
        {
            for (var n = 0; n < 3; n++)
                t.WriteEntry(ArchiveTestData.File_(
                    $"d/f{n}.txt", string.Concat(Enumerable.Range(0, 300).Select(i => $"file {n} line {i:D4}\n"))));
        });

        return [.. File.ReadAllBytes(path), .. new byte[512 * extraNulBlocks]];
    }

    private static byte[] Compress(ArchiveFormat format, byte[] data)
    {
        var ms = new MemoryStream();

        using (var z = ArchiveTestData.Compressor(format)(ms)) z.Write(data);

        return ms.ToArray();
    }

    /// <summary><paramref name="data"/> compressed as two members, split
    /// inside a tar block so neither member ends on a boundary.</summary>
    private static byte[] TwoMembers(ArchiveFormat format, byte[] data)
    {
        var half = data.Length / 2 + 37;

        return [.. Compress(format, data[..half]), .. Compress(format, data[half..])];
    }

    private SortedDictionary<string, string> Extract(string name, byte[] bytes)
    {
        var archive = At(name);
        File.WriteAllBytes(archive, bytes);

        var done = Archives.Extract(archive, Directory.CreateDirectory(At("out-" + Guid.NewGuid().ToString("N")[..6])).FullName);

        var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(done.Landed, "*", SearchOption.AllDirectories))
            tree[Path.GetRelativePath(done.Landed, file).Replace('\\', '/')] = File.ReadAllText(file);

        return tree;
    }

    private void AssertExtractsAsThePlainTar(string name, byte[] bytes)
    {
        var truth = Extract("truth.tar", Tar());

        Assert.Equal(3, truth.Count);
        Assert.Equal(truth, Extract(name, bytes));
    }

    // ---- honest shapes past the tar's end ----------------------------------------

    [Theory]
    [InlineData(ArchiveFormat.TarGz, "two.tar.gz")]
    public void A_tar_compressed_in_two_members_extracts_whole(ArchiveFormat format, string name)
        => AssertExtractsAsThePlainTar(name, TwoMembers(format, Tar()));

    [Theory]
    [InlineData(ArchiveFormat.TarGz, "nul.tar.gz")]
    public void Extra_nul_blocks_after_the_end_marker_are_not_damage(ArchiveFormat format, string name)
        => AssertExtractsAsThePlainTar(name, Compress(format, Tar(extraNulBlocks: 40)));

    /// <summary>BGZF (bgzip, samtools) ends every file with an empty gzip
    /// member: 28 bytes that decode to nothing.</summary>
    [Fact]
    public void An_empty_gzip_member_at_the_end_is_not_damage()
        => AssertExtractsAsThePlainTar("bgzf.tar.gz", [.. Compress(ArchiveFormat.TarGz, Tar()), .. Compress(ArchiveFormat.TarGz, [])]);

    // ---- a failed check in a member that is not the last ------------------------------

    /// <summary>
    /// Each member carries its own check, and the drain is what reaches every
    /// one after the tar's end marker. Damaged in the SECOND member: its check
    /// sits at the very end of the file. Damaged in the FIRST: the tar's end
    /// is in the second member, so the reader passed the bad check mid-way.
    /// </summary>
    [Theory]
    [InlineData(ArchiveFormat.TarGz, "bad2.tar.gz", 8, true)]
    [InlineData(ArchiveFormat.TarGz, "bad1.tar.gz", 8, false)]
    public void A_failed_check_in_either_member_is_damage(ArchiveFormat format, string name, int fromEnd, bool second)
    {
        var tar = Tar();
        var half = tar.Length / 2 + 37;
        var first = Compress(format, tar[..half]);
        byte[] bytes = [.. first, .. Compress(format, tar[half..])];

        bytes[(second ? bytes.Length : first.Length) - fromEnd] ^= 0xFF;

        var archive = At(name);
        File.WriteAllBytes(archive, bytes);
        var into = Directory.CreateDirectory(At("out-" + name)).FullName;

        var damaged = Assert.Throws<ArchiveDamagedException>(() => Archives.Extract(archive, into));

        Assert.Contains($"{name} is damaged", damaged.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    /// <summary>
    /// **The drain reads to the end, not one buffer's worth** (RC QA). Every
    /// other trailer test here is on an archive whose whole tail fits in the
    /// drain's first read, so a drain that read once and stopped passed them
    /// all but the lzip cases. Two MiB of NUL blocks after the end marker put
    /// the check well past one read; a failed check there is damage, and the
    /// same archive with its check intact still lands.
    /// </summary>
    [Theory]
    [InlineData(ArchiveFormat.TarGz, "tail.tar.gz", 8)]
    public void A_failed_check_past_a_long_tail_is_still_damage(ArchiveFormat format, string name, int fromEnd)
    {
        var bytes = Compress(format, Tar(extraNulBlocks: 4096));

        AssertExtractsAsThePlainTar(name, bytes);

        bytes[^fromEnd] ^= 0xFF;

        var archive = At("bad-" + name);
        File.WriteAllBytes(archive, bytes);
        var into = Directory.CreateDirectory(At("out-bad-" + name)).FullName;

        var damaged = Assert.Throws<ArchiveDamagedException>(() => Archives.Extract(archive, into));

        Assert.Contains($"bad-{name} is damaged", damaged.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    // ---- cancelled while the drain reads ----------------------------------------------

    private sealed class CancelAtLastFile(CancellationTokenSource cts, string lastLeaf) : IExtractionObserver
    {
        public bool Landed { get; private set; }

        public void BeforeCreate(string path) { }

        public void WhileWriting(string temporary, string final)
        {
            if (Path.GetFileName(final) == lastLeaf) cts.Cancel();
        }

        public void BeforeLanding(string target) => Landed = true;
    }

    /// <summary>
    /// The last file is written, the token is cancelled, and 256 MiB of NULs
    /// are still to be decompressed after the end marker: the run stops
    /// there, as a cancellation and not as damage, and nothing lands.
    /// </summary>
    [Fact]
    public void Cancelling_while_the_rest_of_the_stream_is_read_lands_nothing()
    {
        var bytes = Compress(ArchiveFormat.TarGz, Tar(extraNulBlocks: 512 * 1024));
        var archive = At("long.tar.gz");
        File.WriteAllBytes(archive, bytes);

        var into = Directory.CreateDirectory(At("out-cancel")).FullName;
        using var cts = new CancellationTokenSource();
        var hooks = new CancelAtLastFile(cts, "f2.txt");

        Assert.ThrowsAny<OperationCanceledException>(
            () => Archives.Extract(archive, into, null, cts.Token, ArchiveRoom.Real, hooks));

        Assert.True(cts.IsCancellationRequested, "the last file was never written, so the drain was never reached");
        Assert.False(hooks.Landed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }
}
