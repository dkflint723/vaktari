using Vaktari.Core.Updates;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The once-a-day question to the releases page, and what it may and may
/// not say.
///
/// **A person on an old release had no way to hear about the fix they were
/// living with.** This pins the shape the answer has to keep: newer by
/// number, not by string; once a day whatever the network said; never for a
/// development build; and silence — not a line, not an exception — when the
/// network has nothing to say. The network itself is a seam.
/// </summary>
public sealed class ReleaseCheckTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "vaktari-updates-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_state, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private static string Answer(string tag)
        => $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/dkflint723/vaktari/releases/tag/{{tag}}","body":"..."}""";

    private int _fetches;

    private ReleaseCheck Checking(string tag, DateTimeOffset? now = null)
        => new(_state)
        {
            FetchOverride = _ => { _fetches++; return Task.FromResult(Answer(tag)); },
            Clock = () => now ?? new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
        };

    // ---- newer by number ---------------------------------------------------------

    [Theory]
    [InlineData("v0.11.0", "0.10.2", true)]
    [InlineData("v0.10.3", "0.10.2", true)]
    [InlineData("v1.0.0", "0.10.2", true)]
    [InlineData("v0.10.2", "0.10.2", false)]
    [InlineData("v0.9.9", "0.10.2", false)]
    [InlineData("0.11.0", "0.10.2", true)]
    public void A_release_is_newer_by_its_number(string tag, string running, bool newer)
        => Assert.Equal(newer, ReleaseCheck.IsNewer(tag, running));

    /// <summary>**0.10.2 is above 0.9.9**, which "0.10.2" &lt; "0.9.9" as
    /// strings gets exactly wrong — and the tag has to be a number at all.</summary>
    [Theory]
    [InlineData("nightly", "0.10.2")]
    [InlineData("v2026-preview", "0.10.2")]
    [InlineData("", "0.10.2")]
    public void A_tag_that_is_not_a_number_is_not_newer(string tag, string running)
        => Assert.False(ReleaseCheck.IsNewer(tag, running));

    [Fact]
    public void The_answer_is_read_for_its_tag_and_its_page()
    {
        var latest = ReleaseCheck.Parse(Answer("v0.11.0"));

        Assert.NotNull(latest);
        Assert.Equal("0.11.0", latest.Version);
        Assert.Equal("https://github.com/dkflint723/vaktari/releases/tag/v0.11.0", latest.Url);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"v0.11.0"}""")]
    [InlineData("[]")]
    public void An_answer_without_both_is_nothing(string json)
        => Assert.Null(ReleaseCheck.Parse(json));

    // ---- the check --------------------------------------------------------------

    [Fact]
    public async Task A_newer_release_is_reported_with_its_page()
    {
        var found = await Checking("v0.11.0").CheckAsync("0.10.2", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("0.11.0", found.Version);
        Assert.EndsWith("/releases/tag/v0.11.0", found.Url);
    }

    [Fact]
    public async Task The_release_already_running_is_not_news()
        => Assert.Null(await Checking("v0.10.2").CheckAsync("0.10.2", CancellationToken.None));

    /// <summary>
    /// **Once a day, whatever the answer was.** The stamp is written before
    /// the request, so a network that never answers is asked once a day too
    /// rather than on every start — and it is on disk, so a restart does not
    /// reset the day.
    /// </summary>
    [Fact]
    public async Task The_question_is_asked_once_a_day()
    {
        var noon = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

        await Checking("v0.11.0", noon).CheckAsync("0.10.2", CancellationToken.None);
        var again = await Checking("v0.11.0", noon.AddHours(6)).CheckAsync("0.10.2", CancellationToken.None);

        Assert.Null(again);
        Assert.Equal(1, _fetches);

        var tomorrow = await Checking("v0.11.0", noon.AddHours(25)).CheckAsync("0.10.2", CancellationToken.None);

        Assert.NotNull(tomorrow);
        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task A_network_that_does_not_answer_is_silence_and_is_not_asked_again_today()
    {
        var noon = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

        var check = new ReleaseCheck(_state)
        {
            FetchOverride = _ => { _fetches++; throw new HttpRequestException("no route to host"); },
            Clock = () => noon,
        };

        Assert.Null(await check.CheckAsync("0.10.2", CancellationToken.None));
        Assert.Null(await check.CheckAsync("0.10.2", CancellationToken.None));
        Assert.Equal(1, _fetches);
    }

    /// <summary>A development build reports 0.0.0, which every release is
    /// newer than; asking would tell a developer on every start that the
    /// release they are working towards exists.</summary>
    [Fact]
    public async Task A_development_build_never_asks()
    {
        Assert.Null(await Checking("v0.11.0").CheckAsync("0.0.0", CancellationToken.None));
        Assert.Equal(0, _fetches);
    }
}
