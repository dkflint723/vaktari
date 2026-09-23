using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;

namespace Vaktari.Windows;

/// <summary>
/// Name search by walking the tree, and content search by reading the files
/// the names do not answer.
///
/// **No index behind it, and it says so.** This is a managed walk of the
/// drives. Two indexes were considered and set aside: Everything is
/// third-party, may not be installed, and talks over an IPC protocol worth its
/// own decision; Windows Search is COM. A managed walk is honest and has no
/// dependency.
///
/// The interface used to carry an <c>IsAvailable</c> flag, which this answered
/// true. It meant "will this return results", not "is it fast", and this
/// comment used to say that false would send the UI to a fallback walk of its
/// own. The UI never had one; the flag is gone, and what the band says now
/// comes from <see cref="AnswersFromIndex"/>.
/// </summary>
public sealed class WindowsSearchProvider : ISearchProvider
{
    /// <summary>
    /// False for every question, and the class comment above is the whole
    /// argument: there is no index behind this on Windows, only a managed walk
    /// of the drives.
    ///
    /// **Nothing on screen ever said so.** The band's warning was hung on
    /// IsAvailable, which this answered true and had to — so the one platform
    /// where "there is no index on this machine" is unconditionally true was
    /// the platform that could never show it.
    ///
    /// The query is ignored because the answer does not depend on it: unlike
    /// Baloo there is no fast path here for any question to take.
    /// </summary>
    public bool AnswersFromIndex(SearchQuery query) => false;

    /// <summary>
    /// The walk below skips anything carrying the System attribute — see
    /// <c>AttributesToSkip</c> in <see cref="Walk"/> — and until this line the
    /// band never said so. Kept rather than dropped for now: without the skip
    /// the walk descends <c>$Recycle.Bin</c> and turns up <c>pagefile.sys</c>
    /// for "sys", and neither is an answer anyone asked for. Said, so that a
    /// folder a sync client marked System is not searched past in silence.
    /// </summary>
    public string? Caveat => "files Windows marks as system are not searched";

    public string BackendName => "directory walk";

    /// <summary>
    /// True: the walk reads a file whose name does not match when it is asked
    /// to, through ContentMatcher.
    ///
    /// **This used to be false, on the grounds that reading every file without
    /// an index "would be indistinguishable from a hang on any real folder".**
    /// The cost is real and nothing here pretends otherwise — the band says
    /// every text file is being read — but a hang is a wait that cannot be
    /// ended, and this can: Stop is honoured before every open and every
    /// 64 KiB read, and a file over ContentMatcher.MaxBytes is never opened at
    /// all.
    /// </summary>
    public bool SupportsContentSearch => true;

    /// <summary>
    /// True, and it always was — <see cref="Walk"/> has read
    /// <c>query.CaseSensitive</c> for both the substring comparison and the
    /// glob's ignoreCase since it was written. Saying so is what puts the box
    /// in the search band; the walk needs no change to honour it.
    /// </summary>
    public bool SupportsCaseSensitivity => true;

    /// <summary>
    /// **The walk runs on the thread pool, not on the caller's thread.**
    ///
    /// This is the whole reason for the channel. An async iterator runs
    /// synchronously on whoever starts enumerating it until it hits a real
    /// await — and the caller starts enumerating from the UI thread. The first
    /// version's only await was a Task.Yield() after each match, which both ran
    /// every directory read between matches on the dispatcher and captured the
    /// dispatcher as its continuation context, so it kept coming back. A search
    /// over a home directory made the window stop redrawing and drop keystrokes
    /// while it ran.
    ///
    /// Same shape as <see cref="WindowsFileSystemProvider.EnumerateAsync"/>,
    /// for the same reason: a directory read is a blocking syscall and must not
    /// be on the dispatcher.
    /// </summary>
    public async IAsyncEnumerable<FileEntry> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<FileEntry>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var entry in Walk(query, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    await channel.Writer.WriteAsync(entry, ct).ConfigureAwait(false);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, ct);

        await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return entry;

        await producer.ConfigureAwait(false);
    }

    /// <summary>
    /// The drives an unscoped search covers.
    ///
    /// **Removable ones were left out, and that is the case the box exists
    /// for.** Searching without a scope skipped the stick, the SD card and the
    /// external disk — the drives whose contents somebody is least likely to
    /// remember the layout of, and so most likely to be searching.
    ///
    /// Network drives stay out, deliberately and not by omission. A mapped
    /// drive whose server has gone away blocks for the whole SMB timeout, and
    /// this walk would pay that once per unscoped search with nothing on screen
    /// to say why — the same hazard the places provider names for reading a
    /// volume label. It is also why <see cref="Everywhere"/> says "on this
    /// machine": a mapped drive is on a server, and the phrase is true.
    /// </summary>
    private static List<string> SearchableDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => Searchable(d.DriveType, d.IsReady))
                .Select(d => d.Name)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// The rule itself, apart from DriveInfo so it can be asked about a drive
    /// type this machine does not have. Nobody can plug a CD-ROM into a test.
    /// </summary>
    internal static bool Searchable(DriveType type, bool ready)
        => ready && type is DriveType.Fixed or DriveType.Removable;

    public string Everywhere => "every drive on this machine";

    /// <summary>
    /// One directory read per directory, and nothing else — unless the question
    /// asks for contents, when a folder holding a file whose name did not
    /// answer is read a second time with placeholders exposed, and that file
    /// is opened and read. That is the whole cost of the box, and
    /// <see cref="Contains"/> is where it is paid.
    ///
    /// **No follow-up stat per entry.** `FileEntry`'s own rule is that nothing
    /// on it may require a second call, and this walk broke it twice: once to
    /// ask <c>File.GetAttributes</c> whether an entry was a directory, and again
    /// to build a <see cref="FileInfo"/> for each match. Three syscalls per file
    /// where the directory read already carried the answer.
    /// <see cref="FileSystemEnumerable{TResult}"/> hands over name, attributes,
    /// length and timestamp from the entry the OS already returned.
    ///
    /// **A link was not descended into and was not listed either.** Reparse
    /// points were named in <see cref="EnumerationOptions.AttributesToSkip"/>,
    /// which is one setting doing two jobs when only one of them was wanted.
    /// The job that was wanted is termination: a profile directory is full of
    /// legacy junctions — "Application Data", "My Documents" — that point back
    /// at their own ancestors, and a recursive walk that follows them does not
    /// terminate. The job that was not is that AttributesToSkip drops the entry
    /// from the results as well, so a junction or symbolic link a person made
    /// and named was the one name this search could never return, with nothing
    /// to say it had been left out — while the same query on Linux listed it.
    /// Termination is the enqueue below's business now; the link itself is
    /// matched and returned like any other name.
    ///
    /// System stays skipped, because that is a separate question from links:
    /// every junction or symbolic link an ordinary user makes is a plain
    /// reparse point carrying no System bit, and the legacy profile junctions
    /// carry System as well, so they stay out of the results on the attribute
    /// that was always hiding them.
    ///
    /// The Symlink flag <c>WindowsEntryFlags</c> sets is load-bearing twice
    /// over now: it draws the link emblem in the listing, and it is what the
    /// enqueue below reads to stop. Both come out of the one directory read, so
    /// the guard costs no extra syscall — but deleting that line un-terminates
    /// the walk as well as losing the emblem.
    ///
    /// It marks a Windows shortcut as well, off the name rather than the
    /// attributes. That changes nothing about the walk: a .lnk is a file, so
    /// it was always a row and never a way in. It matters because these
    /// results ARE a listing — they fill the same Entries the three row
    /// templates draw — so a backend that described a shortcut differently
    /// from the folder it came from would draw the arrow in one place and
    /// not the other.
    /// </summary>
    private static IEnumerable<FileEntry> Walk(SearchQuery query, CancellationToken ct)
    {
        var comparison = query.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // A pattern is matched as a pattern; anything else is treated as a
        // substring, which is what people expect when they just type a word.
        //
        // Windows had only the substring half, so typing `*.cs` matched
        // nothing at all -- no filename contains those three characters in that
        // order -- while the same query on Linux listed every C# file. A glob
        // is the one search syntax a person is likely to try without being told
        // it exists, and failing it silently reads as "there are no results".
        var glob = query.IsPattern;

        // Asked once rather than per file. False for a pattern whatever the
        // box says: a pattern is a question about names.
        var contents = query.ReadsContents;

        // A null scope means "everywhere indexed", and with no index the honest
        // reading is every drive on the machine — which is the phrase the box
        // beside the search field uses, from Everywhere above.
        var roots = string.IsNullOrEmpty(query.ScopePath)
            ? SearchableDrives()
            : [query.ScopePath];

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            // ReparsePoint is deliberately absent, per the note above: this
            // setting hides the row, and hiding the row was never the point.
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        // An explicit frontier rather than SearchOption.AllDirectories: the
        // built-in recursive enumeration abandons the whole walk on one
        // unreadable directory, and on a Windows drive it always meets one.
        //
        // **It was a stack, so the cap was spent underground.** A stack makes
        // the walk depth-first, and depth-first from C:\ means the first ten
        // thousand matches come out of whichever branch happened to be popped
        // first. Nothing a person owns was in the answer at all: over every
        // fixed drive on a real machine, "e" capped at ten thousand spent the
        // whole budget inside one game's asset tree, nine levels down, and
        // returned ZERO rows from the home folder. A queue spends the same
        // budget level by level. It is not a cure — the same walk gets 45 rows
        // under the home folder and never reaches depth 4, because System32,
        // SysWOW64 and INF are two levels down and enormous — but it is the
        // difference between a shallow answer and one branch of a deep one,
        // and the sentence the band now carries is what admits the rest.
        //
        // Ordering, and a frontier about twice as wide: 99,612 paths at peak
        // over C:\ against a stack's 46,286, some fifteen megabytes. The cap
        // below is unchanged, and so is everything the note above says about
        // links and System.
        var pending = new Queue<string>(roots);

        var found = 0;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var directory = pending.Dequeue();

            // Materialised per directory so a mid-enumeration failure costs this
            // folder rather than everything still on the frontier — and so no
            // file is read while the directory handle is still open.
            List<FileEntry> entries;
            try
            {
                entries = new FileSystemEnumerable<FileEntry>(directory, Transform, options)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Which of these a sync client holds online, asked of the folder
            // once and only when a file in it is going to be opened. The rows
            // above cannot say: this process sees placeholders disguised as
            // ordinary files. See Placeholders.
            HashSet<string>? online = null;

            foreach (var entry in entries)
            {
                // A junction is a name in this folder, so it is matched below
                // like any other; it is simply not a way in. This is where the
                // walk terminates now that the attribute no longer hides them.
                if (entry.IsDirectory && !entry.IsSymlink) pending.Enqueue(entry.FullPath);

                // Name first, because it costs nothing: a file whose name
                // answers is never opened.
                if (!Matches(entry.Name, query.Text, glob, comparison, query.CaseSensitive)
                    // A folder is not a file to open, and asking which
                    // files are online costs a second read of this folder,
                    // so neither happens for one. GUARD rather than rule:
                    // opening a folder would only fail, and fail quietly.
                    && !(contents && !entry.IsDirectory
                         && Contains(entry,
                                     (online ??= Placeholders.HeldOnlineIn(directory)).Contains(entry.Name),
                                     query, ct)))
                    continue;

                yield return entry;

                if (++found >= query.MaxResults) yield break;
            }
        }
    }

    /// <summary>
    /// The same rule LinuxSearchProvider applies, so a query means the same
    /// thing on both systems.
    ///
    /// <see cref="FileSystemName.MatchesSimpleExpression"/> is the matcher the
    /// enumeration itself uses for a search pattern, so `*.cs`, `note?.txt` and
    /// `*report*` behave here exactly as they do everywhere else in Windows.
    /// </summary>
    internal static bool Matches(
        string name, string text, bool glob, StringComparison comparison, bool caseSensitive)
        => glob
            ? FileSystemName.MatchesSimpleExpression(text, name, ignoreCase: !caseSensitive)
            : name.Contains(text, comparison);

    /// <summary>
    /// Whether a file's contents answer the question, for an entry whose name
    /// did not.
    ///
    /// **A file held online is not opened, because opening it downloads it.**
    /// OneDrive, and any other sync client built on the cloud files API,
    /// leaves a placeholder whose data is not on the disk, and reading one
    /// fetches the whole file — so a content search over a synced folder would
    /// download everything in it, silently, to answer one question.
    /// <paramref name="heldOnline"/> comes from <see cref="Placeholders"/>,
    /// which asks with placeholders exposed, because this process sees them
    /// disguised as ordinary files and its own directory read cannot tell.
    /// The refusal is counted, and the band says how many.
    ///
    /// **A link is matched by its name and not read.** What it holds is what
    /// it points at, which is searched where it lives if it is inside the walk
    /// at all — and a symbolic link to a share that has gone away blocks in
    /// the open for the whole SMB timeout, where no Stop can reach it. The same
    /// flag marks a shortcut, which is binary and so would be refused anyway.
    /// </summary>
    internal static bool Contains(
        FileEntry entry, bool heldOnline, SearchQuery query, CancellationToken ct)
    {
        if (entry.IsSymlink) return false;

        // A row the pane is going to drop is not worth opening.
        if (entry.IsConcealed && !query.ReadsConcealed) return false;

        if (heldOnline)
        {
            query.Skipped?.CountOnline();
            return false;
        }

        // **A length of zero is not believed here.** NTFS writes a file's size
        // into its directory entry when a handle closes, so a log a running
        // program still holds open lists at 0 bytes however much is in it —
        // measured: 7,200 bytes written and flushed, and the directory read
        // said 0 until the writer let go. The zero-length refusal exists for
        // Linux, where it keeps a FIFO from being opened; Windows has no FIFO
        // in a folder to protect against, and trusting the 0 skipped exactly
        // the file somebody searching a logs folder is looking for. A truly
        // empty file costs one open. The read is still bounded: past MaxBytes
        // it stops and says TooLarge whatever the directory said.
        return ContentMatcher.Answers(query, Extended(entry.FullPath), Math.Max(entry.Length, 1), ct);
    }

    /// <summary>
    /// The path with the extended prefix, so Windows opens the entry the walk
    /// listed and not a name it would rewrite first.
    ///
    /// **Without it, three kinds of legal name are read from somewhere else.**
    /// The Win32 path rules strip a trailing dot or space, so "report." and
    /// "report " open their neighbour "report" (ReachablePath records the same,
    /// measured); and "nul" opens the NUL device and reads nothing. Every check
    /// above — online, link, length — was made against the entry, so reading
    /// a different file answered for the wrong one, and could have opened a
    /// placeholder the checks never saw. ReachablePath refuses these names
    /// because acting on the wrong one can destroy it; a read can simply reach
    /// the right one.
    /// </summary>
    internal static string Extended(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..]
                : @"\\?\" + path;

    private static FileEntry Transform(ref FileSystemEntry entry)
    {
        var full = entry.ToFullPath();

        return new(
            Name: entry.FileName.ToString(),
            FullPath: full,
            Length: entry.IsDirectory ? 0 : entry.Length,
            LastWriteTime: entry.LastWriteTimeUtc,
            Flags: WindowsEntryFlags.For(
                entry.FileName, entry.Attributes, entry.IsDirectory,
                WindowsEntryFlags.TagFor(full, entry.Attributes)),
            // The third path a row arrives by, and it draws in the same details
            // columns as the other two. Out of the same directory read, so the
            // "no follow-up stat per entry" rule above holds for every unmarked
            // row; a row wearing the ReparsePoint attribute pays TagFor's look,
            // which is accounted for there.
            CreationTime: entry.CreationTimeUtc);
    }
}
