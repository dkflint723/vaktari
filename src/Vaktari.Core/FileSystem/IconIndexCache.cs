using System.Security.Cryptography;
using System.Text;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// Remembers what reading an icon theme cost, so it is paid once rather than
/// once per launch.
///
/// **The measurement that made this worth writing.** Reading Papirus-Dark means
/// enumerating every file in it and in Papirus behind it — a quarter of a
/// gigabyte across some fifty thousand entries — and then folding in forty
/// thousand recorded symlinks. Timed on the machine that reported a slow start:
/// 2.8–3.1 seconds to build, and 0 ms for a hundred lookups afterwards. All of
/// the cost is the index, none of it is the answers, and the index does not
/// change between launches.
///
/// **Freshness is a stamp, not a rescan.** Checking whether a theme changed by
/// walking it would cost exactly what this exists to avoid, so the stamp is the
/// theme folder's own timestamp together with the size and timestamp of the two
/// files that feed the index — <c>index.theme</c> and the recorded-links file.
/// Themes are installed and replaced wholesale, which is what that catches. A
/// file edited in place deep inside a theme is not caught, and the escape hatch
/// for that is the sample check below rather than a promise this cannot keep.
/// </summary>
internal static class IconIndexCache
{
    /// <summary>
    /// Where cache files live, or null to keep nothing. Null is the default so
    /// that Core has no opinion about where application state belongs — the UI
    /// sets this, and every test that does not set it runs uncached.
    /// </summary>
    public static string? Folder { get; set; }

    private const string Header = "vaktari-icon-index 1";

    /// <summary>
    /// The index for a theme directory, or null where there is nothing usable
    /// cached. Never throws: an unreadable or malformed cache is a miss, and a
    /// miss simply costs what it always cost.
    /// </summary>
    public static Dictionary<string, List<string>>? Load(string themeDir)
    {
        if (Folder is null) return null;

        try
        {
            var file = FileFor(themeDir);
            if (!File.Exists(file)) return null;

            using var reader = new StreamReader(file, Encoding.UTF8);

            if (reader.ReadLine() != Header) return null;

            // The directory is written down and checked rather than trusted to
            // the hash in the filename. A collision would otherwise hand one
            // theme's icons to another, which is the kind of wrong that looks
            // like a rendering bug.
            if (!PathsEqual(reader.ReadLine(), themeDir)) return null;

            if (reader.ReadLine() != Stamp(themeDir)) return null;

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;

                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                var paths = new List<string>(parts.Length - 1);

                for (var i = 1; i < parts.Length; i++)
                    if (parts[i].Length > 0)
                        paths.Add(parts[i]);

                if (paths.Count > 0) map[parts[0]] = paths;
            }

            // **One file, checked.** The stamp cannot see a theme whose folder
            // was replaced with an identical timestamp, nor one edited inside.
            // Probing a single path costs nothing and turns the worst outcome —
            // a listing of icons that are all missing — back into a rebuild.
            return map.Count > 0 && Sample(map) ? map : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the index for next time. Failure is silent by design: not being
    /// able to cache is slow, and slow is not worth an error in front of
    /// somebody who only opened a window.
    /// </summary>
    public static void Save(string themeDir, Dictionary<string, List<string>> map)
    {
        if (Folder is null || map.Count == 0) return;

        try
        {
            Directory.CreateDirectory(Folder);

            var file = FileFor(themeDir);

            // Written beside the real name and moved over it, so a launch that
            // dies mid-write leaves the previous cache rather than half of a
            // new one. A half-written cache would read as a valid map with
            // missing entries, which is worse than none.
            //
            // **Uniquely named, and cleaned up whatever happens.** A fixed name
            // was left behind whenever the rename failed — which it does, on a
            // machine where a virus scanner opens a newly written file before
            // anyone else can touch it. The cache then silently never appeared
            // and every launch paid the full rebuild, with nothing to say why.
            var temporary = $"{file}.{Guid.NewGuid():N}.writing";

            using (var writer = new StreamWriter(temporary, append: false, Encoding.UTF8))
            {
                writer.NewLine = "\n";
                writer.WriteLine(Header);
                writer.WriteLine(themeDir);
                writer.WriteLine(Stamp(themeDir));

                foreach (var (name, paths) in map)
                {
                    // A name or path holding a tab or a newline would be read
                    // back as something else. Neither occurs in any real theme;
                    // dropping the entry is still better than corrupting the
                    // file around it.
                    if (Unwritable(name)) continue;

                    writer.Write(name);

                    foreach (var path in paths)
                    {
                        if (Unwritable(path)) continue;

                        writer.Write('\t');
                        writer.Write(path);
                    }

                    writer.Write('\n');
                }
            }

            try
            {
                // Two tries. A scanner's hold on a file it has just seen written
                // is brief, and losing the cache to it means paying seconds on
                // every launch from here on.
                try
                {
                    File.Move(temporary, file, overwrite: true);
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                    File.Move(temporary, file, overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Said out loud rather than swallowed: silence here reads
                // exactly like a cache that works, and the only symptom is a
                // launch that stays slow forever.
                Console.Error.WriteLine(
                    $"[vaktari] icon index: could not be kept — {e.Message.Trim()}");

                throw;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No cache, and the next launch pays what this one did.
        }
        finally
        {
            // Whatever happened, nothing half-written is left lying about. This
            // is what a fixed temporary name got wrong: a failed rename left the
            // file behind, and the next attempt wrote over the same litter.
            Sweep(stagingFor: themeDir);
        }
    }

    /// <summary>
    /// Drops cache entries for themes that no longer exist.
    ///
    /// **The cache grows and nothing else shrinks it.** An index runs to about
    /// sixteen megabytes for a large theme, keyed by the theme folder's path —
    /// so trying a few themes and deleting them leaves their indexes behind
    /// forever, invisible, in a folder nobody browses. Each file records the
    /// directory it was built from on its second line; a recorded directory
    /// that is gone means the entry can never be loaded again and is purely
    /// dead weight.
    ///
    /// Also sweeps stale half-written litter, but only when it is old: a young
    /// .writing file may belong to a build happening right now in another
    /// window, and deleting it mid-write is how caches vanish mysteriously.
    /// </summary>
    public static void Prune()
    {
        if (Folder is null) return;

        try
        {
            if (!Directory.Exists(Folder)) return;

            foreach (var file in Directory.EnumerateFiles(Folder, "*.idx"))
            {
                try
                {
                    string? header, recorded;

                    using (var reader = new StreamReader(file, Encoding.UTF8))
                    {
                        header = reader.ReadLine();
                        recorded = reader.ReadLine();
                    }

                    // A file this reader cannot even begin to parse will never
                    // be loaded either; it is litter with the wrong name.
                    if (header != Header
                        || string.IsNullOrEmpty(recorded)
                        || !Directory.Exists(recorded))
                        File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Held open — likely being written or read this instant.
                    // It will still be here next launch.
                }
            }

            foreach (var litter in Directory.EnumerateFiles(Folder, "*.writing"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(litter) < DateTime.UtcNow.AddHours(-1))
                        File.Delete(litter);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be tidied is a cache that still works.
        }
    }

    /// <summary>
    /// Removes any half-written file for this theme. Cheap, and bounded to the
    /// one theme's own name so a cache being written by another window is left
    /// alone.
    /// </summary>
    private static void Sweep(string stagingFor)
    {
        if (Folder is null) return;

        try
        {
            var prefix = Path.GetFileName(FileFor(stagingFor));

            foreach (var litter in Directory.EnumerateFiles(Folder, prefix + ".*.writing"))
                try { File.Delete(litter); } catch { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Litter that cannot be swept is litter; it is not a reason to fail.
        }
    }

    private static bool Unwritable(string value)
        => value.Contains('\t', StringComparison.Ordinal)
           || value.Contains('\n', StringComparison.Ordinal)
           || value.Contains('\r', StringComparison.Ordinal);

    /// <summary>The first path of the first entry, still on disk.</summary>
    private static bool Sample(Dictionary<string, List<string>> map)
    {
        foreach (var paths in map.Values)
            foreach (var path in paths)
                return File.Exists(path);

        return false;
    }

    private static bool PathsEqual(string? left, string right)
        => left is not null
           && string.Equals(
               left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
               right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
               OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string Stamp(string themeDir)
    {
        var root = new DirectoryInfo(themeDir);
        var index = new FileInfo(Path.Combine(themeDir, "index.theme"));
        var aliases = new FileInfo(Path.Combine(themeDir, IconThemeArchive.AliasIndex));

        return string.Join(
            ':',
            root.LastWriteTimeUtc.Ticks,
            index.Exists ? index.LastWriteTimeUtc.Ticks : 0,
            index.Exists ? index.Length : 0,
            aliases.Exists ? aliases.LastWriteTimeUtc.Ticks : 0,
            aliases.Exists ? aliases.Length : 0);
    }

    /// <summary>
    /// A theme's cache file. Named from a hash because a theme's path is not a
    /// filename — it holds separators, and on Windows a colon.
    /// </summary>
    private static string FileFor(string themeDir)
    {
        var normalised = themeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!OperatingSystem.IsLinux()) normalised = normalised.ToLowerInvariant();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));

        return Path.Combine(Folder!, Convert.ToHexString(hash)[..16] + ".idx");
    }
}
