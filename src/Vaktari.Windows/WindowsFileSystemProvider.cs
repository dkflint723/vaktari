using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Windows implementation, and deliberately the same shape as the Linux one:
/// <see cref="FileSystemEnumerable{TResult}"/> builds a <see cref="FileEntry"/>
/// straight from the directory entry the OS already returned, so a 200k-file
/// listing costs 200k struct copies rather than 200k FileInfo allocations and
/// 200k follow-up stats.
///
/// **Two things differ from Linux, and only two.** Hidden is an attribute rather
/// than a leading dot, and the filesystem is case-insensitive. Everything else
/// is the same BCL.
/// </summary>
public sealed class WindowsFileSystemProvider : IFileSystemProvider
{
    /// <summary>
    /// NTFS is case-insensitive as shipped. Per-directory case sensitivity can
    /// be switched on and WSL does exactly that, so this is "how Windows
    /// behaves", not "how every directory behaves" — the same simplification
    /// the Linux side makes in reverse for a case-insensitive FAT mount.
    /// </summary>
    public bool IsCaseSensitive => false;

    public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
        string path,
        ListingOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<FileEntry>(
            new BoundedChannelOptions(options.BatchSize * 4)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        // Enumeration runs off the UI thread. A directory read can block for a
        // long time on a mapped network drive and must never be on the dispatcher.
        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var entry in Enumerate(path, options))
                {
                    ct.ThrowIfCancellationRequested();
                    await channel.Writer.WriteAsync(entry, ct).ConfigureAwait(false);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, ct);

        var batch = new List<FileEntry>(options.BatchSize);

        await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            batch.Add(entry);
            if (batch.Count >= options.BatchSize)
            {
                yield return batch;
                batch = new List<FileEntry>(options.BatchSize);
            }
        }

        if (batch.Count > 0)
            yield return batch;

        await producer.ConfigureAwait(false);
    }

    private static FileSystemEnumerable<FileEntry> Enumerate(string path, ListingOptions options)
    {
        return new FileSystemEnumerable<FileEntry>(
            path,
            static (ref FileSystemEntry entry) => new FileEntry(
                Name: entry.FileName.ToString(),
                FullPath: entry.ToFullPath(),
                Length: entry.IsDirectory ? 0 : entry.Length,
                LastWriteTime: entry.LastWriteTimeUtc,
                Flags: ToFlags(ref entry)),
            new EnumerationOptions
            {
                RecurseSubdirectories = false,

                // **False, so a folder you cannot read says so.** With this
                // true, the BCL swallows the access denial on the ROOT handle
                // and the enumeration simply ends: the listing came back empty,
                // IsEmpty was true, and the pane drew "this folder is empty"
                // over somebody else's account data. Failures.Describe has said
                // "you do not have permission to open that folder" all along
                // and could never be reached for the one case it names.
                //
                // Nothing else is suppressed by turning it off: with
                // RecurseSubdirectories false, the root is the only directory
                // handle opened.
                IgnoreInaccessible = false,
                // Filtered below rather than here, so the flag still reaches
                // FileEntry when hidden entries ARE being shown.
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            })
        {
            // Hidden OR System. System alone would leave pagefile.sys and
            // System Volume Information sitting at the top of C:\ — both are
            // Hidden+System, so testing Hidden covers them, but a System-only
            // file is no more browsable and is hidden for the same reason.
            ShouldIncludePredicate = options.IncludeHidden
                ? null
                : static (ref FileSystemEntry entry) =>
                    (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0,
        };
    }

    private static EntryFlags ToFlags(ref FileSystemEntry entry)
    {
        var flags = EntryFlags.None;
        var attributes = entry.Attributes;

        if (entry.IsDirectory)
            flags |= EntryFlags.Directory;

        // An attribute, not a leading dot. A file named ".gitignore" is an
        // ordinary visible file here, which is the whole difference.
        if ((attributes & FileAttributes.Hidden) != 0)
            flags |= EntryFlags.Hidden;

        if ((attributes & FileAttributes.System) != 0)
            flags |= EntryFlags.System;

        // Covers symbolic links, junctions and mount points alike. The UI only
        // asks "is this an indirection", and telling them apart needs the
        // reparse tag, which costs another call per entry.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            flags |= EntryFlags.Symlink;

        if ((attributes & FileAttributes.ReadOnly) != 0)
            flags |= EntryFlags.ReadOnly;

        return flags;
    }

    public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists && !Directory.Exists(path))
                return ValueTask.FromResult<FileEntry?>(null);

            var attributes = File.GetAttributes(path);
            var isDir = (attributes & FileAttributes.Directory) != 0;

            // A drive root has no file name, so it names itself — the same rule
            // PathRules.LeafName applies, and the reason a C:\ tab is not blank.
            var name = PathRules.LeafName(path);

            var flags = EntryFlags.None;
            if (isDir) flags |= EntryFlags.Directory;
            if ((attributes & FileAttributes.Hidden) != 0) flags |= EntryFlags.Hidden;
            if ((attributes & FileAttributes.System) != 0) flags |= EntryFlags.System;
            if ((attributes & FileAttributes.ReparsePoint) != 0) flags |= EntryFlags.Symlink;
            if ((attributes & FileAttributes.ReadOnly) != 0) flags |= EntryFlags.ReadOnly;

            return ValueTask.FromResult<FileEntry?>(new FileEntry(
                name,
                path,
                isDir ? 0 : info.Length,
                info.LastWriteTimeUtc,
                flags));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<FileEntry?>(null);
        }
    }

    public IDisposable Watch(string path, Action<FileSystemChange> onChange)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,
        };

        watcher.Created += (_, e) => onChange(new FileSystemChange(ChangeKind.Added, e.FullPath));
        watcher.Deleted += (_, e) => onChange(new FileSystemChange(ChangeKind.Removed, e.FullPath));
        watcher.Changed += (_, e) => onChange(new FileSystemChange(ChangeKind.Changed, e.FullPath));
        watcher.Renamed += (_, e) => onChange(new FileSystemChange(ChangeKind.Renamed, e.FullPath, e.OldFullPath));

        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    public async ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            // Directory.Exists on a disconnected mapped drive or a dead UNC path
            // blocks on the redirector's own timeout and ignores cancellation, so
            // it goes on the pool and we abandon it rather than wait.
            return await Task.Run(() => Directory.Exists(path), cts.Token)
                             .WaitAsync(timeout, cts.Token)
                             .ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    public string Combine(string basePath, string name) => Path.Combine(basePath, name);

    /// <summary>
    /// Via <see cref="PathRules"/> rather than <see cref="Path.GetDirectoryName(string)"/>
    /// directly, so a drive root answers null instead of the empty string that
    /// once left the Up button enabled with nowhere to go.
    /// </summary>
    public string? GetParent(string path) => PathRules.Parent(path);
}
