namespace Vaktari.Core.Places;

/// <summary>An image file that is currently attached, and where its contents
/// appear.</summary>
public sealed record MountedImage(string ImagePath, string MountPath);

/// <summary>
/// Mounting a disk image — an .iso, mostly — so its contents can be browsed as
/// a drive, and detaching it again.
///
/// **The two platforms share nothing here but this interface.** Windows has a
/// virtual-disk service that attaches an image as a real device; Linux hands a
/// loop device to udisks2. There is no common mechanism to factor out, so the
/// only thing in Core is the vocabulary — and the extension gate, which is a
/// decision about what to OFFER and belongs where both sides can be held to it.
/// </summary>
public interface IDiskImages
{
    /// <summary>False when this machine has no way to mount an image — an
    /// unprivileged Linux desktop with no udisks2. The menu offers nothing
    /// rather than an entry that fails.</summary>
    bool IsAvailable { get; }

    /// <summary>What is missing, when it is.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Whether this file is one this platform can actually mount.
    ///
    /// **Deliberately narrower than "is it a disk image".** FileCategories
    /// already answers the broad question, because it is picking an icon and a
    /// coarse answer is the right one there. Offering a MOUNT verb is a promise
    /// the file will open, so .dmg and .qcow2 are excluded on both platforms
    /// (neither desktop can read them natively) and .vhdx is excluded on
    /// Windows for a different reason — see the implementation.
    /// </summary>
    bool CanMount(string path);

    /// <summary>Attaches the image and answers where its contents appear.</summary>
    Task<MountedImage> MountAsync(string imagePath, CancellationToken ct);

    /// <summary>Detaches it. The image file itself is untouched.</summary>
    Task UnmountAsync(string imagePath, CancellationToken ct);

    /// <summary>
    /// Where this image is mounted right now, or null when it is not — which is
    /// what flips the menu entry between Mount and Unmount.
    /// </summary>
    MountedImage? MountOf(string imagePath);
}
