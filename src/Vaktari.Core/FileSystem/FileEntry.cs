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
    Unreadable = 1 << 5,
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
