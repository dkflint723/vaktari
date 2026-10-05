using System.Text.RegularExpressions;

namespace Vaktari.Core.FileSystem;

/// <summary>Every kind of archive Extract all reads.</summary>
public enum ArchiveFormat { Zip, Tar, TarGz, Gz }

/// <summary>
/// What an archive is, by its name and by its bytes.
///
/// **Two answers, because two different questions are asked.** The menu row
/// is asked every time the selection changes and before anything is clicked,
/// so it goes by name (<see cref="ByName"/>) and never opens the file. The
/// extraction itself goes by the first bytes (<see cref="Sniff"/>), and the
/// bytes win: a download that arrived as an error page is not an archive
/// whatever it is called.
///
/// **Zip and tar, plain or gzip-compressed, and nothing else.** Extract all
/// used to read 7z, RAR and tar compressed with bzip2, xz, zstd or lzip as
/// well; those are left to the system's own archive tool, which a double-click
/// already opened them in. Their signatures are still KNOWN
/// (<see cref="Foreign"/>), so a 7z named .zip or an xz tar named .tar.gz is
/// refused for what it is — "Vaktari extracts zip and tar.gz archives only" —
/// rather than called a damaged zip, or read as one.
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
        (".tar.gz", ArchiveFormat.TarGz),
        (".tgz", ArchiveFormat.TarGz),
        (".zip", ArchiveFormat.Zip),
        (".tar", ArchiveFormat.Tar),
        (".gz", ArchiveFormat.Gz),
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
    /// **Not a menu guard any more** (review M7): none of these names is
    /// offered Extract all — <see cref="ByName"/> answers null for every one.
    /// It is kept for the callers that extract by path without the menu
    /// (<see cref="ArchiveSelfTest"/>, a direct <c>Archives.Extract</c>), so a
    /// part handed over by name is still refused in words rather than read as
    /// a damaged whole: a folder holding the entries that happened to fit in
    /// part one is the half-written folder Extract all exists to never leave.
    /// </summary>
    public static bool IsSplitVolume(string path) => SplitName().IsMatch(Leaf(path));

    [GeneratedRegex(@"(\.part\d+\.rar|\.r\d\d|\.7z\.\d{3}|\.zip\.\d{3}|\.z\d\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitName();

    /// <summary>
    /// The format the first bytes say, or null when they say none of these.
    /// A gzip stream is answered as the bare compressor here; whether a tar is
    /// inside is <see cref="LooksLikeTar"/>'s question, asked of the
    /// decompressed bytes.
    /// </summary>
    internal static ArchiveFormat? Sniff(ReadOnlySpan<byte> head)
    {
        ReadOnlySpan<byte> gzip = [0x1F, 0x8B];

        // PK00 is the marker a spanning writer leaves on an archive that
        // turned out to need one part; the runtime's own reader opened such
        // files (it reads from the end), so this does too.
        if (head.StartsWith("PK\x03\x04"u8) || head.StartsWith("PK\x05\x06"u8) || head.StartsWith("PK00PK\x03\x04"u8))
            return ArchiveFormat.Zip;
        if (head.StartsWith(gzip)) return ArchiveFormat.Gz;
        if (LooksLikeTar(head)) return ArchiveFormat.Tar;

        return null;
    }

    /// <summary>
    /// The word for an archive format Vaktari recognises and does not extract,
    /// by its first bytes, or null.
    ///
    /// **Asked only when <see cref="Sniff"/> found nothing**, and before a
    /// file NAMED .zip is tried as a zip anyway (review H2). Asked first, a
    /// tar whose first member is named "BZh…" would have been refused as
    /// bzip2; asked never, a 7z named .zip went to the zip reader to be
    /// searched for a directory it does not have, and an xz tar named .tar.gz
    /// was called "not a gzip file, or damaged" — an intact file, called
    /// broken.
    /// </summary>
    internal static string? Foreign(ReadOnlySpan<byte> head)
    {
        ReadOnlySpan<byte> sevenZip = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        ReadOnlySpan<byte> xz = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
        ReadOnlySpan<byte> zstd = [0x28, 0xB5, 0x2F, 0xFD];

        if (head.StartsWith(sevenZip)) return "7z";
        if (head.StartsWith("Rar!\x1A\x07\x00"u8) || head.StartsWith("Rar!\x1A\x07\x01\x00"u8)) return "RAR";
        if (head.StartsWith(xz)) return "xz";
        if (head.StartsWith("BZh"u8)) return "bzip2";
        if (head.StartsWith(zstd)) return "zstd";
        if (head.StartsWith("LZIP"u8)) return "lzip";

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
        ArchiveFormat.Tar => "tar",
        _ => "gzip",
    };

    /// <summary>A single compressed stream with no archive of its own.</summary>
    internal static bool IsBare(ArchiveFormat format) => format is ArchiveFormat.Gz;

    /// <summary>A tar, plain or compressed.</summary>
    internal static bool IsTar(ArchiveFormat format) => format is ArchiveFormat.Tar or ArchiveFormat.TarGz;

    /// <summary>The tar that a bare compressor turns out to be holding.</summary>
    internal static ArchiveFormat AsTar(ArchiveFormat bare) => bare == ArchiveFormat.Gz ? ArchiveFormat.TarGz : bare;

    private static string Leaf(string path)
        => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
