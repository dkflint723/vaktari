namespace Vaktari.Core.FileSystem;

/// <summary>
/// The archive, or part of it, is encrypted. Stage A refuses it in words;
/// asking for the password is a later stage.
///
/// **Never a hang and never a stack trace.** Without a password the formats
/// failed in five different ways (measured, E-3r and refutations 2, when 7z
/// and RAR were read too). So the refusal is decided up front, from the flag
/// a zip entry exposes before any byte is decoded, and a
/// <c>CryptographicException</c> on the way is mapped to it as well.
/// </summary>
public sealed class ArchivePasswordRequiredException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>
/// The archive is readable but will not be extracted, for a reason the
/// message states: split, sparse, too many entries, built to overlap, or no
/// room. Every one is decided before a byte is written, or stops the run and
/// has everything discarded.
/// </summary>
public sealed class ArchiveRefusedException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>
/// The archive stopped making sense part-way: truncated, corrupt, a CRC that
/// does not match, an entry longer than it said it was.
/// </summary>
/// <param name="EntriesBefore">How many entries were read whole before it —
/// what a partial listing could still show.</param>
public sealed class ArchiveDamagedException(string message, int entriesBefore, Exception? inner = null)
    : IOException(message, inner)
{
    public int EntriesBefore { get; } = entriesBefore;
}

/// <summary>
/// The file the archive is in could not be read — the drive went away, the
/// share dropped — as opposed to the archive being damaged. Its HResult is
/// the underlying failure's.
/// </summary>
public sealed class ArchiveUnreadableException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>The sentences, in one place so tests and callers agree on
/// them.</summary>
internal static class ArchiveSentences
{
    internal static string NotThisFormat(string leaf, ArchiveFormat format)
        => $"{leaf} is not a {ArchiveFormats.Word(format)} file, or is damaged";

    /// <summary>
    /// A format Vaktari recognises by its bytes and does not extract — a 7z,
    /// a RAR, an xz, bzip2, zstd or lzip stream — however the file is named.
    /// A refusal, not damage: the file is very likely intact, and what to do
    /// is open it elsewhere.
    /// </summary>
    internal static string NotSupported(string leaf, string word)
        => $"{leaf} is {(word is "xz" or "lzip" ? "an" : "a")} {word} archive. "
           + "Vaktari extracts zip and tar.gz archives only — open this one with another app";

    internal static string DamagedAfter(string leaf, int entries)
        => entries == 0
            ? $"{leaf} is damaged at its first entry — nothing was extracted"
            : $"{leaf} is damaged after {entries} {(entries == 1 ? "entry" : "entries")} — nothing was extracted";

    internal static string Unreadable(string leaf)
        => $"{leaf} could not be read — nothing was extracted";

    internal static string Password(string leaf)
        => $"{leaf} is password-protected — Vaktari cannot extract it yet";

    internal static string Split(string leaf)
        => $"{leaf} is one part of a split archive — Vaktari cannot extract split archives";

    internal static string Sparse(string leaf)
        => $"{leaf} holds a sparse file, which Vaktari cannot extract — nothing was extracted";

    internal static string Overlap(string leaf)
        => $"{leaf} is built to unpack to far more than it holds — nothing was extracted";

    internal static string TooMany(string leaf, int cap)
        => $"{leaf} holds more than {cap:N0} entries — Vaktari does not extract archives that large";

    internal static string ImpossibleSizes(string leaf)
        => $"{leaf} is damaged — it declares sizes no archive can hold";

    internal static string NothingWritten(string leaf, string leftOut)
        => $"nothing in {leaf} could be written: {leftOut}";

    internal static string Floor(string leaf, string drive)
        => $"stopped before {leaf} filled {drive} — nothing was extracted";
}
