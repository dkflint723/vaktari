namespace Vaktari.Core.FileSystem;

/// <summary>
/// The three things an elevated run is ever asked to do.
///
/// **Closed, and deliberately without a "run this" verb.** The whole surface of
/// what an administrator copy can be talked into doing is this list: it moves
/// bytes between paths and it removes paths. Nothing here starts a program,
/// reads a setting or writes anything the request did not name, so an argument
/// list that arrived from somewhere unexpected still cannot do more than copy
/// or delete the files it names.
///
/// **Trash is not one of them.** Recycling as root on a freedesktop desktop
/// puts the files into ROOT's trash, where the person who deleted them cannot
/// see or restore them — the one outcome the bin exists to prevent. A permanent
/// delete has no such second meaning, and is the only removal offered here.
/// </summary>
public enum ElevatedVerb { Copy, Move, Delete }

/// <summary>
/// One file operation, written as an argument list a second copy of this
/// program can be started with.
///
/// **THIS IS THE TRUST BOUNDARY.** The process that builds one of these is the
/// unelevated file manager; the process that acts on one has administrator
/// rights. What crosses between them is exactly this: a verb from the closed
/// set above, one destination, and a list of absolute paths. Nothing else —
/// no command string, no shell, no file the elevated side goes and reads.
///
/// **An argument list rather than a request FILE**, which was the obvious
/// alternative and is worse. A file written before the consent prompt and read
/// after it can be rewritten in between by anything running as the same person,
/// so the work the elevated process does need not be the work the person
/// agreed to. A command line is fixed when the process is created and cannot be
/// changed afterwards by anybody.
///
/// **What this cannot do is decide whether the person meant it.** The system's
/// prompt — Windows' consent dialog, polkit's authentication — names the
/// PROGRAM, never the paths. So the window is the consent surface: the failures
/// are listed under "details" and the count is on the button before the prompt
/// ever appears. And it is worth writing down that on Windows this is not a
/// security boundary at all, by Microsoft's own account of UAC: code already
/// running as the person can replace the executable that is about to be
/// elevated. polkit on Linux IS such a boundary, but only as far as the
/// binary's own permissions go, which for a build in a home directory is not
/// far.
///
/// **And it is honoured whether or not this process is elevated.** The flag
/// is a command line like any other, so <c>vaktari --elevated-file-op delete
/// &lt;path&gt;</c> permanently removes what it names from any caller — no window,
/// no confirmation, no bin. That is not new power (anybody who can start this
/// program can start a delete), but it is a new way to spend it, and a route to
/// permanent deletion that no part of the window offers.
/// </summary>
/// <param name="Destination">
/// The folder things are copied or moved INTO. Null for a delete, which has no
/// second place.
/// </param>
public sealed record ElevatedRequest(
    ElevatedVerb Verb, string? Destination, IReadOnlyList<string> Sources)
{
    /// <summary>
    /// The switch that turns a launch into an elevated file operation, answered
    /// before the window, the settings or the theme — see Program.Main, which
    /// answers <c>--restore-file-manager</c> in the same place and for the same
    /// reason: this runs with no display attached and must depend on nothing
    /// that needs one.
    /// </summary>
    public const string Flag = "--elevated-file-op";

    /// <summary>
    /// How many things one elevated run may be asked about.
    ///
    /// **A round number, and the count is not what keeps the launch possible.**
    /// app.manifest sets longPathAware — "non-negotiable for this application
    /// specifically", says its own note — so a path here is NOT capped at 260
    /// characters and sixty-four of them can exceed Windows' 32767-character
    /// command line on their own. The count keeps the list readable under
    /// "details"; <see cref="MaxLineLength"/> is what keeps it launchable, and
    /// both are checked.
    ///
    /// Beyond either the offer is simply not made and the ordinary retry still
    /// stands — an elevated run that silently did the first sixty-four of two
    /// hundred would be worse than none, and one that threw on Process.Start
    /// would come back through the catch as "declined" and clear the bar with
    /// nothing having happened.
    /// </summary>
    public const int MaxSources = 64;

    /// <summary>
    /// Room the command line actually has, with headroom for the quoting the
    /// runtime adds and for the program's own path in front.
    /// </summary>
    public const int MaxLineLength = 30000;

    /// <summary>
    /// What the elevated process exits with when it would not act on what it
    /// was handed.
    ///
    /// Above <see cref="MaxSources"/> so it can never be mistaken for a count
    /// of things left behind, and below the codes a failed launch produces —
    /// pkexec answers 126 when the authentication is dismissed and 127 when it
    /// could not run at all, and neither of those is this program speaking.
    /// </summary>
    public const int Refused = 100;

    /// <summary>
    /// The argument list, in the order <see cref="Parse"/> reads it.
    ///
    /// Positional and short: the verb decides whether a destination follows,
    /// so there is no option to parse and no way for a path to be mistaken for
    /// a switch. Handed to the process as an argv — never joined into a string
    /// and never given to a shell — so a file called <c>; rm -rf ~</c> is a
    /// file called <c>; rm -rf ~</c> on both platforms.
    /// </summary>
    public IReadOnlyList<string> ToArguments()
        => Destination is { } into
            ? [Flag, Name(Verb), into, .. Sources]
            : [Flag, Name(Verb), .. Sources];

    private static string Name(ElevatedVerb verb) => verb switch
    {
        ElevatedVerb.Copy => "copy",
        ElevatedVerb.Move => "move",
        _ => "delete",
    };

    /// <summary>
    /// Reads a request off the whole command line, or refuses.
    ///
    /// **The whole of it, not a switch found among others.** An elevated launch
    /// carries nothing but the request, so anything else on the line — a folder
    /// to open, a second flag — means this is not the launch it claims to be
    /// and nothing is done. That is one rule instead of a list of interactions
    /// between elevation and every other argument this program will ever grow.
    ///
    /// Fully-qualified paths only: a relative one would be resolved against the
    /// elevated process's working directory, which is not the one the person
    /// was looking at, and "did what you asked somewhere else" is the worst
    /// thing an administrator copy can do.
    /// </summary>
    public static ElevatedRequest? Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 3 || arguments[0] != Flag) return null;

        var verb = arguments[1] switch
        {
            "copy" => ElevatedVerb.Copy,
            "move" => ElevatedVerb.Move,
            "delete" => ElevatedVerb.Delete,
            _ => (ElevatedVerb?)null,
        };

        if (verb is not { } kind) return null;

        var wantsDestination = kind != ElevatedVerb.Delete;

        var destination = wantsDestination ? arguments[2] : null;
        var sources = arguments.Skip(wantsDestination ? 3 : 2).ToList();

        // A copy whose only argument after the verb was its destination has
        // nothing to copy, and lands here with an empty source list — so the
        // arity is checked by the emptiness rather than by counting twice.
        if (sources.Count == 0 || sources.Count > MaxSources) return null;

        // **And it has to fit on a command line.** Long paths are on for this
        // program (app.manifest, longPathAware), so sixty-four of them can
        // exceed Windows' 32767-character line on their own — and a request
        // that overran it would throw inside Process.Start, come back through
        // the launcher's catch as null, and read on the bar as though the
        // person had declined a prompt they were never shown.
        if (sources.Sum(s => s.Length + 3) + (destination?.Length ?? 0) > MaxLineLength)
            return null;

        if (destination is not null && !Rooted(destination)) return null;

        return sources.All(Rooted) ? new ElevatedRequest(kind, destination, sources) : null;
    }

    /// <summary>
    /// Absolute, and a path at all. IsPathFullyQualified answers false for
    /// "file.txt" and for "C:file.txt" — the second being the drive-relative
    /// form that looks absolute and is not — and throws for nothing, so the
    /// empty string is refused on its own.
    /// </summary>
    private static bool Rooted(string path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
}

/// <summary>
/// The elevated process's whole job: do the request, say how much of it was
/// left undone, exit.
///
/// **Through the ordinary engine, not a second copy of one.** The alternative
/// was to shell out to robocopy or to cp, and then an administrator copy would
/// name its collisions differently, refuse different paths and report different
/// problems from every other copy in the application. There is one engine, and
/// the elevated process is another caller of it.
/// </summary>
public static class ElevatedFileOp
{
    /// <summary>
    /// Runs it and answers with an exit code: nothing but a number, because
    /// nothing but a number can be got back from a process started through
    /// Windows' consent verb — that route forbids redirecting its output.
    ///
    /// Zero means everything asked for was done. One to the number of SOURCES
    /// in the request is how many of them were not — never more, because the
    /// caller reads anything above that as a code this program did not write.
    /// <see cref="ElevatedRequest.Refused"/> means it would not act on the
    /// request at all.
    ///
    /// **Which ones were left behind is deliberately not reported**, and that
    /// is the honest limit of this shape rather than an oversight. Naming them
    /// needs a channel back, and the only one available under the consent verb
    /// is a file — which is a second thing to write, to read, to get wrong, and
    /// to have rewritten underneath us, all to improve a sentence.
    /// </summary>
    public static async Task<int> RunAsync(
        IFileOperations operations, ElevatedRequest request)
    {
        // **Skipped, not overwritten and not renamed around.** This is a RETRY
        // of something that failed, with nobody to ask: overwriting would
        // destroy a file the person never agreed to lose, and keeping both —
        // the answer this application uses when a headless run meets a clash —
        // would invent "readme (1).txt" inside a protected folder, which is not
        // what "go again with rights" means. Skipping does neither, and the
        // count below says it happened.
        var refused = 0;

        ValueTask<ConflictResolution> Skip(FileConflict _)
        {
            refused++;
            return ValueTask.FromResult(ConflictResolution.Skip);
        }

        var handle = request.Verb switch
        {
            ElevatedVerb.Copy when request.Destination is { } into =>
                operations.Copy(request.Sources, into, Skip),
            ElevatedVerb.Move when request.Destination is { } into =>
                operations.Move(request.Sources, into, Skip),
            ElevatedVerb.Delete => operations.Delete(request.Sources),
            _ => null,
        };

        if (handle is null) return ElevatedRequest.Refused;

        await handle.Completion.ConfigureAwait(false);

        // **The retry offer's own count, not the problem count.** A folder that
        // could not be created records every one of its planned descendants as
        // a problem, so counting those would report four hundred for one
        // unreadable folder. The offer answers "how many roots are worth going
        // again on", which is the nearest thing this run knows.
        //
        // A run that ended any other way did none of it — the disk-space
        // refusal is decided before a byte moves — so everything asked for is
        // still to do.
        var left = handle.State == OperationState.Completed
            ? (handle.Retry?.Count ?? 0) + refused
            : request.Sources.Count;

        // **Clamped to the SOURCES, not to the maximum, and the two are not the
        // same population.** The offer counts the outermost failures inside
        // THIS run, which can be descendants of a source: copying one folder
        // whose three files were held open answered 3 for a request naming 1
        // source, and the caller reads anything above the source count as "the
        // administrator run did not say what it did" — so a run that said
        // exactly what it did was reported as incoherent. Measured: code=3,
        // sources=1. The cost of the clamp is precision, not truth: three files
        // inside one source come back as "1 of 1 could not be done", which is
        // the same resolution the request itself has.
        return Math.Clamp(left, 0, request.Sources.Count);
    }
}
