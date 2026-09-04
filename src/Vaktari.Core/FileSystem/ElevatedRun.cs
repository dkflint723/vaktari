namespace Vaktari.Core.FileSystem;

/// <summary>
/// An elevated file operation, wearing the same clothes as every other one.
///
/// **A handle, because everything downstream of an operation is fed by one.**
/// The work happens in another process, but the transfer bar, the listing
/// refresh at the end and the undo state all hang off
/// <see cref="IOperationHandle.Completion"/> — so a retry that ran without one
/// would be the only operation in the application that finished without the
/// folder it changed being re-read.
///
/// **It never reports a plan.** Nothing calls
/// <see cref="OperationHandle.Begin"/> here, so the bar draws no fraction and
/// no bytes: the elevated process says nothing at all until it exits, and a
/// progress line that sat at "0/3 0 B/0 B" and then jumped to the end would be
/// inventing every number on it. What the bar shows instead is the sentence the
/// shell wrote when it asked for consent, and then the sentence this writes
/// when the answer comes back.
///
/// The consequence, said out loud: pause and cancel on the bar reach only THIS
/// side of it. An unelevated process has no rights over an elevated one, so
/// cancelling stops us waiting for the answer and does not stop the work.
/// </summary>
public static class ElevatedRun
{
    /// <summary>
    /// Asks for consent, lets the elevated process do the work, and reports.
    ///
    /// <see cref="OperationHandle.Cancelled"/> for a declined prompt, which is
    /// the shape the rest of the application already gives to "the person said
    /// no": the bar clears its line and offers nothing back, exactly as
    /// cancelling a copy does. A refusal is an answer, not a fault — the same
    /// reading the launcher already gives ERROR_CANCELLED.
    ///
    /// **The paths are recorded**, so the eject guard sees an administrator
    /// copy onto a stick the way it sees an ordinary one; a handle that claimed
    /// no paths would let the drive be ejected out from under it.
    /// </summary>
    public static IOperationHandle Start(
        IApplicationLauncher launcher, ElevatedRequest request)
    {
        var handle = new OperationHandle
        {
            Paths = request.Destination is { } into
                ? [.. request.Sources, into]
                : [.. request.Sources],
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var code = await launcher
                    .RunSelfElevatedAsync(request.ToArguments(), handle.Token)
                    .ConfigureAwait(false);

                if (code is not { } exit) handle.Cancelled();
                else Finish(handle, request, exit);
            }
            catch (OperationCanceledException) { handle.Cancelled(); }
            catch (Exception ex) { handle.Failed(ex); }
        });

        return handle;
    }

    /// <summary>
    /// What an exit code means, in the words the transfer bar shows.
    ///
    /// **Anything outside the agreed range means the elevated process never
    /// spoke.** pkexec answers 127 when it could not run the program at all,
    /// and a crash answers whatever the runtime chose; reading either as "127
    /// items were left behind" would put a number on the bar that no file
    /// anywhere corresponds to. Zero, a count, a refusal, and everything else
    /// are the whole vocabulary.
    ///
    /// Internal and separate so the mapping can be pinned without starting a
    /// process — which on Windows means a consent dialog with nobody there to
    /// answer it.
    /// </summary>
    internal static void Finish(OperationHandle handle, ElevatedRequest request, int exit)
    {
        var total = request.Sources.Count;

        if (exit == 0) { handle.Complete(); return; }

        // IOException rather than a type of our own: Failures.Describe hands an
        // IOException's own message back untouched, which is the point — these
        // sentences are written for the person and there is nothing to
        // translate them into.
        handle.Failed(new IOException(
            exit == ElevatedRequest.Refused
                ? "the administrator run would not act on what it was given"
                : exit > 0 && exit <= total
                    ? $"{exit} of {total} could not be done as administrator"
                    : "the administrator run did not say what it did"));
    }
}
