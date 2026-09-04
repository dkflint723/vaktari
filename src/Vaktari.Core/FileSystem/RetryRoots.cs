namespace Vaktari.Core.FileSystem;

/// <summary>
/// One thing an operation could not do, and the place THIS run had decided to
/// put it.
///
/// **The target is carried rather than recomputed.** By the time a retry is
/// pressed, "source into destination" is no longer enough to say where
/// something goes: a Keep both has renamed the root it lives under, a duplicate
/// in place has given it a " - Copy" name, and only the engine knew any of that
/// while it was running. Recomputing would put the retried items into the
/// folder the user asked to keep separate.
/// </summary>
/// <param name="IsDirectory">
/// **Carried rather than asked of the disk.** Only a folder can contain another
/// failure, so this decides which failures are worth starting again from — and
/// by the time the offer is built the source may have been moved, renamed or
/// removed, so a Directory.Exists probe answers about the world as it is now
/// rather than about the plan that failed. It also keeps the rule below pure
/// path arithmetic, with no syscall per failure.
/// </param>
public readonly record struct RetryRoot(string Source, string Target, bool IsDirectory);

/// <summary>
/// The offer itself: how many things a retry will attempt, and how to attempt
/// them.
///
/// **The count is the number of ROOTS, not the number of problems.** A folder
/// that could not be created reports every one of its planned descendants as a
/// problem, so "retry 431" for one unreadable folder is a number that tells the
/// person nothing about what the button will do.
/// </summary>
/// <param name="AsAdministrator">
/// The same work, written out for a second copy of this program to do with
/// administrator rights — or null where elevation would not help or could not
/// be asked for faithfully. Data rather than a closure, unlike
/// <paramref name="Again"/>: what crosses to an elevated process is the one
/// thing worth being able to read, assert on and refuse, and an engine that
/// held the process-starting itself would have to be handed an elevator to
/// build an offer.
///
/// Its own count, which can be SMALLER than <paramref name="Count"/>: a batch
/// that lost one file to a program holding it open and three to a protected
/// folder is offered four to retry and three to retry with rights. Both numbers
/// are on their buttons, and both are true.
/// </param>
public sealed record RetryOffer(
    int Count, Func<IOperationHandle> Again, ElevatedRequest? AsAdministrator = null);

public static class RetryRoots
{
    /// <summary>
    /// The failures worth starting again from: the outermost ones.
    ///
    /// **A folder that failed drags its contents down with it**, and every one
    /// of those is recorded too — a folder that could not be created means
    /// every file planned inside it also failed. Offering "retry 431" for one
    /// unreadable folder is a number that tells the person nothing, and
    /// retrying each descendant separately re-attempts work the folder's own
    /// retry does anyway.
    ///
    /// Same rather than an ordinal compare, and Contains rather than
    /// StartsWith: the first is why a folder does not exclude itself, and the
    /// second is why "/media/one" does not claim "/media/onetwo".
    /// </summary>
    public static List<RetryRoot> Outermost(IReadOnlyList<RetryRoot> failed)
    {
        var folders = failed.Where(f => f.IsDirectory).Select(f => f.Source).ToList();

        return failed
            .Where(f => !folders.Any(root =>
                !PathRules.Same(root, f.Source) && PathRules.Contains(root, f.Source)))
            .ToList();
    }

    /// <summary>
    /// Which of those an elevated run could be asked to do, written out as a
    /// request — or null when the answer is none of them.
    ///
    /// **Only the ones refused for permission.** Elevation does nothing
    /// whatever about a file another program has open, a full disk or a path
    /// too long for the filesystem, and a shielded button that changes nothing
    /// is worse than no button: it teaches the person to reach for the consent
    /// prompt when the consent prompt is not the answer.
    ///
    /// **And only the ones that would land where their name says**, which for a
    /// copy or a move means WHOLE SOURCES and nothing below them. A root
    /// carries the target THIS run decided on, while the elevated request
    /// carries a destination and a list of sources from which the elevated copy
    /// works the target out again as destination plus leaf name. That
    /// arithmetic is wrong for a root a Keep both renamed — where it would
    /// merge the retry into the folder somebody asked to keep separate — and it
    /// is wrong just as often, and far more quietly, for a root DESCENDED from
    /// a source, whose target is the destination plus its whole relative path.
    /// Measured: a denied file inside a copied folder gets the ordinary retry
    /// and no elevated one. Both are left to that retry, which does know where
    /// they go; <see cref="LandsWhereItsNameSays"/> carries the detail.
    ///
    /// **Checked by being parsed back.** The offer is only made if the elevated
    /// process's own reader accepts what would be handed to it, so the rules
    /// about absolute paths and how many are allowed live in exactly one place
    /// — the side that does not trust the other.
    /// </summary>
    /// <param name="destination">Null for a delete, which has no second place.</param>
    /// <param name="refused">
    /// The sources that failed for want of permission. A set rather than the
    /// exceptions themselves: only the engine that caught them can say which
    /// were which, and only it knows.
    /// </param>
    public static ElevatedRequest? Administrator(
        ElevatedVerb verb, string? destination,
        IReadOnlyList<RetryRoot> worthRetrying, ISet<string> refused)
    {
        var sources = worthRetrying
            .Where(root => refused.Contains(root.Source))
            .Where(root => destination is null || LandsWhereItsNameSays(root, destination))
            .Select(root => root.Source)
            .ToList();

        // No separate emptiness guard: an empty list is one of the things the
        // reader below refuses, and a second copy of that rule here was a line
        // no mutation of it could redden — measured, by mutating it and
        // watching the suite stay green.
        var request = new ElevatedRequest(verb, destination, sources);

        return ElevatedRequest.Parse(request.ToArguments()) is null ? null : request;
    }

    /// <summary>
    /// Whether the elevated side would work this root's target out again
    /// correctly from destination plus leaf name.
    ///
    /// **This excludes two different things, and the second is the common
    /// one.** A root a Keep both renamed, yes — but also every root that is a
    /// DESCENDANT of a source, whose target is destination plus its whole
    /// relative path rather than plus its own name. Measured: copying a folder
    /// containing one file this person may not read records that file as an
    /// UnauthorizedAccessException, offers "retry 1", and offers NO
    /// administrator retry, because "into" plus "secret.txt" is not
    /// "into\stuff\secret.txt".
    ///
    /// So the elevated offer reaches WHOLE SOURCES only. A denied item inside
    /// a copied folder is left to the ordinary retry, which does know where it
    /// goes. Widening it means carrying a per-root sub-destination across, and
    /// that widens the argument list the elevated side has to trust — worth
    /// deciding on its own rather than under cover of this.
    /// </summary>
    private static bool LandsWhereItsNameSays(RetryRoot root, string destination)
        => PathRules.Same(
            root.Target, Path.Combine(destination, PathRules.LeafName(root.Source)));
}
