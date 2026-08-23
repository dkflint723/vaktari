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
    /// </summary>
    internal static string? BalooOverride { get; set; }

    private static string? Baloo => BalooOverride ?? Detected.Value;

    public bool IsAvailable => true;

    public string BackendName => Baloo is null ? "walk" : "baloo";

    /// <summary>Only the index can search inside files; the walk matches names.</summary>
    public bool SupportsContentSearch => Baloo is not null;

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

    /// <summary>True if the query is a shell-style pattern rather than a substring.</summary>
    private static bool IsGlob(string text)
        => text.Contains('*') || text.Contains('?');

    public IAsyncEnumerable<FileEntry> SearchAsync(SearchQuery query, CancellationToken ct)
        // Baloo indexes words, not filename patterns, so a glob has to go
        // through the walk — that is what MatchesSimpleExpression is for.
        => Baloo is { } baloo && !IsGlob(query.Text)
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
    /// </summary>
    private static async IAsyncEnumerable<FileEntry> SearchWithBalooThenWalkingAsync(
        string binary, SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var found = 0;

        await foreach (var entry in SearchWithBalooAsync(binary, query, ct).ConfigureAwait(false))
        {
            found++;
            yield return entry;
        }

        if (found > 0 || ct.IsCancellationRequested) yield break;

        // Said out loud, because the two routes have very different costs and a
        // search that suddenly takes seconds should be explicable.
        Console.Error.WriteLine(
            "[vaktari] search: baloo returned nothing — walking the folder instead "
            + "(an index that is switched off or was never built looks exactly like no matches)");

        await foreach (var entry in SearchByWalkingAsync(query, ct).ConfigureAwait(false))
            yield return entry;
    }

    private static async IAsyncEnumerable<FileEntry> SearchWithBalooAsync(
        string binary, SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
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

                // Baloo indexes the whole home; the scope is applied here rather
                // than trusting a flag whose name differs across KDE versions.
                if (query.ScopePath is { Length: > 0 } scope &&
                    !path.StartsWith(scope, StringComparison.Ordinal)) continue;

                if (Describe(path) is { } entry)
                {
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

    private static async IAsyncEnumerable<FileEntry> SearchByWalkingAsync(
        SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var root = query.ScopePath is { Length: > 0 } scope
            ? scope
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var comparison = query.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var text = query.Text;
        var glob = IsGlob(text);
        var ignoreCase = !query.CaseSensitive;

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
            // A pattern is matched as a pattern; anything else is treated as a
            // substring, which is what people expect when they just type a word.
            ShouldIncludePredicate = glob
                ? (ref FileSystemEntry entry) =>
                    FileSystemName.MatchesSimpleExpression(text, entry.FileName, ignoreCase)
                : (ref FileSystemEntry entry) =>
                    entry.FileName.ToString().Contains(text, comparison),
        };

        var count = 0;

        // The walk is blocking and can run for a long time, so it is pulled on
        // the thread pool in chunks rather than holding the caller.
        using var enumerator = walk.GetEnumerator();

        while (true)
        {
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
