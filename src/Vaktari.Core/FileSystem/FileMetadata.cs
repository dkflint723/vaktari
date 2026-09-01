namespace Vaktari.Core.FileSystem;

/// <summary>
/// Carries a file's metadata onto a copy of it.
///
/// **A copy that keeps only the bytes is not a copy of the file.** Both engines
/// copied through a bare stream loop, so every copy landed with today's date,
/// default permissions, and no attributes: a copied shell script lost its
/// executable bit, a 0600 private key came out world-readable, and copying a
/// photo library re-dated every file in it — which is the sort of loss nobody
/// notices until the sort order is wrong months later.
///
/// In Core because it is the same decision on both platforms; only the last
/// step differs, and it differs by which of two BCL calls is legal here.
/// </summary>
public static class FileMetadata
{
    /// <summary>
    /// The attributes worth carrying. Deliberately NOT a wholesale copy:
    /// Directory, ReparsePoint, Compressed and Encrypted describe what the
    /// file IS or where it lives, and setting them on a plain copy either
    /// throws or lies.
    /// </summary>
    private const FileAttributes Carried =
        FileAttributes.ReadOnly | FileAttributes.Hidden
        | FileAttributes.System | FileAttributes.Archive;

    /// <summary>
    /// Applies <paramref name="source"/>'s timestamps, attributes and — on
    /// Linux — permission bits to <paramref name="target"/>.
    ///
    /// Never throws. A copy that landed correctly must not be reported as
    /// failed because the filesystem underneath it will not take a timestamp;
    /// FAT, a phone over MTP and most network shares all refuse something here.
    /// </summary>
    public static void Carry(string source, string target)
    {
        try
        {
            var from = new FileInfo(source);

            if (!from.Exists) return;

            // **Times before attributes, always.** Setting ReadOnly first makes
            // the file refuse its own timestamps, so the order is not
            // cosmetic — reversing it silently drops the dates on exactly the
            // read-only files most likely to be archival.
            File.SetLastWriteTimeUtc(target, from.LastWriteTimeUtc);

            TrySet(() => File.SetCreationTimeUtc(target, from.CreationTimeUtc));
            TrySet(() => File.SetLastAccessTimeUtc(target, from.LastAccessTimeUtc));

            if (OperatingSystem.IsWindows())
            {
                var wanted = from.Attributes & Carried;

                if (wanted != 0) TrySet(() => File.SetAttributes(target, wanted));
            }

            // **A positive platform test, not the else of the one above.** The
            // analyser will not narrow "not Windows" to "supports unix modes",
            // and Vaktari is built with warnings as errors — so the else arm
            // fails the build rather than merely warning.
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                CarryUnixMode(source, target);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("file-ops", ex);
        }
    }

    /// <summary>
    /// The permission bits, including the executable one a stream copy always
    /// loses.
    ///
    /// **Its own method with the platform attribute on it**, rather than the
    /// call sitting inline behind an OperatingSystem check: the check guards a
    /// lambda, and the analyser cannot see through one — under warnings-as-
    /// errors that is a failed build, not a hint.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void CarryUnixMode(string source, string target)
        => TrySet(() => File.SetUnixFileMode(target, File.GetUnixFileMode(source)));

    /// <summary>
    /// One piece of metadata the filesystem will not take is not a failed copy.
    /// Creation time in particular is unsupported on several filesystems Linux
    /// mounts every day.
    /// </summary>
    private static void TrySet(Action set)
    {
        try
        {
            set();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or PlatformNotSupportedException or ArgumentException)
        {
            Quiet.Swallowed("file-ops", e);
        }
    }
}
