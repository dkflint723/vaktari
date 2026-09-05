using System.Diagnostics;
using System.IO.Enumeration;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

public sealed partial class LinuxPropertiesProvider : IPropertiesProvider, IAccessEditor
{
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();

    // Stand-ins for the machine, null in the application. Ownership is four
    // machine facts -- two files, a uid and a chown -- and a test that had to
    // arrange all four for real could only run as root on Linux.
    internal Func<IEnumerable<string>>? PasswdLines { get; init; }
    internal Func<IEnumerable<string>>? GroupLines { get; init; }
    internal Func<uint>? Euid { get; init; }
    internal Func<IReadOnlyList<string>, CancellationToken, Task<(int Code, string Error)>>?
        RunOverride { get; init; }
    internal Func<string, string, CancellationToken, ValueTask<string?>>? StatOverride { get; init; }

    public async ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
    {
        var isDirectory = Directory.Exists(path);
        var info = isDirectory
            ? new DirectoryInfo(path) as FileSystemInfo
            : new FileInfo(path);

        var groups = new List<PropertyGroup>();

        if (BuildPermissions(path) is { } permissions) groups.Add(permissions);
        if (await BuildOwnershipAsync(path, ct).ConfigureAwait(false) is { } ownership)
            groups.Add(ownership);

        return new FileDetails
        {
            Name = Path.GetFileName(path.TrimEnd('/')) is { Length: > 0 } n ? n : path,
            FullPath = path,
            IsDirectory = isDirectory,
            Kind = isDirectory ? "Folder" : DescribeKind(path),
            Size = info is FileInfo file ? file.Length : 0,
            Modified = info.Exists ? info.LastWriteTime : null,
            Accessed = info.Exists ? info.LastAccessTime : null,
            // Linux has no creation time on most filesystems; .NET reports the
            // change time or the epoch, so it is only shown when believable.
            Created = info.Exists && info.CreationTime.Year > 1971 ? info.CreationTime : null,
            SymlinkTarget = info.LinkTarget,
            Groups = groups,
        };
    }

    private static string DescribeKind(string path)
    {
        // Somebody is looking at this dialog, so it spends the interactive
        // budget rather than competing with the row icons and coming back
        // empty. Globs first, for the same reason the menu reads them.
        var mime = SharedMimeInfo.ForPath(path) is { Length: > 0 } known
            ? known
            : DesktopEntries.QueryMimeType(path, waiting: true);

        // **The type itself was the whole answer.** "application/vnd.oasis.
        // opendocument.text" is an identifier for programs, and this row is the
        // one place somebody asks "what IS this file" — where Dolphin answers
        // "ODT document". Describe falls back to the type when the machine has
        // no description for it, so the worst case is what this said before.
        return string.IsNullOrEmpty(mime) ? "File" : SharedMimeInfo.Describe(mime);
    }

    /// <summary>
    /// The mode the way ls writes it.
    ///
    /// **The three special bits appeared nowhere.** ls has shown them in the
    /// execute column for fifty years — s for setuid or setgid, t for the
    /// sticky bit — and a permissions row that omits them says a setuid binary
    /// is an ordinary one.
    ///
    /// A CAPITAL where the execute bit beneath is off, which is not decoration:
    /// "setuid, and executable" and "setuid, and not" are different situations,
    /// and a lowercase s for both would hide the second — a file that carries
    /// the bit and cannot use it.
    ///
    /// Separated from the file it describes so the rule can be read at every
    /// combination without one on disk to match, and on a machine that has no
    /// unix modes at all.
    /// </summary>
    internal static string Symbolic(UnixFileMode mode)
    {
        static string Triplet(bool r, bool w, bool x, char? special)
            => $"{(r ? 'r' : '-')}{(w ? 'w' : '-')}"
               + (special is { } c
                   ? (x ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))
                   : x ? "x" : "-");

        return Triplet(mode.HasFlag(UnixFileMode.UserRead), mode.HasFlag(UnixFileMode.UserWrite),
                       mode.HasFlag(UnixFileMode.UserExecute),
                       mode.HasFlag(UnixFileMode.SetUser) ? 's' : null)
             + Triplet(mode.HasFlag(UnixFileMode.GroupRead), mode.HasFlag(UnixFileMode.GroupWrite),
                       mode.HasFlag(UnixFileMode.GroupExecute),
                       mode.HasFlag(UnixFileMode.SetGroup) ? 's' : null)
             + Triplet(mode.HasFlag(UnixFileMode.OtherRead), mode.HasFlag(UnixFileMode.OtherWrite),
                       mode.HasFlag(UnixFileMode.OtherExecute),
                       mode.HasFlag(UnixFileMode.StickyBit) ? 't' : null);
    }

    private static PropertyGroup? BuildPermissions(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);

            var symbolic = Symbolic(mode);

            var octal =
                (mode.HasFlag(UnixFileMode.UserRead) ? 400 : 0) +
                (mode.HasFlag(UnixFileMode.UserWrite) ? 200 : 0) +
                (mode.HasFlag(UnixFileMode.UserExecute) ? 100 : 0) +
                (mode.HasFlag(UnixFileMode.GroupRead) ? 40 : 0) +
                (mode.HasFlag(UnixFileMode.GroupWrite) ? 20 : 0) +
                (mode.HasFlag(UnixFileMode.GroupExecute) ? 10 : 0) +
                (mode.HasFlag(UnixFileMode.OtherRead) ? 4 : 0) +
                (mode.HasFlag(UnixFileMode.OtherWrite) ? 2 : 0) +
                (mode.HasFlag(UnixFileMode.OtherExecute) ? 1 : 0);

            return new PropertyGroup("permissions",
            [
                new PropertyRow("mode", symbolic),
                new PropertyRow("octal", octal.ToString("D3")),
            ]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shelled out because .NET exposes the permission bits but not the owner
    /// and group *names* — and resolving a uid to a name means nsswitch, which
    /// is not something to reimplement.
    /// </summary>
    private async ValueTask<PropertyGroup?> BuildOwnershipAsync(
        string path, CancellationToken ct)
    {
        if (await StatAsync(path, "%U|%G|%i|%h", ct).ConfigureAwait(false) is not { } output)
            return null;

        var parts = output.Split('|');
        if (parts.Length < 4) return null;

        return new PropertyGroup("ownership",
        [
            new PropertyRow("owner", parts[0]),
            new PropertyRow("group", parts[1]),
            new PropertyRow("inode", parts[2]),
            new PropertyRow("links", parts[3]),
        ]);
    }

    /// <summary>
    /// One stat, in whatever format is asked for, or null when it could not be
    /// run at all.
    ///
    /// **One spawn shared by two callers rather than two spellings of it.** The
    /// ownership rows and the ownership CHOOSER want the same two names, and
    /// the second copy of this was the obvious way to get them.
    /// </summary>
    private async ValueTask<string?> StatAsync(string path, string format, CancellationToken ct)
    {
        if (StatOverride is { } fake) return await fake(path, format, ct).ConfigureAwait(false);

        try
        {
            var info = new ProcessStartInfo("stat")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(format);
            info.ArgumentList.Add(path);

            using var process = Process.Start(info);
            if (process is null) return null;

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    // ---- access editing ------------------------------------------------

    public bool CanEdit => true;

    private static readonly (string Key, string Group, string Label, UnixFileMode Flag)[] Bits =
    [
        ("ur", "owner",  "read",    UnixFileMode.UserRead),
        ("uw", "owner",  "write",   UnixFileMode.UserWrite),
        ("ux", "owner",  "execute", UnixFileMode.UserExecute),
        ("gr", "group",  "read",    UnixFileMode.GroupRead),
        ("gw", "group",  "write",   UnixFileMode.GroupWrite),
        ("gx", "group",  "execute", UnixFileMode.GroupExecute),
        ("or", "others", "read",    UnixFileMode.OtherRead),
        ("ow", "others", "write",   UnixFileMode.OtherWrite),
        ("ox", "others", "execute", UnixFileMode.OtherExecute),
    ];

    public async ValueTask<AccessState?> GetAccessAsync(string path, CancellationToken ct)
    {
        UnixFileMode mode;

        try
        {
            mode = File.GetUnixFileMode(path);
        }
        catch
        {
            return null;
        }

        var toggles = Bits
            .Select(b => new AccessToggle(b.Key, b.Group, b.Label, mode.HasFlag(b.Flag)))
            .ToList();

        // **Never allowed to cost the toggles.** Reading the owner is a `stat`
        // and reading the candidates is two files in /etc; any of those can be
        // missing on a machine whose accounts come from a directory service,
        // and a permissions sheet that refused to open because it could not
        // list the groups would be a worse dialog than the one that opened
        // without them.
        Ownership? ownership = null;

        try
        {
            if (await ReadOwnerGroupAsync(path, ct).ConfigureAwait(false) is var (owner, group))
                ownership = Decide(
                    owner, group,
                    PasswdLines?.Invoke() ?? ReadLines("/etc/passwd"),
                    GroupLines?.Invoke() ?? ReadLines("/etc/group"),
                    Environment.UserName,
                    root: (Euid?.Invoke() ?? GetEuid()) == 0);
        }
        catch
        {
            ownership = null;
        }

        return new AccessState(toggles, Octal(mode)) { Ownership = ownership };
    }

    /// <summary>
    /// What the two choosers may offer, which is not everything that exists.
    ///
    /// **Only root may give a file away.** chown(2) is root-only precisely so
    /// somebody cannot dodge a quota by handing their files to a stranger, and
    /// the owner list is therefore empty for everybody else -- an editable box
    /// listing every account on the machine, every entry of which is refused,
    /// is worse than a line of text.
    ///
    /// The group is the half an ordinary person can change, and only to a group
    /// they are IN. Root may pick any of them.
    ///
    /// The name already on the file is always in its own list even when it is
    /// in neither /etc file, which is the NSS case: a chooser that could not
    /// display the current value would silently propose changing it.
    /// </summary>
    internal static Ownership Decide(
        string owner, string group,
        IEnumerable<string> passwd, IEnumerable<string> groups,
        string me, bool root)
    {
        var groupLines = groups as IReadOnlyList<string> ?? [.. groups];

        var owners = root ? UnixAccounts.UsersIn(passwd) : [];

        var mine = root
            ? UnixAccounts.GroupsIn(groupLines)
            : UnixAccounts.GroupsFor(passwd, groupLines, me);

        // Ordinary people may change the group of files they own, and of
        // nothing else. Root is not stopped by the check because root never
        // fails it in a way that matters -- and a root session looking at
        // somebody else's file is exactly when this is being used.
        var canChangeGroup = (root || owner == me) && mine.Count > 0;

        return new Ownership(
            owner, group,
            With(owners, owner),
            With(mine, group),
            CanChangeOwner: root && owners.Count > 0,
            CanChangeGroup: canChangeGroup);
    }

    /// <summary>The current value belongs in its own list, once, first.</summary>
    private static IReadOnlyList<string> With(IReadOnlyList<string> names, string current)
    {
        if (current.Length == 0) return names;
        if (names.Count == 0) return [current];

        return names.Contains(current, StringComparer.Ordinal)
            ? names
            : [current, .. names];
    }

    private static IEnumerable<string> ReadLines(string path)
        => File.Exists(path) ? File.ReadLines(path) : [];

    /// <summary>The owner and group by name, from the same stat the ownership
    /// rows are built from.</summary>
    private async ValueTask<(string Owner, string Group)> ReadOwnerGroupAsync(
        string path, CancellationToken ct)
    {
        if (await StatAsync(path, "%U|%G", ct).ConfigureAwait(false) is not { } output)
            return ("", "");

        var parts = output.Split('|');

        return parts.Length >= 2 ? (parts[0], parts[1]) : ("", "");
    }

    public async ValueTask<string?> SetOwnershipAsync(
        string path, string owner, string group, bool recursive, CancellationToken ct)
    {
        // chown takes both at once and this passes both at once: two calls
        // would leave a file half moved when the second was refused.
        var argv = new List<string>();

        if (recursive) argv.Add("-R");

        argv.Add(owner + ":" + group);
        argv.Add(path);

        var (code, error) = await RunAsync(argv, ct).ConfigureAwait(false);

        if (code == 0) return null;

        // chown's own words where it gave any -- "invalid group", "Operation
        // not permitted" -- because they name which of the two halves was the
        // problem and a sentence of ours would not.
        return error.Trim() is { Length: > 0 } said
            ? said.Replace("chown: ", "", StringComparison.Ordinal)
            : "that could not be changed";
    }

    private async Task<(int Code, string Error)> RunAsync(
        IReadOnlyList<string> argv, CancellationToken ct)
    {
        if (RunOverride is { } fake) return await fake(argv, ct).ConfigureAwait(false);

        try
        {
            var info = new ProcessStartInfo("chown")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in argv) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null) return (-1, "chown could not be started");

            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return (process.ExitCode, error);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string Octal(UnixFileMode mode)
    {
        var value =
            (mode.HasFlag(UnixFileMode.UserRead) ? 400 : 0) +
            (mode.HasFlag(UnixFileMode.UserWrite) ? 200 : 0) +
            (mode.HasFlag(UnixFileMode.UserExecute) ? 100 : 0) +
            (mode.HasFlag(UnixFileMode.GroupRead) ? 40 : 0) +
            (mode.HasFlag(UnixFileMode.GroupWrite) ? 20 : 0) +
            (mode.HasFlag(UnixFileMode.GroupExecute) ? 10 : 0) +
            (mode.HasFlag(UnixFileMode.OtherRead) ? 4 : 0) +
            (mode.HasFlag(UnixFileMode.OtherWrite) ? 2 : 0) +
            (mode.HasFlag(UnixFileMode.OtherExecute) ? 1 : 0);

        return value.ToString("D3");
    }

    public async ValueTask<AccessOutcome> SetAccessAsync(
        string path,
        IReadOnlyList<AccessToggle> toggles,
        bool recursive,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var mode = UnixFileMode.None;
        foreach (var bit in Bits)
        {
            if (toggles.FirstOrDefault(t => t.Key == bit.Key)?.Value == true)
                mode |= bit.Flag;
        }

        // Directories need execute wherever read is granted, or the tree
        // becomes untraversable. This is chmod's "X" and it is not optional.
        var directoryMode = mode;
        if (mode.HasFlag(UnixFileMode.UserRead)) directoryMode |= UnixFileMode.UserExecute;
        if (mode.HasFlag(UnixFileMode.GroupRead)) directoryMode |= UnixFileMode.GroupExecute;
        if (mode.HasFlag(UnixFileMode.OtherRead)) directoryMode |= UnixFileMode.OtherExecute;

        // **Applying any change cleared the three bits nothing here offers.**
        // The mode is assembled from the nine toggles alone, so a setuid binary
        // stopped being one and a shared directory lost its sticky bit — the
        // one that stops people deleting each other's files in it — because
        // somebody ticked "group can write". Neither is a change anybody asked
        // for, and neither says a word when it happens.
        //
        // Read per PATH, never once: the parent's special bits are not the
        // children's, and carrying them down a recursive apply would SET setuid
        // on every file in a tree, which is worse than clearing it.
        static UnixFileMode Special(string of)
        {
            try
            {
                return File.GetUnixFileMode(of)
                       & (UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return UnixFileMode.None;
            }
        }

        return await Task.Run(() =>
        {
            var isDirectory = Directory.Exists(path);
            File.SetUnixFileMode(path, (isDirectory ? directoryMode : mode) | Special(path));

            if (!recursive || !isDirectory) return AccessOutcome.Complete;

            var done = 0;
            var skipped = 0;
            Exception? first = null;

            // **A walk that never follows a link out of the tree.** The obvious
            // version — AllDirectories — descends into linked directories, and
            // SetUnixFileMode is chmod rather than lchmod, so it follows the
            // link a second time. A folder holding a link to the user's photo
            // library, given a recursive 700, quietly rewrote the real library;
            // a link pointing at an ancestor never finished at all. The copy
            // engine measured and fixed this for itself and left the rule in
            // its own file — this is that rule, now shared.
            foreach (var child in SafeWalk.Descend(path, ct))
            {
                ct.ThrowIfCancellationRequested();

                // A link's own permissions mean nothing on Linux, and changing
                // them means changing its target's. Counted as skipped so the
                // report says the tree was not uniformly applied.
                if (child.IsLink)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    File.SetUnixFileMode(
                        child.Path,
                        (child.IsDirectory ? directoryMode : mode) | Special(child.Path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A single unwritable entry must not abort the whole tree -
                    // but it must not be forgotten either, which is what
                    // reporting "applied" over a tree that refused every child
                    // amounted to.
                    skipped++;
                    first ??= e;
                }

                if (++done % 200 == 0) progress?.Report(done);
            }

            progress?.Report(done);

            return new AccessOutcome(skipped, first);
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<SizeProgress> MeasureAsync(
        string path, IProgress<SizeProgress> progress, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        var folders = 0;

        await Task.Run(() =>
        {
            var walk = new FileSystemEnumerable<(long Length, bool IsDirectory)>(
                path,
                static (ref FileSystemEntry entry) => (entry.IsDirectory ? 0 : entry.Length, entry.IsDirectory),
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                })
            {
                // Links counted but not followed: a folder holding a link to
                // the home directory would otherwise report the size of the
                // home directory, and one pointing at an ancestor would never
                // finish being measured.
                ShouldRecursePredicate = (ref FileSystemEntry entry)
                    => !entry.Attributes.HasFlag(FileAttributes.ReparsePoint),
            };

            var sinceReport = 0;

            foreach (var (length, isDirectory) in walk)
            {
                ct.ThrowIfCancellationRequested();

                if (isDirectory) folders++;
                else { files++; bytes += length; }

                // Reported in batches: a directory with a million entries
                // should not mean a million progress callbacks.
                if (++sinceReport < 500) continue;

                sinceReport = 0;
                progress.Report(new SizeProgress(bytes, files, folders));
            }
        }, ct).ConfigureAwait(false);

        var final = new SizeProgress(bytes, files, folders);
        progress.Report(final);
        return final;
    }
}
