using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What an archive is, by name (the menu row) and by bytes (the extraction).
/// </summary>
public sealed class ArchiveFormatsTests
{
    [Theory]
    [InlineData("a.zip", ArchiveFormat.Zip)]
    [InlineData("A.ZIP", ArchiveFormat.Zip)]
    [InlineData("a.tar", ArchiveFormat.Tar)]
    [InlineData("a.tar.gz", ArchiveFormat.TarGz)]
    [InlineData("a.TAR.GZ", ArchiveFormat.TarGz)]
    [InlineData("a.tgz", ArchiveFormat.TarGz)]
    [InlineData("notes.txt.gz", ArchiveFormat.Gz)]
    [InlineData("folder.zip", ArchiveFormat.Zip)]
    public void The_name_says_the_format(string name, ArchiveFormat format)
        => Assert.Equal(format, ArchiveFormats.ByName(name));

    /// <summary>Zips in all but name, and not offered: a menu row that takes
    /// a Word document apart is not one anybody expects.</summary>
    [Theory]
    [InlineData("report.docx")]
    [InlineData("app.jar")]
    [InlineData("book.epub")]
    [InlineData("notes.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_names_are_not_archives(string? name) => Assert.Null(ArchiveFormats.ByName(name));

    /// <summary>
    /// **The formats Extract all no longer reads are not offered it.** They
    /// open in the system's own archive tool on a double-click, which they
    /// always did; the menu row is the only thing that went.
    /// </summary>
    [Theory]
    [InlineData("a.7z")]
    [InlineData("a.rar")]
    [InlineData("a.tar.xz")]
    [InlineData("a.txz")]
    [InlineData("a.tar.bz2")]
    [InlineData("a.tbz2")]
    [InlineData("a.tbz")]
    [InlineData("a.tar.zst")]
    [InlineData("a.tzst")]
    [InlineData("a.tar.lz")]
    [InlineData("a.tlz")]
    [InlineData("notes.txt.xz")]
    [InlineData("notes.txt.bz2")]
    [InlineData("notes.txt.zst")]
    [InlineData("notes.txt.lz")]
    public void Removed_formats_are_not_offered(string name) => Assert.Null(ArchiveFormats.ByName(name));

    [Theory]
    [InlineData("a.part1.rar", true)]
    [InlineData("a.part01.rar", true)]
    [InlineData("a.r00", true)]
    [InlineData("a.R12", true)]
    [InlineData("a.7z.001", true)]
    [InlineData("a.zip.001", true)]
    [InlineData("a.z01", true)]
    [InlineData("a.rar", false)]
    [InlineData("a.7z", false)]
    [InlineData("partial.rar", false)]
    [InlineData("a.zip", false)]
    public void Split_volumes_are_known_by_name(string name, bool split)
        => Assert.Equal(split, ArchiveFormats.IsSplitVolume(name));

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0 }, ArchiveFormat.Zip)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x05, 0x06, 0 }, ArchiveFormat.Zip)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x30, 0x30, 0x50, 0x4B, 0x03, 0x04 }, ArchiveFormat.Zip)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, ArchiveFormat.Gz)]
    public void The_bytes_say_the_format(byte[] head, ArchiveFormat format)
        => Assert.Equal(format, ArchiveFormats.Sniff(head));

    /// <summary>The formats left to the system are still KNOWN by their
    /// bytes, so they can be refused for what they are (review H2).</summary>
    [Theory]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0 }, "7z")]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, "RAR")]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, "RAR")]
    [InlineData(new byte[] { 0x42, 0x5A, 0x68, 0x39 }, "bzip2")]
    [InlineData(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, "xz")]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, "zstd")]
    [InlineData(new byte[] { 0x4C, 0x5A, 0x49, 0x50, 0x01 }, "lzip")]
    public void The_bytes_name_a_format_left_to_the_system(byte[] head, string word)
    {
        Assert.Null(ArchiveFormats.Sniff(head));
        Assert.Equal(word, ArchiveFormats.Foreign(head));
    }

    [Fact]
    public void Text_is_no_format()
    {
        Assert.Null(ArchiveFormats.Sniff("<html>404</html>"u8));
        Assert.Null(ArchiveFormats.Foreign("<html>404</html>"u8));
    }

    /// <summary>A POSIX tar by its magic, and a v7 tar — which has none — by
    /// its header checksum.</summary>
    [Theory]
    [InlineData("v7.tar")]
    [InlineData("paxglobal.tar")]
    [InlineData("latin1-gnu.tar")]
    public void A_tar_is_known_by_its_header(string fixture)
    {
        var head = File.ReadAllBytes(ArchiveTestData.Fixture(fixture)).AsSpan(0, 512).ToArray();

        Assert.Equal(ArchiveFormat.Tar, ArchiveFormats.Sniff(head));
    }

    [Fact]
    public void A_v7_header_with_a_wrong_checksum_is_not_a_tar()
    {
        var head = File.ReadAllBytes(ArchiveTestData.Fixture("v7.tar")).AsSpan(0, 512).ToArray();

        head[0] ^= 0x01;

        Assert.False(ArchiveFormats.LooksLikeTar(head));
    }

    [Theory]
    [InlineData("x.tar.gz", "x")]
    [InlineData("x.TGZ", "x")]
    [InlineData("report.txt.gz", "report.txt")]
    [InlineData("photos.zip", "photos")]
    [InlineData("v1.2.zip", "v1.2")]
    [InlineData(".zip", "Archive")]
    [InlineData("dir/sub/x.tar", "x")]
    public void The_stem_takes_off_the_whole_suffix(string path, string stem)
        => Assert.Equal(stem, ArchiveFormats.Stem(path));

    [Theory]
    [InlineData(ArchiveFormat.Zip, "zip")]
    [InlineData(ArchiveFormat.Tar, "tar")]
    [InlineData(ArchiveFormat.TarGz, "gzip")]
    [InlineData(ArchiveFormat.Gz, "gzip")]
    public void Each_format_has_a_word(ArchiveFormat format, string word)
        => Assert.Equal(word, ArchiveFormats.Word(format));
}
