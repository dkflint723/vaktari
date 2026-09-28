using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;

namespace Vaktari.Windows;

/// <summary>
/// Listing, restoring and emptying the Recycle Bin.
///
/// **Returning null for this was the honest answer for one release and is no
/// longer.** docs/history/WINDOWS.md recorded the Recycle Bin as needing COM, and COM under
/// NativeAOT as the risky combination that would fail at runtime rather than at
/// compile time. That was an assumption, and it was wrong: a source-generated
/// IShellItem enumeration of the bin runs correctly in a published AOT binary.
/// Measuring it also showed the shell was not the right tool here anyway — see
/// <see cref="RecycleBin"/> for why the metadata format wins on the three
/// things this interface actually asks for.
///
/// The semantics are XdgTrashMaintenance's, deliberately. Both systems keep a
/// payload and a sidecar, so "restore alongside rather than clobber, and say
/// where it landed" means the same thing on each.
/// </summary>
public sealed class WindowsTrashMaintenance : ITrashMaintenance
{
    public bool HasAny() => RecycleBin.HasAny();

    /// <summary>
    /// The <c>$I</c> paths, straight off the directory entries. A trash key here
    /// IS the metadata path, so no part of a key is guessed at to make this
    /// fast — but the answer is a SUPERSET of the keys <see cref="List"/>
    /// reports, because an <c>$I</c> whose payload has gone still has a name.
    /// See <see cref="RecycleBin.Names"/> for the measurement, and for why a
    /// difference across a recycle wants the wider answer.
    /// </summary>
    public IEnumerable<string> Keys() => RecycleBin.Names();

    /// <summary>One <c>$I</c> file read, rather than every one in every bin.</summary>
    public string? OriginalPathOf(string key) => RecycleBin.Read(key)?.OriginalPath;

    public IReadOnlyList<TrashedItem> List()
        => RecycleBin.List()
            .Select(e => new TrashedItem(
                // The metadata path is the key, not the bare filename: a
                // volume's bin can hold $IABC123.txt while another holds the
                // same name, and Restore has to know which one it was handed.
                TrashName: e.InfoPath,
                OriginalPath: e.OriginalPath,
                Payload: e.PayloadPath,
                Deleted: e.Deleted,
                Size: e.Size,
                IsDirectory: e.IsDirectory))
            .ToList();

    /// <summary>
    /// One item, gone for good.
    ///
    /// Through the same Read and Purge that emptying uses, so a read-only tree
    /// is cleared before the delete rather than half-destroyed — the fault
    /// Purge's own comment describes at length, and one that would arrive here
    /// too through any second route.
    ///
    /// An item that is no longer there is not an error: the bin is shared with
    /// every other program on the machine, so between the click and the delete
    /// somebody else may have taken it.
    /// </summary>
    /// <remarks>
    /// **A delete that did not happen was reported as one that did.** Purge
    /// answers false when it could not remove the item — locked, or a folder
    /// holding a name the tree delete refuses — and this dropped the answer, so
    /// "Delete for good" said "deleted 1 item(s) for good" over an item still
    /// in the bin (seventh round's hunt). Thrown, so the pane counts it failed.
    /// </remarks>
    public void Delete(string trashName)
    {
        if (RecycleBin.Read(trashName) is { } entry && !Purge(entry))
            throw new IOException(
                $"\"{PathRules.LeafName(entry.OriginalPath)}\" could not be deleted for good; it is still in the Recycle Bin.");
    }

    /// <summary>
    /// Puts one item back, and answers with where it actually went.
    ///
    /// The order matters and is the same as the Linux side's: move first, drop
    /// the metadata second. A crash between them leaves an orphaned $I file,
    /// which lists as nothing and is harmless. The reverse order would lose the
    /// only record of where the payload belonged while the payload still
    /// existed — recoverable bytes with no memory of their home.
    /// </summary>
    public string Restore(string trashName)
    {
        var entry = RecycleBin.Read(trashName)
            ?? throw new FileNotFoundException("Nothing in the Recycle Bin for " + trashName);

        var target = entry.OriginalPath;

        // Something has taken the name back since. Restore beside it rather
        // than over it: the file being restored is the one the user asked for,
        // and the one in the way is one they may not know is there.
        if (Occupied(target))
            target = Deduplicate(target, entry.IsDirectory);

        // **Landed through the spelling that keeps its name.** A plain
        // "…\report " is written as "…\report" — Win32 folds the target as it
        // folds a source — so every check and the move itself go through
        // "\\?\". The path handed back is the one the listing will show.
        var landing = Exact(target);

        var parent = Path.GetDirectoryName(landing);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        // **Both ends in one spelling.** Directory.Move compares the two roots
        // as written, so a plain "$R" payload moved to "\\?\…\album " threw
        // "Source and destination path must have identical roots" and the
        // folder stayed in the bin (fix-8 verification) — and a payload read
        // through "\\?\" (RecycleBin.Read) is the same mismatch the other way
        // round. When either side is extended, both are.
        var extended = IsExtended(landing) || IsExtended(entry.PayloadPath);
        var from = extended ? ReachablePath.Extended(entry.PayloadPath) ?? entry.PayloadPath : entry.PayloadPath;
        var to = extended ? ReachablePath.Extended(landing) ?? landing : landing;

        if (entry.IsDirectory) Directory.Move(from, to);
        else File.Move(from, to);

        try { File.Delete(entry.InfoPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The payload is home, which is what was asked for. A metadata file
            // left behind lists as nothing, because Read requires its payload.
            Quiet.Swallowed("trash", e);
        }

        return target;
    }

    /// <summary>
    /// The spelling a restore reads and writes <paramref name="path"/> through:
    /// its own when nothing in it folds, "\\?\" when something does, and a
    /// refusal naming it when it has neither.
    /// </summary>
    private static string Exact(string path)
        => ReachablePath.Exact(path) ?? throw new IOException(ReachablePath.Refuse(path) ?? path);

    private static bool IsExtended(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal);

    /// <summary>Whether anything already holds this name, asked of the name
    /// itself rather than of what Win32 would fold it to.</summary>
    private static bool Occupied(string path)
    {
        var exact = Exact(path);

        return File.Exists(exact) || Directory.Exists(exact);
    }

    /// <summary>
    /// "notes.txt" becomes "notes (1).txt" — Windows' own phrasing for the same
    /// situation, and the same rule WindowsFileOperations uses when a copy
    /// collides. A restore should not invent a naming convention of its own.
    /// </summary>
    private static string Deduplicate(string path, bool isDirectory)
    {
        var directory = PathRules.Parent(path) ?? "";

        // **The same split the copy path uses.** This one did not know about
        // folders or dotfiles, so restoring a second copy of `my.project` gave
        // `my (1).project`, and a second `.bashrc` gave ` (1).bashrc` - a name
        // with nothing before the suffix at all.
        var (stem, extension) = PathRules.SplitLeaf(PathRules.LeafName(path), isDirectory);

        for (var n = 1; n < 10_000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!Occupied(candidate)) return candidate;
        }

        throw new IOException("Could not find a free name beside " + path);
    }

    /// <summary>
    /// Applies the policy, and does nothing when neither half of it is on. The
    /// disabled state is not "sweep with defaults" — this deletes files with
    /// nobody watching, so it acts only when asked.
    /// </summary>
    public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
    {
        // The "nothing to do" answer stays synchronous — it touches no disk,
        // and the hourly timer asks it far more often than it acts.
        if (!policy.DeleteOldFiles && !policy.LimitSize)
            return ValueTask.FromResult(TrashSweepResult.Nothing);

        // Everything past here reads metadata files and deletes payloads, on a
        // timer, with the window in front of somebody. Same shape as the Linux
        // twin's SweepAsync.
        return new(Task.Run(() => Sweep(policy, ct), ct));
    }

    private static TrashSweepResult Sweep(TrashSettings policy, CancellationToken ct)
        => Sweep(
            policy,
            SweptBins(RecycleBin.Directories(), SystemRoot()),
            policy.LimitSize ? Allowance(policy.MaximumPercentOfDisk) : 0,
            DateTimeOffset.UtcNow,
            ct);

    /// <summary>
    /// The sweep itself, over the bins it is handed and nothing else, so a
    /// test can run it on "$I"/"$R" pairs in folders of its own rather than on
    /// anybody's Recycle Bin.
    /// </summary>
    internal static TrashSweepResult Sweep(
        TrashSettings policy, IReadOnlyList<string> bins, long allowance, DateTimeOffset now, CancellationToken ct)
    {
        List<RecycleEntry> Listed()
            => bins.SelectMany(RecycleBin.InfoFiles).Select(RecycleBin.Read).OfType<RecycleEntry>().ToList();

        var removed = 0;
        long freed = 0;

        if (AgeCutoff(policy, now) is { } cutoff)
        {
            foreach (var entry in Listed().Where(e => e.Deleted < cutoff))
            {
                ct.ThrowIfCancellationRequested();
                if (Purge(entry)) { removed++; freed += entry.Size; }
            }
        }

        // **Reported, not enforced, unless the policy says otherwise.** Over the
        // allowance the choice is the user's: Warn hands the fact back and
        // deletes nothing.
        var overLimit = false;

        if (policy.LimitSize)
        {
            var entries = Listed();
            var total = entries.Sum(e => e.Size);

            if (allowance > 0 && total > allowance)
            {
                if (policy.WhenLimitReached == TrashLimitAction.Warn) overLimit = true;
                else
                {
                    // **The largest first when that is what was picked.** Every
                    // choice but Warn went oldest-first here, so "Delete the
                    // largest until it fits" took the oldest items instead —
                    // any number of small ones, before the one large file the
                    // setting names (0.11.1 changelog check). The Linux sweep
                    // has read the choice all along; this is its ordering.
                    // Oldest-first otherwise: deleting newest-first would take
                    // the thing most likely to be wanted back.
                    var queue = policy.WhenLimitReached == TrashLimitAction.DeleteLargest
                        ? entries.OrderByDescending(e => e.Size)
                        : entries.OrderBy(e => e.Deleted);

                    foreach (var entry in queue)
                    {
                        if (total <= allowance) break;
                        ct.ThrowIfCancellationRequested();

                        if (Purge(entry)) { removed++; freed += entry.Size; total -= entry.Size; }
                    }
                }
            }
        }

        return new TrashSweepResult
        {
            Removed = removed,
            BytesFreed = freed,
            OverLimit = overLimit,
        };
    }

    /// <summary>
    /// Anything deleted before this goes, or null when the age half of the
    /// policy is off.
    ///
    /// **Zero days swept everything older than a day.** A day count of zero
    /// or less was raised to one, so a hand-edited
    /// <c>"trash": {"deleteOldFiles": true}</c> with no number — which reads
    /// as zero — emptied the Recycle Bin of all but today's deletions, while
    /// the Linux sweep treated the same file as off. A number nobody typed is
    /// not a reason to delete files: off here too, and the disk-share half
    /// already was (<see cref="Allowance"/> answers zero). Separate from the
    /// sweep so a test can ask it without a Recycle Bin to empty.
    /// </summary>
    internal static DateTimeOffset? AgeCutoff(TrashSettings policy, DateTimeOffset now)
        => policy.DeleteOldFiles && policy.DeleteAfterDays > 0
            ? now.AddDays(-policy.DeleteAfterDays)
            : null;

    /// <summary>
    /// The bins a sweep may delete from: the one on the system drive, and no
    /// other.
    ///
    /// **Every drive's bin was swept**, although the page has said since the
    /// bin was first called the Recycle Bin that "files deleted from another
    /// drive live in a Recycle Bin on that drive and are not covered"
    /// (0.11.1 changelog check). The listing walks every drive, rightly — the
    /// bin view has to show all of it — and the sweep read that same listing,
    /// so a USB stick or a second disk was aged and trimmed with nobody
    /// watching, and its bytes were counted against a share of the SYSTEM
    /// drive. The page's promise is the Linux sweep's design, which stays in
    /// the home trash for the reason XdgTrashMaintenance gives: a drive that
    /// is not always there cannot have a policy applied to it consistently.
    /// Unattended deletion never reaches further than the page says.
    ///
    /// A bin is <c>&lt;drive root&gt;\$Recycle.Bin\&lt;sid&gt;</c>, so its
    /// drive is its grandparent — asked that way so a test can hand it bins in
    /// folders of its own.
    /// </summary>
    internal static IReadOnlyList<string> SweptBins(IEnumerable<string> bins, string? systemRoot)
        => string.IsNullOrEmpty(systemRoot)
            ? []
            : bins.Where(bin => string.Equals(
                    Path.GetDirectoryName(Path.GetDirectoryName(bin)), systemRoot,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

    /// <summary>The root of the drive Windows runs from — the drive whose bin
    /// a sweep covers, and whose size its allowance is a share of.</summary>
    private static string? SystemRoot()
        => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));

    /// <summary>The size the bin is allowed, as a share of the system volume.
    /// Zero — nothing is ever over it — for a share of zero or less.</summary>
    internal static long Allowance(int percent)
    {
        if (percent is <= 0 or > 100) return 0;

        try
        {
            var root = SystemRoot();
            if (string.IsNullOrEmpty(root)) return 0;

            return (long)(new DriveInfo(root).TotalSize * (percent / 100.0));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// **On a pool thread, as the Linux twin has always been.** This was
    /// ValueTask.FromResult around the whole walk, so every await of it
    /// completed synchronously and the deletion ran on the UI thread — a full
    /// bin froze the window, and the Dispatcher.InvokeAsync calls in
    /// PaneViewModel.EmptyTrashAsync could not do the job their comment claims
    /// because there was never a suspension for them to resume after.
    /// </summary>
    public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
        => new(Task.Run(() =>
        {
            var removed = 0;
            long freed = 0;

            foreach (var entry in RecycleBin.List())
            {
                ct.ThrowIfCancellationRequested();
                if (Purge(entry)) { removed++; freed += entry.Size; }
            }

            return new TrashSweepResult { Removed = removed, BytesFreed = freed };
        }, ct));

    /// <summary>
    /// Deletes one entry for good: payload first, then its metadata.
    ///
    /// **Not SHEmptyRecycleBin**, which would be one call for EmptyAsync and no
    /// use at all for SweepAsync — it empties everything or nothing, and a
    /// policy sweep is by definition selective. One code path for both means
    /// the dangerous one is the one that gets exercised every time.
    /// </summary>
    internal static bool Purge(RecycleEntry entry)
    {
        // **Through "\\?\", so the names inside are read as they are.** A
        // binned folder holding "x..." beside "x" could never be purged: the
        // tree delete refuses a tree it would read by the wrong names, so
        // Empty, the sweep and "Delete for good" all left it in the bin for
        // good (the hunt). The payload is the bin's own, all of it going,
        // and spelled this way the walk reaches every name as itself — which
        // is what DeleteTree's own note says such a tree needs.
        var payload = ReachablePath.Extended(entry.PayloadPath) ?? entry.PayloadPath;

        try
        {
            // **Before the delete, because a read-only file refuses to be
            // deleted and .NET removes what it can BEFORE throwing.** Recycle a
            // cloned git repository - git writes its pack files read-only - and
            // emptying the bin destroyed everything up to the first pack file,
            // then threw, leaving the $I metadata intact. The entry still
            // listed, still advertised its original size, and Restore handed
            // back a gutted folder. The application reported "removed 0".
            //
            // Worse unattended: the hourly sweep reaches this same method, so
            // with an age or size policy on, the half-destruction happened with
            // no user action at all. And a single read-only FILE could never be
            // purged, so Empty would never actually empty the bin.
            //
            // WindowsFileOperations.Delete has cleared the tree first since the
            // day the same fault was found there; this is that same routine,
            // not a second copy of it.
            WindowsFileOperations.ClearReadOnlyTree(payload);

            // **And through the same tree delete, because a junction inside the
            // payload gutted it in exactly the same way.** .NET's recursive
            // delete calls DeleteVolumeMountPoint on any child carrying a
            // junction's reparse tag, which fails for a junction whether or not
            // the caller is elevated; it removed the link, emptied the rest and
            // then threw without removing the payload folder. The $I entry was
            // left listed over a gutted $R, still advertising its original
            // size, and the sweep said "removed 0" - the most misleading answer
            // this class can give, and the one the comment below already names.
            // Recycling a checkout with a node_modules tree in it is enough.
            //
            // Not a second copy of that walk, for the reason the line above is
            // not a second copy of the read-only one.
            if (entry.IsDirectory) WindowsFileOperations.DeleteTree(payload);
            else File.Delete(payload);

            File.Delete(entry.InfoPath);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A locked item stays and the sweep carries on - failing the whole
            // sweep over one file would be worse. But said out loud rather than
            // filed under Quiet: by the time this is reached the payload may be
            // PARTLY deleted, and "removed 0" over a silently gutted item is the
            // most misleading answer this class can give. XdgTrashMaintenance
            // has printed for this case all along.
            Console.Error.WriteLine(
                $"[vaktari] trash: could not remove '{entry.PayloadPath}' - {e.Message.Trim()}"
                + " (what it held may now be incomplete)");

            return false;
        }
    }
}
