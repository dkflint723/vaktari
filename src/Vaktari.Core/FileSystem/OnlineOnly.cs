namespace Vaktari.Core.FileSystem;

/// <summary>
/// Whether a file's data is somewhere other than this disk — a sync client's
/// online-only placeholder — so that reading any of it would download it.
///
/// **Reading a few bytes of such a file fetches it.** Measured against a cloud
/// files sync root the Windows tests register for themselves: a 32-byte header
/// read by <see cref="ImageSize"/> and the duplicate finder's first block each
/// made the filter ask the provider for the file, and left it on the disk.
/// Under the full-hydration policy that root registers, any read fetches all
/// of the file; under a partial one it fetches at least what was read.
/// Content search already refused these files; the image header read, the
/// thumbnail beside it and the duplicate scan did not, so previews of a synced
/// folder of photos, and a scan of one for copies, fetched what they looked at.
///
/// The question is a call into the operating system and this assembly makes
/// none, so it is a seam the platform fills in — the same arrangement as
/// <see cref="SafeWalk.ReparseTag"/> and <see cref="DuplicateFinder.Identity"/>.
/// Null where nothing is adopted, and then every file is taken to be on the
/// disk, which is what these readers did before they asked.
/// </summary>
public static class OnlineOnly
{
    /// <summary>
    /// The platform's answer for one path: true when its data is held online.
    /// Must not throw, and must not open the file for reading.
    /// </summary>
    public static Func<string, bool>? Test { get; set; }

    /// <summary>True when the platform says reading <paramref name="path"/> would download it.</summary>
    public static bool Is(string path) => Test is { } test && test(path);
}
