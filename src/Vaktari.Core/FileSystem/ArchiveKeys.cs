namespace Vaktari.Core.FileSystem;

/// <summary>
/// An entry's raw name, cut into the segments it lands as.
///
/// **Which separator an archive uses depends on the format, and only two of
/// them may use a backslash.** A zip written on Windows by an old tool says
/// <c>docs\a.txt</c>, and SharpCompress hands RAR keys over with backslashes
/// whatever the archive stored — measured on both RAR4 and RAR5 fixtures:
/// <c>exe\test.exe</c>. In tar and 7z a backslash is an ordinary character of
/// a name; on Windows it stays inside its segment and
/// <see cref="ArchiveNames.Land"/> turns it into <c>_</c>, so <c>a\..\b</c>
/// from a tar becomes one file called <c>a_.._b</c> rather than a walk up a
/// folder that was never there.
///
/// **A <c>..</c> anywhere refuses the whole entry.** Dropping just that
/// segment would put the file somewhere its archive never said, and the point
/// is that nothing lands outside the folder being extracted into — see
/// <see cref="ArchiveExtraction"/> for the rest of that guarantee.
/// </summary>
internal static class ArchiveKeys
{
    internal static string[]? Split(string raw, ArchiveFormat format, out bool isFolder)
    {
        if (format is ArchiveFormat.Zip or ArchiveFormat.Rar) raw = raw.Replace('\\', '/');

        isFolder = raw.EndsWith('/');

        var segments = new List<string>();

        foreach (var segment in raw.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..") return null;

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : [.. segments];
    }
}
