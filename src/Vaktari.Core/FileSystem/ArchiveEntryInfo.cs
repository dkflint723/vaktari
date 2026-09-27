namespace Vaktari.Core.FileSystem;

/// <summary>What an archive entry is, as far as landing it is concerned.</summary>
public enum ArchiveEntryKind { File, Folder, SymbolicLink, HardLink, Special }

/// <summary>
/// One entry of an archive, whichever format it came out of.
/// </summary>
/// <param name="RawKey">Its name as the archive gave it, decoded but not yet
/// cut into segments — see <see cref="ArchiveKeys"/>.</param>
/// <param name="Size">What it claims to unpack to, or null when the format
/// does not say before the bytes arrive (a bare .gz).</param>
/// <param name="Crc">The CRC-32 the archive recorded, when there is one.</param>
/// <param name="CrcIsOurs">Whether nothing underneath checks
/// <paramref name="Crc"/>, so the extraction must. See
/// <see cref="ArchiveReader"/> for which formats that is.</param>
/// <param name="UnixMode">The permission bits a Unix-made entry carries.</param>
/// <param name="WindowsAttributes">What a Windows-made entry carries. Read,
/// and never applied: nothing an archive says becomes an attribute on
/// disk.</param>
/// <param name="LinkTarget">Where a link points, for a link.</param>
public sealed record ArchiveEntryInfo(
    string RawKey,
    ArchiveEntryKind Kind,
    long? Size,
    long? PackedSize,
    uint? Crc,
    bool CrcIsOurs,
    DateTimeOffset? Modified,
    int? UnixMode,
    FileAttributes? WindowsAttributes,
    bool Encrypted,
    string? LinkTarget);
