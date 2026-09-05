using Vaktari.Core.FileSystem;

namespace Vaktari.Core.Search;

public sealed record SearchQuery
{
    public required string Text { get; init; }

    /// <summary>Null searches everything the provider reaches unscoped.</summary>
    public string? ScopePath { get; init; }

    public bool MatchContent { get; init; }
    public bool CaseSensitive { get; init; }
    public bool Regex { get; init; }
    public int MaxResults { get; init; } = 1000;
}

/// <summary>
/// Name and content search. Backed by an index someone else already maintains —
/// Everything on Windows, Baloo on Fedora KDE. Writing our own indexer is a
/// last resort, not a starting point.
/// </summary>
public interface ISearchProvider
{
    /// <summary>
    /// False when the backing index is absent or disabled (Everything not
    /// running, Baloo switched off). The UI degrades to a slow recursive walk
    /// with a visible warning rather than silently returning nothing.
    /// </summary>
    bool IsAvailable { get; }

    string BackendName { get; }

    bool SupportsContentSearch { get; }

    /// <summary>
    /// Whether <see cref="SearchQuery.CaseSensitive"/> reaches anything here.
    ///
    /// **The flag had two readers and no writer, so nothing ever asked.** Both
    /// walks branch on it — <c>WindowsSearchProvider.Walk</c> picks the
    /// StringComparison and the glob's ignoreCase from it, and
    /// <c>LinuxSearchProvider.WalkOneAsync</c> does the same — and no caller
    /// has ever set it, so every search in the application has run with it
    /// false since the record was written.
    ///
    /// The box that sets it is drawn from this rather than unconditionally,
    /// because an index answers however it answers: on a KDE box
    /// <c>SearchWithBalooAsync</c> hands the query to baloosearch and filters
    /// its answers by scope alone, so a tick there would change nothing at all
    /// — which is the same silence this whole finding is about, moved from a
    /// field to a checkbox.
    ///
    /// Defaulted to FALSE, the opposite way round from <see cref="Everywhere"/>
    /// below: a phrase that has not been thought about is merely vague, while a
    /// control that has not been thought about makes a promise the backend does
    /// not keep. A provider that honours the flag says so.
    /// </summary>
    bool SupportsCaseSensitivity => false;

    /// <summary>
    /// What an unscoped search actually covers, as a phrase that finishes
    /// "searching …".
    ///
    /// **The box said "everywhere" and meant something narrower on both
    /// platforms.** Windows walked the fixed drives — so a search with the box
    /// unticked skipped the stick you had just plugged in — and Linux walked
    /// the home folder alone, so it skipped every other disk on the machine.
    /// Neither is everywhere, and the one word nobody could argue with is the
    /// one that was there.
    ///
    /// Answered by the provider because the provider is what decides the
    /// roots. A phrase kept next to the checkbox would be a second copy of a
    /// rule that lives here, and the two would part company the first time
    /// either moved.
    ///
    /// Defaulted to the old word so a provider that has not thought about it is
    /// no worse than before, and so the null-provider case in the UI has
    /// something to say.
    /// </summary>
    string Everywhere => "everywhere";

    /// <summary>
    /// Streams results as the index answers, so the panel fills progressively
    /// instead of waiting on a complete result set.
    /// </summary>
    IAsyncEnumerable<FileEntry> SearchAsync(SearchQuery query, CancellationToken ct);
}
