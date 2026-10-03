namespace Vaktari.Core.FileSystem;

/// <summary>
/// How the file operations tell the application that a folder is about to
/// move, and where it went.
///
/// **Vaktari blocked its own renames.** Every tab watches its folder, and on
/// Windows a folder cannot be renamed or binned whole while anything beneath it
/// is open — so a tab in a subfolder, a background tab, another window, or the
/// watch a tab keeps on a repository's .git made renaming the folder above
/// fail with "Access to the path is denied" (rename-notes, plan.md §1). The
/// engine cannot see the tabs and the tabs cannot see the engine, so this is
/// the one seam between them: the engine asks for the folder before it moves
/// it, and says afterwards where it went.
///
/// Null in an engine nobody has given one — a test's, or one used before any
/// window exists — and then everything happens exactly as it did before.
/// </summary>
public interface IFolderHandover
{
    /// <summary>
    /// Lets go of everything the application holds at or under these folders,
    /// in every window, and keeps it let go until the lease is disposed.
    /// </summary>
    ValueTask<IFolderLease> ReleaseAsync(IReadOnlyList<string> folders, CancellationToken ct);

    /// <summary>
    /// A folder moved without being leased — a move between folders, which the
    /// engine performs item by item and which nothing of ours can block — so
    /// the tabs that were inside it follow it to where it went.
    /// </summary>
    void Followed(string from, string to);
}

/// <summary>
/// The folders an engine is about to move. Disposing it ends the hold:
/// whatever was told <see cref="Moved"/> follows, whatever was told
/// <see cref="Gone"/> is read again where it was (and finds itself gone), and
/// anything told neither — a rename that failed — is put back exactly as it was.
/// </summary>
public interface IFolderLease : IAsyncDisposable
{
    void Moved(string from, string to);

    void Gone(string folder);
}

/// <summary>
/// The engines' half of the hand-over, shared so the two cannot drift.
/// </summary>
public static class FolderMoves
{
    /// <summary>
    /// Leases <paramref name="moves"/>' sources, runs <paramref name="act"/>,
    /// and reports each move as the disk now shows it — so a batch that did
    /// some of its folders and not others is followed for the ones it did.
    /// </summary>
    /// <param name="moves">Each folder and where it is going; a null
    /// destination is a folder being binned.</param>
    public static async ValueTask RunAsync(
        IFolderHandover? handover,
        IReadOnlyList<(string From, string? To)> moves,
        Func<ValueTask> act,
        CancellationToken ct)
    {
        if (handover is null || moves.Count == 0)
        {
            await act().ConfigureAwait(false);
            return;
        }

        var lease = await handover.ReleaseAsync([.. moves.Select(m => m.From)], ct).ConfigureAwait(false);
        var threw = true;

        try
        {
            await act().ConfigureAwait(false);
            threw = false;
        }
        finally
        {
            try
            {
                foreach (var (from, to) in moves)
                {
                    if (to is null)
                    {
                        if (!Directory.Exists(from) && !File.Exists(from)) lease.Gone(from);
                    }
                    else if (Arrived(from, to, threw)) lease.Moved(from, to);
                }
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether a folder is now at <paramref name="to"/> rather than
    /// <paramref name="from"/>, asked of the disk.
    ///
    /// **A case-only rename is the same folder by both names** on a
    /// case-insensitive file system, so the disk cannot answer for it; there
    /// the call's own success does.
    /// </summary>
    public static bool Arrived(string from, string to, bool threw)
        => PathRules.Same(from, to)
            ? !threw
            : Directory.Exists(to) && !Directory.Exists(from);
}
