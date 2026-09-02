using System.Diagnostics;
using System.IO.Enumeration;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

public sealed class LinuxPropertiesProvider : IPropertiesProvider, IAccessEditor
{
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
        return string.IsNullOrEmpty(mime) ? "File" : mime;
    }

    private static PropertyGroup? BuildPermissions(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);

            static string Triplet(bool r, bool w, bool x)
                => $"{(r ? 'r' : '-')}{(w ? 'w' : '-')}{(x ? 'x' : '-')}";

            var symbolic =
                Triplet(mode.HasFlag(UnixFileMode.UserRead), mode.HasFlag(UnixFileMode.UserWrite), mode.HasFlag(UnixFileMode.UserExecute)) +
                Triplet(mode.HasFlag(UnixFileMode.GroupRead), mode.HasFlag(UnixFileMode.GroupWrite), mode.HasFlag(UnixFileMode.GroupExecute)) +
                Triplet(mode.HasFlag(UnixFileMode.OtherRead), mode.HasFlag(UnixFileMode.OtherWrite), mode.HasFlag(UnixFileMode.OtherExecute));

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
    private static async ValueTask<PropertyGroup?> BuildOwnershipAsync(
        string path, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("stat")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("%U|%G|%i|%h");
            info.ArgumentList.Add(path);

            using var process = Process.Start(info);
            if (process is null) return null;

            var output = (await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

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

    public ValueTask<AccessState?> GetAccessAsync(string path, CancellationToken ct)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);

            var toggles = Bits
                .Select(b => new AccessToggle(b.Key, b.Group, b.Label, mode.HasFlag(b.Flag)))
                .ToList();

            return ValueTask.FromResult<AccessState?>(new AccessState(toggles, Octal(mode)));
        }
        catch
        {
            return ValueTask.FromResult<AccessState?>(null);
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

        return await Task.Run(() =>
        {
            var isDirectory = Directory.Exists(path);
            File.SetUnixFileMode(path, isDirectory ? directoryMode : mode);

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
                    File.SetUnixFileMode(child.Path, child.IsDirectory ? directoryMode : mode);
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
