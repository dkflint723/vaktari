using Vaktari.Core.FileSystem;

namespace Vaktari.Core.Search;

public sealed record SearchQuery
{
    public required string Text { get; init; }

    /// <summary>Null searches everything the provider reaches unscoped.</summary>
    public string? ScopePath { get; init; }

    /// <summary>
    /// Whether a file whose contents hold the text is an answer as well as one
    /// whose name does: name OR contents, never contents alone, so ticking the
    /// box only ever adds rows.
    ///
    /// Asked for, not promised — see <see cref="ReadsContents"/> for when a
    /// walk actually opens anything.
    /// </summary>
    public bool MatchContent { get; init; }

    public bool CaseSensitive { get; init; }
    public int MaxResults { get; init; } = 1000;

    /// <summary>
    /// Where a search that reads contents counts the files it would not read.
    /// Null when nobody is going to say the number, which is every caller
    /// except the pane.
    /// </summary>
    public ContentSkips? Skipped { get; init; }

    /// <summary>
    /// Whether a hidden file is worth opening to read. True unless a caller
    /// says otherwise, so a query built by hand reads everything it walks.
    ///
    /// **The pane drops hidden rows after the backend has found them**, so a
    /// content search that opened them read files nobody would be shown — a
    /// whole AppData or ~/.cache, one file after another — and counted the
    /// large ones among what it "could not read". Names are still matched as
    /// before: a hidden file is cheap to match and the pane's rule decides
    /// whether its row is shown.
    /// </summary>
    public bool ReadsConcealed { get; init; } = true;

    /// <summary>
    /// Called by a backend that meant to answer from its index and is walking
    /// the folders instead, at the moment it starts to — so the band can say
    /// so while the walk runs rather than after. Null when nobody is listening.
    ///
    /// **The band decides whether to explain a wait before the backend has
    /// answered**, from <see cref="ISearchProvider.AnswersFromIndex"/>, and on
    /// a KDE desktop the answer is often wrong: Baloo installed but switched
    /// off, a folder it does not index, or a word nothing holds, and Baloo
    /// says nothing and the walk runs. That was a silent name walk before; with
    /// Search contents ticked it opens every file in reach, and the one line
    /// that says so was hidden because an index was supposed to be answering.
    /// </summary>
    public Action? WalkingInstead { get; init; }

    /// <summary>
    /// Whether <see cref="Text"/> is a filename pattern rather than a word.
    ///
    /// **One rule for both walks and the band.** Each provider had its own
    /// copy of it — the same two characters, spelled once inline and once as a
    /// helper — and now the band needs it too, to know whether contents are
    /// being read. Three copies of a rule part company the first time any of
    /// them moves.
    /// </summary>
    public bool IsPattern => Text.Contains('*') || Text.Contains('?');

    /// <summary>
    /// Whether a walk opens files for this question: contents were asked for,
    /// and the text is not a pattern.
    ///
    /// **A pattern is a question about names.** "*.txt" asks for files called
    /// that; nobody typing it means "files with an asterisk in them", and
    /// reading every file on the drive to look for one would be the most
    /// expensive way there is to answer a question nobody asked.
    /// </summary>
    public bool ReadsContents => MatchContent && !IsPattern;
}

/// <summary>
/// Name and content search. Two backends implement it: Baloo on a KDE desktop,
/// which is an index someone else already maintains, and a managed directory
/// walk everywhere else — always on Windows, and on Linux when Baloo is absent.
/// Writing our own indexer is a last resort, not a starting point.
///
/// **This used to promise two things that were never built.** It named
/// Everything as the Windows backend, and said the UI would fall back to a
/// slow walk of its own when no index answered. Neither happened: Everything
/// was set aside (see WindowsSearchProvider), the walk lives inside the
/// providers themselves, and a null provider gives an empty listing rather
/// than a fallback.
/// </summary>
public interface ISearchProvider
{
    /// <summary>
    /// Whether an index answers THIS question, as against every folder being
    /// read in turn.
    ///
    /// This is what somebody waiting on a search is actually asking, and it is
    /// the only thing the band above the results says about the backend now.
    ///
    /// **The band used to ask <c>IsAvailable</c>, and both shipped providers
    /// hardcoded that true.** It meant "will this return results at all", and
    /// WindowsSearchProvider argued it had to go on meaning that: false, its
    /// comment said, would send the UI to a fallback walk of its own — a walk
    /// the UI never had, but the argument kept the flag true. A machine with
    /// no index therefore reported exactly what a machine with one did, and
    /// the sentence hung on the false arm was unreachable in the source before
    /// anybody noticed that no markup file bound it either. Nothing read
    /// IsAvailable once the band stopped, so it is gone rather than left as a
    /// member with no consequence.
    ///
    /// **A parameter rather than a property, because the routing takes one.**
    /// LinuxSearchProvider sends a glob past Baloo to the walk — the index
    /// stores words, not filename patterns — so a query-independent answer was
    /// wrong for "*.pdf" on every KDE box, which is the commonest shape of slow
    /// search there is. A provider answers for the question it was handed, and
    /// routes on the same answer.
    ///
    /// Defaulted to FALSE, the cautious way round and the same way round as
    /// <see cref="SupportsCaseSensitivity"/>: a provider that has not thought
    /// about it gets the warning, because a promise of speed nobody made is
    /// worse than a warning nobody needed. Claiming an index is the thing that
    /// has to be said out loud.
    /// </summary>
    bool AnswersFromIndex(SearchQuery query) => false;

    /// <summary>
    /// Which backend this is, for diagnostics and for the tests that need to
    /// know which of two paths ran.
    ///
    /// **Not words for a person.** It was interpolated into the line above the
    /// results, where it read "searching with directory walk" on Windows and
    /// "searching with baloo" on Fedora — the names of implementation details,
    /// handed to somebody who only wanted to know why finding a file was
    /// taking so long. What the band says now comes from
    /// <see cref="AnswersFromIndex"/> and is about the machine rather than
    /// about the code.
    /// </summary>
    string BackendName { get; }

    /// <summary>
    /// Whether <see cref="SearchQuery.MatchContent"/> reaches anything here,
    /// which is what decides whether the band draws the "Search contents" box.
    ///
    /// **It was true for Baloo alone and nothing read it.** No caller set
    /// MatchContent either, so the flag reached nothing: the walks could not
    /// look inside a file, and Baloo, the one backend that could, did so
    /// whatever was asked, because that is what baloosearch does. Both
    /// shipped providers answer true now: Baloo by its index, and both walks
    /// by ContentMatcher.
    ///
    /// No default, unlike the members around it: a provider has to say, the
    /// way it has to say its <see cref="BackendName"/>.
    /// </summary>
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
    /// its answers by scope, and by name when contents were not asked for —
    /// ignoring case both times, as Baloo does — so a tick there would change
    /// nothing at all, which is the same silence this whole finding is about,
    /// moved from a field to a checkbox.
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
    /// What this backend's search leaves out, said in a few words for the band
    /// above the results — or null when there is nothing to confess.
    ///
    /// **Windows skipped every file carrying the System attribute and nothing
    /// on screen said so.** The walk's <c>AttributesToSkip</c> was the
    /// framework's default narrowed, not a decision, and a folder a sync client
    /// had marked System was searched past in silence. Explorer searches those.
    /// Whether to search them is the provider's call; that the call is VISIBLE
    /// is this member's, and it sits beside <see cref="Everywhere"/> for the
    /// same reason that one does — the band is not the place for a second copy
    /// of a rule that lives here.
    ///
    /// Defaulted to null so a provider with nothing to leave out says nothing.
    /// </summary>
    string? Caveat => null;

    /// <summary>
    /// Streams results as the index answers, so the panel fills progressively
    /// instead of waiting on a complete result set.
    /// </summary>
    IAsyncEnumerable<FileEntry> SearchAsync(SearchQuery query, CancellationToken ct);
}
