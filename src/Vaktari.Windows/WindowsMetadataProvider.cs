using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The short, type-specific fact shown inline in a listing: an image's
/// dimensions, a folder's item count.
///
/// **Image dimensions come from Core**, which already solves it by reading the
/// header rather than decoding — see <see cref="ImageSize"/>. Media duration
/// has no BCL equivalent and is not attempted; the property shelf that would
/// answer it is the same COM surface as thumbnails.
/// </summary>
public sealed class WindowsMetadataProvider : IFileMetadataProvider
{
    private static readonly HashSet<string> Measurable =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

    public bool CanDescribe(string path, bool isDirectory) =>
        isDirectory || Measurable.Contains(Path.GetExtension(path));

    /// <summary>
    /// **Off the UI thread, now that something calls it.** This was fully
    /// synchronous, which was harmless while nothing did — the whole provider
    /// path was dead code. Once the viewport asks it per folder row, an
    /// enumeration on a slow share stutters scrolling. The Linux twin already
    /// does this and says the same thing.
    /// </summary>
    public async ValueTask<string?> DescribeAsync(string path, bool isDirectory, CancellationToken ct)
    {
        try
        {
            if (isDirectory)
            {
                // Top level only. Counting recursively is what makes a
                // properties dialog hang on a home directory, and this runs for
                // every folder row in the viewport.
                return await Task.Run(() =>
                {
                    var count = 0;

                    foreach (var _ in Directory.EnumerateFileSystemEntries(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++count > 9999) return "9999+ items";
                    }

                    return count == 1 ? "1 item" : $"{count} items";
                }, ct).ConfigureAwait(false);
            }

            return await Task.Run(
                () => ImageSize.TryRead(path) is { } size ? $"{size.Width} × {size.Height}" : null,
                ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// File attributes, where Linux shows the POSIX mode. The interface returns
    /// a string for exactly this reason: the two models have almost nothing in
    /// common beyond "how you may use this file".
    ///
    /// Only the attributes a user acts on. Archive is set on essentially every
    /// file and means nothing outside a backup program, so listing it would
    /// crowd out the ones that matter.
    /// </summary>
    public ValueTask<string?> DescribeAccessAsync(string path, bool isDirectory, CancellationToken ct)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var parts = new List<string>(4);

            if ((attributes & FileAttributes.ReadOnly) != 0) parts.Add("read-only");
            if ((attributes & FileAttributes.Hidden) != 0) parts.Add("hidden");
            if ((attributes & FileAttributes.System) != 0) parts.Add("system");
            if ((attributes & FileAttributes.ReparsePoint) != 0) parts.Add("link");
            if ((attributes & FileAttributes.Encrypted) != 0) parts.Add("encrypted");

            return ValueTask.FromResult<string?>(
                parts.Count == 0 ? null : string.Join(", ", parts));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }
}
