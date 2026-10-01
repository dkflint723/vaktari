using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// Linux implementation. The whole point of this class is the transform in
/// <see cref="Enumerate"/>: FileSystemEnumerable lets us build a FileEntry
/// directly from the kernel's directory entry, so a 200k-file listing costs
/// 200k struct copies rather than 200k FileInfo allocations plus 200k stat
/// calls.
/// </summary>
public sealed class LinuxFileSystemProvider : IFileSystemProvider
{
    public bool IsCaseSensitive => true;

    public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
        string path,
        ListingOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<FileEntry>(
            new System.Threading.Channels.BoundedChannelOptions(options.BatchSize * 4)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });

        // Enumeration runs off the UI thread. The directory read itself can block
        // for a long time on a network mount and must never be on the dispatcher.
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

    /// <summary>
    /// The names a folder asks to have hidden, from its own <c>.hidden</c>
    /// file.
    ///
    /// **A freedesktop convention Vaktari did not read, and both references
    /// do.** It is how a project marks generated output, and how a
    /// distribution keeps its own scaffolding out of a home directory, without
    /// renaming anything — the file cannot be renamed to start with a dot
    /// because a build tool or a script has to find it under its real name.
    /// Nautilus and Dolphin both honour it, so a folder tidy in either was a
    /// mess here.
    ///
    /// One stat and at most one small read per LISTING, not per entry — the
    /// cost the enumeration path is careful about is per-file work, and this is
    /// not that.
    ///
    /// Ordinal, because names on this filesystem are bytes: a .hidden naming
    /// "Build" does not hide "build".
    /// </summary>
    internal static HashSet<string> HiddenNames(string directory)
    {
        var listing = new HashSet<string>(StringComparer.Ordinal);
        var file = Path.Combine(directory, ".hidden");

        try
        {
            if (!File.Exists(file)) return listing;

            foreach (var line in File.ReadLines(file))
            {
                var name = line.Trim();

                // A name, never a path: the convention names entries in THIS
                // directory, and honouring a "../x" or "sub/x" would hide a row
                // in a folder that never asked.
                if (name.Length == 0 || name.Contains('/')) continue;

                listing.Add(name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable .hidden hides nothing, which is the behaviour this
            // folder had before the file existed.
            Vaktari.Core.Quiet.Swallowed("hidden", e);
        }

        return listing;
    }

    private static FileSystemEnumerable<FileEntry> Enumerate(string path, ListingOptions options)
    {
        // Read once for the whole listing, and captured — the delegates below
        // were static, which is why this rule could not be applied at all.
        var concealed = HiddenNames(path);

        return new FileSystemEnumerable<FileEntry>(
            path,
            (ref FileSystemEntry entry) => new FileEntry(
                Name: entry.FileName.ToString(),
                FullPath: entry.ToFullPath(),
                Length: entry.IsDirectory ? 0 : entry.Length,
                LastWriteTime: entry.LastWriteTimeUtc,
                Flags: ToFlags(ref entry, concealed)),
            new System.IO.EnumerationOptions
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
                // We filter hidden ourselves — on Linux "hidden" is a leading dot,
                // which System.IO's AttributesToSkip does not model.
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            })
        {
            ShouldIncludePredicate = options.IncludeHidden
                ? null
                : (ref FileSystemEntry entry) =>
                    entry.FileName.Length != 0
                    && entry.FileName[0] != '.'
                    && !concealed.Contains(entry.FileName.ToString()),
        };
    }

    private static EntryFlags ToFlags(ref FileSystemEntry entry, HashSet<string> concealed)
    {
        var flags = EntryFlags.None;

        if (entry.IsDirectory)
            flags |= EntryFlags.Directory;

        // **The flag as well as the filter**, or "show hidden files" reveals a
        // .hidden entry as an ordinary row — undimmed, and indistinguishable
        // from one the folder never asked to conceal.
        if ((entry.FileName.Length > 0 && entry.FileName[0] == '.')
            || concealed.Contains(entry.FileName.ToString()))
            flags |= EntryFlags.Hidden;

        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            flags |= EntryFlags.Symlink;

        if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
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

            var isDir = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            var name = Path.GetFileName(path);

            // **This worked the flags out from scratch and got fewer of them.**
            // ToFlags, a dozen lines up, also sets Symlink and ReadOnly — so a
            // row that arrived through the WATCHER differed from the same row
            // from enumeration in two bits nobody could see. FileEntry is a
            // record struct compared by every member, so the two were unequal:
            // the selection would not resolve onto a freshly created file, and
            // a symlink that appeared while you watched was drawn as the thing
            // it points at.
            var attributes = File.GetAttributes(path);

            var flags = EntryFlags.None;
            if (isDir) flags |= EntryFlags.Directory;
            // The same rule the enumeration applies, for the same reason the
            // block above gives: a row that arrives through the WATCHER must
            // carry the flags a row from a listing carries, or FileEntry's
            // structural equality makes the two unequal and the selection will
            // not resolve onto it.
            if (name.StartsWith('.')
                || (Path.GetDirectoryName(path) is { } parent
                    && HiddenNames(parent).Contains(name)))
                flags |= EntryFlags.Hidden;
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

    /// <summary>Stands in for /proc/mounts, for the tests that decide where a
    /// folder lives — the same seam the places provider has.</summary>
    internal Func<IEnumerable<string>>? MountLines { get; init; }

    public IDisposable Watch(string path, Action<FileSystemChange> onChange)
    {
        // **A folder on a network mount never updated on its own.** inotify
        // reports what passed through this kernel, and a file written by
        // another machine — or by the desktop's own gvfs or KIO daemon behind
        // a FUSE mount — is not that, so a share sat as it was until F5.
        // Polled instead, every five seconds, which is what Dolphin and
        // Nautilus do on the same mounts.
        if (MountTable.IsOnNetworkMount(MountLines is { } fake ? fake() : MountTable.Lines(), path))
            return new PollingWatch(path, onChange);

        // **One inotify instance for the process, not one per folder** — see
        // Inotify for the leak a FileSystemWatcher left behind every folder
        // deleted while it was shown. Watch out for the default
        // fs.inotify.max_user_watches ceiling if this is ever made recursive.
        return FolderWatch.Open(path, onChange);
    }

    public async ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            // Directory.Exists on a dead NFS/SMB mount blocks for the mount's own
            // timeout and ignores cancellation, so it goes on the pool and we
            // abandon it rather than wait.
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

    public string? GetParent(string path) => Path.GetDirectoryName(path);
}
