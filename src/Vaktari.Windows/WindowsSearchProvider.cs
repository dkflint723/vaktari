using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;

namespace Vaktari.Windows;

/// <summary>
/// Name search by walking the tree.
///
/// **No index behind it, and it says so.** <see cref="ISearchProvider"/> is
/// documented as sitting on an index someone else maintains — Everything on
/// Windows — and this is not that. Everything is third-party, may not be
/// installed, and talks over an IPC protocol worth its own decision; Windows
/// Search is COM. A managed walk is honest, has no dependency, and is the same
/// thing the interface says the UI falls back to.
///
/// <see cref="IsAvailable"/> is nonetheless true. It means "will this return
/// results", not "is it fast", and returning false would send the UI to its own
/// fallback walk — the same work, done twice as far as the user can tell.
/// </summary>
public sealed class WindowsSearchProvider : ISearchProvider
{
    public bool IsAvailable => true;

    public string BackendName => "directory walk";

    /// <summary>
    /// False. Reading every file to match text is a different order of cost from
    /// matching names, and doing it without an index would be indistinguishable
    /// from a hang on any real folder.
    /// </summary>
    public bool SupportsContentSearch => false;

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

    private static List<string> FixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.Name)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// One directory read per directory, and nothing else.
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
    /// The Symlink flag <c>ToFlags</c> sets below is load-bearing twice over
    /// now: it draws the link emblem in the listing, and it is what the enqueue
    /// below reads to stop. Both come out of the one directory read, so the guard
    /// costs no extra syscall — but deleting that line un-terminates the walk
    /// as well as losing the emblem.
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
        var glob = query.Text.Contains('*') || query.Text.Contains('?');

        // A null scope means "everywhere indexed", and with no index the honest
        // reading is every fixed drive.
        var roots = string.IsNullOrEmpty(query.ScopePath)
            ? FixedDrives()
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
            // folder rather than everything still on the frontier.
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

            foreach (var entry in entries)
            {
                // A junction is a name in this folder, so it is matched below
                // like any other; it is simply not a way in. This is where the
                // walk terminates now that the attribute no longer hides them.
                if (entry.IsDirectory && !entry.IsSymlink) pending.Enqueue(entry.FullPath);

                if (!Matches(entry.Name, query.Text, glob, comparison, query.CaseSensitive))
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

    private static FileEntry Transform(ref FileSystemEntry entry) => new(
        Name: entry.FileName.ToString(),
        FullPath: entry.ToFullPath(),
        Length: entry.IsDirectory ? 0 : entry.Length,
        LastWriteTime: entry.LastWriteTimeUtc,
        Flags: ToFlags(ref entry));

    private static EntryFlags ToFlags(ref FileSystemEntry entry)
    {
        var flags = EntryFlags.None;
        var attributes = entry.Attributes;

        if (entry.IsDirectory) flags |= EntryFlags.Directory;
        if ((attributes & FileAttributes.Hidden) != 0) flags |= EntryFlags.Hidden;
        if ((attributes & FileAttributes.System) != 0) flags |= EntryFlags.System;
        if ((attributes & FileAttributes.ReparsePoint) != 0) flags |= EntryFlags.Symlink;
        if ((attributes & FileAttributes.ReadOnly) != 0) flags |= EntryFlags.ReadOnly;

        return flags;
    }
}
