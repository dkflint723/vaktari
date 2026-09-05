using System.IO.Compression;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// The two archive verbs a file manager is expected to have of its own: put a
/// selection into a zip, and take a zip apart.
///
/// **Zip and nothing else.** <see cref="IconThemeArchive"/> next door reads tar,
/// gzip and xz as well, because it is fed whatever a theme's publisher chose;
/// these are driven by a menu row that has to say up front what it will do, and
/// "Extract all" on a .rar that then fails is worse than no row. The runtime
/// writes and reads zip on both platforms with no dependency, which is the
/// other half of the reason.
///
/// **The containment check is <see cref="IconThemeArchive.Contained"/>, called
/// rather than copied.** An entry is free to call itself
/// <c>..\..\Windows\System32\something</c>, and the rule that stops it — resolve
/// the path and require it to be genuinely underneath the destination — is
/// already written and already exercised by the theme installer's own escape
/// test, which unpacks an archive holding <c>theme/../../near.svg</c>. It is
/// not a rule to have two of.
/// </summary>
public static class Archives
{
    public const string Extension = ".zip";

    /// <summary>
    /// What <see cref="Extract"/> will open.
    ///
    /// By extension rather than by the file's first bytes, unlike the theme
    /// reader: this answers a MENU ROW, so it is asked every time the selection
    /// changes and before anything has been clicked. Sniffing would open and
    /// read the file to decide whether to draw an entry.
    /// </summary>
    public static bool CanExtract(string? path)
        => path is not null
           && Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether these can go into one archive together, which they can when
    /// they all come out of one folder.
    ///
    /// **Two sources sharing a leaf name land on one entry.** <see cref="Add"/>
    /// stores a top-level source under <c>Path.GetFileName</c> alone, and a
    /// details listing can hold rows from several folders at once — the pane's
    /// expansion splices an opened folder's contents in underneath it. Measured
    /// before this rule existed: compressing <c>2023\notes.txt</c> and
    /// <c>2024\notes.txt</c> wrote an archive holding two entries called
    /// notes.txt, and extracting that archive produced ONE file, holding the
    /// second. The first was gone, and <see cref="Extraction.Refused"/> — the
    /// counter that exists to notice an archive losing entries — did not count
    /// it, because nothing was refused.
    ///
    /// Refused rather than numbered because the archive's own name is the
    /// folder it lands in: a zip called 2023 holding a file that came out of
    /// 2024 is a second way of losing track of what went into it.
    /// </summary>
    public static bool CanCompress(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0) return false;

        var parent = Path.GetDirectoryName(sources[0]);

        if (string.IsNullOrEmpty(parent)) return false;

        for (var i = 1; i < sources.Count; i++)
            if (!PathRules.Same(Path.GetDirectoryName(sources[i]), parent)) return false;

        return true;
    }

    /// <summary>
    /// Writes <paramref name="sources"/> into a new zip in
    /// <paramref name="destination"/> and hands back where it landed.
    ///
    /// **Written under a working name and moved onto the real one at the end.**
    /// The catch below removes a failed archive, and that half is pinned — but
    /// it only runs while the process is still there to run it, and a truncated
    /// zip looks exactly like a finished one in a listing. This is the half
    /// that covers a stop the catch never sees, and it is pinned from inside
    /// the write — see
    /// <c>The_landing_name_is_never_occupied_while_the_archive_is_being_written</c>.
    ///
    /// The working file is a sibling rather than a temp-folder file because the
    /// last step is <see cref="File.Move(string, string)"/>, and a move across
    /// volumes is a second copy of everything.
    /// </summary>
    public static string Compress(
        IReadOnlyList<string> sources, string destination, CancellationToken token = default)
    {
        if (sources.Count == 0) throw new ArgumentException("nothing to compress", nameof(sources));

        // No paramName, unlike the line above. Failures.Describe's
        // ArgumentException arm is the exception's Message as it stands, and
        // measured here, a paramName is printed into that Message: "a plain
        // sentence" becomes "a plain sentence (Parameter 'sources')". The line
        // above is a caller's mistake and never reaches a person; this one is a
        // sentence for one.
        if (!CanCompress(sources))
            throw new ArgumentException("everything in one archive has to come from one folder");

        destination = Path.GetFullPath(destination);

        var landing = NewItemName.Free(destination, StemFor(sources, destination), Extension);
        var working = Path.Combine(destination, ".vaktari-zipping-" + Guid.NewGuid().ToString("N")[..12]);

        try
        {
            using (var file = File.Create(working))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var source in sources)
                {
                    token.ThrowIfCancellationRequested();
                    Add(zip, source, token);
                }
            }

            File.Move(working, landing);

            return landing;
        }
        catch
        {
            Discard(working);
            throw;
        }
    }

    /// <summary>What one extraction did.</summary>
    /// <param name="Folder">The folder that was made for it.</param>
    /// <param name="Files">How many files came out.</param>
    /// <param name="Refused">Entries whose resolved path was not underneath
    /// <paramref name="Folder"/>. Counted rather than swallowed, because an
    /// archive quietly losing entries is the failure this whole check exists to
    /// notice.</param>
    public readonly record struct Extraction(string Folder, int Files, int Refused);

    /// <summary>
    /// Unpacks <paramref name="archive"/> into a new folder in
    /// <paramref name="destination"/>, named after the archive.
    ///
    /// **Into a folder of its own, always.** A zip is free to hold fifty loose
    /// files at its top level, and unpacking those straight into the folder the
    /// archive sits in scatters them among what was already there with no way
    /// to tell which arrived.
    ///
    /// The folder is created at a free name, so nothing that was already on
    /// disk is written over; it is removed again if the unpacking throws, for
    /// the reason the working file above exists — a folder holding half an
    /// archive looks like one holding all of it.
    /// </summary>
    public static Extraction Extract(
        string archive, string destination, CancellationToken token = default)
    {
        destination = Path.GetFullPath(destination);

        var folder = NewItemName.Free(destination, Stem(archive), "");

        Directory.CreateDirectory(folder);

        var files = 0;
        var refused = 0;

        try
        {
            using var zip = ZipFile.OpenRead(archive);

            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();

                if (IconThemeArchive.Contained(folder, entry.FullName) is not { } path)
                {
                    refused++;
                    continue;
                }

                // A directory is an entry whose name ends in a separator and so
                // has nothing after it; every other directory is implied by the
                // files inside it.
                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // **Counted before it is written, and only when the name is
                // still free.** An archive is free to hold two entries under
                // one name — measured here: one written with two entries called
                // notes.txt unpacked to a single file holding the second, while
                // the count said two, so the status line promised two items in
                // a folder holding one. The folder was made fresh a few lines
                // above, so a name that is taken was taken by this same loop.
                if (!File.Exists(path)) files++;

                entry.ExtractToFile(path, overwrite: true);
            }
        }
        catch (InvalidDataException e)
        {
            Discard(folder);

            // **The runtime's own words for this are unusable, and this is the
            // failure the verb will meet most.** The menu row decides by
            // extension, so a download that arrived as an error page, a renamed
            // .rar and a file that stopped halfway all reach here. Measured
            // before this sentence existed: opening a text file named
            // download.zip raised "End of Central Directory record could not be
            // found.", and Failures.Describe handed that back unchanged — it is
            // neither an IOException nor an ArgumentException, so it fell to
            // the arm that shows the exception's own message.
            //
            // Said here rather than added to Failures, which keys on the
            // exception's TYPE: InvalidDataException is raised at five other
            // places in this project, all in IconThemeArchive, and each already
            // carries a sentence written for a person. Only the code that
            // opened the file knows it was opening a zip.
            throw new InvalidDataException($"{Leaf(archive)} is not a zip file, or is damaged", e);
        }
        catch
        {
            Discard(folder);
            throw;
        }

        return new Extraction(folder, files, refused);
    }

    /// <summary>
    /// One source, at the top level of the archive.
    ///
    /// **The walk is <see cref="SafeWalk"/>, which reports links and never
    /// enters them.** Measured here with a junction in a temp tree:
    /// <c>Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)</c>
    /// walks straight through one, so compressing a folder that happens to hold
    /// a junction to a photo library puts the photo library in the zip.
    ///
    /// **The row that was PICKED is followed even when it is a link**, and that
    /// is the decision rather than an oversight. <see cref="SafeWalk.Descend"/>
    /// tests each CHILD for a reparse point and pushes the root it was handed
    /// without testing it, so <c>Directory.Exists</c> below is true for a
    /// junction and the walk begins inside it. Measured here: compressing a
    /// junction row pointing at a sibling tree wrote <c>shortcut/</c> and
    /// <c>shortcut/report.txt</c> — the target's contents under the link's
    /// name, which is what asking to zip a shortcut means. What the rule above
    /// guards is a link nobody chose, found on the way down.
    /// </summary>
    private static void Add(ZipArchive zip, string source, CancellationToken token)
    {
        if (!Directory.Exists(source))
        {
            Store(zip, source, Path.GetFileName(source));
            return;
        }

        var top = Leaf(source);

        // Named even when it turns out to be empty: a folder that was selected
        // and does not appear in the archive at all reads as a failed compress.
        zip.CreateEntry(top + "/");

        foreach (var found in SafeWalk.Descend(source, token))
        {
            // ZipArchive can write a file or a folder and has no member for a
            // link, so following one would silently put a copy of somebody
            // else's tree in the archive instead of recording the link.
            if (found.IsLink) continue;

            var name = top + "/" + Path.GetRelativePath(source, found.Path).Replace('\\', '/');

            if (found.IsDirectory) zip.CreateEntry(name + "/");
            else Store(zip, found.Path, name);
        }
    }

    private static void Store(ZipArchive zip, string path, string name)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);

        // **Kept, because a fresh entry does not keep it.** Measured here: with
        // this line removed, a file written with a 2001 timestamp comes out of
        // the archive carrying a date of the entry's own rather than the
        // file's, so the dates in a zip of an old folder are not its dates.
        //
        // Guarded on 1980, which is where the DOS timestamp a zip stores
        // begins: assigning anything earlier raises ArgumentOutOfRangeException
        // and would lose the whole archive over one odd file.
        var when = File.GetLastWriteTime(path);

        if (when.Year >= 1980) entry.LastWriteTime = when;

        using var from = File.OpenRead(path);
        using var to = entry.Open();

        from.CopyTo(to);
    }

    /// <summary>
    /// What to call the archive: the one thing selected, or the folder holding
    /// several.
    /// </summary>
    private static string StemFor(IReadOnlyList<string> sources, string destination)
        => Stem(sources.Count == 1 ? sources[0] : destination);

    /// <summary>
    /// A path's name with any extension taken off, and "Archive" where that
    /// leaves nothing at all — a drive root has no name.
    ///
    /// **That last is a guard, and no test here reddens without it.** Both
    /// verbs are driven from a listing row, and every row in a listing has a
    /// name; reaching it means calling this class directly with a root, which
    /// no test can then compress or extract without writing to one.
    ///
    /// **<see cref="PathRules.SplitLeaf"/> is the rule, called rather than
    /// written out again.** It already says that a leading dot begins a name
    /// rather than an extension — <c>.gitignore</c> compresses to
    /// <c>.gitignore.zip</c>, not to <c>.zip</c> — and that a FOLDER keeps
    /// everything, because a folder called <c>v1.2</c> has no extension to
    /// drop. Its own comment records that the copy path and the restore path
    /// had each written this out and drifted apart; a third copy here would be
    /// the same mistake again.
    /// </summary>
    private static string Stem(string path)
    {
        var leaf = Leaf(path);

        return leaf.Length == 0
            ? "Archive"
            : PathRules.SplitLeaf(leaf, Directory.Exists(path)).Stem;
    }

    private static string Leaf(string path)
        => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>
    /// Removes something this class made and then could not finish. Never
    /// anything that was already there: both callers pass a path that was free
    /// a moment ago.
    /// </summary>
    private static void Discard(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The original failure is the one worth reporting.
        }
    }
}
