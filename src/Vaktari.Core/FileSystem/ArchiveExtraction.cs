namespace Vaktari.Core.FileSystem;

/// <summary>
/// A hook into the one writer, for tests: it is told before anything is
/// created and while a file is being written, and may plant a link or throw
/// exactly where a real disk would.
///
/// **In options, never in a static**, because xUnit runs test classes in
/// parallel and a static hook set by one would fire in another's extraction.
/// </summary>
internal interface IExtractionObserver
{
    /// <summary>A file or folder is about to be created at
    /// <paramref name="path"/>. Throwing here is a failure of that entry's own
    /// create.</summary>
    void BeforeCreate(string path);

    /// <summary>A file's bytes are in <paramref name="temporary"/>, and
    /// <paramref name="final"/> is where it will land.</summary>
    void WhileWriting(string temporary, string final);

    /// <summary>The finished result is about to be moved to
    /// <paramref name="target"/>, a name that was free a moment ago.</summary>
    void BeforeLanding(string target);
}

/// <summary>How one run is to land its entries.</summary>
/// <param name="WindowsRules">See <see cref="ArchiveNames.WindowsRulesFor"/>.</param>
/// <param name="SkipMacMetadata"><c>__MACOSX/</c> and <c>._*</c>: the
/// resource forks a Mac zips beside every file, which are noise anywhere
/// else. Skipped and counted.</param>
/// <param name="ZoneMark">The archive's own mark of the web, carried onto
/// every file, or null.</param>
/// <param name="MaxEntries">The entry cap; a parameter so a test can reach
/// it with ten entries rather than a million.</param>
internal sealed record ExtractionOptions(
    bool WindowsRules,
    bool SkipMacMetadata,
    string? ZoneMark,
    ArchiveRoom Room,
    IExtractionObserver? Observer,
    int MaxEntries = ArchiveLimits.MaxEntries);

/// <summary>What one run wrote, and what it left out.</summary>
internal sealed record ExtractionResult(int Files, int Folders, int Renamed, Archives.LeftOut LeftOut);

/// <summary>Bounds on one extraction.</summary>
internal static class ArchiveLimits
{
    /// <summary>Entries per archive (Core-25). Refused up front where the
    /// archive declares its count, stopped at the first entry over it where
    /// it does not.</summary>
    public const int MaxEntries = 1_000_000;

    /// <summary>
    /// Folders deep, per entry. **A 32 KB zip holding one entry of eight
    /// thousand <c>a/</c> segments stack-overflowed the process**, and two
    /// thousand took two minutes and 11.8 GB (review of Stage A). Nothing a
    /// person archives is 512 folders deep; an entry deeper is counted as
    /// unwritable before it is planned.
    /// </summary>
    public const int MaxDepth = 512;

    /// <summary>The longest full path an entry may land at: Windows' own
    /// limit in UTF-16 units, and Linux's PATH_MAX in bytes, each less a
    /// margin for the working folder's name.</summary>
    public static int MaxPath => OperatingSystem.IsWindows() ? 32_000 : 4_000;
}

/// <summary>
/// **The only code that writes bytes out of an archive.** Every rule about
/// where an entry may land and what it may become is enforced here, once, so
/// a later caller — the listing's copy-out, the Open cache, drag-out — cannot
/// forget one of them.
///
/// **Containment is by construction, not by checking a resolved path.** Every
/// segment of a landing path has come through <see cref="ArchiveNames.Land"/>,
/// so it holds no separator, is never <c>..</c> and, under Windows rules, has
/// no <c>:</c> — there is nothing left in it that could climb out. The
/// <see cref="PathRules.Contains"/> check before each create stays as a
/// backstop, and throws rather than skips: reaching it means this class is
/// wrong, not the archive.
///
/// **A link is never followed on the way down.** Every folder a file is
/// written into must be one this run created and must not be a reparse point
/// or a link, checked before every create and again before every final move
/// (Core-9) — something planted at a parent between the two is caught at the
/// second. Measured (E-40) at 10,000 files five folders deep: 827 ms of stats
/// on Windows NVMe, 82 ms on ext4, reading attributes alone. Asking for
/// <c>LinkTarget</c> as well doubled the Windows cost to 1.57 s and adds
/// nothing, because only a reparse point has a target.
///
/// **Links are never created, on either system.** A symbolic link from an
/// archive is the classic way to write outside the folder: the link lands
/// first, and a later entry is written THROUGH it. Links are counted instead;
/// a hard link to a file this run already landed becomes a copy of it.
///
/// **Never written over, and never written at its final name.** Each file is
/// written to a sibling working name with <c>CreateNew</c> and moved onto its
/// landing name with <c>overwrite: false</c>, so a file that stops half-way
/// never sits at a name that looks finished.
///
/// **Failures are classified per entry (Core-5).** Something that goes wrong
/// creating or writing THIS entry's own file — a name a FAT stick refuses, a
/// case clash on a case-insensitive mount, a file too large for FAT32 — costs
/// that entry, which is counted, and the run goes on. Anything that goes wrong
/// READING the archive, a full disk, or a cancellation stops the run, and the
/// caller discards everything it wrote.
/// </summary>
internal static class ArchiveExtraction
{
    private const int BufferSize = 80 * 1024;

    public static ExtractionResult Run(
        ArchivePass pass, string root, ExtractionOptions options, OperationHandle? handle, CancellationToken token)
        => new Walk(pass, root, options, handle, token).Go();

    private sealed class Walk(
        ArchivePass pass, string root, ExtractionOptions options, OperationHandle? handle, CancellationToken token)
    {
        private readonly LandingPlanner _planner = new(options.WindowsRules);

        /// <summary>Folders this run created. Nothing else may be written
        /// into.</summary>
        private readonly HashSet<string> _created = new(PathRules.Comparer) { root };

        /// <summary>Where each file landed, by its raw key's segments — for
        /// hard links, which name their target the way the archive does.</summary>
        private readonly Dictionary<string, string> _landed = new(StringComparer.Ordinal);

        /// <summary>Where each folder node this run created landed.</summary>
        private readonly Dictionary<LandingPlanner.Node, string> _paths = [];

        private readonly List<(string Path, DateTimeOffset When)> _folderTimes = [];
        private readonly List<(string Path, int Mode)> _folderModes = [];

        private int _files, _folders, _items, _expected;
        private int _unsafe, _links, _special, _mac, _unwritable;
        private long _sinceFloorCheck;
        private long _compressedSeen;
        private long _declaredReported;

        public ExtractionResult Go()
        {
            foreach (var item in pass.Items())
            {
                token.ThrowIfCancellationRequested();
                handle?.WaitIfPaused();

                if (++_items > options.MaxEntries)
                    throw new ArchiveRefusedException(ArchiveSentences.TooMany(pass.Leaf, options.MaxEntries));

                Land(item);

                ReportCompressedProgress();
            }

            FinishFolders();

            // The rest of a stream the pass never needed to read — a tar's
            // end blocks — so the bar ends at the end.
            if (pass.DeclaredTotal is null && handle is not null && pass.ArchiveLength > _compressedSeen)
            {
                handle.BytesCopied(pass.ArchiveLength - _compressedSeen);
                _compressedSeen = pass.ArchiveLength;
            }

            return new ExtractionResult(
                _files, _folders, _planner.Renamed,
                new Archives.LeftOut(_unsafe, _links, _special, _mac, _unwritable));
        }

        /// <summary>
        /// One entry, and what the bar hears about it.
        ///
        /// **Progress counts the way items finish.** The total was every
        /// entry, folders included, and only written files counted as done,
        /// so a clean zip ended at "4/7"; a tar began at "4/0"; and a link or
        /// an unwritable entry's bytes were never counted, so the bar never
        /// reached the end (review of Stage A). Every entry that is not a
        /// folder now finishes — written or left out — and whatever of its
        /// declared size was not written is counted when it does.
        /// </summary>
        private void Land(ArchiveItem item)
        {
            var info = item.Info;
            var folder = info.Kind == ArchiveEntryKind.Folder
                         || (info.Kind == ArchiveEntryKind.File && info.RawKey.Length > 0 && info.RawKey[^1] is '/' or '\\');
            var reportedBefore = _declaredReported;

            if (!folder && pass.DeclaredItems is null) handle?.ItemsExpected(++_expected);

            LandCore(item);

            if (folder) return;

            if (pass.DeclaredTotal is not null && info.Size is { } size && size > _declaredReported - reportedBefore)
            {
                var rest = size - (_declaredReported - reportedBefore);

                _declaredReported += rest;
                handle?.BytesCopied(rest);
            }

            handle?.ItemFinished();
        }

        private void LandCore(ArchiveItem item)
        {
            var info = item.Info;

            if (ArchiveKeys.Split(info.RawKey, pass.Format, out var keyIsFolder) is not { } segments)
            {
                _unsafe++;
                return;
            }

            // Before anything is planned: the planner's own work grows with
            // the depth, and so did the stack before Ensure was a loop.
            if (segments.Length > ArchiveLimits.MaxDepth || TooLong(segments))
            {
                _unwritable++;
                return;
            }

            if (options.SkipMacMetadata
                && (segments[0] == "__MACOSX" || segments[^1].StartsWith("._", StringComparison.Ordinal)))
            {
                _mac++;
                return;
            }

            switch (info.Kind)
            {
                case ArchiveEntryKind.SymbolicLink:
                    _links++;
                    return;

                case ArchiveEntryKind.Special:
                    _special++;
                    return;

                case ArchiveEntryKind.Folder:
                    EntryLevel(() => LandFolder(segments, info));
                    return;

                case ArchiveEntryKind.HardLink:
                    EntryLevel(() => LandHardLink(segments, info));
                    return;

                default:
                    if (keyIsFolder) goto case ArchiveEntryKind.Folder;

                    EntryLevel(() => LandFile(segments, info, item));
                    return;
            }
        }

        /// <summary>
        /// Runs one entry's own work, and counts it as unwritable when the
        /// disk refused THAT entry — anything else propagates and stops the
        /// run. The order of the catches is the classification: the reader's
        /// failures and cancellation first, disk full next, and only then an
        /// IO failure that belongs to this entry alone.
        /// </summary>
        private void EntryLevel(Action land)
        {
            try
            {
                land();
            }
            catch (Exception e) when (e is OperationCanceledException or ArchiveDamagedException
                                          or ArchiveRefusedException or ArchivePasswordRequiredException
                                          or ArchiveUnreadableException)
            {
                throw;
            }
            catch (IOException e) when (Failures.IsDiskFull(e))
            {
                throw;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Quiet.Swallowed("extract", e);
                _unwritable++;
            }
        }

        private void LandFolder(string[] segments, ArchiveEntryInfo info)
        {
            if (_planner.FolderNode(segments) is not { } node)
            {
                _unsafe++;
                return;
            }

            var path = Ensure(node);

            if (info.Modified is { } when) _folderTimes.Add((path, when));
            if (info.UnixMode is { } mode) _folderModes.Add((path, mode));
        }

        private void LandFile(string[] segments, ArchiveEntryInfo info, ArchiveItem item)
        {
            if (_planner.FileNode(segments) is not { } node)
            {
                _unsafe++;
                return;
            }

            if (info.Size > ArchiveRoom.Fat32Limit && options.Room.IsFat32(root))
            {
                _unwritable++;
                return;
            }

            using var data = item.OpenData();

            Write(node, info, data, Key(segments));
        }

        /// <summary>
        /// A hard link, as a copy of the file it names — which must be one
        /// this run has already landed. Everything is wanted in Extract all,
        /// so a target that has not landed is not coming.
        /// </summary>
        private void LandHardLink(string[] segments, ArchiveEntryInfo info)
        {
            if (info.LinkTarget is null
                || ArchiveKeys.Split(info.LinkTarget, pass.Format, out _) is not { } target
                || !_landed.TryGetValue(Key(target), out var source)
                || _planner.FileNode(segments) is not { } node)
            {
                _links++;
                return;
            }

            using var data = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);

            Write(node, info with { Size = data.Length, Crc = null, CrcIsOurs = false }, data, Key(segments));
        }

        private void Write(LandingPlanner.Node node, ArchiveEntryInfo info, Stream data, string key)
        {
            var parent = Ensure(node.Parent!);
            var final = Path.Combine(parent, node.Name);
            var temporary = Path.Combine(parent, ".vaktari-x-" + Guid.NewGuid().ToString("N")[..12]);

            Backstop(final);

            RoomFor(info);

            options.Observer?.BeforeCreate(final);

            if (!ChainIsOurs(parent))
            {
                _unwritable++;
                return;
            }

            try
            {
                handle?.ItemStarted(final);

                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    Copy(data, target, info);

                options.Observer?.WhileWriting(temporary, final);

                // **The mark, then the time, then the mode** — see ZoneMarks
                // for why the mark cannot come later.
                if (options.ZoneMark is { } mark) ZoneMarks.Apply(temporary, mark);

                if (info.Modified is { } when && Settable(when)) File.SetLastWriteTimeUtc(temporary, when.UtcDateTime);

                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, ModeFor(info.UnixMode));

                if (!ChainIsOurs(parent))
                {
                    _unwritable++;
                    return;
                }

                final = MoveIntoPlace(node, parent, temporary);
            }
            finally
            {
                if (File.Exists(temporary)) TryDelete(temporary);
            }

            _landed[key] = final;
            _files++;
        }

        /// <summary>
        /// **For a format that declares no total, each entry's own size is
        /// asked about before it is written**: a tar declares every entry's
        /// size, so the running floor below — which used to run only for
        /// entries with no size at all — never ran for tar, and a 3 MB
        /// tar.gz holding 300 MB extracted in full onto a disk the room check
        /// said had 100 MB (review of Stage A).
        /// </summary>
        private void RoomFor(ArchiveEntryInfo info)
        {
            if (pass.DeclaredTotal is null
                && info.Size is { } size
                && options.Room.FreeBytes(root) is { } free
                && size + ArchiveRoom.StreamFloor > free)
                throw new ArchiveRefusedException(ArchiveSentences.Floor(pass.Leaf, ArchiveRoom.Drive(root)));
        }

        /// <summary>
        /// A time a file can carry. **Windows cannot date anything before
        /// 1601**, and a tar is free to say so: a PAX time of −12,000,000,000
        /// seconds threw ArgumentOutOfRangeException from
        /// SetLastWriteTimeUtc, which no catch expected, and the whole
        /// extraction failed in the runtime's words (review of Stage A). Such
        /// a file keeps the time it was written at.
        /// </summary>
        internal static bool Settable(DateTimeOffset when) => when.UtcDateTime >= Earliest;

        private static readonly DateTime Earliest = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private bool TooLong(string[] segments)
        {
            long length = OperatingSystem.IsWindows() ? root.Length : System.Text.Encoding.UTF8.GetByteCount(root);

            foreach (var segment in segments)
                length += 1 + (OperatingSystem.IsWindows() ? segment.Length : System.Text.Encoding.UTF8.GetByteCount(segment));

            return length > ArchiveLimits.MaxPath;
        }

        /// <summary>
        /// The copy, with every rule a byte has to pass on the way.
        ///
        /// **Stopped at the declared size, as it is written** — an archive is
        /// free to declare ten bytes and deliver a gigabyte, so the header is
        /// a first filter and this is the one that holds. More than declared,
        /// or less, is damage.
        /// </summary>
        private void Copy(Stream from, Stream to, ArchiveEntryInfo info)
        {
            var buffer = new byte[BufferSize];
            var crc = info.CrcIsOurs && info.Crc is not null ? new SharpCompress.Compressors.Deflate.CRC32() : null;
            long total = 0;
            int read;

            while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                handle?.WaitIfPaused();

                total += read;

                if (info.Size is { } declared && total > declared)
                    throw Damaged();

                crc?.SlurpBlock(buffer, 0, read);

                to.Write(buffer, 0, read);

                if (pass.DeclaredTotal is not null)
                {
                    _declaredReported += read;
                    handle?.BytesCopied(read);
                }
                else
                {
                    ReportCompressedProgress();
                }

                if (pass.DeclaredTotal is null && (_sinceFloorCheck += read) >= ArchiveRoom.FloorInterval)
                {
                    _sinceFloorCheck = 0;

                    if (options.Room.BelowFloor(root))
                        throw new ArchiveRefusedException(ArchiveSentences.Floor(pass.Leaf, ArchiveRoom.Drive(root)));
                }
            }

            if (info.Size is { } expected && total != expected) throw Damaged();

            if (crc is not null && (uint)crc.Crc32Result != info.Crc) throw Damaged();
        }

        private ArchiveDamagedException Damaged()
            => new(ArchiveSentences.DamagedAfter(pass.Leaf, _files), _files);

        /// <summary>
        /// Onto the landing name without replacing anything. A name that turns
        /// out to be taken — an 8.3 alias of a sibling, a folder that is case
        /// sensitive — is numbered now and counted as renamed (Core-24).
        /// </summary>
        private string MoveIntoPlace(LandingPlanner.Node node, string parent, string temporary)
        {
            while (true)
            {
                var final = Path.Combine(parent, node.Name);

                Backstop(final);

                try
                {
                    File.Move(temporary, final, overwrite: false);
                    return final;
                }
                catch (IOException) when (File.Exists(final) || Directory.Exists(final))
                {
                    _planner.Renumber(node, name => Taken(Path.Combine(parent, name)));
                }
            }
        }

        /// <summary>
        /// The folder a node lands as, created along with its ancestors. A
        /// folder already on disk that this run did not create is a
        /// collision, not a merge, and the node is renumbered.
        ///
        /// **A loop, from the deepest folder that exists down**, where it was
        /// a recursion that climbed to the root before creating anything —
        /// one stack frame per segment, which is what overflowed. And each new
        /// folder's PARENT is checked, once, rather than the whole chain above
        /// it: that chain was checked as each of its folders was made, and
        /// walking it again for every folder made a deep entry quadratic. The
        /// whole chain is still checked before every FILE is created and
        /// moved, which is where a swapped-in link would be written through.
        /// </summary>
        private string Ensure(LandingPlanner.Node node)
        {
            var chain = new List<LandingPlanner.Node>();
            var at = node;

            for (; at.Parent is not null && !_paths.ContainsKey(at); at = at.Parent) chain.Add(at);

            var parent = at.Parent is null ? root : _paths[at];

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();

                var next = chain[i];

                while (true)
                {
                    var path = Path.Combine(parent, next.Name);

                    Backstop(path);

                    if (Taken(path))
                    {
                        var within = parent;
                        _planner.Renumber(next, name => Taken(Path.Combine(within, name)));
                        continue;
                    }

                    options.Observer?.BeforeCreate(path);

                    if (!IsOurs(parent)) throw new UnauthorizedAccessException("a folder on the way is not one this extraction made");

                    Directory.CreateDirectory(path);

                    _created.Add(path);
                    _paths[next] = path;
                    _folders++;

                    parent = path;
                    break;
                }
            }

            return node.Parent is null ? root : _paths[node];
        }

        /// <summary>One folder: made by this run, and not a link.</summary>
        private bool IsOurs(string folder)
        {
            if (!_created.Contains(folder)) return false;

            try
            {
                return (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Every folder from <paramref name="folder"/> up to the root is one
        /// this run created, and none of them is a link or a junction.
        /// </summary>
        private bool ChainIsOurs(string folder)
        {
            for (var at = folder; ; at = Path.GetDirectoryName(at)!)
            {
                token.ThrowIfCancellationRequested();

                if (!_created.Contains(at)) return false;

                try
                {
                    if ((File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0) return false;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return false;
                }

                if (PathRules.Same(at, root)) return true;
            }
        }

        /// <summary>Unreachable while <see cref="ArchiveNames.Land"/> is
        /// right; a throw, so a mistake there fails loudly instead of writing
        /// somewhere else.</summary>
        private void Backstop(string path)
        {
            if (!PathRules.Contains(root, path) || PathRules.Same(root, path))
                throw new InvalidOperationException($"refusing to write outside the extraction: {path}");
        }

        /// <summary>
        /// Folder times, and folder modes on Linux, deepest first and after
        /// everything inside them: writing a file into a folder changes its
        /// time, and a folder made read-only first could not be written into.
        /// </summary>
        private void FinishFolders()
        {
            foreach (var (path, when) in _folderTimes.OrderByDescending(f => f.Path.Length))
            {
                try { if (Settable(when)) Directory.SetLastWriteTimeUtc(path, when.UtcDateTime); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                {
                    Quiet.Swallowed("extract", e);
                }
            }

            if (OperatingSystem.IsWindows()) return;

            foreach (var (path, mode) in _folderModes.OrderByDescending(f => f.Path.Length))
            {
                try { File.SetUnixFileMode(path, (UnixFileMode)((mode & 0x1ED) | 0x1C0)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Quiet.Swallowed("extract", e);
                }
            }
        }

        /// <summary>
        /// A file's mode on Linux: the permission bits, less group and other
        /// write, and never setuid, setgid or sticky. 0644 when the archive
        /// does not say — and **always readable and writable by its owner**:
        /// a zip from a Unix host with an external mode of 0 landed as
        /// <c>----------</c>, a file its owner could not open (review of
        /// Stage A). Nothing an archive says makes a file unreadable.
        /// </summary>
        internal static UnixFileMode ModeFor(int? mode)
        {
            var bits = (mode ?? 0) & 0x1FF;

            if (bits == 0) bits = 0x1A4;

            return (UnixFileMode)((bits & ~0x12) | 0x180);
        }

        /// <summary>Progress in compressed bytes, for formats that declare no
        /// sizes: the bar then measures how far through the ARCHIVE the run
        /// is, which is the one total known.</summary>
        private void ReportCompressedProgress()
        {
            if (pass.DeclaredTotal is not null || handle is null) return;

            var now = pass.CompressedBytesRead;
            var delta = now - _compressedSeen;

            if (delta <= 0) return;

            _compressedSeen = now;
            handle.BytesCopied(delta);
        }

        private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path);

        private static string Key(string[] segments) => string.Join('/', segments);

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Quiet.Swallowed("extract", e); }
        }
    }
}
