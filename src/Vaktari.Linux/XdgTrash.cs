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
public static class XdgTrash
{
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
    /// Moves one item to the trash and returns the name it was given there, so
    /// an undo can find it again.
    /// </summary>
    public static string Trash(string sourcePath)
    {
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(InfoDir);

        var full = Path.GetFullPath(sourcePath);
        var name = ReserveName(Path.GetFileName(full), full);

        var destination = Path.Combine(FilesDir, name);

        try
        {
            MoveAcrossDevices(full, destination);
        }
        catch
        {
            // Never leave an info file pointing at something that isn't there —
            // that would show as a phantom entry in every trash browser.
            File.Delete(Path.Combine(InfoDir, name + ".trashinfo"));
            throw;
        }

        return name;
    }

    /// <summary>Puts a trashed item back where it came from.</summary>
    public static string Restore(string trashName)
    {
        var infoPath = Path.Combine(InfoDir, trashName + ".trashinfo");
        var originalPath = ReadOriginalPath(infoPath)
            ?? throw new FileNotFoundException("No trash info for " + trashName);

        var source = Path.Combine(FilesDir, trashName);
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
    private static string ReserveName(string preferred, string originalPath)
    {
        var stem = Path.GetFileNameWithoutExtension(preferred);
        var ext = Path.GetExtension(preferred);

        for (var i = 0; i < 10_000; i++)
        {
            var candidate = i == 0 ? preferred : $"{stem}.{i}{ext}";
            var infoPath = Path.Combine(InfoDir, candidate + ".trashinfo");

            try
            {
                using var stream = new FileStream(
                    infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));

                writer.WriteLine("[Trash Info]");
                writer.WriteLine("Path=" + EncodePath(originalPath));
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
            if (line.StartsWith("Path=", StringComparison.Ordinal))
                return DecodePath(line[5..]);
        }

        return null;
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
    /// </summary>
    internal static void MoveAcrossDevices(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            try
            {
                Directory.Move(source, destination);
            }
            catch (IOException)
            {
                CopyDirectory(source, destination);
                Directory.Delete(source, recursive: true);
            }
        }
        else
        {
            File.Move(source, destination, overwrite: false);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
    }
}
