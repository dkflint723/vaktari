using System.IO.Compression;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// The two archive verbs a file manager is expected to have of its own: put a
/// selection into a zip, and take an archive apart.
///
/// **Compress writes zip and nothing else.** The runtime writes zip on both
/// platforms with no dependency, and a zip opens everywhere the person might
/// send it.
///
/// **Extract reads every format <see cref="ArchiveFormats"/> names** — zip,
/// 7z, RAR and tar however it is compressed, and a bare .gz, .bz2, .xz, .zst
/// or .lz. RAR is read under the unRAR licence's terms, which are reproduced
/// in THIRD-PARTY-NOTICES.txt.
///
/// **Containment is by construction.** Every segment of every landing path
/// has come through <see cref="ArchiveNames.Land"/>, which leaves no
/// separator, no <c>..</c> and — under Windows rules — no <c>:</c>, and every
/// folder written into is checked to be one the extraction made itself. The
/// whole of that is <see cref="ArchiveExtraction"/>, the only code that writes
/// bytes out of an archive. The resolve-and-compare check this class used to
/// borrow from <see cref="IconThemeArchive.Contained"/> is still that class's
/// own, and is left alone.
/// </summary>
public static class Archives
{
    public const string Extension = ".zip";

    /// <summary>
    /// What <see cref="Extract"/> will open.
    ///
    /// By name rather than by the file's first bytes: this answers a MENU
    /// ROW, so it is asked every time the selection changes and before
    /// anything has been clicked. Sniffing would open and read the file to
    /// decide whether to draw an entry. The extraction itself goes by the
    /// bytes.
    /// </summary>
    public static bool CanExtract(string? path) => ArchiveFormats.ByName(path) is not null;

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
    /// second. The first was gone, and the refused count — the
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

    /// <summary>
    /// The entries an extraction did not write, by why.
    ///
    /// **Counted rather than swallowed**, because an archive quietly losing
    /// entries is exactly what somebody needs to be told: the status line says
    /// "3 left out (2 links, 1 unsafe name)".
    /// </summary>
    /// <param name="Unsafe">A name that is not a name (<c>..</c>, empty) or
    /// that climbs out of the folder.</param>
    /// <param name="Links">Symbolic links, which are never created, and hard
    /// links to a file that did not land.</param>
    /// <param name="Special">Devices, pipes, and sparse tar members.</param>
    /// <param name="MacMetadata"><c>__MACOSX/</c> and <c>._*</c>.</param>
    /// <param name="Unwritable">Entries the disk refused one at a time — a
    /// name a FAT stick will not take, a file too large for FAT32, a folder on
    /// the way that turned into a link.</param>
    public readonly record struct LeftOut(int Unsafe, int Links, int Special, int MacMetadata, int Unwritable)
    {
        public int Total => Unsafe + Links + Special + MacMetadata + Unwritable;

        /// <summary>"3 left out (2 links, 1 unsafe name)", or null when
        /// nothing was.</summary>
        public string? Describe()
        {
            if (Total == 0) return null;

            var parts = new List<string>(5);

            if (Links > 0) parts.Add(Links == 1 ? "1 link" : $"{Links} links");
            if (Unsafe > 0) parts.Add(Unsafe == 1 ? "1 unsafe name" : $"{Unsafe} unsafe names");
            if (Special > 0) parts.Add(Special == 1 ? "1 device or pipe" : $"{Special} devices or pipes");
            if (MacMetadata > 0) parts.Add($"{MacMetadata} Mac metadata");
            if (Unwritable > 0) parts.Add(Unwritable == 1 ? "1 that could not be written" : $"{Unwritable} that could not be written");

            return $"{Total} left out ({string.Join(", ", parts)})";
        }
    }

    /// <summary>What one extraction did.</summary>
    /// <param name="Landed">The one new thing in the destination: a folder,
    /// or — for a bare compressed file — the file itself.</param>
    /// <param name="IsFile">Whether <paramref name="Landed"/> is a file.</param>
    /// <param name="Files">How many files came out.</param>
    /// <param name="Folders">How many folders were made for them.</param>
    /// <param name="Renamed">How many names were changed to be written —
    /// unwritable characters replaced, or a second copy numbered.</param>
    public readonly record struct Extraction(
        string Landed, bool IsFile, int Files, int Folders, int Renamed, LeftOut LeftOut);

    /// <summary>
    /// Into exactly one new folder — the archive's own single top-level folder
    /// when it has exactly one, otherwise a folder named after the archive; a
    /// compressed single file lands beside it; never loose into the
    /// destination, never over anything already there.
    ///
    /// **No double wrap.** An archive holding <c>trip/</c> and nothing else
    /// extracts as <c>trip</c>, not <c>trip\trip</c> — the maintainer's
    /// decision, and what every archiver's "extract here, smart" does.
    ///
    /// **All or nothing.** The run writes into a working folder
    /// (<c>.vaktari-extracting-…</c>) in the destination and renames it into
    /// place at the end, so a folder holding half an archive never sits at a
    /// name that looks finished. Cancelling, a damaged archive, a CRC that
    /// does not match, a full disk: the working folder is discarded.
    /// </summary>
    public static Extraction Extract(
        string archive, string destination, OperationHandle? handle = null, CancellationToken token = default)
        => Extract(archive, destination, handle, token, ArchiveRoom.Real, observer: null);

    internal static Extraction Extract(
        string archive, string destination, OperationHandle? handle, CancellationToken token,
        ArchiveRoom room, IExtractionObserver? observer, int maxEntries = ArchiveLimits.MaxEntries)
    {
        destination = Path.GetFullPath(destination);

        var leaf = Leaf(archive);

        if (ArchiveFormats.IsSplitVolume(archive))
            throw new ArchiveRefusedException(ArchiveSentences.Split(leaf));

        using var linked = handle is null
            ? CancellationTokenSource.CreateLinkedTokenSource(token)
            : CancellationTokenSource.CreateLinkedTokenSource(token, handle.Token);

        var cancel = linked.Token;

        using var pass = ArchiveReader.Open(archive, cancel);

        // Everything that can be decided before a byte is written is decided
        // here, in this order, so nothing is created for an archive that was
        // never going to extract.
        if (pass.AnyEncrypted) throw new ArchivePasswordRequiredException(ArchiveSentences.Password(leaf));

        if (pass.Directory is { Overlapping: true }) throw new ArchiveRefusedException(ArchiveSentences.Overlap(leaf));

        if (pass.DeclaredCount > maxEntries) throw new ArchiveRefusedException(ArchiveSentences.TooMany(leaf, maxEntries));

        if (room.RefuseUpFront(destination, pass.DeclaredTotal ?? 0, pass.DeclaredCount ?? 0, leaf) is { } noRoom)
            throw new ArchiveRefusedException(noRoom);

        handle?.Begin(pass.DeclaredItems ?? 0, pass.DeclaredTotal ?? pass.ArchiveLength);

        Sweep(destination);

        var (working, held) = Working(destination);

        try
        {
            var options = new ExtractionOptions(
                ArchiveNames.WindowsRulesFor(room.DriveFormat(destination)),
                SkipMacMetadata: true,
                ZoneMarks.Read(archive),
                room,
                observer,
                maxEntries);

            var done = ArchiveExtraction.Run(pass, working, options, handle, cancel);

            var (landed, isFile) = Publish(working, destination, archive, pass.Format, observer);

            return new Extraction(landed, isFile, done.Files, done.Folders, done.Renamed, done.LeftOut);
        }
        catch
        {
            Discard(working);
            throw;
        }
        finally
        {
            Release(working, held);
        }
    }

    private const string WorkingPrefix = ".vaktari-extracting-";

    /// <summary>Working folders a run in this process is writing.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Active =
        new(PathRules.Comparer);

    /// <summary>
    /// A fresh working folder in the destination: a sibling of where the
    /// result lands, so landing it is a rename and not a second copy.
    ///
    /// **Held by a lock file beside it** for as long as the run lasts —
    /// opened with no sharing, which Windows enforces and .NET turns into an
    /// advisory lock on Linux, and released by the system when a process
    /// dies — so <see cref="Sweep"/> can tell a live run from a dead one
    /// without trusting a clock. Hidden on Windows while it is being written,
    /// where the leading dot does not hide it.
    /// </summary>
    private static (string Working, FileStream Held) Working(string destination)
    {
        while (true)
        {
            var working = Path.Combine(destination, WorkingPrefix + Guid.NewGuid().ToString("N")[..12]);

            if (Directory.Exists(working) || File.Exists(working) || File.Exists(working + ".lock")) continue;

            var held = new FileStream(working + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose);

            Directory.CreateDirectory(working);
            Active[working] = 0;

            if (OperatingSystem.IsWindows())
            {
                Hide(held.Name, true);
                Hide(working, true);
            }

            return (working, held);
        }
    }

    private static void Release(string working, FileStream held)
    {
        Active.TryRemove(working, out _);

        try { held.Dispose(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Quiet.Swallowed("extract", e); }

        try { if (File.Exists(working + ".lock")) File.Delete(working + ".lock"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Quiet.Swallowed("extract", e); }
    }

    /// <summary>A working folder this long untouched, and unheld, is
    /// abandoned.</summary>
    internal static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Clears away what an earlier run left in <paramref name="destination"/>.
    ///
    /// **A crash, or a discard that failed four times, left a
    /// <c>.vaktari-extracting-…</c> folder behind for good** (review of
    /// Stage A); nothing ever looked for one. The next extraction into the
    /// same folder does: one whose lock nobody holds, that no run in this
    /// process is writing, and that has not changed for
    /// <see cref="Abandoned"/>, is removed. The age is belt and braces for a
    /// lock the platform could not enforce.
    /// </summary>
    internal static int Sweep(string destination)
    {
        var swept = 0;
        IEnumerable<string> found;

        try
        {
            found = Directory.EnumerateDirectories(destination, WorkingPrefix + "*").ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Quiet.Swallowed("extract", e);
            return 0;
        }

        foreach (var folder in found)
        {
            try
            {
                if (Active.ContainsKey(folder)) continue;
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < Abandoned) continue;

                var lockFile = folder + ".lock";

                if (File.Exists(lockFile))
                {
                    // Held means live: another Vaktari is writing it.
                    using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

                    File.Delete(lockFile);
                }

                Hide(folder, false);
                Discard(folder);

                if (!Directory.Exists(folder)) swept++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Quiet.Swallowed("extract", e);
            }
        }

        return swept;
    }

    private static void Hide(string path, bool hidden)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var attributes = File.GetAttributes(path);

            File.SetAttributes(path, hidden ? attributes | FileAttributes.Hidden : attributes & ~FileAttributes.Hidden);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Quiet.Swallowed("extract", e);
        }
    }

    /// <summary>
    /// Moves what the run wrote to its final name, and says where that is.
    /// </summary>
    private static (string Landed, bool IsFile) Publish(
        string working, string destination, string archive, ArchiveFormat format, IExtractionObserver? observer)
    {
        var top = Directory.GetFileSystemEntries(working);

        // A bare compressed file is the file: report.txt.gz lands as
        // report.txt, numbered if that is taken.
        if (ArchiveFormats.IsBare(format) && top is [var only] && File.Exists(only))
        {
            var target = Land(destination, Path.GetFileName(only), isFolder: false,
                to => File.Move(only, to, overwrite: false), observer);

            Discard(working);

            return (target, true);
        }

        // One folder and nothing beside it: that folder is the result.
        if (top is [var folder] && Directory.Exists(folder))
        {
            var target = Land(destination, Path.GetFileName(folder), isFolder: true,
                to => Directory.Move(folder, to), observer);

            Discard(working);

            return (target, false);
        }

        // The working folder itself becomes the result, so it stops hiding.
        Hide(working, false);

        return (Land(destination, ArchiveFormats.Stem(archive), isFolder: true,
            to => Directory.Move(working, to), observer), false);
    }

    /// <summary>
    /// The result, moved to the first free name — and to the next one when
    /// the free name is taken between looking and moving.
    ///
    /// **Two things were wrong with borrowing <see cref="NewItemName.Free"/>
    /// here** (review of Stage A). It numbers by appending, so a 255-unit
    /// name numbered past the limit and the move threw; and it answers once,
    /// so a second Extract all of the same archive finishing a moment
    /// earlier — or anything else appearing at the name — failed the move
    /// and discarded the whole extraction. Numbering is
    /// <see cref="ArchiveNames.Numbered"/>, which shortens the stem to fit,
    /// and a move refused because the name is now taken tries the next.
    /// </summary>
    private static string Land(
        string destination, string name, bool isFolder, Action<string> move, IExtractionObserver? observer)
    {
        for (var n = 1; ; n++)
        {
            var target = Path.Combine(destination, n == 1 ? name : ArchiveNames.Numbered(name, n, isFolder));

            if (File.Exists(target) || Directory.Exists(target)) continue;

            observer?.BeforeLanding(target);

            try
            {
                Retrying(() => move(target));
                return target;
            }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target))
            {
                // Taken since it was looked at: the next number.
            }
        }
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
    /// anything that was already there: every caller passes a path that was
    /// free a moment ago.
    ///
    /// **Tried three more times, after 100, 300 and 900 ms** (Core-13): a
    /// virus scanner opens each new file as it appears and holds it for a
    /// moment, and a delete in that moment fails with a sharing or access
    /// error that is gone a second later.
    /// </summary>
    private static void Discard(string path)
    {
        Retrying(() =>
        {
            if (Directory.Exists(path)) DeleteTree(path);
            else if (File.Exists(path)) File.Delete(path);
        }, swallow: true);
    }

    /// <summary>
    /// A folder and everything under it, without recursion.
    ///
    /// **The runtime's recursive delete recurses once per level**, and a
    /// folder thousands of levels deep overflowed the stack inside it —
    /// measured by revert-check on the depth cap, whose absence let such a
    /// tree be created, and whose discard then killed the process. Nothing
    /// this run makes is deeper than <see cref="ArchiveLimits.MaxDepth"/>,
    /// but <see cref="Sweep"/> deletes what it finds, and what it finds was
    /// not necessarily made by this build. A link inside is removed as a link
    /// and never entered.
    /// </summary>
    internal static void DeleteTree(string root)
    {
        var folders = new List<string>();
        var pending = new Stack<string>();

        pending.Push(root);

        while (pending.TryPop(out var folder))
        {
            folders.Add(folder);

            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos().ToList())
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;

                if (entry is DirectoryInfo sub && (entry.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub.FullName);
                else if (entry is DirectoryInfo link) link.Delete();
                else entry.Delete();
            }
        }

        for (var i = folders.Count - 1; i >= 0; i--) Directory.Delete(folders[i]);
    }

    private static readonly int[] Backoff = [100, 300, 900];

    private static void Retrying(Action act, bool swallow = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                act();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt < Backoff.Length && Transient(e))
                {
                    Thread.Sleep(Backoff[attempt]);
                    continue;
                }

                // The original failure is the one worth reporting.
                if (swallow) return;

                throw;
            }
        }
    }

    /// <summary>A sharing, lock or access-denied error: the shape a scanner
    /// holding a file for a moment takes.</summary>
    private static bool Transient(Exception e)
        => e is UnauthorizedAccessException
           || e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) or unchecked((int)0x80070005);
}
