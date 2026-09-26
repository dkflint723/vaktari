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

        private readonly List<(string Path, DateTimeOffset When)> _folderTimes = [];
        private readonly List<(string Path, int Mode)> _folderModes = [];

        private int _files, _folders, _items;
        private int _unsafe, _links, _special, _mac, _unwritable;
        private long _sinceFloorCheck;
        private long _compressedSeen;

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

            return new ExtractionResult(
                _files, _folders, _planner.Renamed,
                new Archives.LeftOut(_unsafe, _links, _special, _mac, _unwritable));
        }

        private void Land(ArchiveItem item)
        {
            var info = item.Info;

            if (ArchiveKeys.Split(info.RawKey, pass.Format, out var keyIsFolder) is not { } segments)
            {
                _unsafe++;
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
                                          or ArchiveRefusedException or ArchivePasswordRequiredException)
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

                if (info.Modified is { } when) File.SetLastWriteTimeUtc(temporary, when.UtcDateTime);

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

            handle?.ItemFinished();
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

                if (pass.DeclaredTotal is not null) handle?.BytesCopied(read);
                else ReportCompressedProgress();

                if (info.Size is null && (_sinceFloorCheck += read) >= ArchiveRoom.FloorInterval)
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
        /// </summary>
        private string Ensure(LandingPlanner.Node node)
        {
            if (node.Parent is null) return root;

            var parent = Ensure(node.Parent);

            while (true)
            {
                var path = Path.Combine(parent, node.Name);

                if (_created.Contains(path)) return path;

                Backstop(path);

                if (Taken(path))
                {
                    _planner.Renumber(node, name => Taken(Path.Combine(parent, name)));
                    continue;
                }

                options.Observer?.BeforeCreate(path);

                if (!ChainIsOurs(parent)) throw new UnauthorizedAccessException("a folder on the way is not one this extraction made");

                Directory.CreateDirectory(path);

                _created.Add(path);
                _folders++;

                return path;
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
                try { Directory.SetLastWriteTimeUtc(path, when.UtcDateTime); }
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
        /// does not say.
        /// </summary>
        private static UnixFileMode ModeFor(int? mode) => (UnixFileMode)((mode ?? 0x1A4) & 0x1FF & ~0x12);

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
