namespace Vaktari.Core.Sharing;

/// <summary>
/// A durable public link to something kept in a cloud drive.
///
/// <paramref name="LocalPath"/> is where the item lives on THIS machine — the
/// sync folder copy — and is what the context menu matches against.
/// <paramref name="RemotePath"/> is the drive's own name for it, which is what
/// every CLI call takes. <paramref name="Url"/> is what a friend receives.
/// </summary>
public sealed record DriveLink(string LocalPath, string RemotePath, string Url)
{
    public string Label => Vaktari.Core.FileSystem.PathRules.LeafName(LocalPath);
}

/// <summary>
/// Shares files by LINK, as distinct from <see cref="IFileSharing"/> which
/// shares by serving.
///
/// The two answer different needs and both belong on the menu: a copyparty
/// share is live only while Vaktari runs and shines on a LAN; a drive link
/// outlives the app, crosses the internet, and can carry a password and an
/// expiry — but only for things that live in the drive's sync folder.
///
/// Like the serving seam, Vaktari implements none of the hard part itself.
/// The provider drives an official client's own tooling, so the encryption
/// stays with the people who own it.
/// </summary>
public interface ILinkSharing
{
    /// <summary>False when the tool is not installed; the UI hides the feature.</summary>
    bool IsAvailable { get; }

    /// <summary>What the user needs to do, when unavailable.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Fetches and installs the tool, so the feature is a menu click rather
    /// than a hunt through a vendor's site. Same contract as
    /// <see cref="IFileSharing.InstallAsync"/>: progress lines are for a
    /// status bar, false means it did not work and a line already said why.
    /// Never automatic — putting software on someone's machine is something
    /// they choose.
    /// </summary>
    Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct);

    /// <summary>
    /// Runs the tool's own browser sign-in and waits for it to finish.
    ///
    /// <paramref name="openUrl"/> is called if the tool prints a link rather
    /// than opening the browser itself — both behaviours exist in the wild,
    /// and the difference must not become the user's problem. True when the
    /// sign-in completed; the session then lives in the operating system's
    /// credential store and Vaktari holds nothing.
    /// </summary>
    Task<bool> SignInAsync(Action<string> openUrl, CancellationToken ct);

    /// <summary>
    /// The drive's name for a local path, or null when the path does not live
    /// inside the drive's folder — in which case there is nothing remote to
    /// link to, and the menu should not offer it.
    /// </summary>
    string? MapToRemote(string localPath);

    /// <summary>Creates (or re-creates) the public link for an item.</summary>
    Task<DriveLink> CreateLinkAsync(string localPath, CancellationToken ct);

    /// <summary>Removes the public link. The item itself is untouched.</summary>
    Task RevokeAsync(DriveLink link, CancellationToken ct);
}
