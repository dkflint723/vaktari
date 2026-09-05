namespace Vaktari.Core.FileSystem;

/// <summary>
/// Cross-platform entry flags. Deliberately not System.IO.FileAttributes, which
/// is Windows-shaped and carries a dozen values that mean nothing on ext4.
/// </summary>
[Flags]
public enum EntryFlags
{
    None       = 0,
    Directory  = 1 << 0,
    Hidden     = 1 << 1,
    Symlink    = 1 << 2,
    ReadOnly   = 1 << 3,
    System     = 1 << 4,

    /// <summary>
    /// What is behind this row cannot be read right now: an unmounted volume, a
    /// share whose server is not answering.
    ///
    /// **Declared when this enum was written and set by nothing for as long.**
    /// The one listing that knows about availability is This PC, which is built
    /// from the same places the sidebar shows — and it dropped the place's
    /// IsAvailable on the floor, so a mapped drive whose server had gone away
    /// drew exactly like a drive you can open, while the sidebar three inches
    /// away had been dimming that very place all along.
    /// </summary>
    Unreadable = 1 << 5,

    /// <summary>
    /// A whole volume rather than a directory on one — a drive row in This PC.
    ///
    /// Nothing else can tell the two apart, and that is deliberate: a drive is
    /// a directory to everything downstream, which is what makes sorting,
    /// selection and the three layouts work on This PC unchanged. The one place
    /// the difference is real is the size cell, where a drive has a capacity
    /// and a folder has a count.
    /// </summary>
    Volume     = 1 << 6,
}

/// <summary>
/// One directory entry. A readonly struct because a 200k-file directory means
/// 200k of these, and the enumeration path must not allocate per item.
/// </summary>
/// <remarks>
/// Every field here comes from a single directory read. Nothing on this type
/// may require a follow-up stat — that is the rule that keeps SMB usable.
/// Anything more expensive (icon, thumbnail, owner, ACL) is fetched lazily and
/// only for rows currently in the viewport.
/// </remarks>
public readonly record struct FileEntry(
    string Name,
    string FullPath,
    long Length,
    DateTimeOffset LastWriteTime,
    EntryFlags Flags,

    // When the file was created, where the platform records one.
    //
    // **Widening this type is a decision, so it was measured rather than
    // assumed.** On this machine, 50,000 empty files on a local NVMe with the
    // cache warm, Release, median of seven warm runs through
    // WindowsFileSystemProvider.EnumerateAsync:
    //
    //   * without this member — Unsafe.SizeOf 48 bytes, 19 ms, 12.3 MB
    //     allocated;
    //   * with it — 64 bytes, 20 ms, 13.0 MB. The seven timings were 17-20 ms
    //     and 18-21 ms, so the sixteen bytes cost nothing this bench can
    //     separate from noise. That is why this is a DateTimeOffset like
    //     LastWriteTime beside it rather than a narrower field spelled
    //     differently;
    //   * with the same value read back per entry by File.GetCreationTimeUtc
    //     — 1,568 ms, seventy-eight times the cost of the whole enumeration it
    //     was added to. Repeated on the no-member build it was 1,948 ms, so
    //     that ratio moves with the weather; two orders of magnitude is the
    //     part that reproduces.
    //
    // That last bullet is the whole argument for it being here: creation time is
    // already in the record the OS hands back for every entry, so a column that
    // fetched it later would be paying for a second read of something we had
    // and dropped. The remarks above still hold — nothing here needs a
    // follow-up stat.
    //
    // Defaulted, because the listings that build entries from something other
    // than a directory — This PC's drives, Recent, the path bar — have no
    // creation date to give. default is below the Unix epoch, which is the "no
    // answer" the date converter already renders as an empty cell.
    DateTimeOffset CreationTime = default)
{
    public bool IsDirectory => (Flags & EntryFlags.Directory) != 0;
    public bool IsHidden    => (Flags & EntryFlags.Hidden) != 0;
    public bool IsSymlink   => (Flags & EntryFlags.Symlink) != 0;
    public bool IsVolume    => (Flags & EntryFlags.Volume) != 0;

    /// <summary>Set only by This PC, for a drive that is not there.</summary>
    public bool IsUnreadable => (Flags & EntryFlags.Unreadable) != 0;

    /// <summary>
    /// Concealed by the LISTING's rule rather than by the platform's: Windows
    /// hides System alongside Hidden, and the flags carry them separately.
    /// </summary>
    public bool IsConcealed => IsHidden || (Flags & EntryFlags.System) != 0;

    public ReadOnlySpan<char> Extension => IsDirectory ? default : ExtensionOf(Name);

    /// <summary>
    /// The extension rule on its own, for the one kind of caller that has a
    /// NAME and no entry to ask yet.
    ///
    /// **The rule was about to be written a second time, to decide whether a
    /// row is a link.** The Windows providers work a row's flags out from the
    /// directory entry the OS handed them, while the FileEntry that would
    /// answer this is still being constructed — and a private copy of "the
    /// last dot, and never the first character" there is free to drift from
    /// this one. That is the same drift the properties window's Kind was
    /// pulled back from, which is why <see cref="FileKind.IsShortcut"/> is
    /// public and platform-blind.
    ///
    /// A leading dot begins a name rather than an extension: ".gitignore" is a
    /// file called .gitignore, not a GITIGNORE file.
    /// </summary>
    public static ReadOnlySpan<char> ExtensionOf(ReadOnlySpan<char> name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? default : name[(dot + 1)..];
    }
}
