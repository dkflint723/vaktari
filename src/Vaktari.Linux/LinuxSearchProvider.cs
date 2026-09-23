using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;

using Vaktari.Core;

namespace Vaktari.Linux;

/// <summary>
/// Baloo when it's indexing, a recursive walk when it isn't.
///
/// Building an indexer was never the plan — KDE already runs one and has been
/// indexing this machine for months. The walk exists so search still returns
/// something on a box where Baloo is switched off, just slowly and with a
/// visible warning rather than silently empty results.
/// </summary>
public sealed class LinuxSearchProvider : ISearchProvider
{
    // KDE Frameworks 6 suffixes its CLI tools so they can coexist with KF5, so
    // the plain name is not what Fedora KDE actually installs.
    private static readonly Lazy<string?> Detected =
        new(() => Locate("baloosearch6") ?? Locate("baloosearch"));

    /// <summary>
    /// Stands in for what is on PATH.
    ///
    /// The state worth testing — the tool installed but answering nothing,
    /// because no index was ever built — cannot be arranged on a machine that
    /// has a working index, nor on one with no Baloo at all. A script that
    /// prints nothing and exits cleanly is exactly what baloosearch does there.
    ///
    /// **A plain string could not say "not installed".** Null meant "go and
    /// look at this machine", so the no-Baloo case could only be asserted on an
    /// agent that happens to have no KDE on it — on Linux, where the answer
    /// actually matters, the test read the developer's own box and had to be a
    /// guard that asserted nothing. A probe returning null says absent and
    /// means it.
    /// </summary>
    internal static Func<string?>? BalooOverride { get; set; }

    private static string? Baloo => BalooOverride is { } probe ? probe() : Detected.Value;

    /// <summary>
    /// Whether Baloo answers this particular question, which is both how
    /// <see cref="SearchAsync"/> routes and what the band above the results
    /// reads. One rule, in one place, so the sentence explaining a slow search
    /// cannot disagree with the code that made it slow.
    ///
    /// **The glob is why the question has to carry a query.** Baloo indexes
    /// words, not filename patterns, so "*.pdf" has always gone straight past
    /// it to the walk — and a claim of "there is an index here" made without
    /// looking at the text would have suppressed the warning for the commonest
    /// slow search on a KDE box, walking home plus every mounted drive in
    /// silence.
    ///
    /// **The binary being on PATH is still not the same as an index existing**,
    /// and <c>SearchWithBalooThenWalkingAsync</c> exists for exactly that gap.
    /// Nothing here can close it: whether Baloo has anything to say is only
    /// learned by asking it, and the band is drawn before the answer comes
    /// back. Every empty answer inside the scope falls back to the walk — an
    /// index never built, one switched off, a folder it does not cover, a word
    /// nothing holds — and the walk says so as it starts, through
    /// <see cref="SearchQuery.WalkingInstead"/>, so the band can take back
    /// what this told it.
    /// </summary>
    public bool AnswersFromIndex(SearchQuery query) => Baloo is not null && !query.IsPattern;

    public string BackendName => Baloo is null ? "walk" : "baloo";

    /// <summary>
    /// True with or without Baloo. The index searches inside the files it has
    /// read; the walk reads plain text itself, through ContentMatcher.
    ///
    /// **It was true only with Baloo, and that was the walk's limit rather
    /// than a rule.** The walk could match names and nothing else, so on a box
    /// with no indexer there was no way to find a file by what was in it.
    /// </summary>
    public bool SupportsContentSearch => true;

    private static string? Locate(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }

        return null;
    }

    public IAsyncEnumerable<FileEntry> SearchAsync(SearchQuery query, CancellationToken ct)
        // Baloo indexes words, not filename patterns, so a glob has to go
        // through the walk — that is what MatchesSimpleExpression is for. The
        // condition is AnswersFromIndex rather than a copy of it, because the
        // band tells somebody which of these two branches they are waiting on.
        => Baloo is { } baloo && AnswersFromIndex(query)
            ? SearchWithBalooThenWalkingAsync(baloo, query, ct)
            : SearchByWalkingAsync(query, ct);

    /// <summary>
    /// Baloo first, and the walk when Baloo produces nothing.
    ///
    /// **The binary being on PATH is not the same as an index existing**, and
    /// that gap swallowed searches whole. Locate only asks whether baloosearch
    /// is installed; any desktop with one KDE application pulls it in, and on a
    /// machine where the indexer has never run — or where the user turned
    /// indexing off — a query returns an empty result set and exits cleanly.
    /// Nothing to read from stderr, nothing wrong with the exit code, simply no
    /// answers. The panel then said "no results (baloo)", which is a definite
    /// negative about the filesystem rather than what it really was: an index
    /// that does not exist.
    ///
    /// So an empty answer is treated as no answer. Falling back costs a walk
    /// that was going to happen anyway on any box without Baloo, and only in
    /// the case where the fast path found nothing at all — a search that DOES
    /// hit the index still returns at index speed and never walks.
    ///
    /// **"Produced nothing" is counted before the name narrowing, not after.**
    /// With "Search contents" unticked, Baloo's answers that match only by
    /// their contents are dropped — and an index that answered with nothing
    /// BUT those has plainly been built. Counting what survived the narrowing
    /// would read that as no index, and walk home and every mounted drive to
    /// give the same empty answer the index already had.
    /// </summary>
    private static async IAsyncEnumerable<FileEntry> SearchWithBalooThenWalkingAsync(
        string binary, SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var heard = false;

        await foreach (var entry in SearchWithBalooAsync(binary, query, () => heard = true, ct)
                           .ConfigureAwait(false))
        {
            yield return entry;
        }

        if (heard || ct.IsCancellationRequested) yield break;

        // Said out loud, because the two routes have very different costs and a
        // search that suddenly takes seconds should be explicable — to the
        // band, which drew itself before this was known, and to the log.
        query.WalkingInstead?.Invoke();

        Console.Error.WriteLine(
            "[vaktari] search: baloo returned nothing — walking the folder instead "
            + "(an index that is switched off or was never built looks exactly like no matches)");

        await foreach (var entry in SearchByWalkingAsync(query, ct).ConfigureAwait(false))
            yield return entry;
    }

    /// <summary>
    /// baloosearch, read line by line.
    ///
    /// <paramref name="heard"/> is called for every answer inside the scope,
    /// before the name narrowing below — which is what the caller's "the index
    /// said nothing" test has to be about.
    /// </summary>
    private static async IAsyncEnumerable<FileEntry> SearchWithBalooAsync(
        string binary, SearchQuery query, Action heard,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var info = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(query.Text);

        Process? process = null;
        try { process = Process.Start(info); }
        catch { /* index present at detection but unusable now — yield nothing */ }

        if (process is null) yield break;

        var count = 0;

        // Two ways out of the loop below leave the child alive otherwise:
        // cancellation, and hitting MaxResults and breaking. Disposing a Process
        // closes our handle, not the process — so baloosearch would keep walking
        // the index for a query nobody is listening to any more.
        using var cancellation = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Quiet.Swallowed("search", ex); }
        });

        using (process)
        using (var reader = process.StandardOutput)
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (count >= query.MaxResults) break;

                // Output carries timing and summary lines as well as paths.
                var path = line.Trim();
                if (path.Length == 0 || path[0] != '/') continue;

                if (!InScope(query.ScopePath, path)) continue;

                if (Describe(path) is { } entry)
                {
                    heard();

                    // Baloo answers from names and contents alike, however it
                    // is asked. With the box unticked the question is about
                    // names, so the rest are dropped here — the box has to mean
                    // the same thing on a KDE desktop as on the walk.
                    if (!query.MatchContent && !NamedFor(entry.Name, query.Text)) continue;

                    count++;
                    yield return entry;
                }
            }

            // The MaxResults break above is not a cancellation, so the
            // registration never fires for it. Same outcome wanted either way:
            // nobody is reading, so nothing should still be searching.
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Quiet.Swallowed("search", ex); }
            }
        }
    }

    /// <summary>
    /// Whether one of Baloo's answers is inside the folder being searched.
    ///
    /// Baloo indexes the whole home, so the scope is applied here rather than
    /// trusting a flag whose name differs across KDE versions.
    ///
    /// **It was a bare string prefix, so searching one folder searched its
    /// neighbours too.** Scoped to /home/u/proj this also let through
    /// /home/u/projects and /home/u/proj-old — "this folder only" quietly
    /// meaning something else, and only in the indexed path, so the same search
    /// gave different answers depending on whether Baloo happened to be
    /// running. The prefix has to end at a separator, which is what
    /// PathRules.Contains says and documents for mount points.
    ///
    /// Named rather than inline because the loop it came out of drives a
    /// subprocess, and this is the whole of the rule worth pinning.
    /// </summary>
    internal static bool InScope(string? scope, string path)
        => scope is not { Length: > 0 } || PathRules.Contains(scope, path);

    /// <summary>
    /// Whether one of Baloo's answers is there because of its name.
    ///
    /// **Every word, not the whole question.** Baloo reads "report 2024" as
    /// two terms that must both be present, so it finds report-2024.pdf by its
    /// name — and a substring test on the whole question would drop that file
    /// for want of a space the name never had. Ignoring case, because Baloo
    /// does.
    /// </summary>
    internal static bool NamedFor(string name, string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
               .All(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    public string Everywhere => "your home folder and any mounted drives";

    /// <summary>
    /// Stands in for /proc/mounts in tests, the same seam LinuxPlacesProvider
    /// gives its own mount reading and for the same reason. Null in the
    /// application.
    /// </summary>
    internal static Func<IEnumerable<string>>? MountLines { get; set; }

    /// <summary>
    /// What an unscoped walk covers.
    ///
    /// **It was the home folder and nothing else.** A machine with a second
    /// disk, or a stick plugged in, answered a search of "everywhere" from
    /// somewhere the box could not scope to — This PC, a search listing — with
    /// results from one directory tree, and said "everywhere" while doing it.
    ///
    /// Mounts are taken from /proc/mounts through MountTable.IsRealVolume,
    /// which is the same rule the sidebar uses to decide what is a drive. That
    /// also settles the network question without a second rule: IsRealVolume
    /// requires a source under /dev, and a cifs mount's source is
    /// //server/share while an nfs one is server:/path, so neither can pass —
    /// which matters because a stale network mount blocks rather than failing,
    /// and a walk cannot time out of it.
    ///
    /// Anything already under home is dropped: a mount inside the home
    /// directory would otherwise be walked twice, once as itself and once on
    /// the way down.
    /// </summary>
    internal static List<string> Roots(SearchQuery query)
    {
        if (query.ScopePath is { Length: > 0 } scope) return [scope];

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();

        if (home.Length > 0) roots.Add(home);

        foreach (var mount in Volumes())
        {
            if (home.Length > 0 && PathRules.Contains(home, mount)) continue;
            if (roots.Any(r => PathRules.Same(r, mount))) continue;

            roots.Add(mount);
        }

        return roots;
    }

    private static IEnumerable<string> Volumes()
    {
        IEnumerable<string> lines;

        try
        {
            lines = MountLines is { } stub
                ? stub()
                : File.Exists("/proc/mounts") ? File.ReadLines("/proc/mounts") : [];
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;

            var source = MountTable.Unescape(parts[0]);
            var mountPoint = MountTable.Unescape(parts[1]);

            if (!MountTable.IsRealVolume(source, mountPoint, parts[2])) continue;
            if (mountPoint is "/") continue;

            yield return mountPoint;
        }
    }

    private static async IAsyncEnumerable<FileEntry> SearchByWalkingAsync(
        SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var found = 0;

        foreach (var root in Roots(query))
        {
            if (ct.IsCancellationRequested || found >= query.MaxResults) yield break;

            await foreach (var entry in WalkOneAsync(root, query, ct).ConfigureAwait(false))
            {
                yield return entry;

                if (++found >= query.MaxResults) yield break;
            }
        }
    }

    private static async IAsyncEnumerable<FileEntry> WalkOneAsync(
        string root, SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var comparison = query.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var text = query.Text;
        var glob = query.IsPattern;
        var ignoreCase = !query.CaseSensitive;

        // False for a pattern whatever the box says: a pattern is a question
        // about names.
        var contents = query.ReadsContents;

        // Where files are not opened, decided from the mount table once for
        // the walk rather than once per file.
        var mounts = contents
            ? new MountRules(MountLines is { } stub ? stub() : MountTable.Lines(), root)
            : null;

        var walk = new FileSystemEnumerable<string>(
            root,
            static (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            })
        {
            // **Links are not descended into.** Without this a search under a
            // folder holding a link to the home directory walks the whole home
            // directory, and one pointing at an ancestor never terminates — the
            // result cap was the only thing stopping it, by accident rather
            // than by design. The link itself is still matched and returned;
            // only the recursion stops.
            ShouldRecursePredicate = (ref FileSystemEntry entry)
                => !entry.Attributes.HasFlag(FileAttributes.ReparsePoint),

            // A pattern is matched as a pattern; anything else is treated as a
            // substring, which is what people expect when they just type a word.
            //
            // Contents only after the name has failed, so a file whose name
            // answers is never opened. The read happens here, inside the
            // enumerator, which is already on the pool — see below.
            ShouldIncludePredicate = glob
                ? (ref FileSystemEntry entry) =>
                    FileSystemName.MatchesSimpleExpression(text, entry.FileName, ignoreCase)
                : (ref FileSystemEntry entry) =>
                    entry.FileName.ToString().Contains(text, comparison)
                    || (contents && Contains(ref entry, query, mounts!, ct)),
        };

        var count = 0;

        // The walk is blocking and can run for a long time, so it is pulled on
        // the thread pool in chunks rather than holding the caller.
        using var enumerator = walk.GetEnumerator();

        while (true)
        {
            // A ceiling for THIS root, not the answer's. The caller counts
            // across every root and stops there; this only keeps one root from
            // running past the point where the total is already full.
            if (ct.IsCancellationRequested || count >= query.MaxResults) yield break;

            string? path = null;
            var moved = await Task.Run(() =>
            {
                if (!enumerator.MoveNext()) return false;
                path = enumerator.Current;
                return true;
            }, ct).ConfigureAwait(false);

            if (!moved) yield break;
            if (path is null) continue;

            if (Describe(path) is { } entry)
            {
                count++;
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Whether a file's contents answer the question, for an entry whose name
    /// did not.
    ///
    /// **A link is matched by its name and not read.** What it holds is what
    /// it points at, which is searched where it lives if it is inside the walk
    /// at all. And here, reading through one can hang for good: the length a
    /// directory entry gives for a link is the link's own — the length of the
    /// path it holds, as SpaceUsage found when it counted links at exactly
    /// that — so the zero-length rule that keeps ContentMatcher out of a FIFO
    /// does not stop one reached through a link, and opening a FIFO for
    /// reading blocks until something writes.
    ///
    /// A FIFO, socket or device node that is NOT behind a link reports a
    /// length of zero, and ContentMatcher never opens those.
    /// </summary>
    private static bool Contains(
        ref FileSystemEntry entry, SearchQuery query, MountRules mounts, CancellationToken ct)
    {
        if (entry.IsDirectory || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

        // A row the pane is going to drop is not worth opening. The same rule
        // Describe marks Hidden by.
        if (!query.ReadsConcealed && entry.FileName.StartsWith('.')) return false;

        var path = entry.ToFullPath();

        if (mounts.IsKernel(path)) return false;

        if (mounts.IsRemote(path))
        {
            query.Skipped?.CountOnline();
            return false;
        }

        return ContentMatcher.Answers(query, path, entry.Length, ct);
    }

    /// <summary>
    /// Which files a content search does not open, by the filesystem each one
    /// is on — the deepest mount point above it, so a local disk mounted
    /// inside a share is local.
    ///
    /// **A kernel filesystem is never read.** /proc, /sys and their kind are
    /// windows onto the running system; a search scoped to / walks into them,
    /// and sysfs reports 4096 bytes for files that hold a line, so the
    /// zero-length rule that keeps procfs out does not keep sysfs out. Not
    /// counted: nobody searching for words means those.
    ///
    /// **A network or cloud mount the walk only reached by walking down into it
    /// is not read, and is counted.** An rclone or sshfs mount under the home
    /// folder is an ordinary folder to the walk, and reading every file in it
    /// fetches every file from the other end — what the Windows walk refuses
    /// to do to a sync client's placeholders, for the same reason. A search
    /// SCOPED to such a mount is read: somebody went there on purpose, the way
    /// a search of a mapped drive on Windows reads the share.
    /// </summary>
    internal sealed class MountRules
    {
        private readonly List<(string Point, string Type)> _mounts = [];
        private readonly bool _rootIsRemote;

        internal MountRules(IEnumerable<string> lines, string root)
        {
            foreach (var line in lines)
            {
                var parts = line.Split(' ');
                if (parts.Length < 3) continue;

                var point = MountTable.Unescape(parts[1]).TrimEnd('/');

                _mounts.Add((point.Length == 0 ? "/" : point, parts[2]));
            }

            // Deepest first, so the first that contains a path is its own.
            _mounts.Sort((a, b) => b.Point.Length.CompareTo(a.Point.Length));

            _rootIsRemote = TypeOf(root) is { } type && MountTable.IsNetworkFs(type);
        }

        internal bool IsKernel(string path) => TypeOf(path) is { } type && MountTable.IsKernelFs(type);

        internal bool IsRemote(string path)
            => !_rootIsRemote && TypeOf(path) is { } type && MountTable.IsNetworkFs(type);

        private string? TypeOf(string path)
        {
            foreach (var (point, type) in _mounts)
            {
                if (string.Equals(path, point, StringComparison.Ordinal)) return type;

                if (path.StartsWith(point, StringComparison.Ordinal)
                    && (point == "/" || path[point.Length] is '/' or '\\'))
                    return type;
            }

            return null;
        }
    }

    private static FileEntry? Describe(string path)
    {
        try
        {
            var isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return null;

            var name = Path.GetFileName(path);
            var flags = EntryFlags.None;
            if (isDir) flags |= EntryFlags.Directory;
            if (name.StartsWith('.')) flags |= EntryFlags.Hidden;

            var info = new FileInfo(path);

            return new FileEntry(
                name,
                path,
                isDir ? 0 : info.Length,
                info.LastWriteTimeUtc,
                flags);
        }
        catch
        {
            // Indexed but since deleted, or unreadable — skip it silently.
            return null;
        }
    }
}
