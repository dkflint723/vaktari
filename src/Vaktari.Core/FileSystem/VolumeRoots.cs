namespace Vaktari.Core.FileSystem;

/// <summary>
/// The paths no operation may move, bin or delete: the root of a volume.
///
/// **Shift+Delete on a drive in This PC deleted the drive's contents.** The
/// row was hidden from the menu but its key was not, and nothing below the key
/// asked what it was being handed: the prompt named "D:\", a yes went to
/// DeleteChosen, and WindowsFileOperations.Delete cleared the read-only bits of
/// the whole tree and walked it deleting files, until Directory.Delete refused
/// the root at the very end. Ctrl+X then Ctrl+V on a drive moved every file on
/// it. It shipped in 0.11.0.
///
/// **Asked twice on purpose.** The pane refuses before anything is queued, so
/// the person gets a sentence rather than a failed operation; the engines ask
/// again as their first act, so a route nobody has thought of yet — a drop, a
/// script of keys, a caller added next year — still cannot reach the walk. The
/// two are independent guards of one rule and each is revert-checked alone.
///
/// A root is <see cref="PathRules.IsRoot"/> — "C:\", "/", a UNC share root —
/// or, on Linux, any mount point: a USB stick is a directory under /media, and
/// removing it would empty the stick.
/// </summary>
public static class VolumeRoots
{
    /// <summary>The sentence a refusal says, in the pane and in a failed
    /// operation alike.</summary>
    public const string Refusal = "a drive cannot be copied, moved, renamed or deleted — only what is on it";

    /// <summary>
    /// The sentence for a Windows device path outside the forms understood
    /// here — see <see cref="IsUnreadDevicePath"/>.
    /// </summary>
    public const string DeviceRefusal =
        "Vaktari does not copy, move or delete through a device path — open the folder by its ordinary name";

    /// <summary>
    /// The sentence for a Windows path with a colon after its drive — see
    /// <see cref="NamesAStream"/>.
    /// </summary>
    public const string StreamRefusal =
        "a colon after the drive names a stream, not a file or folder — Vaktari does not copy, move or delete through one";

    /// <summary>
    /// The mount points to compare against, or null for the machine's own.
    /// A seam for tests, which must never have a guard's absence tried on a
    /// real volume; null in the application.
    /// </summary>
    public static Func<IReadOnlyList<string>>? MountPointsOverride { get; set; }

    /// <summary>Whether this path is the root of a volume.</summary>
    public static bool IsVolumeRoot(string? path)
        => IsVolumeRootIn(AsWin32Reads(path).Path, MountPoints(), new(StringComparer.Ordinal));

    /// <summary>The refusal if any of these paths is a volume's root, or null.
    ///
    /// **The mount table is read once per call, not once per path.** It was
    /// read per path: a thousand selected files read /proc/mounts a thousand
    /// times, in the pane on the UI thread and again in the engine.
    ///
    /// **And never kept between calls.** It was kept for two seconds, and the
    /// pane and the engine read the same copy — so a stick mounted a moment
    /// before Shift+Delete was a folder to both guards at once, and one stale
    /// read stood in for two independent ones. Each call reads the table as
    /// it is now; it is a few dozen lines of a file the kernel writes.
    ///
    /// **A device path outside the forms read here is refused too**, with its
    /// own sentence — see <see cref="IsUnreadDevicePath"/>.</summary>
    public static string? Refuse(IEnumerable<string> paths)
    {
        IReadOnlyList<string>? points = null;
        Dictionary<string, string>? folders = null;

        foreach (var written in paths)
        {
            var (path, climbed) = AsWin32Reads(written);

            if (climbed) return DeviceRefusal;

            if (string.IsNullOrEmpty(path)) continue;

            if (IsVolumeRootIn(path, points ??= MountPoints(), folders ??= new(StringComparer.Ordinal)))
                return Refusal;

            if (OperatingSystem.IsWindows() && IsUnreadDevicePath(path)) return DeviceRefusal;

            if (OperatingSystem.IsWindows() && NamesAStream(path)) return StreamRefusal;
        }

        return null;
    }

    /// <summary>
    /// Whether a path is a root by its text alone — no mount table, nothing
    /// opened. For the engine's file-system answer, which reads back the path
    /// a handle reached and asks this of it.
    /// </summary>
    public static bool IsRootSpelling(string? path) => IsVolumeRootIn(AsWin32Reads(path).Path, [], new(StringComparer.Ordinal));

    /// <summary>
    /// A Windows device path as Win32 will open it, and whether reading it so
    /// climbed out of the device it was written under. Anything else — and
    /// every path on Linux — comes back as written.
    ///
    /// **Only a literal "\\?\" or "\??\" is opened as written.** Every other
    /// device spelling — "\\.\", and each slash variant of "\\?\" ("//?/",
    /// "\\?/", "/\?\", "\/?/") — is folded by Win32 first, with "\\.\" as a
    /// root: trailing spaces and dots go and "." and ".." are taken, and ".."
    /// right after the device takes the DEVICE away. So "\\.\W:\..\NAME\"
    /// opens "\\.\NAME\", while this class read the first name as "W:" and
    /// folded nothing past it: the fifth review round had Delete remove a
    /// folder that a colon-free DOS device named, and Trash hand "\\.\NAME\"
    /// to the recycler, through exactly that. And "//?/X:/ " opens X:'s root
    /// while it was read as the literal name " ".
    ///
    /// So such a path is read after Path.GetFullPath, which folds it the way
    /// Win32 does — and leaves a literal "\\?\" or "\??\" exactly as written,
    /// as Win32 does (.NET's PathInternal.IsExtended) — and a path whose
    /// device changed on the way is reported as climbed: it is refused as a
    /// device path, whatever it now names; a ".." that leaves the device it is
    /// written under is not a spelling this class reads. A path GetFullPath
    /// will not fold is reported climbed too.
    /// </summary>
    private static (string? Path, bool Climbed) AsWin32Reads(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return (path, false);

        var unified = path.Replace('/', '\\');

        if (!unified.StartsWith(@"\\?\", StringComparison.Ordinal) && !unified.StartsWith(@"\\.\", StringComparison.Ordinal))
            return (path, false);

        string folded;

        try
        {
            folded = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException
                                      or System.Security.SecurityException)
        {
            return (path, true);
        }

        // As a person reads it — "\\.\X: \" is written under X: — so a fold
        // that lands on the device "X: " instead has left the one written.
        var written = unified[4..].Split('\\')[0].TrimEnd(' ', '.');
        var reached = folded.Length > 4 ? folded[4..].Split('\\')[0] : "";

        return (folded, !written.Equals(reached, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a path is in the Win32 device namespace ("\\?\…", "\\.\…") but
    /// not in one of the three forms this class reads: a drive letter
    /// ("\\?\X:\…", "\\.\X:\…"), a share ("\\?\UNC\server\share\…") or a
    /// volume by its GUID ("\\?\Volume{…}\…").
    ///
    /// **Every other device name was a spelling to chase, and the chase did
    /// not end.** Three review rounds each found drives the text read as
    /// folders and the engine then emptied: the object manager's names under
    /// GLOBALROOT, then a logon session's own
    /// ("\\?\GLOBALROOT\Sessions\0\DosDevices\…\G:\"), "Global", a disk and
    /// partition number ("\\?\GLOBALROOT\Device\Harddisk0\Partition3\"). The
    /// namespace is open-ended, so the destructive verbs deny by default: any
    /// such path is refused, whatever it names. Nothing in Vaktari hands one
    /// out — a person has to type it — and the ordinary name of the same
    /// folder works.
    ///
    /// **Copying out is refused as well**, though it is not destructive: a
    /// copy of a root has no name to land under, and whether one of these
    /// names a root is the question this refuses to guess at.
    /// </summary>
    private static bool IsUnreadDevicePath(string path)
    {
        if (DevicePrefix(path) is not { } prefix) return false;

        var names = DeviceNames(path);
        var first = names[0];

        // A literal "\\?\" or "\??\" is opened as written, so a "." or ".."
        // in it is a NAME the file system is asked for, not a step — never
        // one a folder here has. Unless the whole reads as a drive's root,
        // which is refused as that first, it is refused as a device path:
        // deliberately, rather than guess what the file system makes of it.
        if (prefix == @"\\?\" && names.Skip(1).Any(name => name is "." or "..")) return true;

        if (first.Length == 2 && char.IsAsciiLetter(first[0]) && first[1] == ':') return false;

        if (prefix == @"\\?\" && first.Equals("UNC", StringComparison.OrdinalIgnoreCase)) return false;

        if (prefix == @"\\?\" && first.Length == 44
            && first.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) && first.EndsWith('}')
            && Guid.TryParse(first.AsSpan(6), out _))
            return false;

        return true;
    }

    /// <summary>
    /// "\\?\" or "\\.\" — either slash — or null for a path outside the device
    /// namespace. The NT prefix "\??\" answers as "\\?\".
    ///
    /// **"\??\" is the device namespace too, and was not asked about.** Win32
    /// hands a path starting "\??\" to the object manager as written, and
    /// .NET reads it as an extended device path (PathInternal.IsExtended), so
    /// "\??\GLOBALROOT\Sessions\0\DosDevices\…\K:\" reached every name the
    /// "\\?\" spelling did — and, unseen here, got past the pane and the
    /// engine's text alike: the fourth review round's Delete emptied a subst
    /// drive and a mapped drive through it. Backslashes only, exactly as
    /// IsExtended asks, because "/??/" is not one: .NET reads it as a folder
    /// named "??" on the current drive.
    /// </summary>
    private static string? DevicePrefix(string path)
    {
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) return @"\\?\";

        var unified = path.Replace('/', '\\');

        return unified.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\"
             : unified.StartsWith(@"\\.\", StringComparison.Ordinal) ? @"\\.\"
             : null;
    }

    /// <summary>
    /// Whether a Windows path has a colon after its drive — an alternate data
    /// stream or an attribute type ("X:\::$INDEX_ALLOCATION",
    /// "X:\:$I30:$INDEX_ALLOCATION", "file.txt:secret").
    ///
    /// **A directory's index stream names the directory.** "X:\::$INDEX_ALLOCATION"
    /// opens the root of X: — the file system's own check said root on the
    /// system drive — while the text read a folder with a strange name, and
    /// on a subst drive the engine handed it to the recycler. No name
    /// Vaktari lists has a colon after the drive, so every verb refuses one.
    /// </summary>
    private static bool NamesAStream(string path)
    {
        var rest = path.Replace('/', '\\');

        if (DevicePrefix(path) is not null) rest = rest[4..];

        if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':') rest = rest[2..];

        return rest.Contains(':');
    }

    /// <summary>
    /// The names after a device prefix. A "\\.\" path has already been folded
    /// the way Win32 folds it — its trailing spaces and dots taken, "\\.\G:\ "
    /// read as G:'s root — by <see cref="AsWin32Reads"/>; "\\?\" is opened as
    /// written, where "\\?\G:\ " is a name of its own. "." and ".." are left
    /// for <see cref="Folded"/>.
    /// </summary>
    private static string[] DeviceNames(string path) => path.Replace('/', '\\')[4..].Split('\\');

    /// <summary>
    /// The refusal if the filesystem itself says one of these paths is a
    /// volume's root, or null — the engines' own question, asked in their
    /// workers rather than on the thread that pressed the key.
    ///
    /// **Text cannot see everything a filesystem can.** A mount reached
    /// through a link, a volume named by a device path nobody thought of, a
    /// mount made after the table was read: <see cref="Refuse"/> answers from
    /// the path and the table, and each of those got past it in review and had
    /// its contents deleted. The engine asks the operating system as well, by
    /// its own means (a handle on Windows, statx on Linux), so the two guards
    /// share no read. A path the filesystem cannot answer for is not refused
    /// here; the text guard has already had its say.
    /// </summary>
    public static string? RefuseOnDisk(IEnumerable<string> paths, Func<string, bool> isRootOnDisk)
    {
        foreach (var path in paths)
            if (!string.IsNullOrEmpty(path) && isRootOnDisk(path)) return Refusal;

        return null;
    }

    /// <summary>
    /// Whether a path is a root, however it is spelled.
    ///
    /// **Five spellings of a root got past both guards**, measured on the
    /// review's probe against the real engine: "\\server\share\" (the ordinary
    /// trailing-backslash form), "\\?\UNC\server\share\", "Z:\\", "Z:\." and
    /// "Z:\x\..". PathRules.IsRoot compares a path with its own root as TEXT,
    /// so each of them read as a folder: Delete walked on, Trash handed "Z:\"
    /// to the recycler, and Move landed the root on itself.
    ///
    /// So the path is asked as written first — "Z:" must be, because
    /// Path.GetFullPath("Z:") resolves to Z:'s CURRENT folder, which is not
    /// its root — then with its trailing separators taken off, which is what
    /// "\\server\share\" and "\\?\UNC\server\share\" needed (GetPathRoot
    /// answers either without its last backslash), and then resolved, which
    /// folds "." and ".." away and collapses a doubled separator. A mount point
    /// is compared the same way on both sides, so "/media/me/STICK/.",
    /// "//media/me/STICK" and "/media/me/./STICK" all name the stick.
    ///
    /// **The trimmed spelling is asked twice, and each time hides the
    /// other's absence**: once as written and once resolved, because
    /// resolving a path that is only trailing separators away from a root
    /// gives the same path back. The first stands for a path GetFullPath
    /// refuses. Revert-checked as a pair.
    /// </summary>
    private static bool IsVolumeRootIn(string? path, IReadOnlyList<string> points, Dictionary<string, string> folders)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (PathRules.IsRoot(path)) return true;

        if (OperatingSystem.IsWindows() && DevicePath(path) is { } device)
            return device.Root || (device.Plain is { } plain && IsVolumeRootIn(plain, points, folders));

        var spellings = new List<string> { PathRules.Normalise(path) };

        if (Resolved(path) is { } full)
        {
            spellings.Add(full);
            spellings.Add(PathRules.Normalise(full));
        }

        if (spellings.Any(PathRules.IsRoot)) return true;

        if (points.Count == 0) return false;

        // On Linux the path as the kernel reaches it, and as .NET reaches it —
        // which folds "." and ".." as text before the kernel sees them, so an
        // engine handed "a/link/../STICK" acts on "a/STICK". Either being a
        // mount is enough.
        string[] canonical = OperatingSystem.IsLinux()
            ? [PathRules.Normalise(Followed(path, folders)), PathRules.Normalise(Followed(Resolved(path) ?? path, folders))]
            : [Canonical(path)];

        return points.Any(point => canonical.Contains(Canonical(point), StringComparer.Ordinal));
    }

    /// <summary>
    /// What a Win32 device path — "\\?\…" or "\\.\…" — stands for: a root
    /// outright, or the ordinary path to ask instead, or neither (a folder on
    /// a volume). Null for a path that is not a device path.
    ///
    /// **GetFullPath leaves a "\\?\" path exactly as written**, so none of the
    /// folding the other spellings rely on happens to it: "\\?\Z:\.",
    /// "\\?\Z:\x\..", "\\?\Volume{…}\." and "\\?\UNC\server\share\." were all
    /// folders, and "\\?\GLOBALROOT\??\Z:\" and
    /// "\\?\GLOBALROOT\Device\HarddiskVolume3\" — the object manager's names
    /// for a drive — reached the engine's Delete, which emptied a subst drive
    /// through the first. So the prefix is taken apart here: "UNC\" becomes
    /// the share it names, a "??" (or "DosDevices", "GLOBAL??") under
    /// GLOBALROOT becomes the "\\?\" path it stands for, and otherwise the
    /// device is the first name — "Z:", "Volume{…}", or under GLOBALROOT
    /// "Device\HarddiskVolume3" — and the path is a root when what follows it
    /// folds away to nothing. A network redirector's device carries the
    /// server and the share as well, so those two belong to its root.
    /// </summary>
    private static (bool Root, string? Plain)? DevicePath(string path)
    {
        if (DevicePrefix(path) is null) return null;

        var names = DeviceNames(path);

        if (names[0].Equals("UNC", StringComparison.OrdinalIgnoreCase))
            return (false, @"\\" + string.Join('\\', names[1..]));

        if (!names[0].Equals("GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            return (Folded(names[1..]).Count == 0, null);

        var inside = Folded(names[1..]);

        if (inside.Count == 0) return (true, null);

        if (inside[0] is "??" || inside[0].Equals("DosDevices", StringComparison.OrdinalIgnoreCase)
                              || inside[0].Equals("GLOBAL??", StringComparison.OrdinalIgnoreCase))
            return inside.Count == 1 ? (true, null) : DevicePath(@"\\?\" + string.Join('\\', inside.Skip(1)));

        // "Device\Name", and for a network redirector "\server\share" after it,
        // past any ";LanmanRedirector"-style names the redirector adds.
        var rest = inside.Skip(2).SkipWhile(name => name.StartsWith(';')).ToList();

        var redirector = inside.Count > 1
                         && (inside[1].Equals("Mup", StringComparison.OrdinalIgnoreCase)
                             || inside[1].Equals("LanmanRedirector", StringComparison.OrdinalIgnoreCase));

        return (rest.Count <= (redirector ? 2 : 0), null);
    }

    /// <summary>Names with "." dropped and each ".." taking the one before it
    /// away — never past the first.</summary>
    private static List<string> Folded(IEnumerable<string> names)
    {
        var kept = new List<string>();

        foreach (var name in names)
        {
            if (name is "" or ".") continue;

            if (name == "..")
            {
                if (kept.Count > 0) kept.RemoveAt(kept.Count - 1);
                continue;
            }

            kept.Add(name);
        }

        return kept;
    }

    /// <summary>
    /// A Linux path as the kernel reaches it: links along the way followed,
    /// "." and ".." taken the way the kernel takes them — ".." after a link is
    /// the parent of where the link LEADS — and the last name, when it is a
    /// name, left alone, because a link named as itself is the link.
    ///
    /// **A mount reached through a linked folder was a folder.** On Fedora
    /// Silverblue /mnt, /media and /home are links, and the review's probe
    /// deleted a "stick" through a linked parent: the mount table names the
    /// real path and GetFullPath folds text, so the two never met.
    /// "toproc/." — the folder a link leads to, not the link — was a folder
    /// too, because GetFullPath folded it to the link's own name.
    ///
    /// Asked of the filesystem with an lstat per name; a folder already
    /// followed in this call is remembered, so a thousand files in one folder
    /// follow it once. A name that cannot be asked is kept as written.
    /// </summary>
    private static string Followed(string path, Dictionary<string, string> folders, int depth = 0)
    {
        if (depth > 40) return path;

        var absolute = path.StartsWith('/') ? path : Path.Join(Environment.CurrentDirectory, path);
        var names = absolute.Split('/');
        var trailing = absolute.EndsWith('/');
        var reached = "";

        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];

            if (name is "" or ".") continue;

            if (name == "..")
            {
                var cut = reached.LastIndexOf('/');
                reached = cut <= 0 ? "" : reached[..cut];
                continue;
            }

            // Not "//name" after a link that led to "/": the table has "/proc",
            // and "toroot/proc" read as "//proc" was a folder to it.
            var next = (reached == "/" ? "" : reached) + "/" + name;

            // The last name is the entry itself unless something after it — a
            // separator, a "." — asks for what it leads to.
            var last = !trailing && names.Skip(i + 1).All(n => n.Length == 0);

            reached = last ? next : Through(next, folders, depth);
        }

        return reached.Length == 0 ? "/" : reached;
    }

    /// <summary>Where a folder on the way leads: itself, or its link's target
    /// followed in turn. Remembered for the rest of the call.</summary>
    private static string Through(string folder, Dictionary<string, string> folders, int depth)
    {
        if (folders.TryGetValue(folder, out var known)) return known;

        string? target;

        try
        {
            target = new FileInfo(folder).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            target = null;
        }

        var reached = target is null
            ? folder
            : Followed((target.StartsWith('/') ? target : folder[..folder.LastIndexOf('/')] + "/" + target) + "/",
                       folders, depth + 1).TrimEnd('/');

        if (reached.Length == 0) reached = "/";

        folders[folder] = reached;
        return reached;
    }

    /// <summary>The path with "." and ".." folded away, or null where it
    /// cannot be resolved.</summary>
    private static string? Resolved(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException
                                      or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>A path as a mount point is compared: resolved, and without a
    /// trailing separator.</summary>
    private static string Canonical(string path)
        => PathRules.Normalise(Resolved(path) ?? path);

    /// <summary>
    /// The mount points to compare against: the seam's, or the machine's own,
    /// read afresh — see <see cref="Refuse"/> for why nothing is kept.
    /// </summary>
    private static IReadOnlyList<string> MountPoints() => MountPointsOverride?.Invoke() ?? ReadMountTable();

    /// <summary>
    /// What reads the mount table: on Linux /proc/mounts, and on Windows
    /// nothing — a volume mounted in a folder is rare, and the fallback that
    /// lists them asks every drive letter, a dead mapped drive included. A
    /// seam, so the reads can be counted and a mount made between two of them.
    /// </summary>
    internal static Func<IReadOnlyList<string>> ReadMountTable { get; set; } = MachineMountTable;

    private static IReadOnlyList<string> MachineMountTable() => OperatingSystem.IsLinux() ? Volumes.MountPoints() : [];

    /// <summary>
    /// An operation that has already failed with the refusal, for an engine to
    /// hand back instead of starting — or null when none of the paths is a root.
    /// </summary>
    public static IOperationHandle? RefusedOperation(IReadOnlyList<string> paths, OperationKind kind)
    {
        if (Refuse(paths) is not { } why) return null;

        var handle = new OperationHandle { Paths = paths, Kind = kind };

        handle.Failed(new IOException(why));

        return handle;
    }
}
