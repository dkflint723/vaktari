using System.Buffers.Binary;
using System.IO.Compression;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **A compressed tar is checked to the end of its compression, not to the
/// end of the tar.** The pass stopped at the tar's end blocks and never read
/// the rest of the stream, so the checks each compressor keeps there — gzip's
/// CRC-32 and length, the xz block check, lzip's and bzip2's CRCs, a zstd
/// frame's checksum — were never read. A .tar.gz with one byte flipped
/// extracted with the wrong bytes in a file and no word said: 55 flips in 60
/// did (0.11.1 changelog check, archive.md).
///
/// Every archive here is copied into a folder of the test's own before it is
/// damaged; the committed fixtures are only read.
/// </summary>
public sealed class ArchiveStreamEndTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-streamend").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(string name) => Path.Combine(_root, name);

    private string Dir(string name) => Directory.CreateDirectory(At(name)).FullName;

    /// <summary>Every file under <paramref name="folder"/>, by its path
    /// inside it, with its bytes.</summary>
    private static SortedDictionary<string, byte[]> Tree(string folder)
    {
        var tree = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            tree[Path.GetRelativePath(folder, file).Replace('\\', '/')] = File.ReadAllBytes(file);

        return tree;
    }

    /// <summary>
    /// <paramref name="bytes"/> written as <paramref name="name"/> and
    /// extracted into a fresh folder: the damaged sentence with nothing
    /// landed, or the tree that landed.
    /// </summary>
    private (ArchiveDamagedException? Damaged, SortedDictionary<string, byte[]>? Landed) Extract(
        string name, byte[] bytes, string into)
    {
        var archive = At(name);
        File.WriteAllBytes(archive, bytes);

        var destination = Dir(into);

        try
        {
            var done = Archives.Extract(archive, destination);

            return (null, done.IsFile
                ? new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { [""] = File.ReadAllBytes(done.Landed) }
                : Tree(done.Landed));
        }
        catch (ArchiveDamagedException damaged)
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));

            return (damaged, null);
        }
    }

    private void AssertDamaged(string name, byte[] bytes, string into)
    {
        var (damaged, landed) = Extract(name, bytes, into);

        Assert.True(damaged is not null,
            $"{name} extracted {landed?.Count} file(s) with no word said; its check should have failed");
        Assert.Contains($"{name} is damaged", damaged.Message, StringComparison.Ordinal);
        Assert.EndsWith("nothing was extracted", damaged.Message, StringComparison.Ordinal);
    }

    /// <summary>A tar of three files whose bytes are all different, so a
    /// changed byte anywhere shows.</summary>
    private byte[] TarOfThree(ArchiveFormat format)
    {
        var path = ArchiveTestData.Tar(At("made" + Guid.NewGuid().ToString("N")[..6]), t =>
        {
            for (var n = 0; n < 3; n++)
            {
                var content = string.Concat(Enumerable.Range(0, 400).Select(i => $"file {n} line {i:D4}\n"));
                t.WriteEntry(ArchiveTestData.File_($"dir/f{n}.txt", content));
            }
        }, format == ArchiveFormat.Tar ? null : ArchiveTestData.Compressor(format));

        return File.ReadAllBytes(path);
    }

    // ---- a byte flipped where the stream still decodes -------------------------

    /// <summary>
    /// **The flip that always decodes.** A gzip of stored deflate blocks
    /// carries the tar's bytes as they are, so a byte flipped inside one is
    /// a changed byte in a file, and nothing in the deflate data can notice.
    /// Only the trailer's CRC-32 can, and it sits past the tar's end blocks.
    /// </summary>
    [Fact]
    public void A_stored_tar_gz_with_a_changed_byte_is_damaged()
    {
        var path = ArchiveTestData.Tar(At("stored-src"), t =>
                t.WriteEntry(ArchiveTestData.File_("one.txt", new string('a', 4000))),
            s => new GZipStream(s, CompressionLevel.NoCompression, leaveOpen: true));

        var bytes = File.ReadAllBytes(path);

        // Honest, it extracts; and the byte chosen is one of one.txt's 'a's.
        var (none, clean) = Extract("stored.tar.gz", bytes, "clean");
        Assert.Null(none);
        Assert.Equal(new string('a', 4000), System.Text.Encoding.ASCII.GetString(clean!["one.txt"]));

        var at = Array.IndexOf(bytes, (byte)'a', bytes.Length / 4);
        Assert.True(at > 0);
        bytes[at] = (byte)'b';

        AssertDamaged("stored.tar.gz", bytes, "out");
    }

    /// <summary>
    /// The sweep that found it, for each compressor with a check: 60 bytes
    /// flipped across the file, one at a time. Each must either be refused,
    /// with nothing landed, or land exactly what the honest archive does —
    /// never different bytes in silence. Before the fix, tree.tar.gz gave
    /// different bytes 55 times and sc-tar.tar.zst 52.
    /// </summary>
    [Theory]
    [InlineData("tree.tar.gz")]
    [InlineData("bare.txt.gz")]
    public void A_flipped_byte_is_never_extracted_as_different_bytes(string fixture)
    {
        var honest = File.ReadAllBytes(ArchiveTestData.Fixture(fixture));

        var (none, truth) = Extract(fixture, honest, "truth");
        Assert.Null(none);

        var wrong = new List<int>();

        for (var n = 0; n < 60; n++)
        {
            var at = (int)((long)n * (honest.Length - 1) / 59);
            var bytes = (byte[])honest.Clone();
            bytes[at] ^= 0x01;

            try
            {
                var (_, landed) = Extract(fixture, bytes, "flip" + n);

                if (landed is not null && !Same(truth!, landed)) wrong.Add(at);
            }
            catch (Exception e) when (e is InvalidDataException or ArchiveRefusedException)
            {
                // Refused up front — "not a gz file, or is damaged" — is an
                // answer too; nothing was written for it to be wrong about.
                Assert.Empty(Directory.EnumerateFileSystemEntries(At("flip" + n)));
            }
        }

        Assert.True(wrong.Count == 0,
            $"{fixture}: {wrong.Count} of 60 flips extracted different bytes in silence, at {string.Join(", ", wrong)}");
    }

    private static bool Same(SortedDictionary<string, byte[]> a, SortedDictionary<string, byte[]> b)
        => a.Keys.SequenceEqual(b.Keys) && a.Keys.All(k => a[k].AsSpan().SequenceEqual(b[k]));

    // ---- the trailer's own check, damaged ---------------------------------------

    /// <summary>
    /// **The check alone, damaged, every byte of the data intact.** The
    /// bytes that land would be right — but the check is the only thing that
    /// says so, and a check that fails is damage, whatever else decoded. Each
    /// case flips one byte of the stored check itself:
    /// gzip's CRC-32 and its length (the last eight bytes). (The lzip, zstd,
    /// xz and bzip2 cases went with those formats.)
    /// </summary>
    [Theory]
    [InlineData("tree.tar.gz", "gzip crc")]
    [InlineData("tree.tar.gz", "gzip length")]
    public void A_compressed_tar_whose_trailer_check_fails_is_damaged(string fixture, string check)
    {
        var bytes = File.ReadAllBytes(ArchiveTestData.Fixture(fixture));

        bytes[TrailerCheck(bytes, check)] ^= 0xFF;

        AssertDamaged(fixture, bytes, "out");
    }

    /// <summary>The same, on a tar compressed here rather than a fixture: the
    /// four compressors the tests can write.</summary>
    [Theory]
    [InlineData(ArchiveFormat.TarGz, "x.tar.gz", "gzip crc")]
    [InlineData(ArchiveFormat.TarGz, "x.tar.gz", "gzip length")]
    public void A_tar_compressed_here_whose_trailer_check_fails_is_damaged(ArchiveFormat format, string name, string check)
    {
        var bytes = TarOfThree(format);

        var (none, _) = Extract(name, bytes, "clean");
        Assert.Null(none);

        bytes[TrailerCheck(bytes, check)] ^= 0xFF;

        AssertDamaged(name, bytes, "out");
    }

    /// <summary>The bare streams, read to their end by the copy already:
    /// pinned beside the tar cases so the two cannot drift apart.</summary>
    [Theory]
    [InlineData("bare.txt.gz", "gzip crc")]
    [InlineData("bare.txt.gz", "gzip length")]
    public void A_bare_stream_whose_trailer_check_fails_is_damaged(string fixture, string check)
    {
        var bytes = File.ReadAllBytes(ArchiveTestData.Fixture(fixture));

        bytes[TrailerCheck(bytes, check)] ^= 0xFF;

        AssertDamaged(fixture, bytes, "out");
    }

    /// <summary>Where the named check's byte is, in a single-stream file.</summary>
    private static int TrailerCheck(byte[] bytes, string check) => check switch
    {
        "gzip crc" => bytes.Length - 8,
        "gzip length" => bytes.Length - 1,
        _ => throw new ArgumentOutOfRangeException(nameof(check)),
    };
}
