namespace Vaktari.Core.Places;

/// <summary>A remote location that the desktop has made reachable as a path.</summary>
public sealed record RemoteMount
{
    /// <summary>Where it appears on the local filesystem.</summary>
    public required string Path { get; init; }

    /// <summary>What to show the user: "media on nas", "example.com".</summary>
    public required string Label { get; init; }

    /// <summary>smb, sftp, ftp, dav, mtp, …</summary>
    public required string Protocol { get; init; }

    /// <summary>
    /// False when the mount point exists but the far end is gone. A dead mount
    /// lists as an empty folder, which is indistinguishable from an empty share
    /// unless something says otherwise.
    /// </summary>
    public required bool Reachable { get; init; }
}

/// <summary>
/// Finds remote locations the desktop has already mounted, and asks it to mount
/// new ones.
///
/// Vaktari does not speak SMB, SFTP or MTP itself. KIO and gvfs already do, and
/// both expose their mounts as ordinary paths — kio-fuse under
/// /run/user/$UID/kio-fuse-*, gvfs under /run/user/$UID/gvfs. Consuming those
/// means every protocol the desktop supports works here for the cost of reading
/// a directory, instead of reimplementing a protocol stack per scheme.
/// </summary>
public interface IRemoteMounts
{
    /// <summary>False when no mount helper is present on this system.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// What to put in the connect prompt before the user types, and what to
    /// offer underneath it.
    ///
    /// Here rather than in the view because the answer is a property of the
    /// mounter: `smb://` is right where gio does the work and wrong where the
    /// Windows redirector does, and a prompt that suggests `sftp://` on a
    /// system that cannot mount it is worse than one that suggests nothing.
    /// </summary>
    string AddressPrefill { get; }

    /// <summary>The address forms this accepts, as one short line.</summary>
    string AddressHint { get; }

    /// <summary>Everything currently mounted. Cheap enough to poll.</summary>
    IReadOnlyList<RemoteMount> Discover();

    /// <summary>
    /// Asks the desktop to mount a URI such as <c>smb://nas/media</c>, and
    /// returns where it landed.
    /// </summary>
    Task<RemoteMount> MountAsync(string uri, CancellationToken ct);

    /// <summary>
    /// Disconnects a mount. Returns false when the desktop refuses — usually
    /// because something still has a file open on it, which is worth saying
    /// rather than retrying behind the user's back.
    /// </summary>
    Task<bool> UnmountAsync(RemoteMount mount, CancellationToken ct);

    /// <summary>
    /// Gives back a connection named by the path it appears at.
    ///
    /// **A mapped drive is not one of the mounts this reports.** Discover names
    /// the letterless connections Vaktari made itself, deliberately, so a drive
    /// the person mapped does not appear in the sidebar twice — but its row
    /// still needs a way to be given back, and Z: is what identifies it.
    ///
    /// Defaulted to "there is nothing here I can disconnect", because a place
    /// only offers the verb when its provider says the platform can: Linux's
    /// network places are cifs and nfs mounts read out of /proc/mounts, and
    /// `gio mount -u` has no authority over a kernel mount.
    /// </summary>
    Task<bool> DisconnectAsync(string path, CancellationToken ct)
        => Task.FromResult(false);
}
