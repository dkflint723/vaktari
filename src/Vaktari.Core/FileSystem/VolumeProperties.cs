namespace Vaktari.Core.FileSystem;

/// <summary>How much room a filesystem has, and what it is.</summary>
public readonly record struct VolumeUsage(
    string Root, string Label, string Format, long Total, long Free);

/// <summary>
/// The volume rows on a folder's properties.
///
/// **Properties on a drive said less about the drive than the sidebar row you
/// opened it from.** The window filled its general group from
/// <see cref="FileDetails"/> — a name, a location, dates — and a volume has
/// none of the interesting ones: no modified time worth showing, no size,
/// nothing about capacity, the filesystem or the label. The sidebar has drawn a
/// free-space bar and a "1 TiB free of 4 TiB" tooltip for a while; the dialog
/// somebody opens to ask that exact question had no answer in it.
///
/// Folders got nothing either, and that is the more common ask of the two:
/// "will this fit" is asked about the folder you are copying into, not about
/// its drive.
///
/// **Here rather than in either platform's provider**, though the providers are
/// where platform extras belong, because this is not a platform extra —
/// DriveInfo answers on both, and the interface's own comment reserves
/// <see cref="FileDetails.Groups"/> for the facts that only mean something on
/// one operating system.
/// </summary>
public static class VolumeProperties
{
    /// <summary>
    /// Stands in for the machine's drives. Null in the application; a test sets
    /// it so its assertions are about the rows rather than about whatever this
    /// particular disk happens to hold.
    /// </summary>
    internal static Func<string, VolumeUsage?>? Reader { get; set; }

    /// <summary>
    /// The volume a path sits on, or null when the question does not apply.
    ///
    /// **A UNC path is not a drive and throws.** Measured:
    /// <c>new DriveInfo(@"\\localhost\C$")</c> raises ArgumentException, "Drive
    /// name must be a root directory" — so a network share gets no volume rows
    /// rather than a plausible-looking set belonging to the local disk.
    /// </summary>
    public static VolumeUsage? Read(string path)
    {
        if (Reader is { } stand) return stand(path);

        try
        {
            var drive = new DriveInfo(Path.GetFullPath(path));

            if (!drive.IsReady) return null;

            return new VolumeUsage(
                drive.RootDirectory.FullName,
                Ask(() => drive.VolumeLabel),
                Ask(() => drive.DriveFormat),
                drive.TotalSize,
                drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is ArgumentException or IOException
                                    or UnauthorizedAccessException
                                    or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The label and the format are the two a drive can refuse individually —
    /// an unformatted or encrypted volume answers IsReady and then throws on
    /// one of these — so neither is allowed to cost the numbers.
    ///
    /// **A GUARD, and unpinned on purpose.** Nothing here can make a real drive
    /// refuse its own label, so deleting this catch reddens no test. What it
    /// leads to is pinned:
    /// A_drive_that_will_not_name_itself_shows_no_empty_rows covers an empty
    /// answer producing no row, which is where a refusal ends up.
    /// </summary>
    private static string Ask(Func<string> read)
    {
        try { return read() ?? ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or NotSupportedException)
        {
            return "";
        }
    }

    /// <summary>
    /// The rows, or null where there are none worth drawing.
    ///
    /// Folders only. A file lives on a volume too, but "how much room is left"
    /// is a question about where you ARE, and the row would be noise on the
    /// properties of every file anybody opened.
    /// </summary>
    public static PropertyGroup? Describe(string path, bool isDirectory)
    {
        if (!isDirectory) return null;

        if (Read(path) is not { } volume) return null;

        // A pseudo filesystem — /proc, /sys, a FUSE mount that does not
        // account — reports zero, and "0 B free of 0 B" is worse than silence.
        if (volume.Total <= 0) return null;

        var rows = new List<PropertyRow>();

        if (AtRoot(path, volume.Root))
        {
            if (volume.Label.Length > 0) rows.Add(new PropertyRow("label", volume.Label));
            if (volume.Format.Length > 0) rows.Add(new PropertyRow("file system", volume.Format));

            rows.Add(new PropertyRow("capacity", ByteSize.Format(volume.Total)));
            rows.Add(new PropertyRow("used", ByteSize.Format(volume.Total - volume.Free)));
            rows.Add(new PropertyRow("free", ByteSize.Format(volume.Free)));
        }
        else
        {
            // Which volume, because the answer is otherwise unattributed: a
            // folder under a mount point is on a different disk from its
            // parent, and the figure would look like the parent's.
            rows.Add(new PropertyRow("volume", volume.Root));
            rows.Add(new PropertyRow(
                "free", $"{ByteSize.Format(volume.Free)} of {ByteSize.Format(volume.Total)}"));
        }

        return new PropertyGroup("volume", rows);
    }

    /// <summary>
    /// Whether the folder IS the volume — D:\ rather than something on it, / or
    /// /media/stick rather than a folder under one.
    ///
    /// Trimmed on both sides, and TrimEndingDirectorySeparator is the right
    /// tool for it: measured, it leaves a root alone ("D:\" and "/" come back
    /// unchanged) and trims elsewhere, so a folder handed over as
    /// "D:\git_projects\" still matches "D:\git_projects" and neither root is
    /// mangled into something that can never equal itself.
    /// </summary>
    private static bool AtRoot(string path, string root)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                Path.TrimEndingDirectorySeparator(root),
                PathRules.Comparison);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException
                                    or NotSupportedException)
        {
            return false;
        }
    }
}
