namespace Vaktari.Core.FileSystem;

/// <summary>
/// Whether an extraction has room, up front and while it runs.
///
/// **The copy engines' rule, not a margin of our own.** Refused only when
/// what is needed is more than what is free, and never when free space cannot
/// be read — a network share often cannot say, and refusing because the
/// question failed would be worse than trying (the engines' own
/// <c>FreeSpaceOn</c> says the same). A draft rule of "total plus 1 GiB"
/// refused a 10 KB zip onto a stick with 500 MB free (Core-6).
///
/// **A running floor for sizes nobody declared.** A tar stream and a bare
/// .gz say nothing about their size before the bytes arrive, and a 200 MB
/// tar.xz of zeros is 29 KB on disk — a ratio of 6,800 that is perfectly
/// legitimate. Rather than ask about ratios, the run checks free space every
/// 64 MiB it writes and stops once less than 256 MiB is left, before the
/// disk is full rather than when it is.
/// </summary>
/// <param name="FreeBytes">Free space where a path lives, or null when it
/// cannot be read.</param>
/// <param name="DriveFormat">The filesystem a path lives on — "NTFS", "FAT32",
/// "vfat", "ext4" — or null.</param>
internal sealed record ArchiveRoom(Func<string, long?> FreeBytes, Func<string, string?> DriveFormat)
{
    public const long StreamFloor = 256L * 1024 * 1024;
    public const long FloorInterval = 64L * 1024 * 1024;

    /// <summary>The largest file FAT32 can hold.</summary>
    public const long Fat32Limit = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>What a cluster costs an entry, at most, on the usual
    /// filesystems: counted per entry so ten thousand tiny files are not
    /// measured as the few bytes they declare.</summary>
    public const long Slack = 4096;

    public static ArchiveRoom Real { get; } = new(FreeOn, FormatOf);

    public string? RefuseUpFront(string destination, long declaredTotal, int entries, string leaf)
    {
        if (FreeBytes(destination) is not { } free) return null;

        // **Saturated, never wrapped.** A Zip64 size of long.MaxValue plus
        // the slack overflowed to a negative need, which every disk has room
        // for; the run then decoded until the disk filled (verification of
        // Stage A).
        var slack = Math.Max(0, entries) * Slack;
        var needed = declaredTotal > long.MaxValue - slack ? long.MaxValue : Math.Max(0, declaredTotal) + slack;

        return needed > free
            ? $"there is not enough room on {Drive(destination)} — {leaf} needs {ByteSize.Format(needed)}, {ByteSize.Format(free)} is free"
            : null;
    }

    public bool BelowFloor(string destination) => FreeBytes(destination) is { } free && free < StreamFloor;

    public bool IsFat32(string destination)
        => DriveFormat(destination)?.ToUpperInvariant() is "FAT32" or "FAT" or "VFAT" or "MSDOS";

    /// <summary>What a sentence calls the place: the drive on Windows, the
    /// folder's own name elsewhere, as the engines say it.</summary>
    internal static string Drive(string destination)
        => OperatingSystem.IsWindows() && Path.GetPathRoot(destination) is { Length: > 0 } root
            ? root.TrimEnd('\\', '/')
            : PathRules.LeafName(destination);

    private static long? FreeOn(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var at = OperatingSystem.IsWindows() ? Path.GetPathRoot(full) : full;

            return at is { Length: > 0 } ? new DriveInfo(at).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? FormatOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var at = OperatingSystem.IsWindows() ? Path.GetPathRoot(full) : full;

            return at is { Length: > 0 } ? new DriveInfo(at).DriveFormat : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
