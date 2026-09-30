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
/// **A tar entry is held to the size it declares.** A tar has no total up
/// front, but each entry's header says its exact size, and the run stops an
/// entry that delivers more (the damage guard in the copy). So each entry is
/// asked about before it is written, as the engines ask: refused only when
/// its size, a cluster of <see cref="Slack"/> and <see cref="SizedMargin"/>
/// are more than is free. A 256 MiB floor asked here refused a 10 KB .tar
/// onto a drive with 200 MB free, however large the drive, where the same
/// files in a zip landed.
///
/// **A running floor for sizes nobody declared.** A bare .gz, .xz, .bz2,
/// .lz or .zst says nothing about its size before the bytes arrive, and a
/// 200 MB .xz of zeros is 29 KB on disk — a ratio of 6,800 that is perfectly
/// legitimate. Rather than ask about ratios, the run measures free space as
/// such a stream starts and gives it a budget: all of it but the reserve, or
/// half of it when the reserve is more than half (<see cref="StreamReserve"/>).
/// Its own bytes are held to that budget before they are written, and free
/// space is looked at again as it goes, for anyone else filling the drive.
///
/// **The reserve scales with the drive.** The reserve is 1% of the drive, never more
/// than 256 MiB and never less than 16 MiB (<see cref="FloorFor"/>): any
/// drive of 25.6 GB and up keeps the old 256 MiB exactly, and a stick, an SD
/// card or a small tmpfs gets a margin its own size. The check interval is a
/// quarter of the reserve (<see cref="IntervalFor"/>), so a run that was
/// above the reserve at one check has written at most a quarter of it by the
/// next — a stream cannot pass from "room left" to "disk full" between two
/// looks, however small the reserve.
/// </summary>
/// <param name="FreeBytes">Free space where a path lives, or null when it
/// cannot be read.</param>
/// <param name="DriveFormat">The filesystem a path lives on — "NTFS", "FAT32",
/// "vfat", "ext4" — or null.</param>
/// <param name="TotalBytes">The size of the drive a path lives on, or null
/// when it cannot be read — then the reserve is the full 256 MiB, as it was
/// before it scaled.</param>
internal sealed record ArchiveRoom(
    Func<string, long?> FreeBytes, Func<string, string?> DriveFormat, Func<string, long?>? TotalBytes = null)
{
    /// <summary>The most a run keeps free: what every drive kept before the
    /// reserve scaled, and still what any drive of 25.6 GB and up keeps.</summary>
    public const long MostReserve = 256L * 1024 * 1024;

    /// <summary>The least a run keeps free, however small the drive: four
    /// checks of 4 MiB, so the worst overshoot between two checks still
    /// leaves 12 MiB for the filesystem's own metadata and anyone else
    /// writing.</summary>
    public const long LeastReserve = 16L * 1024 * 1024;

    /// <summary>The share of the drive kept free: 1 in 100.</summary>
    public const long ReserveShare = 100;

    public const long FloorInterval = 64L * 1024 * 1024;

    /// <summary>The largest file FAT32 can hold.</summary>
    public const long Fat32Limit = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>What a cluster costs an entry, at most, on the usual
    /// filesystems: counted per entry so ten thousand tiny files are not
    /// measured as the few bytes they declare.</summary>
    public const long Slack = 4096;

    /// <summary>What a sized entry leaves free besides its own bytes and
    /// cluster: room for the folder entries, the filesystem's own metadata
    /// and the temporary name the entry is written under.</summary>
    public const long SizedMargin = 4L * 1024 * 1024;

    /// <summary>Whether an entry of this declared size would leave less than
    /// its cluster and <see cref="SizedMargin"/> free. Subtracted, never
    /// added: a PAX size of long.MaxValue plus a margin wraps negative and
    /// passes (second verification).</summary>
    public static bool TooBigFor(long size, long free) => size > free - Slack - SizedMargin;

    public static ArchiveRoom Real { get; } = new(FreeOn, FormatOf, TotalOn);

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

    /// <summary>How much a run leaves free on the drive a path lives on:
    /// 1% of it, between 16 MiB and 256 MiB, or 256 MiB when its size cannot
    /// be read.</summary>
    public long FloorFor(string destination)
        => TotalBytes?.Invoke(destination) is > 0 and var total
            ? Math.Clamp(total / ReserveShare, LeastReserve, MostReserve)
            : MostReserve;

    /// <summary>How often a run with no declared total looks at free space:
    /// every 64 MiB, or every quarter of a smaller reserve.</summary>
    public static long IntervalFor(long floor) => Math.Min(FloorInterval, floor / 4);

    /// <summary>
    /// What a stream with no declared size leaves free, from the free space
    /// found as it starts: the drive's reserve, or half of what was found
    /// when that is less. So it may write the rest — never less than half
    /// of what was free, and on a roomy drive all but the reserve — and a
    /// 5 KB .gz lands on a 1 TiB drive with 200 MiB free, where asking for
    /// the whole 256 MiB turned it away.
    /// </summary>
    public static long StreamReserve(long floor, long found) => Math.Min(floor, Math.Max(0, found) / 2);

    public bool BelowFloor(string destination, long floor) => FreeBytes(destination) is { } free && free < floor;

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

    private static long? TotalOn(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var at = OperatingSystem.IsWindows() ? Path.GetPathRoot(full) : full;

            return at is { Length: > 0 } ? new DriveInfo(at).TotalSize : null;
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
