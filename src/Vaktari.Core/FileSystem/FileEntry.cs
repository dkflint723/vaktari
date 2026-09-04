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
    EntryFlags Flags)
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

    public ReadOnlySpan<char> Extension
    {
        get
        {
            if (IsDirectory) return default;
            var i = Name.LastIndexOf('.');
            return i <= 0 ? default : Name.AsSpan(i + 1);
        }
    }
}
