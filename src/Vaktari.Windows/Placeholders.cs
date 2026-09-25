using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace Vaktari.Windows;

/// <summary>
/// Which files in a folder a sync client holds online — asked by a thread
/// that has said it wants to see placeholders as they are.
///
/// **Vaktari cannot see that on its own, and that is not a guess.** Windows
/// gives every process a placeholder compatibility mode. In the one it calls
/// disguised, "the presence of a reparse point, the sparse bit, and the
/// offline bit" are "completely hidden, making the file look like a normal
/// file" (RtlSetThreadPlaceholderCompatibilityMode, Microsoft's documentation).
/// Vaktari runs disguised: measured in 0633df3 against a real sync root on
/// this machine, the placeholders there carried no ReparsePoint attribute to
/// a .NET process, and did in PowerShell, which runs exposed. A search that
/// tested the attributes from its own directory read would therefore see an
/// online-only file as an ordinary one, open it, and download it.
///
/// **So the question is asked again, exposed, and only this question.** The
/// mode is set for the calling thread alone, for the length of one directory
/// read, and put back in a finally. The rows of the listing still come from
/// the walk's own read in the process's own mode, so a search result is drawn
/// exactly as the same file in its folder is — exposing the walk itself would
/// have made every placeholder a reparse point to the row code.
///
/// Where the call does not exist — Windows before 1709, which is also before
/// cloud files existed — the read is made in the process's mode, because
/// there is nothing a placeholder could be hiding.
///
/// **Measured since against real online-only files**, in a cloud files sync
/// root the Windows tests register in a temp folder of their own
/// (OnlineOnlyFilesTests): the exposed read names every placeholder whose data
/// was not fetched. The same run found the test host — whose process mode
/// reads back as disguised — seeing ReparsePoint, Offline and
/// RECALL_ON_DATA_ACCESS on those files through its own reads as well, which
/// is not what 0633df3 measured on the sync root beside it. Which of the two a
/// given sync client's files get is not settled here, so the question is still
/// asked exposed: it answers correctly either way.
/// </summary>
internal static partial class Placeholders
{
    /// <summary>
    /// FILE_ATTRIBUTE_OFFLINE, FILE_ATTRIBUTE_RECALL_ON_OPEN and
    /// FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: the three ways a file says its
    /// data is somewhere other than this disk. The last two have no name in
    /// <see cref="FileAttributes"/>.
    /// </summary>
    internal const FileAttributes HeldOnline =
        FileAttributes.Offline | (FileAttributes)0x0004_0000 | (FileAttributes)0x0040_0000;

    private const sbyte Expose = 2;

    [LibraryImport("ntdll.dll")]
    private static partial sbyte RtlSetThreadPlaceholderCompatibilityMode(sbyte mode);

    [LibraryImport("ntdll.dll")]
    internal static partial sbyte RtlQueryThreadPlaceholderCompatibilityMode();

    /// <summary>False once the call has turned out not to exist here.</summary>
    private static bool _available = true;

    /// <summary>
    /// Runs inside the exposed read, before it reads anything, with the folder
    /// being listed or the one file being asked about. For the tests that prove
    /// the mode is really set there and that a content walk really asks; null
    /// in the application. Handed the path so a test can tell its own reads
    /// from another class's running beside it.
    ///
    /// **The one-file read needs it as much as the folder read.** The test
    /// host already sees a placeholder's online bits in its own mode (see the
    /// class note), so a real placeholder answers correctly with the expose
    /// step deleted — only the mode read from in here can tell.
    /// </summary>
    internal static Action<string>? WhileExposed { get; set; }

    /// <summary>
    /// The names of the files directly in <paramref name="directory"/> whose
    /// data is not on this disk. Empty when the folder cannot be listed —
    /// the walk's own read has already said what it could of that folder.
    /// </summary>
    internal static HashSet<string> HeldOnlineIn(string directory)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previous = Set(Expose);

        try
        {
            WhileExposed?.Invoke(directory);

            var names = new FileSystemEnumerable<string>(
                directory,
                static (ref FileSystemEntry entry) => entry.FileName.ToString(),
                new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry)
                    => !entry.IsDirectory && (entry.Attributes & HeldOnline) != 0,
            };

            foreach (var name in names) held.Add(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            if (previous >= 0) Set(previous);
        }

        return held;
    }

    /// <summary>
    /// Whether one file's data is not on this disk, asked the same way:
    /// exposed, on this thread only, for the length of one attribute read.
    /// That read does not open the file's data, so asking fetches nothing.
    /// False when the attributes cannot be read — the open that would follow
    /// fails on the same path for the same reason.
    ///
    /// For the readers that take one path rather than walk a folder: the image
    /// header, the thumbnail and the duplicate finder, through
    /// <see cref="Vaktari.Core.FileSystem.OnlineOnly"/>.
    /// </summary>
    internal static bool IsHeldOnline(string path)
    {
        var previous = Set(Expose);

        try
        {
            WhileExposed?.Invoke(path);

            return (File.GetAttributes(path) & HeldOnline) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (previous >= 0) Set(previous);
        }
    }

    /// <summary>The thread's previous mode, or -1 when it could not be set.</summary>
    private static sbyte Set(sbyte mode)
    {
        if (!_available) return -1;

        try
        {
            return RtlSetThreadPlaceholderCompatibilityMode(mode);
        }
        catch (EntryPointNotFoundException)
        {
            _available = false;
            return -1;
        }
    }
}
