using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The elevated run as the rest of the application sees it: a handle, and what
/// the one number it gets back is turned into.
///
/// **A number is all there is.** Windows' consent verb forbids redirecting the
/// started process's output, so an exit code is the entire vocabulary between
/// the two processes — which makes reading it correctly the whole of this.
/// </summary>
public sealed class ElevatedRunTests
{
    private static readonly string Root =
        OperatingSystem.IsWindows() ? @"C:\work" : "/work";

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    private static ElevatedRequest Copying(int howMany) => new(
        ElevatedVerb.Copy, At("into"),
        [.. Enumerable.Range(0, howMany).Select(i => At($"f{i}.txt"))]);

    private static ElevatedRequest Deleting(int howMany) => new(
        ElevatedVerb.Delete, null,
        [.. Enumerable.Range(0, howMany).Select(i => At($"f{i}.txt"))]);

    /// <summary>
    /// Stands in for the consent prompt and the process behind it. Neither can
    /// be reached from a test: starting the real thing raises a dialog with
    /// nobody there to answer it.
    /// </summary>
    private sealed class Answers(int? code) : IApplicationLauncher
    {
        public IReadOnlyList<string>? Saw { get; private set; }

        public bool CanElevate => true;

        public ValueTask<int?> RunSelfElevatedAsync(
            IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Saw = arguments;
            return ValueTask.FromResult(code);
        }

        public Exception? Open(string path) => null;
        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    /// <summary>
    /// Stands in for a route to elevation that goes wrong on the way, rather
    /// than answering.
    /// </summary>
    private sealed class Throws(Exception what) : IApplicationLauncher
    {
        public bool CanElevate => true;

        public ValueTask<int?> RunSelfElevatedAsync(
            IReadOnlyList<string> arguments, CancellationToken ct)
            => throw what;

        public Exception? Open(string path) => null;
        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    [Fact]
    public async Task Everything_done_completes_with_nothing_to_say()
    {
        var handle = ElevatedRun.Start(new Answers(0), Copying(3));

        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Null(handle.Error);
    }

    /// <summary>
    /// **A declined prompt is an answer, not a fault.** It is cancelled, which
    /// is the shape the rest of the application already gives to "the person
    /// said no": the bar clears its line rather than reporting a failure
    /// nobody caused.
    /// </summary>
    [Fact]
    public async Task A_declined_prompt_is_not_a_failure()
    {
        var handle = ElevatedRun.Start(new Answers(null), Copying(2));

        await handle.Completion;

        Assert.Equal(OperationState.Cancelled, handle.State);
        Assert.Null(handle.Error);
    }

    /// <summary>The request is what is handed over, and nothing else.</summary>
    [Fact]
    public async Task What_is_handed_over_is_the_request_itself()
    {
        var launcher = new Answers(0);
        var request = Copying(1);

        await ElevatedRun.Start(launcher, request).Completion;

        Assert.Equal(request.ToArguments(), launcher.Saw);
    }

    /// <summary>
    /// **The paths are recorded**, so the eject guard sees an administrator
    /// copy onto a stick the way it sees an ordinary one. A handle claiming no
    /// paths would let the drive be ejected out from under it.
    /// </summary>
    [Fact]
    public void The_destination_is_among_the_paths_it_claims()
    {
        var request = Copying(2);

        var handle = ElevatedRun.Start(new Answers(null), request);

        Assert.Contains(request.Destination, handle.Paths);
        Assert.Contains(request.Sources[0], handle.Paths);
    }

    /// <summary>
    /// **And a delete claims its sources**, which has no destination to claim
    /// alongside them. The eject guard reads these: an administrator delete
    /// that claimed no paths at all would let the stick be ejected out from
    /// under it while the elevated process was still removing files from it.
    /// </summary>
    [Fact]
    public void A_delete_claims_the_paths_it_is_removing()
    {
        var request = Deleting(2);

        var handle = ElevatedRun.Start(new Answers(null), request);

        Assert.Equal(request.Sources, handle.Paths);
    }

    /// <summary>
    /// **Cancelling is not a failure**, and the token is the shell's own: a
    /// launcher that comes back with an OperationCanceledException is the
    /// operation being called off, which clears the bar rather than putting a
    /// sentence on it that nobody caused.
    /// </summary>
    [Fact]
    public async Task A_run_called_off_on_the_way_is_cancelled_and_not_failed()
    {
        var handle = ElevatedRun.Start(
            new Throws(new OperationCanceledException()), Copying(2));

        await handle.Completion;

        Assert.Equal(OperationState.Cancelled, handle.State);
        Assert.Null(handle.Error);
    }

    /// <summary>
    /// **Anything else IS a failure, and keeps its own sentence.** A route to
    /// elevation that threw — no shell association, a binary that has moved —
    /// has to reach the bar as what went wrong, not as a silent completion
    /// claiming the work was done.
    /// </summary>
    [Fact]
    public async Task A_route_that_throws_reaches_the_bar_as_the_failure_it_was()
    {
        var broken = new IOException("no shell would start it");

        var handle = ElevatedRun.Start(new Throws(broken), Copying(2));

        await handle.Completion;

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Same(broken, handle.Error);
    }

    /// <summary>
    /// A count comes back as a count, in a sentence that says how much of what
    /// was asked for did not happen.
    /// </summary>
    [Fact]
    public void Some_left_behind_says_how_many_of_how_many()
    {
        var handle = new OperationHandle();

        ElevatedRun.Finish(handle, Copying(5), exit: 2);

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal("2 of 5 could not be done as administrator", handle.Error?.Message);
    }

    /// <summary>
    /// **Anything outside the agreed range means the elevated process never
    /// spoke.** pkexec answers 127 when it could not run the program at all;
    /// reading that as "127 items were left behind" would put a number on the
    /// bar that no file anywhere corresponds to.
    /// </summary>
    [Fact]
    public void A_code_from_outside_the_range_is_not_read_as_a_count()
    {
        var handle = new OperationHandle();

        ElevatedRun.Finish(handle, Copying(3), exit: 127);

        Assert.Equal("the administrator run did not say what it did", handle.Error?.Message);
    }

    /// <summary>
    /// And the refusal code is its own sentence: it means our own writer and
    /// reader disagree about what a request is, which is worth saying out loud
    /// rather than counting as files.
    /// </summary>
    [Fact]
    public void A_refusal_says_it_was_refused()
    {
        var handle = new OperationHandle();

        ElevatedRun.Finish(handle, Copying(3), exit: ElevatedRequest.Refused);

        Assert.Equal(
            "the administrator run would not act on what it was given",
            handle.Error?.Message);
    }

    /// <summary>
    /// **An administrator run wears the same clothes as every other operation**,
    /// and that now includes the verb: it takes a row in the list of running
    /// operations like an ordinary copy, and a row with no kind on it reads
    /// "Working" — which says less than the bar said before the list existed.
    ///
    /// The elevated verb is a closed set of three and the row's is a set of
    /// four, so this is a mapping and not a cast; getting it wrong would put
    /// "Copying" on the row of a delete.
    /// </summary>
    [Fact]
    public void An_elevated_run_says_which_verb_it_is_serving()
    {
        Assert.Equal(
            OperationKind.Copy,
            ElevatedRun.Start(new Answers(null), Copying(1)).Kind);

        Assert.Equal(
            OperationKind.Delete,
            ElevatedRun.Start(new Answers(null), Deleting(1)).Kind);

        Assert.Equal(
            OperationKind.Move,
            ElevatedRun.Start(
                new Answers(null),
                new ElevatedRequest(ElevatedVerb.Move, At("into"), [At("f0.txt")])).Kind);
    }

    /// <summary>
    /// **And the destination goes LAST**, which is the arrangement
    /// <see cref="IOperationHandle.Kind"/> states and a row reads to say where
    /// the bytes are going. Get it the other way round and an administrator
    /// copy's row names the last SOURCE as the place they are landing.
    ///
    /// A delete has no second place, so its paths are its sources and nothing
    /// else — which is the branch that appends nothing.
    /// </summary>
    [Fact]
    public void An_elevated_transfer_puts_its_destination_last()
    {
        var copy = ElevatedRun.Start(new Answers(null), Copying(3));

        Assert.Equal(At("into"), copy.Paths[^1]);
        Assert.Equal([At("f0.txt"), At("f1.txt"), At("f2.txt"), At("into")], copy.Paths);

        var delete = ElevatedRun.Start(new Answers(null), Deleting(2));

        Assert.Equal([At("f0.txt"), At("f1.txt")], delete.Paths);
    }

    /// <summary>
    /// **And a copy or a move never arrives here without one**, which is what
    /// keeps the rule above from being a promise the row cannot rely on. Only
    /// two things build an ElevatedRequest, and this is the one the elevated
    /// process itself uses: <see cref="ElevatedRequest.Parse"/> reads
    /// arguments[2] as the destination for every verb but delete, and refuses
    /// the line outright when what it finds there is not a rooted path.
    ///
    /// So the shape that would make a row say "Copying a.txt to b.txt" — a Copy
    /// carrying only sources — cannot be read off a command line at all. The
    /// other producer, <c>RetryRoots.Administrator</c>, passes null only for
    /// <see cref="ElevatedVerb.Delete"/>, and hands its result through this
    /// same Parse before offering it.
    /// </summary>
    [Fact]
    public void A_copy_with_no_destination_is_not_a_request_at_all()
    {
        // The verb, then straight into sources: whatever sits where the
        // destination belongs is read as the destination, and a relative one is
        // refused.
        Assert.Null(ElevatedRequest.Parse([ElevatedRequest.Flag, "copy", "f0.txt", At("f1.txt")]));

        // And a copy with nothing after the destination has nothing to copy.
        Assert.Null(ElevatedRequest.Parse([ElevatedRequest.Flag, "copy", At("into")]));

        // A delete is the verb that legitimately carries none, and it is
        // accepted — so the refusals above are about the destination and not
        // about the arity.
        var delete = ElevatedRequest.Parse([ElevatedRequest.Flag, "delete", At("f0.txt")]);

        Assert.NotNull(delete);
        Assert.Null(delete.Destination);

        var copy = ElevatedRequest.Parse(
            [ElevatedRequest.Flag, "copy", At("into"), At("f0.txt")]);

        Assert.NotNull(copy);
        Assert.Equal(At("into"), copy.Destination);
    }
}
