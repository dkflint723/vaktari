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

    private static IReadOnlyList<PropertyGroup> BuildGroups(FileAttributes attributes)
    {
        if (attributes == FileAttributes.None) return [];

        var rows = new List<PropertyRow>(6);

        void Row(string label, FileAttributes flag) =>
            rows.Add(new PropertyRow(label, (attributes & flag) != 0 ? "yes" : "no"));

        Row("Read-only", FileAttributes.ReadOnly);
        Row("Hidden", FileAttributes.Hidden);
        Row("System", FileAttributes.System);
        Row("Archive", FileAttributes.Archive);

        // Only when set. Both are unusual enough that a "no" row for every
        // ordinary file would be noise rather than information.
        if ((attributes & FileAttributes.Encrypted) != 0)
            rows.Add(new PropertyRow("Encrypted", "yes"));

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            rows.Add(new PropertyRow("Reparse point", "yes"));

        return [new PropertyGroup("attributes", rows)];
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
