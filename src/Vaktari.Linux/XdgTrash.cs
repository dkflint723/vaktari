using System.Globalization;
using System.Text;

using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// The freedesktop.org trash specification, which is what makes deleted files
/// show up in Dolphin's trash and be restorable from it. Rolling our own
/// "deleted" folder would work only inside this app, which is the wrong answer
/// for a file manager meant to sit alongside the rest of the desktop.
/// </summary>
public static partial class XdgTrash
{
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    public static string TrashRoot
    {
        get
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(dataHome))
                dataHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share");

            return Path.Combine(dataHome, "Trash");
        }
    }

    public static string FilesDir => Path.Combine(TrashRoot, "files");
    public static string InfoDir  => Path.Combine(TrashRoot, "info");

    /// <summary>
    /// Which trash a given path belongs in.
    ///
    /// **The home trash is only correct for the home volume.** Everything went
    /// there unconditionally, so deleting a twenty-gigabyte video off a USB
    /// stick COPIED twenty gigabytes onto the home partition — slowly, filling
    /// a disk the user was not deleting from. The entry then survived the stick
    /// being unplugged, and restoring it failed because the original path was
    /// gone. Two shipped strings already promised the behaviour that did not
    /// exist: the settings page says "Files deleted from another drive live in
    /// a trash on that drive".
    ///
    /// The spec puts it at the top of the volume the file lives on:
    /// <c>$topdir/.Trash/$uid</c> when the administrator has made a sticky
    /// <c>.Trash</c> there, and <c>$topdir/.Trash-$uid</c> otherwise — which is
    /// what Dolphin and Nautilus create, and what they read.
    /// </summary>
    internal static string RootFor(string path)
    {
        var home = TrashRoot;

        try
        {
            var mount = Volumes.MountFor(Path.GetFullPath(path));
            var homeMount = Volumes.MountFor(home);

            // Same volume as the home trash — including the ordinary case where
            // everything is one filesystem — means the home trash IS the right
            // answer, and no per-volume directory should be created.
            if (mount is null || homeMount is null
                || string.Equals(mount, homeMount, StringComparison.Ordinal))
                return home;

            return VolumeTrash(mount);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException)
        {
            // A volume that will not answer is not a reason to refuse to
            // delete: falling back to the home trash is the old behaviour, and
            // it works.
            Vaktari.Core.Quiet.Swallowed("trash", e);
            return home;
        }
    }

    /// <summary>
    /// The trash directory at the top of a volume, by the spec's two spellings.
    ///
    /// <c>$topdir/.Trash</c> is only trusted when it is a real directory with
    /// the sticky bit and is not a symbolic link — the spec is explicit, and
    /// the reason is that an unstickied or linked one is a way to have somebody
    /// else's files written somewhere they did not choose.
    /// </summary>
    internal static string VolumeTrash(string mount)
    {
        var uid = OperatingSystem.IsLinux() ? GetUid() : 0;

        var shared = Path.Combine(mount, ".Trash");

        try
        {
            if (Directory.Exists(shared)
                && (File.GetAttributes(shared) & FileAttributes.ReparsePoint) == 0
                && OperatingSystem.IsLinux()
                && File.GetUnixFileMode(shared).HasFlag(UnixFileMode.StickyBit))
                return Path.Combine(shared, uid.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("trash", e);
        }

        return Path.Combine(
            mount, ".Trash-" + uid.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The top of the volume a trash directory belongs to, or null for the home
    /// trash — which has no top directory to be relative to.
    ///
    /// Read off the root's own SHAPE rather than by comparing it with the home
    /// trash: the two spellings the spec allows are recognisable on sight, and
    /// a comparison would be wrong the moment either path crosses a symbolic
    /// link. $topdir/.Trash-$uid puts the top one level up; $topdir/.Trash/$uid
    /// puts it two.
    /// </summary>
    internal static string? TopDirOf(string root)
    {
        var name = Path.GetFileName(root.TrimEnd('/'));
        var parent = Path.GetDirectoryName(root.TrimEnd('/'));

        if (parent is null || name is null) return null;

        if (name.StartsWith(".Trash-", StringComparison.Ordinal)
            && name.AsSpan(7).Length > 0
            && !name.AsSpan(7).ContainsAnyExcept("0123456789"))
            return parent;

        if (!name.AsSpan().ContainsAnyExcept("0123456789")
            && name.Length > 0
            && Path.GetFileName(parent) == ".Trash")
            return Path.GetDirectoryName(parent);

        return null;
    }

    /// <summary>
    /// The trash to actually write into, having made sure it exists.
    ///
    /// **A volume whose top directory the user cannot write to had no trash at
    /// all.** RootFor is careful to fall back to the home trash when a volume
    /// will not say where it is mounted — and nothing guarded the very next
    /// step. Creating $topdir/.Trash-$uid needs write permission on the TOP of
    /// the volume, and plenty of mounts hand out a writable subtree under a
    /// root-owned top: a data mount, /srv, /opt on its own filesystem, a stick
    /// whose top belongs to root. Deleting a file from one of those threw
    /// straight out of CreateDirectory, so the file could not be deleted at all
    /// — while the user's own home trash was available the whole time, and is
    /// what the spec names as the fallback.
    ///
    /// Falling back is only ever a downgrade in tidiness, because the file
    /// crosses to the home volume, and it only ever happens where the
    /// alternative is not deleting the file.
    /// </summary>
    internal static string PrepareRoot(string preferred)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(preferred, "files"));
            Directory.CreateDirectory(Path.Combine(preferred, "info"));

            return preferred;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var home = TrashRoot;

            // The home trash IS the one that just failed: there is nowhere left
            // to fall back to, and swallowing it here would hide the real
            // reason from the per-item report the caller builds.
            if (string.Equals(preferred, home, StringComparison.Ordinal)) throw;

            Vaktari.Core.Quiet.Swallowed("trash", e);

            Directory.CreateDirectory(Path.Combine(home, "files"));
            Directory.CreateDirectory(Path.Combine(home, "info"));

            return home;
        }
    }

    /// <summary>
    /// Moves one item to the trash and returns its key there — the full path of
    /// its .trashinfo — so an undo can find exactly it again.
    ///
    /// **The key was the bare name, and a name is only unique in one trash.**
    /// A file deleted from the home folder and another of the same name
    /// deleted from a stick both became "notes.txt", one in each trash. Restore
    /// took the first trash holding the name, which is always the home one,
    /// so undoing the stick's delete brought back the other file; and deleting
    /// one of the two rows for good destroyed whichever the bin listed first.
    /// The info file's path names one item in one trash, which is what the
    /// Windows bin has always keyed on for the same reason.
    /// </summary>
    public static string Trash(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);

        // Before anything is created: the trash a mount point would be given
        // is INSIDE it. See IsMountPoint.
        if (IsMountPoint(full))
            throw new IOException(
                $"{Path.GetFileName(full)} is a mounted drive — unmount it rather than deleting it");

        // The trash on the volume the file lives on, so a delete is a rename
        // rather than a copy across devices — and so the entry stays with the
        // drive it came from.
        var root = PrepareRoot(RootFor(full));
        var filesDir = Path.Combine(root, "files");
        var infoDir = Path.Combine(root, "info");

        var name = ReserveName(Path.GetFileName(full), full, root);

        var destination = Path.Combine(filesDir, name);
        var infoPath = Path.Combine(infoDir, name + ".trashinfo");

        MoveIntoTrash(full, destination, infoPath);

        return infoPath;
    }

    /// <summary>The mount points, for <see cref="IsMountPoint"/>. Null in the
    /// application; a test that needs a folder to be one sets it.</summary>
    internal static Func<IReadOnlyList<string>>? MountPointsOverride { get; set; }

    /// <summary>
    /// Whether a path is itself where a volume is mounted.
    ///
    /// **Deleting one copied the volume into itself.** The trash for a path is
    /// the one at the top of its volume, and for a mount point that top is the
    /// mount point — so the trash chosen was inside the folder being deleted.
    /// The rename out failed across devices, the copy began, and it walked into
    /// its own destination and copied what it had copied, over and over, until
    /// the path was too long or the share was full; then the info file went,
    /// leaving the nested tree in no bin at all. Refused before anything is
    /// written: a mounted drive is unmounted, not binned.
    /// </summary>
    internal static bool IsMountPoint(string full)
    {
        // A link is moved as a link, whatever it points at.
        if (new FileInfo(full).LinkTarget is not null) return false;

        var points = MountPointsOverride?.Invoke()
                     ?? (OperatingSystem.IsLinux() ? Volumes.MountPoints() : []);

        // **Through the folder's real path.** The mount table holds real
        // paths, so a mount reached through a linked folder — ~/nas pointing
        // at /mnt, the share mounted at /mnt/share — was not found by the text
        // it was asked by, and was copied into itself as before.
        var real = Path.GetDirectoryName(full) is { } parent && FileIdentity.RealPath(parent) is { } resolved
            ? Path.Combine(resolved, Path.GetFileName(full))
            : full;

        static string Trimmed(string path) => path.Length > 1 ? path.TrimEnd('/') : path;

        if (points.Any(point =>
                string.Equals(Trimmed(point), Trimmed(full), StringComparison.Ordinal)
                || string.Equals(Trimmed(point), Trimmed(real), StringComparison.Ordinal)))
            return true;

        // And by device: a folder on another device than the one holding it
        // is where something is mounted, whatever any table says.
        return MountPointsOverride is null
               && FileIdentity.Of(full) is { } self
               && Path.GetDirectoryName(full) is { } above
               && FileIdentity.Of(above) is { } holder
               && self.Device != holder.Device;
    }

    /// <summary>
    /// Thrown when an item reached the bin whole but some of it could not be
    /// removed from where it was. The item is listed and can be restored — it
    /// carries its key — and the person is told what was left behind.
    /// </summary>
    public sealed class PartlyTrashedException(string key, string source, Exception inner)
        : IOException($"{Path.GetFileName(source)} is in the bin, but some of it could not be "
                      + $"removed from where it was: {inner.Message}", inner)
    {
        public string Key { get; } = key;
    }

    /// <summary>
    /// The move into the trash, and what its info file does when the move fails.
    ///
    /// **The info file went whenever anything failed, including when the move
    /// had already copied everything.** Across devices a folder is copied and
    /// then deleted; when the delete failed partway — one folder in the tree
    /// that could not be emptied — most of the original was already gone, the
    /// whole of it sat in the trash, and removing the info file hid that copy
    /// from every bin there is. Nothing lists a payload without its info file,
    /// and nothing sweeps it.
    ///
    /// So the info file goes only when nothing reached the trash, which is the
    /// case its removal was written for: a phantom entry pointing at nothing.
    /// When the payload is there, the entry stays, and what was left behind is
    /// said instead.
    /// </summary>
    internal static void MoveIntoTrash(string source, string destination, string infoPath)
    {
        // What the move is asked to carry, and whether anything stood where it
        // was going before it began — asked now, because afterwards the answer
        // is about what the failure left.
        var folder = Directory.Exists(source) && new FileInfo(source).LinkTarget is null;
        var stoodThere = Occupied(destination);

        try
        {
            MoveAcrossDevices(source, destination);
        }
        catch (Exception e)
        {
            // **Only what this move put there counts as arrived.** Something
            // already at the destination is not this item, whatever it is; nor
            // is the remains of a copy that failed before the original was
            // touched.
            if (stoodThere || !Occupied(destination) || e is NothingMovedException)
            {
                File.Delete(infoPath);

                if (e is NothingMovedException { InnerException: { } cause })
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cause);

                throw;
            }

            // A file or a link is whole in one place or the other: still at its
            // source, it did not go, and what reached the trash is a duplicate
            // — the copy a move across devices makes before it deletes, or the
            // link made in its place. Taken back, so the bin does not list a
            // file that never left.
            if (!folder && Occupied(source))
            {
                try { File.Delete(destination); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    Vaktari.Core.Quiet.Swallowed("trash", cleanup);
                }

                if (!Occupied(destination)) File.Delete(infoPath);
                throw;
            }

            if (e is IOException or UnauthorizedAccessException)
                throw new PartlyTrashedException(infoPath, source, e);

            throw;
        }
    }

    /// <summary>A move that failed before it removed anything from where the item was.</summary>
    private sealed class NothingMovedException(Exception inner) : IOException(inner.Message, inner);

    /// <summary>Whether anything at all is at a path: a file, a folder, or a link to nothing.</summary>
    private static bool Occupied(string path)
        => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    /// <summary>
    /// The trash an item's key names, and its name inside it.
    ///
    /// A key is the full path of the item's .trashinfo, so both come straight
    /// off it: the info file sits at $root/info/$name.trashinfo. A bare name —
    /// what keys used to be — cannot say which trash it means, so it is looked
    /// for in each trash in turn, home first, which is only right while no two
    /// trashes hold the name; nothing in Vaktari makes one any more.
    /// </summary>
    internal static (string Root, string Name) Locate(string key)
    {
        if (Path.IsPathRooted(key)
            && Path.GetDirectoryName(key) is { } infoDir
            && Path.GetDirectoryName(infoDir) is { } root)
            return (root, Path.GetFileNameWithoutExtension(key));

        var found = AllRoots().FirstOrDefault(r =>
            File.Exists(Path.Combine(r, "info", key + ".trashinfo")))
            ?? TrashRoot;

        return (found, key);
    }

    /// <summary>
    /// More trashes to list beside the home one, for the tests that need two
    /// trashes holding one name — which otherwise takes two mounted volumes.
    /// Null in the application.
    /// </summary>
    internal static Func<IEnumerable<string>>? ExtraRoots { get; set; }

    /// <summary>
    /// Every trash this user has on this machine: the home one, and one per
    /// mounted volume that has been deleted from.
    ///
    /// **The listing and the restore both need this.** They read the home trash
    /// only, so anything Dolphin had trashed onto a stick was invisible here —
    /// and once Vaktari started using per-volume trashes, its own deletions
    /// would have been too.
    /// </summary>
    internal static IEnumerable<string> AllRoots()
    {
        yield return TrashRoot;

        if (ExtraRoots is { } extra)
        {
            foreach (var root in extra()) yield return root;
        }

        var home = TrashRoot;
        string? homeMount = null;

        try { homeMount = Volumes.MountFor(home); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("trash", e);
        }

        foreach (var drive in Drives())
        {
            if (homeMount is not null
                && string.Equals(drive, homeMount, StringComparison.Ordinal))
                continue;

            var root = VolumeTrash(drive);

            // Only ones that exist: naming a trash on every mounted volume
            // would have the listing create directories on read-only media.
            if (Directory.Exists(root)) yield return root;
        }
    }

    private static IEnumerable<string> Drives()
    {
        DriveInfo[] drives;

        try { drives = DriveInfo.GetDrives(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("trash", e);
            yield break;
        }

        foreach (var drive in drives)
        {
            string? root = null;

            try { if (drive.IsReady) root = drive.RootDirectory.FullName; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Vaktari.Core.Quiet.Swallowed("trash", e);
            }

            if (root is not null) yield return root;
        }
    }

    /// <summary>
    /// Puts a trashed item back where it came from, from whichever trash holds
    /// it.
    /// </summary>
    public static string Restore(string key)
    {
        var (root, trashName) = Locate(key);

        var infoPath = Path.Combine(root, "info", trashName + ".trashinfo");
        var originalPath = ReadOriginalPath(infoPath)
            ?? throw new FileNotFoundException("No trash info for " + trashName);

        var source = Path.Combine(root, "files", trashName);
        var target = originalPath;

        // If something has taken the original name in the meantime, don't
        // clobber it — restore alongside instead.
        if (File.Exists(target) || Directory.Exists(target))
            target = Deduplicate(target, Directory.Exists(source));

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        MoveAcrossDevices(source, target);
        File.Delete(infoPath);

        return target;
    }

    /// <summary>
    /// Claims a name by creating its info file exclusively. The spec requires
    /// this ordering: two processes trashing "notes.txt" at the same moment
    /// must not both win the same slot.
    /// </summary>
    internal static string ReserveName(string preferred, string originalPath, string root)
    {
        var stem = Path.GetFileNameWithoutExtension(preferred);
        var ext = Path.GetExtension(preferred);
        var infoDir = Path.Combine(root, "info");

        for (var i = 0; i < 10_000; i++)
        {
            var candidate = i == 0 ? preferred : $"{stem}.{i}{ext}";
            var infoPath = Path.Combine(infoDir, candidate + ".trashinfo");

            // **Free in files/ as well, as the spec asks.** A payload with no
            // info file — another program's crash, or this one's before its
            // info file was kept — held the name, the move onto it failed, and
            // the failure read as a payload that had arrived: the stranger was
            // listed under this file's name and restored in its place.
            if (Occupied(Path.Combine(root, "files", candidate))) continue;

            try
            {
                using var stream = new FileStream(
                    infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));

                writer.WriteLine("[Trash Info]");
                writer.WriteLine("Path=" + EncodePath(RecordedPath(originalPath, root)));
                writer.WriteLine("DeletionDate=" + DateTime.Now.ToString(
                    "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));

                return candidate;
            }
            catch (IOException)
            {
                // Name taken; try the next.
            }
        }

        throw new IOException("Could not find a free name in the trash.");
    }

    /// <summary>
    /// Where a trashed item came from. Public because the trash LISTING needs
    /// it too — the payload directory has no memory of origins, so this sidecar
    /// is the only source, and a second parser would be a second thing to get
    /// wrong about URL decoding.
    /// </summary>
    public static string? OriginalPathOf(string infoPath) => ReadOriginalPath(infoPath);

    private static string? ReadOriginalPath(string infoPath)
    {
        if (!File.Exists(infoPath)) return null;

        foreach (var line in File.ReadLines(infoPath))
        {
            if (!line.StartsWith("Path=", StringComparison.Ordinal)) continue;

            var recorded = DecodePath(line[5..]);

            // Nothing recorded stays nothing. Path.Combine would answer the
            // volume's own top directory for an empty second part, which is a
            // real folder and the wrong one — a restore aimed at it would write
            // over the root of the drive.
            if (recorded.Length == 0) return recorded;

            // **A relative path is relative to the VOLUME, not to wherever this
            // process happens to be.** gvfs and Dolphin both write one for a
            // trash on a removable drive, so this is the ordinary case for
            // anything trashed on a stick by another file manager — and
            // returning it raw hands the restore a path resolved against the
            // working directory, which is somewhere in the user's home.
            //
            // The info file's own location says which volume: it sits at
            // $root/info/x.trashinfo, so the root is two levels up.
            var root = Path.GetDirectoryName(Path.GetDirectoryName(infoPath));

            // An absolute path needs no clause of its own: Path.Combine
            // discards everything before a rooted part, which is exactly the
            // rule wanted here — a trashinfo that already names the whole path
            // means it, and every file already in the home trash carries one.
            return root is not null && TopDirOf(root) is { } top
                ? Path.Combine(top, recorded)
                : recorded;
        }

        return null;
    }

    /// <summary>
    /// The path to write down: relative to the volume for a trash that lives on
    /// one, absolute for the home trash.
    ///
    /// **An absolute path on a removable volume records where the stick was
    /// mounted THAT time.** /run/media/me/USB today, /media/USB1 tomorrow, and
    /// a restore then puts the file back at a path on some other filesystem —
    /// or nowhere at all. The spec allows relative for exactly this reason, and
    /// it is what gvfs and Dolphin write, which is also why reading one has to
    /// work: a stick trashed from Nautilus and restored here goes through the
    /// same field.
    /// </summary>
    internal static string RecordedPath(string originalPath, string root)
    {
        if (TopDirOf(root) is not { } top) return originalPath;

        var relative = Path.GetRelativePath(top, originalPath);

        // Only when it really is inside the volume. GetRelativePath answers
        // with ".." rather than failing, and a path that climbs out of the top
        // directory is not one this volume's trash can describe.
        return relative.StartsWith("..", StringComparison.Ordinal)
               || Path.IsPathRooted(relative)
            ? originalPath
            : relative;
    }

    /// <summary>Percent-encoded per the spec, but with separators left intact.</summary>
    private static string EncodePath(string path)
        => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    private static string DecodePath(string encoded)
        => string.Join("/", encoded.Split('/').Select(Uri.UnescapeDataString));

    /// <summary>
    /// A free name beside <paramref name="path"/>.
    ///
    /// The kind matters: see PathRules.SplitLeaf. A folder called `my.photos`
    /// has no `.photos` extension to keep, and a dotfile is a name that starts
    /// with a dot rather than a bare extension - splitting either on the last
    /// dot produced `my (1).photos` and ` (1).bashrc`.
    /// </summary>
    internal static string Deduplicate(string path, bool isDirectory = false)
    {
        var dir = PathRules.Parent(path)!;
        var (stem, ext) = PathRules.SplitLeaf(PathRules.LeafName(path), isDirectory);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        throw new IOException("Could not find a free name.");
    }

    /// <summary>
    /// Directory.Move fails across filesystems, so a directory on another mount
    /// has to be copied and then removed. File.Move handles this itself.
    ///
    /// **Whatever crosses here is copied, and a copy drops the extended
    /// attributes.** This is the route the trash and the restore take, so once
    /// the copy engine started carrying a file's Baloo tags, a delete to a bin
    /// on another drive was the step that destroyed them — the move out kept
    /// the tags and putting the file back lost them.
    ///
    /// **And a copy went through a link.** Neither call below can be trusted
    /// with one: File.Move's own cross-device fallback reads through a link to
    /// a file and writes what it read, and Directory.Exists answers true for a
    /// link to a folder, which sent it to <see cref="CopyDirectory"/> as a
    /// folder — so a shortcut to a photo library, deleted to a bin on another
    /// drive, arrived as the library. A link is remade from its text, which is
    /// the whole of what a link is, and the text alone crosses. On one device
    /// a rename would have kept it as it was; the two calls cannot say which
    /// they did, so the one way that is right on both sides is taken on both.
    /// </summary>
    internal static void MoveAcrossDevices(string source, string destination)
    {
        if (new FileInfo(source).LinkTarget is { } pointsAt)
        {
            File.CreateSymbolicLink(destination, pointsAt);
            File.Delete(source);
            return;
        }

        if (Directory.Exists(source))
        {
            try
            {
                Directory.Move(source, destination);
            }
            catch (IOException e) when (IsCrossDevice(e))
            {
                // **Only across devices.** Any refused rename used to come here
                // — a parent folder the user cannot write refuses it too — and
                // the copy-then-delete that followed emptied the folder and
                // then failed at the one step it could never do, removing the
                // folder itself from a parent it had no right to change.
                try
                {
                    CopyDirectory(source, destination);
                }
                catch (Exception copyFailure)
                {
                    // Nothing has been deleted yet, so a partial copy is only
                    // clutter — clutter no bin lists, since it has no info
                    // file. Taken away before the failure is reported.
                    try { Directory.Delete(destination, recursive: true); }
                    catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                    {
                        Vaktari.Core.Quiet.Swallowed("trash", again);
                    }

                    // Said, so what is left of a copy that could not be taken
                    // away is not then read as an arrival: the original is
                    // whole, and a part of a copy is not the item.
                    throw new NothingMovedException(copyFailure);
                }

                Directory.Delete(source, recursive: true);
            }
        }
        else
        {
            // Read before, written after. A rename keeps the attributes and
            // this rewrites them unchanged; the cross-device fallback inside
            // File.Move is a byte copy and an unlink, which carries the mode
            // and the times and nothing else — and by then the source is gone,
            // so there is nothing left to read them from. File.Move does not
            // say which of the two it did, so both pay for the second.
            var carried = Xattrs.Capture(source);

            File.Move(source, destination, overwrite: false);

            Xattrs.Apply(destination, carried);
        }
    }

    /// <summary>
    /// Whether a refused move was refused because the two paths are on
    /// different filesystems — EXDEV on Linux, ERROR_NOT_SAME_DEVICE where these
    /// tests also run — which is the only refusal a copy can get round.
    /// </summary>
    internal static bool IsCrossDevice(IOException e)
        => e.HResult is 18 or unchecked((int)0x80070011);

    /// <summary>
    /// The copy behind the cross-device fallback above: the folder itself, then
    /// everything under it.
    ///
    /// **Internal so it can be exercised.** Reaching it through
    /// <see cref="MoveAcrossDevices"/> needs Directory.Move to refuse, which
    /// needs two filesystems; the tests that have two go through that, and the
    /// rest call this.
    ///
    /// **Links were followed.** Directory.EnumerateDirectories hands back a
    /// link to a folder as a folder, and File.Copy reads through a link to a
    /// file, so a folder holding a link to a library arrived holding the
    /// library. Measured with a link back up the folder itself: the copy
    /// descended through it 40 times — the kernel's limit on links in one path
    /// — wrote 80 nested empty folders on the way down, copied no file at all,
    /// because folders were taken before files, and then failed the whole
    /// folder with "Too many levels of symbolic links". A link to nothing was
    /// handed to File.Copy and failed the folder the same way. Every link is
    /// now one entry in the copy, remade from its text — a relative target
    /// keeps meaning what it meant beside its neighbours — and nothing it
    /// points at is copied.
    /// </summary>
    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        Xattrs.Carry(source, destination);

        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var landed = Path.Combine(destination, entry.Name);

            if (entry.LinkTarget is { } pointsAt)
            {
                File.CreateSymbolicLink(landed, pointsAt);
            }
            else if (entry is DirectoryInfo)
            {
                CopyDirectory(entry.FullName, landed);
            }
            else
            {
                File.Copy(entry.FullName, landed, overwrite: false);
                Xattrs.Carry(entry.FullName, landed);
            }
        }
    }
}
