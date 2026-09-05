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
