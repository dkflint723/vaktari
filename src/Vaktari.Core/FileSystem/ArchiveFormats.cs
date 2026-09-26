using System.Text.RegularExpressions;

namespace Vaktari.Core.FileSystem;

/// <summary>Every kind of archive Extract all reads.</summary>
public enum ArchiveFormat { Zip, SevenZip, Rar, Tar, TarGz, TarBz2, TarXz, TarZst, TarLz, Gz, Bz2, Xz, Zst, Lz }

/// <summary>
/// What an archive is, by its name and by its bytes.
///
/// **Two answers, because two different questions are asked.** The menu row
/// is asked every time the selection changes and before anything is clicked,
/// so it goes by name (<see cref="ByName"/>) and never opens the file. The
/// extraction itself goes by the first bytes (<see cref="Sniff"/>), and the
/// bytes win: a 7z renamed to .zip is still a 7z, and a download that arrived
/// as an error page is not an archive whatever it is called.
///
/// **No .docx, .jar, .epub, .apk.** They are zips, and extracting one is a
/// legitimate thing to want, but a menu row offering to take a Word document
/// apart is a row nobody expects; 7-Zip's own shell menu does not offer it for
/// them either unless asked.
/// </summary>
public static partial class ArchiveFormats
{
    /// <summary>
    /// Longest suffix first, so <c>.tar.gz</c> is found before <c>.gz</c>.
    /// </summary>
    private static readonly (string Suffix, ArchiveFormat Format)[] Suffixes =
    [
        (".tar.bz2", ArchiveFormat.TarBz2),
        (".tar.zst", ArchiveFormat.TarZst),
        (".tar.gz", ArchiveFormat.TarGz),
        (".tar.xz", ArchiveFormat.TarXz),
        (".tar.lz", ArchiveFormat.TarLz),
        (".tbz2", ArchiveFormat.TarBz2),
        (".tzst", ArchiveFormat.TarZst),
        (".tbz", ArchiveFormat.TarBz2),
        (".tgz", ArchiveFormat.TarGz),
        (".txz", ArchiveFormat.TarXz),
        (".tlz", ArchiveFormat.TarLz),
        (".zip", ArchiveFormat.Zip),
        (".tar", ArchiveFormat.Tar),
        (".bz2", ArchiveFormat.Bz2),
        (".zst", ArchiveFormat.Zst),
        (".rar", ArchiveFormat.Rar),
        (".7z", ArchiveFormat.SevenZip),
        (".gz", ArchiveFormat.Gz),
        (".xz", ArchiveFormat.Xz),
        (".lz", ArchiveFormat.Lz),
    ];

    /// <summary>
    /// The format a file's NAME claims, or null — the menu gate.
    /// Case-insensitive, and by the longest suffix that matches.
    /// </summary>
    public static ArchiveFormat? ByName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var leaf = Leaf(path);

        foreach (var (suffix, format) in Suffixes)
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return format;

        return null;
    }

    /// <summary>
    /// Whether the name is one part of a set split across several files.
    ///
    /// **Refused rather than half-read.** The first part of a split RAR opens
    /// and lists, and then fails on the first entry that crosses into the
    /// second part — measured with SharpCompress's own <c>Rar.multi.part01.rar</c>:
    /// <c>IncompleteArchiveException</c> on the first read. A folder holding
    /// the entries that happened to fit in part one is the half-written folder
    /// Extract all exists to never leave.
    /// </summary>
    public static bool IsSplitVolume(string path) => SplitName().IsMatch(Leaf(path));

    [GeneratedRegex(@"(\.part\d+\.rar|\.r\d\d|\.7z\.\d{3}|\.zip\.\d{3}|\.z\d\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitName();

    /// <summary>
    /// The format the first bytes say, or null when they say none of these.
    /// A compressed stream is answered as the bare compressor here; whether a
    /// tar is inside is <see cref="LooksLikeTar"/>'s question, asked of the
    /// decompressed bytes.
    /// </summary>
    internal static ArchiveFormat? Sniff(ReadOnlySpan<byte> head)
    {
        ReadOnlySpan<byte> sevenZip = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        ReadOnlySpan<byte> gzip = [0x1F, 0x8B];
        ReadOnlySpan<byte> xz = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
        ReadOnlySpan<byte> zstd = [0x28, 0xB5, 0x2F, 0xFD];

        if (head.StartsWith("PK\x03\x04"u8) || head.StartsWith("PK\x05\x06"u8)) return ArchiveFormat.Zip;
        if (head.StartsWith(sevenZip)) return ArchiveFormat.SevenZip;
        if (head.StartsWith("Rar!\x1A\x07\x00"u8) || head.StartsWith("Rar!\x1A\x07\x01\x00"u8)) return ArchiveFormat.Rar;
        if (head.StartsWith(gzip)) return ArchiveFormat.Gz;
        if (head.StartsWith("BZh"u8)) return ArchiveFormat.Bz2;
        if (head.StartsWith(xz)) return ArchiveFormat.Xz;
        if (head.StartsWith(zstd)) return ArchiveFormat.Zst;
        if (head.StartsWith("LZIP"u8)) return ArchiveFormat.Lz;
        if (LooksLikeTar(head)) return ArchiveFormat.Tar;

        return null;
    }

    /// <summary>
    /// A POSIX tar says <c>ustar</c> at offset 257. A v7 tar says nothing, so
    /// its header checksum is the only evidence: the sum of the 512 header
    /// bytes with the checksum field itself counted as spaces, written in
    /// octal at offset 148.
    /// </summary>
    internal static bool LooksLikeTar(ReadOnlySpan<byte> first512)
    {
        if (first512.Length < 512) return false;

        if (first512.Slice(257, 5).SequenceEqual("ustar"u8)) return true;

        // A v7 header begins with a name; an all-zero block is the end of an
        // archive, not the start of one.
        if (first512[0] == 0) return false;

        long sum = 0;

        for (var i = 0; i < 512; i++)
            sum += i is >= 148 and < 156 ? (byte)' ' : first512[i];

        long stored = 0;
        var digits = 0;

        foreach (var b in first512.Slice(148, 8))
        {
            if (b is (byte)' ' or 0)
            {
                if (digits > 0) break;
                continue;
            }

            if (b is < (byte)'0' or > (byte)'7') return false;

            stored = stored * 8 + (b - '0');
            digits++;
        }

        return digits > 0 && stored == sum;
    }

    /// <summary>
    /// What the thing inside is called: the name with the archive's own
    /// suffix taken off. <c>x.tar.gz</c> is <c>x</c>, <c>report.txt.gz</c> is
    /// <c>report.txt</c>, and a bare <c>.zip</c> is "Archive".
    ///
    /// **Not <see cref="Archives"/>' own stem**, which takes off only the last
    /// extension and makes <c>x.tar</c> of <c>x.tar.gz</c> — a folder called
    /// "x.tar" is a folder named after a file nobody has.
    /// </summary>
    public static string Stem(string path)
    {
        var leaf = Leaf(path);

        foreach (var (suffix, _) in Suffixes)
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                leaf = leaf[..^suffix.Length];
                break;
            }

        return leaf.Length == 0 ? "Archive" : leaf;
    }

    /// <summary>The word a sentence uses for the format.</summary>
    public static string Word(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "zip",
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Rar => "RAR",
        ArchiveFormat.Tar => "tar",
        ArchiveFormat.TarGz or ArchiveFormat.Gz => "gzip",
        ArchiveFormat.TarBz2 or ArchiveFormat.Bz2 => "bzip2",
        ArchiveFormat.TarXz or ArchiveFormat.Xz => "xz",
        ArchiveFormat.TarZst or ArchiveFormat.Zst => "zstd",
        _ => "lzip",
    };

    /// <summary>A single compressed stream with no archive of its own.</summary>
    internal static bool IsBare(ArchiveFormat format)
        => format is ArchiveFormat.Gz or ArchiveFormat.Bz2 or ArchiveFormat.Xz
                  or ArchiveFormat.Zst or ArchiveFormat.Lz;

    /// <summary>A tar, however it was compressed.</summary>
    internal static bool IsTar(ArchiveFormat format)
        => format is ArchiveFormat.Tar or ArchiveFormat.TarGz or ArchiveFormat.TarBz2
                  or ArchiveFormat.TarXz or ArchiveFormat.TarZst or ArchiveFormat.TarLz;

    /// <summary>The tar that a bare compressor turns out to be holding.</summary>
    internal static ArchiveFormat AsTar(ArchiveFormat bare) => bare switch
    {
        ArchiveFormat.Gz => ArchiveFormat.TarGz,
        ArchiveFormat.Bz2 => ArchiveFormat.TarBz2,
        ArchiveFormat.Xz => ArchiveFormat.TarXz,
        ArchiveFormat.Zst => ArchiveFormat.TarZst,
        ArchiveFormat.Lz => ArchiveFormat.TarLz,
        _ => bare,
    };

    private static string Leaf(string path)
        => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
