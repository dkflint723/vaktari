using System.Security.Cryptography;

namespace Vaktari.Core.FileSystem;

/// <summary>One set of files whose contents are the same, byte for byte.</summary>
public readonly record struct DuplicateSet(long Length, IReadOnlyList<string> Paths);

/// <summary>
/// What a scan found: the sets, and how much of the tree it could not read.
///
/// **The unreadable count is the honest half**, for the reason
/// <see cref="Usage"/> keeps one: a scan that stepped over a folder or a file
/// in silence answers "no duplicates here" about a tree it did not finish
/// reading, and that answer is indistinguishable from the true one.
/// </summary>
public readonly record struct DuplicateReport(IReadOnlyList<DuplicateSet> Sets, int Unreadable);

/// <summary>
/// Files with identical contents, under one folder.
///
/// **Same bytes, and nothing else.** Not the same name, not the same
/// timestamp: a copy is a copy whatever it was renamed to, and two files of
/// one name that differ are not one file.
/// <see cref="FileSameness"/> is deliberately not reused — it judges size plus
/// a two-second timestamp tolerance, which is the right question for comparing
/// two folders and the wrong one here, where it would call two different files
/// the same.
///
/// A third caller of <see cref="SafeWalk"/> rather than a user of
/// <see cref="SpaceUsage"/>: that answers "how big is this" and "what is under
/// each child", and neither is "every file below here, with its length".
/// </summary>
public static class DuplicateFinder
{
    /// <summary>
    /// The same size <see cref="Checksums"/> settled on, for the same reasons:
    /// big enough that syscall overhead disappears, small enough to stay out of
    /// the large object heap.
    /// </summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Every set of identical files under <paramref name="root"/>.
    ///
    /// <paramref name="progress"/> receives the number of files examined — the
    /// ones a length shared with something else, not every file walked, since
    /// the walk itself is the cheap part.
    /// </summary>
    public static DuplicateReport Find(
        string root, IProgress<int>? progress, CancellationToken ct)
    {
        var counters = new Counters();
        var byLength = new Dictionary<long, List<string>>();

        foreach (var found in SafeWalk.Descend(root, ct, _ => counters.Unreadable++))
        {
            if (found.IsDirectory) continue;

            // Empty files go because every one of them matches every other,
            // which answers a different question from the one anybody asked.
            //
            // **The link half is a GUARD and reddens nothing today.** The walk
            // reports every link with a length of zero, so the test beside it
            // already excludes one — measured: dropping `IsLink` left every
            // test green. It stays because what it guards is a data-safety
            // rule rather than a tidiness one. If the walk ever reported a
            // link's own size, links would start arriving as candidates and be
            // offered as copies of the very files they point at, and this line
            // is the difference between that being caught here and being found
            // by somebody deleting an original.
            if (found.IsLink || found.Length == 0) continue;

            if (!byLength.TryGetValue(found.Length, out var paths))
                byLength[found.Length] = paths = [];

            paths.Add(found.Path);
        }

        var sets = new List<DuplicateSet>();

        foreach (var (length, paths) in byLength)
        {
            // GUARD. Nothing below this opens a file, so the only stretch it
            // covers is the skipping of lengths nothing shares — fast, and
            // reached only after the walk, whose own check has already thrown
            // for a token cancelled before the call. Measured: removing it
            // reddens nothing. The check that matters is the one in the loop
            // that reads, below.
            ct.ThrowIfCancellationRequested();

            // A length nothing else shares is finished, unread. This is most
            // of a disk, and it is why the walk comes first.
            if (paths.Count < 2) continue;

            Partition(length, paths, sets, counters, progress, ct);
        }

        return new DuplicateReport(sets, counters.Unreadable);
    }

    /// <summary>
    /// One length's worth of candidates, split into sets of identical files.
    ///
    /// **The head block is where hashing earns its read.** A shared length
    /// usually means unrelated files — a folder of git objects, a node_modules
    /// tree — so one cheap read of the first block separates them without
    /// reading either file to its end.
    ///
    /// The confirmation afterwards is byte for byte, against one representative
    /// of each set: N−1 comparisons, each stopping at the first byte that
    /// differs. **A digest is never the last word here.** A collision is
    /// vanishingly unlikely and the cost of one is somebody deleting a file
    /// that was not a copy, so the cheap certain check is worth its read.
    /// </summary>
    private static void Partition(
        long length, List<string> paths, List<DuplicateSet> into,
        Counters counters, IProgress<int>? progress, CancellationToken ct)
    {
        var byHead = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            counters.Examined++;
            progress?.Report(counters.Examined);

            if (Head(path, ct) is not { } head)
            {
                counters.Unreadable++;
                continue;
            }

            if (!byHead.TryGetValue(head, out var sharing))
                byHead[head] = sharing = [];

            sharing.Add(path);
        }

        foreach (var sharing in byHead.Values)
        {
            if (sharing.Count < 2) continue;

            Confirm(length, sharing, into, counters, ct);
        }
    }

    /// <summary>
    /// Files that start alike, sorted into sets that really are alike.
    ///
    /// Each candidate is compared against the first member of each set already
    /// found, and starts a set of its own when it matches none of them — so a
    /// block of files sharing a head but differing later still comes out as the
    /// several sets it is.
    /// </summary>
    private static void Confirm(
        long length, List<string> sharing, List<DuplicateSet> into,
        Counters counters, CancellationToken ct)
    {
        var sets = new List<List<string>>();

        foreach (var path in sharing)
        {
            var placed = false;

            foreach (var set in sets)
            {
                var same = Same(set[0], path, ct);

                // Could not be read: counted, and left out of every set rather
                // than guessed into one.
                if (same is null)
                {
                    counters.Unreadable++;
                    placed = true;
                    break;
                }

                if (same is false) continue;

                set.Add(path);
                placed = true;
                break;
            }

            if (!placed) sets.Add([path]);
        }

        foreach (var set in sets)
            if (set.Count > 1)
                into.Add(new DuplicateSet(length, set));
    }

    /// <summary>
    /// The first block, hashed. Null when the file could not be read, which is
    /// counted rather than treated as a file that matches nothing.
    ///
    /// **One of the two checks that stop the reading.** This one after each
    /// file's head block, and <see cref="Same"/>'s inside its buffer loop,
    /// which is what makes comparing two enormous files stoppable part-way
    /// through one. Two loops above these used to check as well and covered
    /// nothing either of these did not — measured, by removing each in turn
    /// and finding every test still green. That is also why no single-line
    /// mutation reddens the cancellation test: whichever check is spoiled, the
    /// other still stops the walk. The test says cancelling works, which is
    /// the claim; it cannot say which line did it.
    /// </summary>
    private static string? Head(string path, CancellationToken ct)
    {
        try
        {
            using var stream = Open(path);

            var buffer = new byte[BufferSize];
            var read = stream.ReadAtLeast(buffer, BufferSize, throwOnEndOfStream: false);

            ct.ThrowIfCancellationRequested();

            return Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether two files hold the same bytes. Null when either could not be
    /// read — which is not the same answer as "different", and saying so is
    /// what keeps an unreadable file out of a set rather than in one.
    /// </summary>
    private static bool? Same(string a, string b, CancellationToken ct)
    {
        try
        {
            using var left = Open(a);
            using var right = Open(b);

            var mine = new byte[BufferSize];
            var theirs = new byte[BufferSize];

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var read = left.ReadAtLeast(mine, BufferSize, throwOnEndOfStream: false);
                var other = right.ReadAtLeast(theirs, BufferSize, throwOnEndOfStream: false);

                if (read != other) return false;
                if (read == 0) return true;

                if (!mine.AsSpan(0, read).SequenceEqual(theirs.AsSpan(0, other))) return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
               BufferSize, FileOptions.SequentialScan);

    /// <summary>Carried rather than returned, because the work that updates
    /// these runs inside two nested loops.</summary>
    private sealed class Counters
    {
        public int Unreadable;
        public int Examined;
    }
}
