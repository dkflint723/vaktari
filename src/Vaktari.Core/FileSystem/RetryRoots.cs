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
public sealed record RetryOffer(int Count, Func<IOperationHandle> Again);

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
}
