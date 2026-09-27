namespace Vaktari.Core.FileSystem;

/// <summary>
/// The mark of the web: the <c>Zone.Identifier</c> stream Windows puts on a
/// download, which is what makes Office open it in Protected View and
/// SmartScreen ask before running it.
///
/// **What comes out of a marked archive is marked with the archive's own
/// mark** — the maintainer's decision, and what Explorer and 7-Zip do. An
/// archive downloaded from the web must not become a way of removing the mark
/// from everything inside it.
///
/// **The order it is written in is load-bearing** (refutations 2, the
/// blocker): writing the stream RESETS the file's modified time to now, and
/// throws UnauthorizedAccessException on a read-only file. So everywhere it is
/// written: the data, then the mark, then the times, then the attributes with
/// ReadOnly last. See <see cref="ArchiveExtraction"/>.
///
/// **Failures are swallowed**, because a mark that cannot be written is a
/// filesystem that cannot hold one: FAT and exFAT have no alternate streams,
/// and a share may refuse. Measured (E-6) over SMB to an NTFS share, the write
/// and the read-back both work; FAT32 and exFAT were not available to test.
/// On Linux nothing is marked; there is no such stream to carry.
/// </summary>
public static class ZoneMarks
{
    private const string Stream = ":Zone.Identifier";

    /// <summary>More than any real mark, which is a few lines; a stream
    /// larger than this is not one worth copying onto every file.</summary>
    private const int Largest = 64 * 1024;

    /// <summary>Zone 3, the internet, with nothing else said.</summary>
    internal const string Internet = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>The mark on <paramref name="file"/>, or null for none.</summary>
    public static string? Read(string file)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var info = new FileInfo(file + Stream);

            if (!info.Exists) return null;

            // **Too large to copy is not the same as not marked.** Returning
            // nothing here extracted everything unmarked from an archive
            // whose mark had merely grown (review of Stage A) — failing open.
            // The smallest mark that still says "from the internet" instead.
            if (info.Length > Largest) return Internet;

            return File.ReadAllText(file + Stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Quiet.Swallowed("zone", e);
            return null;
        }
    }

    /// <summary>Marks <paramref name="file"/>, before its times are set.</summary>
    public static void Apply(string file, string content)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            File.WriteAllText(file + Stream, content);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Quiet.Swallowed("zone", e);
        }
    }
}
