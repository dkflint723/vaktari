using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;

namespace Vaktari.Ui;

/// <summary>
/// A search, as a listing.
///
/// Shaped as the same IAsyncEnumerable the filesystem provider returns, so the
/// pane's load picks a source and changes nothing else — the same contract
/// RecentListing and ComputerListing already meet, and the reason this file is
/// short.
/// </summary>
public static class SearchListing
{
    /// <summary>
    /// **Twenty times the popup's cap, because a listing is not a menu.** The
    /// panel asked for 500 because that is as many as anybody scrolls in a
    /// floating list, so the cap was quietly doing the job of hiding that the
    /// results had nowhere to go. A pane virtualizes, so the only job left for
    /// a cap is stopping an unindexed walk running for ever.
    ///
    /// Not unbounded: a walk from This PC reads every fixed drive, and "e"
    /// matches most of it.
    /// </summary>
    public const int Limit = 10_000;

    /// <summary>
    /// Flushed on a count OR a clock.
    ///
    /// **Batching on the count alone hides a narrow query completely.** The
    /// pane only consults its own flush timer when this yields, so a walk that
    /// found four files in ninety seconds would show nothing at all for ninety
    /// seconds and then everything — precisely the case where progressive
    /// results matter most.
    /// </summary>
    private const int Batch = 32;
    private const int FlushMs = 200;

    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
        ISearchProvider? search,
        string path,
        ListingOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // An absent backend is an EMPTY listing, not a crash. The pane's empty
        // state is what says which of the two it is.
        if (search is null) { yield return []; yield break; }

        var text = VirtualPaths.QueryOf(path);

        if (text.Length == 0) { yield return []; yield break; }

        var query = new SearchQuery
        {
            Text = text,
            ScopePath = VirtualPaths.ScopeOf(path),
            MaxResults = Limit,
        };

        var batch = new List<FileEntry>(Batch);
        var since = System.Diagnostics.Stopwatch.StartNew();

        // **Every step of the backend BEGINS on the pool, and that is what the
        // pump is for.** An async iterator runs on the CALLER's thread until it
        // reaches a genuine suspension, and both backends do real work before
        // theirs — Baloo starts a process, the fallback reads a directory,
        // Everything opens an IPC connection. A ConfigureAwait(false) at the
        // consuming end does not help: it governs continuations AFTER a
        // suspension, not the prologue. This is reached from a navigation, so
        // that work would otherwise land on the dispatcher.
        //
        // A bare Task.Yield does not fix it either — it posts straight back to
        // the context it came from, and YieldAwaitable has no ConfigureAwait to
        // say otherwise. Wrapping the whole enumeration in one Task.Run would,
        // but this streams and cannot be gathered first. So the enumerator is
        // pumped: each MoveNextAsync is started by the pool, whatever thread
        // this iterator's consumer happens to be on.
        var results = search.SearchAsync(query, ct).GetAsyncEnumerator(ct);

        try
        {
        while (await Task.Run(async () => await results.MoveNextAsync().ConfigureAwait(false), ct)
                         .ConfigureAwait(false))
        {
            var entry = results.Current;

            // **The backends return hidden and system files, and the pane
            // expects them already gone.** Everywhere else that rule is the
            // filesystem provider's, applied before the pane sees a row —
            // nothing downstream re-checks it. A search listing that skipped
            // this would show dotfiles and ~$Word.docx while the folder beside
            // it hid them, from one setting.
            if (!options.IncludeHidden && entry.IsConcealed) continue;

            batch.Add(entry);

            if (batch.Count < Batch && since.ElapsedMilliseconds < FlushMs) continue;

            yield return batch;

            batch = new List<FileEntry>(Batch);
            since.Restart();
        }

        }
        finally
        {
            await results.DisposeAsync().ConfigureAwait(false);
        }

        if (batch.Count > 0) yield return batch;
    }
}
