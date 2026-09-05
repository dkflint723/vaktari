using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What a search backend claims about having an index behind it.
///
/// **The warning about reading every folder hung on IsAvailable, and both
/// shipped providers hardcoded that true.** IsAvailable meant "will this return
/// results at all", and WindowsSearchProvider spelled out why it had to go on
/// meaning that: it IS the fallback walk, so answering false would have sent
/// the UI to a second fallback walk of its own. The consequence was that a
/// machine with no index reported the same flag as a machine with one, and the
/// sentence explaining the wait was unreachable in the source before anybody
/// noticed that no markup file bound it either.
///
/// AnswersFromIndex is that question asked on its own, and asked of a QUERY:
/// a provider can have an index that this particular search will not touch.
/// What is worth pinning here is the answer a provider gives by saying nothing
/// — the platform providers' own answers belong to their own suites, where the
/// machinery deciding them is.
/// </summary>
public sealed class SearchIndexClaimTests
{
    /// <summary>
    /// A backend that has not thought about it does NOT claim an index, for any
    /// question.
    ///
    /// The cautious way round, and the same way round as SupportsCaseSensitivity
    /// beside it: an unearned warning costs a dim line under the question, while
    /// an unearned silence is a wait with nothing on screen accounting for it.
    /// </summary>
    [Theory]
    [InlineData("report")]
    [InlineData("*.pdf")]
    public void A_backend_that_says_nothing_does_not_claim_an_index(string text)
        => Assert.False(
            ((ISearchProvider)new Bare()).AnswersFromIndex(new SearchQuery { Text = text }));

    /// <summary>
    /// Everything the interface requires and nothing it merely offers, which is
    /// the point: the member left out is the one under test, and the cast in
    /// the assertion is what makes the default the thing being read.
    /// </summary>
    private sealed class Bare : ISearchProvider
    {
        public string BackendName => "bare";
        public bool SupportsContentSearch => false;

        public IAsyncEnumerable<FileEntry> SearchAsync(SearchQuery query, CancellationToken ct)
            => throw new NotSupportedException("nothing here is ever asked a question");
    }
}
