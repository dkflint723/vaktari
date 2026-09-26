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
    [InlineData("a.7z", ArchiveFormat.SevenZip)]
    [InlineData("a.rar", ArchiveFormat.Rar)]
    [InlineData("a.tar", ArchiveFormat.Tar)]
    [InlineData("a.tar.gz", ArchiveFormat.TarGz)]
    [InlineData("a.TAR.GZ", ArchiveFormat.TarGz)]
    [InlineData("a.tgz", ArchiveFormat.TarGz)]
    [InlineData("a.tar.bz2", ArchiveFormat.TarBz2)]
    [InlineData("a.tbz", ArchiveFormat.TarBz2)]
    [InlineData("a.tbz2", ArchiveFormat.TarBz2)]
    [InlineData("a.tar.xz", ArchiveFormat.TarXz)]
    [InlineData("a.txz", ArchiveFormat.TarXz)]
    [InlineData("a.tar.zst", ArchiveFormat.TarZst)]
    [InlineData("a.tzst", ArchiveFormat.TarZst)]
    [InlineData("a.tar.lz", ArchiveFormat.TarLz)]
    [InlineData("a.tlz", ArchiveFormat.TarLz)]
    [InlineData("notes.txt.gz", ArchiveFormat.Gz)]
    [InlineData("notes.txt.bz2", ArchiveFormat.Bz2)]
    [InlineData("notes.txt.xz", ArchiveFormat.Xz)]
    [InlineData("notes.txt.zst", ArchiveFormat.Zst)]
    [InlineData("notes.txt.lz", ArchiveFormat.Lz)]
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
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0 }, ArchiveFormat.SevenZip)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, ArchiveFormat.Rar)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, ArchiveFormat.Rar)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, ArchiveFormat.Gz)]
    [InlineData(new byte[] { 0x42, 0x5A, 0x68, 0x39 }, ArchiveFormat.Bz2)]
    [InlineData(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, ArchiveFormat.Xz)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, ArchiveFormat.Zst)]
    [InlineData(new byte[] { 0x4C, 0x5A, 0x49, 0x50, 0x01 }, ArchiveFormat.Lz)]
    public void The_bytes_say_the_format(byte[] head, ArchiveFormat format)
        => Assert.Equal(format, ArchiveFormats.Sniff(head));

    [Fact]
    public void Text_is_no_format() => Assert.Null(ArchiveFormats.Sniff("<html>404</html>"u8));

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
    [InlineData("v1.2.7z", "v1.2")]
    [InlineData(".zip", "Archive")]
    [InlineData("dir/sub/x.tar.zst", "x")]
    public void The_stem_takes_off_the_whole_suffix(string path, string stem)
        => Assert.Equal(stem, ArchiveFormats.Stem(path));

    [Theory]
    [InlineData(ArchiveFormat.Zip, "zip")]
    [InlineData(ArchiveFormat.SevenZip, "7z")]
    [InlineData(ArchiveFormat.Rar, "RAR")]
    [InlineData(ArchiveFormat.TarGz, "gzip")]
    [InlineData(ArchiveFormat.Lz, "lzip")]
    public void Each_format_has_a_word(ArchiveFormat format, string word)
        => Assert.Equal(word, ArchiveFormats.Word(format));
}
