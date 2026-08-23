using System;
using System.Collections.Generic;
using System.IO;

namespace Vaktari.Ui.Input;

/// <summary>
/// Takes a copy of dropped files that the thing you dropped them from is about
/// to delete.
///
/// **Measured, not assumed.** Dragging a folder out of a 7-Zip archive hands
/// over a real path — into a temporary folder 7-Zip extracted for the drag. At
/// the instant of the drop that folder held 541 files and 8,985,809 bytes; by
/// the time the copy ran it was gone entirely, and the drop failed with "Could
/// not find a part of the path". 7-Zip deletes its temporary folder as soon as
/// the drop returns, and a copy started from the drop handler runs after that.
/// Nothing about the copy can win that race, because the race is already lost
/// when it starts.
///
/// So the bytes are taken while the drop is still happening, into a folder of
/// our own, and the copy that follows reads from there. That copy is free to be
/// as slow as it likes.
///
/// **Only what lives in the temporary folder.** A drop from an ordinary folder
/// is not volatile and must not be duplicated — that would double the work on
/// every drag in the application to fix a case that only archives have. The
/// temporary directory is where every archive tool stages a drag, and it is not
/// where anybody keeps files they mean to keep.
/// </summary>
internal static class DropStaging
{
    /// <summary>What came back, and whether any of it had to be rescued.</summary>
    internal sealed record Staged(IReadOnlyList<string> Paths, bool Rescued, long Bytes);

    /// <summary>
    /// Whether a path is somewhere its owner is likely to clear up underneath
    /// us — which in practice means the temporary directory.
    ///
    /// Compared as a path prefix at a separator, so a sibling folder whose name
    /// merely starts the same way is not swept in.
    /// </summary>
    internal static bool IsVolatile(string path, string temporaryRoot)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(temporaryRoot)) return false;

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(temporaryRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return full.StartsWith(root, Core.FileSystem.PathRules.Comparison);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies any volatile path into <paramref name="stagingRoot"/> and returns
    /// the list to paste from. Paths that are not volatile are returned
    /// untouched, and nothing is copied for them.
    ///
    /// Never throws: a drop that cannot be rescued should go on to fail the way
    /// it always did, with its own message, rather than take the window with it.
    /// </summary>
    internal static Staged Rescue(
        IReadOnlyList<string> paths, string temporaryRoot, string stagingRoot)
    {
        var anyVolatile = false;

        foreach (var path in paths)
            if (IsVolatile(path, temporaryRoot)) { anyVolatile = true; break; }

        if (!anyVolatile) return new Staged(paths, false, 0);

        var result = new List<string>(paths.Count);
        var bytes = 0L;
        var rescued = false;

        foreach (var path in paths)
        {
            if (!IsVolatile(path, temporaryRoot))
            {
                result.Add(path);
                continue;
            }

            try
            {
                Directory.CreateDirectory(stagingRoot);

                var name = Path.GetFileName(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (string.IsNullOrEmpty(name)) { result.Add(path); continue; }

                var landing = Path.Combine(stagingRoot, name);

                if (Directory.Exists(path)) bytes += CopyTree(path, landing);
                else if (File.Exists(path)) { File.Copy(path, landing, overwrite: true); bytes += Size(landing); }
                else { result.Add(path); continue; }

                result.Add(landing);
                rescued = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
            {
                // Could not rescue this one. Hand back the original so the drop
                // behaves as it did before rather than silently dropping it.
                result.Add(path);
            }
        }

        return new Staged(result, rescued, bytes);
    }

    private static long CopyTree(string from, string to)
    {
        var bytes = 0L;

        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from))
        {
            var landing = Path.Combine(to, Path.GetFileName(file));
            File.Copy(file, landing, overwrite: true);
            bytes += Size(landing);
        }

        foreach (var folder in Directory.EnumerateDirectories(from))
            bytes += CopyTree(folder, Path.Combine(to, Path.GetFileName(folder)));

        return bytes;
    }

    private static long Size(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
