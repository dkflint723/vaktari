using System.Formats.Tar;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The QA round's own archive checks on the streamlining change.
///
/// **The foreign signatures are asked only after Sniff found nothing**, and
/// that order is the whole difference between refusing a 7z named .zip and
/// refusing a perfectly good tar. A plain tar begins with its first member's
/// NAME, so a tar whose first file is called "BZh…" or "LZIP…" starts with
/// bytes that are bzip2's and lzip's signatures. Sniff knows it for a tar by
/// its header ("ustar" at 257, or the checksum), and must be asked first;
/// asked the other way round, the tar is refused as "a bzip2 archive".
/// Nothing pinned that order before this.
/// </summary>
public sealed class StreamlineQaArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-streamline-qa").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    [Theory]
    [InlineData("BZh91AY.txt", TarEntryFormat.Ustar)]
    [InlineData("LZIP notes.txt", TarEntryFormat.Ustar)]
    [InlineData("BZh91AY.txt", TarEntryFormat.V7)]
    public void Foreign_looking_first_member_of_a_plain_tar_still_extracts(string first, TarEntryFormat format)
    {
        var archive = Path.Combine(_root, "names.tar");

        using (var file = File.Create(archive))
        using (var tar = new TarWriter(file, format, leaveOpen: false))
        {
            foreach (var name in new[] { first, "second.txt" })
            {
                TarEntry entry = format == TarEntryFormat.V7
                    ? new V7TarEntry(TarEntryType.V7RegularFile, name)
                    : new UstarTarEntry(TarEntryType.RegularFile, name);

                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("content of " + name));
                tar.WriteEntry(entry);
            }
        }

        // The precondition, so this cannot pass by not testing anything: the
        // archive's first bytes ARE a foreign signature.
        var head = File.ReadAllBytes(archive).AsSpan(0, 512);
        Assert.NotNull(ArchiveFormats.Foreign(head));

        var into = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var done = Archives.Extract(archive, into);

        Assert.False(done.IsFile);
        Assert.Equal("content of " + first, File.ReadAllText(Path.Combine(done.Landed, first)));
        Assert.Equal("content of second.txt", File.ReadAllText(Path.Combine(done.Landed, "second.txt")));
    }

    /// <summary>
    /// **The README says a tar named .zip is read as a tar** (b6184a8): the
    /// name decides whether the row is offered, the first bytes how the file
    /// is read. Nothing tested it. A plain tar, and a gzip-compressed one,
    /// each named .zip, land as the tar they are, in a folder named after the
    /// archive without its ".zip".
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_tar_named_zip_is_read_as_a_tar(bool gzipped)
    {
        var archive = Path.Combine(_root, "backup.zip");

        using (var file = File.Create(archive))
        using (Stream body = gzipped
                   ? new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)
                   : file)
        using (var tar = new TarWriter(body, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var name in new[] { "one.txt", "two.txt" })
            {
                var entry = new UstarTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("content of " + name)),
                };

                tar.WriteEntry(entry);
            }
        }

        Assert.True(Archives.CanExtract(archive), "the row is offered by name");

        var into = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var done = Archives.Extract(archive, into);

        Assert.False(done.IsFile);
        Assert.Equal(Path.Combine(into, "backup"), done.Landed);
        Assert.Equal("content of one.txt", File.ReadAllText(Path.Combine(done.Landed, "one.txt")));
        Assert.Equal("content of two.txt", File.ReadAllText(Path.Combine(done.Landed, "two.txt")));
    }
}
