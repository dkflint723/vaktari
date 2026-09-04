using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Everything the properties window shows.
///
/// The universal fields come from <see cref="FileInfo"/>. The one
/// platform-specific group is the attribute set, which is where NTFS ACLs would
/// also belong if <see cref="IAccessEditor"/> is ever implemented — the
/// interface keeps them in <see cref="FileDetails.Groups"/> precisely so Core
/// never grows a permissions model that only means something on one OS.
/// </summary>
public sealed class WindowsPropertiesProvider : IPropertiesProvider
{
    /// <summary>
    /// The shell's sheet, in place of Vaktari's window. Everything a person
    /// opens properties on Windows FOR — permissions, unblocking a downloaded
    /// file, the pages other applications add — lives there and nowhere else.
    /// </summary>
    public bool ShowSystemDialog(string path) => ShellPropertySheet.Show(path);

    public ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
    {
        var isDirectory = Directory.Exists(path);

        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);

        var attributes = FileAttributes.None;
        try { attributes = info.Attributes; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        // Only resolved for an actual reparse point. ResolveLinkTarget on an
        // ordinary file is harmless but costs a call per properties window, and
        // on a dead network target it is the call that blocks.
        string? linkTarget = null;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            try { linkTarget = info.ResolveLinkTarget(returnFinalTarget: false)?.FullName; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return ValueTask.FromResult(new FileDetails
        {
            // A drive root has no file name, so it names itself rather than
            // showing a blank title bar.
            Name = PathRules.LeafName(path),
            FullPath = path,
            IsDirectory = isDirectory,
            Kind = KindOf(path, isDirectory, attributes),
            Size = info is FileInfo file && file.Exists ? SafeLength(file) : 0,
            Modified = Safe(() => info.LastWriteTimeUtc),
            Accessed = Safe(() => info.LastAccessTimeUtc),
            Created = Safe(() => info.CreationTimeUtc),
            SymlinkTarget = linkTarget,
            Groups = BuildGroups(attributes),
        });
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static DateTimeOffset? Safe(Func<DateTime> read)
    {
        try { return read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// **From the extension, not the registry.** Explorer shows the registered
    /// type description, which lives under HKCR and would mean referencing the
    /// registry — see WINDOWS.md §9. "PNG file" is less specific than "PNG
    /// image" but it is true, and it never claims a handler that is not there.
    /// </summary>
    private static string KindOf(string path, bool isDirectory, FileAttributes attributes)
    {
        if (PathRules.IsRoot(path)) return "Drive";
        if (isDirectory) return (attributes & FileAttributes.ReparsePoint) != 0 ? "Folder link" : "Folder";

        var extension = Path.GetExtension(path);

        if (string.IsNullOrEmpty(extension)) return "File";

        // The same word the listing's Type column uses, from the same
        // predicate. Two copies of this fact would drift into a properties
        // window that says "LNK file" beside a row that says "Shortcut".
        if (FileKind.IsShortcut(extension.AsSpan().TrimStart('.'))) return "Shortcut";

        return $"{extension.TrimStart('.').ToUpperInvariant()} file";
    }

    /// <summary>
    /// The flags both attribute panels list, in the order the single-item panel
    /// has always listed them. One table rather than two: a single-item panel
    /// and a selection's summary listing different flags is the same drift this
    /// file already refuses where the Type column and the "kind" row share one
    /// predicate.
    /// </summary>
    private static readonly (string Label, FileAttributes Flag)[] AlwaysListed =
    [
        ("Read-only", FileAttributes.ReadOnly),
        ("Hidden", FileAttributes.Hidden),
        ("System", FileAttributes.System),
        ("Archive", FileAttributes.Archive),
    ];

    /// <summary>Listed only where something carries them. Both are unusual
    /// enough that a "no" row for every ordinary file would be noise rather than
    /// information.</summary>
    private static readonly (string Label, FileAttributes Flag)[] ListedWhenSet =
    [
        ("Encrypted", FileAttributes.Encrypted),
        ("Reparse point", FileAttributes.ReparsePoint),
    ];

    private static IReadOnlyList<PropertyGroup> BuildGroups(FileAttributes attributes)
    {
        if (attributes == FileAttributes.None) return [];

        var rows = new List<PropertyRow>(AlwaysListed.Length + ListedWhenSet.Length);

        foreach (var (label, flag) in AlwaysListed)
            rows.Add(new PropertyRow(label, (attributes & flag) != 0 ? "yes" : "no"));

        foreach (var (label, flag) in ListedWhenSet)
            if ((attributes & flag) != 0) rows.Add(new PropertyRow(label, "yes"));

        return [new PropertyGroup("attributes", rows)];
    }

    /// <summary>
    /// The attribute set across a whole selection: what the items say where they
    /// agree, and "mixed" where they do not.
    ///
    /// **A selection's properties window stopped at the size line.** The shell's
    /// own sheet is declined for more than one path — SHMultiFileProperties
    /// wants an ITEMIDLIST array rather than paths and shows a reduced sheet —
    /// so Vaktari's window is everything a Windows user gets for a selection,
    /// and it showed a count and a total where the shell shows read-only and
    /// hidden. "Which of these forty are read-only" is a question that cannot be
    /// asked forty single-item sheets.
    ///
    /// One attribute read per path and nothing else: no link resolution, no
    /// kind, no length. This runs while the window is opening, on the same
    /// thread as the count beside it, and a selection can be a whole folder.
    /// </summary>
    public ValueTask<IReadOnlyList<PropertyGroup>> GetSharedAsync(
        IReadOnlyList<string> paths, CancellationToken ct)
    {
        var read = new List<FileAttributes>(paths.Count);

        foreach (var path in paths)
        {
            // A path that has gone, or will not answer, is left out rather than
            // counted as a "no". GetAttributes rather than FileInfo.Attributes
            // for exactly that reason: the property answers for a file that is
            // not there with -1 — every flag set — and a row invented out of
            // that is the fault the single-item sheet is already gated against.
            // This call throws instead, which is the answer worth having.
            try { read.Add(File.GetAttributes(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (read.Count == 0) return ValueTask.FromResult<IReadOnlyList<PropertyGroup>>([]);

        int Carrying(FileAttributes flag) => read.Count(a => (a & flag) != 0);

        var rows = new List<PropertyRow>(AlwaysListed.Length + ListedWhenSet.Length);

        // "yes" where all of them carry it, "no" where none does, and "mixed" —
        // the answer a single-item sheet never has to give.
        foreach (var (label, flag) in AlwaysListed)
        {
            var carrying = Carrying(flag);

            rows.Add(new PropertyRow(
                label, carrying == 0 ? "no" : carrying == read.Count ? "yes" : "mixed"));
        }

        foreach (var (label, flag) in ListedWhenSet)
        {
            var carrying = Carrying(flag);

            if (carrying > 0)
                rows.Add(new PropertyRow(label, carrying == read.Count ? "yes" : "mixed"));
        }

        return ValueTask.FromResult<IReadOnlyList<PropertyGroup>>(
            [new PropertyGroup("attributes", rows)]);
    }

    /// <summary>
    /// Walks a directory to total its contents. Explicitly on demand — doing it
    /// automatically is what makes opening properties on a profile directory
    /// hang.
    /// </summary>
    public async ValueTask<SizeProgress> MeasureAsync(
        string path, IProgress<SizeProgress> progress, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            long bytes = 0;
            var files = 0;
            var folders = 0;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            };

            // An explicit stack rather than AllDirectories: one denied folder
            // must cost that folder, not the whole measurement. On C:\ that is
            // guaranteed rather than likely.
            var pending = new Stack<string>();
            pending.Push(path);

            var sinceReport = 0;

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var directory = pending.Pop();

                try
                {
                    foreach (var entry in new DirectoryInfo(directory)
                                 .EnumerateFileSystemInfos("*", options))
                    {
                        ct.ThrowIfCancellationRequested();

                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            folders++;

                            // Not followed. A junction can point at an ancestor,
                            // and following one turns a measurement into a loop.
                            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                                pending.Push(entry.FullName);
                        }
                        else
                        {
                            files++;
                            if (entry is FileInfo file) bytes += file.Length;
                        }

                        // Reporting per entry floods the dispatcher on a large
                        // tree and is invisible anyway.
                        if (++sinceReport >= 256)
                        {
                            sinceReport = 0;
                            progress.Report(new SizeProgress(bytes, files, folders));
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            var total = new SizeProgress(bytes, files, folders);
            progress.Report(total);
            return total;
        }, ct).ConfigureAwait(false);
    }
}
