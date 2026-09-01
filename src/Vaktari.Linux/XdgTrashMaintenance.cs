using System.Globalization;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;

namespace Vaktari.Linux;

/// <summary>
/// Expiry and size limits over the freedesktop trash.
///
/// This operates on the **shared** home trash at
/// <c>~/.local/share/Trash</c> — the same directory Dolphin, Gwenview and the
/// KDE file dialogs use. So it will delete things other applications put there.
/// That is deliberate and matches Dolphin, whose identical setting does the
/// same, and it is the only coherent reading of "keep the trash under N days":
/// a trash that expired only its own deposits would not stay under anything.
///
/// **It only covers the home trash.** Files deleted from another filesystem land
/// in a <c>.Trash-$uid</c> directory on that volume, which XdgTrash does not
/// implement and this therefore cannot sweep. A sweep is partial in exactly that
/// case, and saying so is better than appearing thorough.
/// </summary>
public sealed class XdgTrashMaintenance : ITrashMaintenance
{
    public async ValueTask<TrashSweepResult> SweepAsync(
        TrashSettings policy, CancellationToken ct)
    {
        // Disabled means disabled. Not "sweep with default numbers".
        if (!policy.DeleteOldFiles && !policy.LimitSize) return TrashSweepResult.Nothing;

        if (!Directory.Exists(XdgTrash.InfoDir)) return TrashSweepResult.Nothing;

        return await Task.Run(() => Sweep(policy, ct), ct).ConfigureAwait(false);
    }

    private static TrashSweepResult Sweep(TrashSettings policy, CancellationToken ct)
    {
        var entries = new List<Entry>();
        var skipped = 0;

        foreach (var infoPath in Directory.EnumerateFiles(XdgTrash.InfoDir, "*.trashinfo"))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileNameWithoutExtension(infoPath);
            var payload = Path.Combine(XdgTrash.FilesDir, name);

            var deleted = ReadDeletionDate(infoPath);

            if (deleted is null)
            {
                // Cannot date it, so cannot judge it. Left alone.
                skipped++;
                continue;
            }

            entries.Add(new Entry(infoPath, payload, deleted.Value, SizeOf(payload)));
        }

        var removed = 0;
        var freed = 0L;

        // ---- age ------------------------------------------------------------

        if (policy.DeleteOldFiles && policy.DeleteAfterDays > 0)
        {
            var cutoff = DateTime.Now.AddDays(-policy.DeleteAfterDays);

            foreach (var entry in entries.Where(e => e.Deleted < cutoff).ToList())
            {
                ct.ThrowIfCancellationRequested();

                if (!Remove(entry)) continue;

                entries.Remove(entry);
                removed++;
                freed += entry.Size;
            }
        }

        // ---- size -----------------------------------------------------------

        if (!policy.LimitSize || policy.MaximumPercentOfDisk <= 0)
            return new TrashSweepResult { Removed = removed, BytesFreed = freed, Skipped = skipped };

        var allowance = Allowance(policy.MaximumPercentOfDisk);
        if (allowance <= 0) return new TrashSweepResult
        {
            Removed = removed, BytesFreed = freed, Skipped = skipped,
        };

        var total = entries.Sum(e => e.Size);
        if (total <= allowance) return new TrashSweepResult
        {
            Removed = removed, BytesFreed = freed, Skipped = skipped,
        };

        if (policy.WhenLimitReached == TrashLimitAction.Warn)
            return new TrashSweepResult
            {
                Removed = removed, BytesFreed = freed, Skipped = skipped, OverLimit = true,
            };

        // Oldest-first or largest-first, deleting only until back under the
        // allowance — not emptying the trash, which is a different action and
        // one the user never asked for.
        var queue = policy.WhenLimitReached == TrashLimitAction.DeleteLargest
            ? entries.OrderByDescending(e => e.Size).ToList()
            : entries.OrderBy(e => e.Deleted).ToList();

        foreach (var entry in queue)
        {
            if (total <= allowance) break;

            ct.ThrowIfCancellationRequested();

            if (!Remove(entry)) continue;

            total -= entry.Size;
            removed++;
            freed += entry.Size;
        }

        return new TrashSweepResult
        {
            Removed = removed,
            BytesFreed = freed,
            Skipped = skipped,
            OverLimit = total > allowance,
        };
    }

    public IReadOnlyList<TrashedItem> List()
    {
        var items = new List<TrashedItem>();

        if (!Directory.Exists(XdgTrash.InfoDir)) return items;

        foreach (var infoPath in Directory.EnumerateFiles(XdgTrash.InfoDir, "*.trashinfo"))
        {
            try
            {
                var trashName = Path.GetFileNameWithoutExtension(infoPath);
                var payload = Path.Combine(XdgTrash.FilesDir, trashName);

                // An info file whose payload is gone is an orphan. Listing it
                // would offer a restore that cannot succeed.
                var isDir = Directory.Exists(payload);
                if (!isDir && !File.Exists(payload)) continue;

                var original = XdgTrash.OriginalPathOf(infoPath);
                if (string.IsNullOrEmpty(original)) continue;

                items.Add(new TrashedItem(
                    trashName,
                    original,
                    payload,
                    ReadDeletionDate(infoPath) is { } d
                        ? new DateTimeOffset(d)
                        : DateTimeOffset.MinValue,
                    isDir ? 0 : SizeOf(payload),
                    isDir));
            }
            catch
            {
                // One unreadable entry must not empty the whole view.
            }
        }

        // Newest first, and an unparseable date sorts last rather than being
        // dropped — the sweep already refuses to DELETE those, so hiding them
        // here would make the one category you cannot clear invisible.
        items.Sort((a, b) => b.Deleted.CompareTo(a.Deleted));

        return items;
    }

    public string Restore(string trashName) => XdgTrash.Restore(trashName);

    public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
        => new(Task.Run(() =>
        {
            var removed = 0;
            long freed = 0;

            foreach (var item in List())
            {
                ct.ThrowIfCancellationRequested();

                var entry = new Entry(
                    Path.Combine(XdgTrash.InfoDir, item.TrashName + ".trashinfo"),
                    item.Payload, item.Deleted.DateTime, item.Size);

                if (!Remove(entry)) continue;

                removed++;
                freed += item.Size;
            }

            Console.Error.WriteLine(
                $"[vaktari] trash: emptied · removed {removed} · freed {ByteSize.Format(freed)}");

            return new TrashSweepResult { Removed = removed, BytesFreed = freed };
        }, ct));

    private sealed record Entry(string InfoPath, string Payload, DateTime Deleted, long Size);

    /// <summary>
    /// Both halves, payload first. An info file without its payload is an
    /// orphan the trash spec forbids; a payload without its info file is
    /// invisible to every trash viewer, which is worse.
    /// </summary>
    private static bool Remove(Entry entry)
    {
        try
        {
            if (Directory.Exists(entry.Payload)) Directory.Delete(entry.Payload, recursive: true);
            else if (File.Exists(entry.Payload)) File.Delete(entry.Payload);

            File.Delete(entry.InfoPath);
            return true;
        }
        catch (Exception ex)
        {
            // Permissions, a race with another trash viewer, a busy mount.
            // Skipping one entry is fine; failing the sweep is not.
            Console.Error.WriteLine(
                $"[vaktari] trash: could not remove {Path.GetFileName(entry.Payload)} — {ex.Message}");

            return false;
        }
    }

    private static DateTime? ReadDeletionDate(string infoPath)
    {
        try
        {
            foreach (var line in File.ReadLines(infoPath))
            {
                if (!line.StartsWith("DeletionDate=", StringComparison.Ordinal)) continue;

                // Local time, no offset — what the spec says and what XdgTrash
                // writes. Parsed exactly rather than leniently, so a malformed
                // date fails into "skip" instead of into some other year.
                return DateTime.TryParseExact(
                    line["DeletionDate=".Length..].Trim(),
                    "yyyy-MM-ddTHH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed)
                    ? parsed
                    : null;
            }
        }
        catch
        {
            // Unreadable is not old.
        }

        return null;
    }

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;

            if (!Directory.Exists(path)) return 0;

            var total = 0L;

            // Links reported and not followed: a trashed folder containing a
            // link to somewhere large would otherwise have that place counted
            // against the trash's allowance, and a link to an ancestor would
            // never finish being measured at all.
            foreach (var found in Vaktari.Core.FileSystem.SafeWalk.Descend(path))
            {
                if (found.IsDirectory || found.IsLink) continue;

                try { total += new FileInfo(found.Path).Length; }
                catch { /* vanished mid-walk */ }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Bytes the trash is allowed, as a share of the volume it sits on.</summary>
    private static long Allowance(int percentOfDisk)
    {
        try
        {
            // The volume the trash ACTUALLY sits on.
            //
            // This used to be `new DriveInfo(Path.GetPathRoot(TrashRoot))`, and
            // on Linux `GetPathRoot` returns "/" for every absolute path — so a
            // percentage of the disk was always measured against the ROOT
            // filesystem, even when the home directory is a separate partition.
            // On a small root and a large home that under-counts the allowance
            // wildly, and deleting against a number computed from the wrong
            // volume is exactly the class of mistake the rest of this file
            // guards against.
            if (MountFor(XdgTrash.TrashRoot) is not { } drive) return 0;

            return (long)(drive.TotalSize * (Math.Clamp(percentOfDisk, 1, 100) / 100.0));
        }
        catch
        {
            // Without a readable volume size there is no limit to enforce, and
            // guessing one would mean deleting against a number we invented.
            return 0;
        }
    }

    /// <summary>
    /// The mount point containing <paramref name="path"/> — the LONGEST mount
    /// whose root prefixes it, since "/" prefixes everything and would otherwise
    /// always win.
    /// </summary>
    private static DriveInfo? MountFor(string path)
    {
        DriveInfo? best = null;

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0) continue;

                var root = drive.RootDirectory.FullName;

                if (!path.StartsWith(root, StringComparison.Ordinal)) continue;

                if (best is null || root.Length > best.RootDirectory.FullName.Length)
                    best = drive;
            }
            catch
            {
                // Pseudo filesystems throw on TotalSize; they are never the
                // answer anyway.
            }
        }

        return best;
    }
}
