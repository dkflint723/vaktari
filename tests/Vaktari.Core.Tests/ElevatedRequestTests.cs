using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What crosses from the unelevated file manager to a copy of itself running
/// with administrator rights.
///
/// **This is the trust boundary, so the reader is the interesting half.** The
/// side that has the rights does not get to assume the side that has not was
/// well behaved: every rule about what a request may be lives in
/// <see cref="ElevatedRequest.Parse"/>, and the offer on the bar is only made
/// at all if Parse would accept it — so there is one set of rules rather than
/// two that can drift apart.
/// </summary>
public sealed class ElevatedRequestTests
{
    private static readonly string Root =
        OperatingSystem.IsWindows() ? @"C:\work" : "/work";

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>
    /// A copy survives being written out as an argument list and read back —
    /// which is the whole contract between the two processes.
    /// </summary>
    [Fact]
    public void A_copy_survives_being_written_out_and_read_back()
    {
        var request = new ElevatedRequest(
            ElevatedVerb.Copy, At("into"), [At("a.txt"), At("b.txt")]);

        var back = ElevatedRequest.Parse(request.ToArguments());

        // Field by field: a record's own equality compares the source list by
        // reference, and the point here is that a second process rebuilt one.
        Assert.NotNull(back);
        Assert.Equal(request.Verb, back.Verb);
        Assert.Equal(request.Destination, back.Destination);
        Assert.Equal(request.Sources, back.Sources);
    }

    /// <summary>
    /// A delete has no second place, so it carries none — and the verb is what
    /// says so, which is why no option has to be parsed.
    /// </summary>
    [Fact]
    public void A_delete_carries_no_destination()
    {
        var request = new ElevatedRequest(ElevatedVerb.Delete, null, [At("gone.txt")]);

        var back = ElevatedRequest.Parse(request.ToArguments());

        Assert.NotNull(back);
        Assert.Null(back.Destination);
        Assert.Equal(ElevatedVerb.Delete, back.Verb);
        Assert.Equal([At("gone.txt")], back.Sources);
    }

    /// <summary>
    /// **A relative path is refused.** It would be resolved against the
    /// elevated process's working directory, which is not the folder anybody
    /// was looking at — and "did what you asked, somewhere else" is the worst
    /// thing an administrator copy can do.
    /// </summary>
    [Fact]
    public void A_relative_path_is_refused()
        => Assert.Null(ElevatedRequest.Parse(
            [ElevatedRequest.Flag, "delete", "notes.txt"]));

    /// <summary>
    /// And so is a destination that is not absolute, which is the same rule on
    /// the other side of the operation.
    /// </summary>
    [Fact]
    public void A_relative_destination_is_refused()
        => Assert.Null(ElevatedRequest.Parse(
            [ElevatedRequest.Flag, "copy", "elsewhere", At("a.txt")]));

    /// <summary>
    /// **The request has to BEGIN the line.** An elevated launch carries
    /// nothing else, which is one rule instead of a list of interactions
    /// between elevation and every other argument this program will ever grow.
    ///
    /// The input is what makes the rule visible rather than redundant: two
    /// folders that happen to be called "copy", opened the ordinary way, put
    /// the word a verb is spelled with where the verb goes. Read from anywhere
    /// on the line that is a copy request; read from the front it is two
    /// folders.
    /// </summary>
    [Fact]
    public void The_request_has_to_begin_the_line()
        => Assert.Null(ElevatedRequest.Parse(
            ["copy", "copy", At("into"), At("a.txt")]));

    /// <summary>
    /// A verb outside the closed set is refused rather than guessed at. There
    /// is no "run" here and there never will be.
    /// </summary>
    [Fact]
    public void A_verb_that_is_not_one_of_the_three_is_refused()
        => Assert.Null(ElevatedRequest.Parse(
            [ElevatedRequest.Flag, "run", At("installer.exe")]));

    /// <summary>
    /// More than it will carry is refused outright rather than truncated: an
    /// elevated run that silently did the first sixty-four of two hundred would
    /// be worse than no offer at all.
    /// </summary>
    [Fact]
    public void More_than_it_will_carry_is_refused()
    {
        var many = Enumerable.Range(0, ElevatedRequest.MaxSources + 1)
            .Select(i => At($"f{i}.txt"))
            .ToArray();

        Assert.Null(ElevatedRequest.Parse(
            [ElevatedRequest.Flag, "delete", .. many]));

        // And the one below the line still goes, so the refusal is the bound
        // and not the feature being broken.
        Assert.NotNull(ElevatedRequest.Parse(
            [ElevatedRequest.Flag, "delete", .. many[..^1]]));
    }

    /// <summary>
    /// **Sixty-four is not on its own enough to keep the launch possible.**
    /// This program turns long paths on (app.manifest, longPathAware), so a
    /// path is not capped at 260 characters and sixty-four of them can exceed
    /// Windows' 32767-character command line between them. A request that
    /// overran it would throw inside Process.Start, come back through the
    /// launcher's catch as null, and read on the bar as a prompt somebody
    /// declined — so it is refused here, where the refusal means the offer is
    /// never made.
    /// </summary>
    [Fact]
    public void A_line_too_long_to_start_a_process_with_is_refused()
    {
        var deep = At(new string('n', 900));

        var many = Enumerable.Range(0, ElevatedRequest.MaxSources)
            .Select(i => Path.Combine(deep, $"f{i}.txt"))
            .ToArray();

        // Inside the count and outside the line, which is the whole point of
        // there being two bounds.
        Assert.True(many.Length <= ElevatedRequest.MaxSources);
        Assert.True(many.Sum(p => p.Length) > ElevatedRequest.MaxLineLength);

        Assert.Null(ElevatedRequest.Parse([ElevatedRequest.Flag, "delete", .. many]));

        // And a handful of the same long paths still goes, so the refusal is
        // the bound and not the feature being broken.
        Assert.NotNull(ElevatedRequest.Parse(
            [ElevatedRequest.Flag, "delete", .. many[..8]]));
    }

    /// <summary>
    /// A copy with a destination and nothing to put in it is not a request.
    /// </summary>
    [Fact]
    public void A_copy_with_no_sources_is_refused()
        => Assert.Null(ElevatedRequest.Parse(
            [ElevatedRequest.Flag, "copy", At("into")]));

    /// <summary>
    /// **The refusal code cannot be mistaken for a count.** It sits above the
    /// most sources a request may carry, so "one hundred" can never mean "one
    /// hundred files were left behind".
    /// </summary>
    [Fact]
    public void The_refusal_code_is_out_of_the_range_a_count_can_reach()
        => Assert.True(ElevatedRequest.Refused > ElevatedRequest.MaxSources);

    /// <summary>
    /// A name a shell would tear apart is one argument here, because there is
    /// no shell: the list is handed over as an argv on both platforms.
    /// </summary>
    [Fact]
    public void A_name_full_of_punctuation_is_still_one_argument()
    {
        var awkward = At("; rm -rf ~ \"and\" $HOME.txt");

        var back = ElevatedRequest.Parse(
            new ElevatedRequest(ElevatedVerb.Delete, null, [awkward]).ToArguments());

        Assert.NotNull(back);
        Assert.Equal([awkward], back.Sources);
    }
}
