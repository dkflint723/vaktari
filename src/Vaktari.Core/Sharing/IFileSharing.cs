namespace Vaktari.Core.Sharing;

/// <summary>A folder currently being served, and where to reach it.</summary>
public sealed record ShareSession
{
    public required string Path { get; init; }

    /// <summary>The address to hand someone, password included: opening it
    /// logs the browser in, and the login box takes the same password.</summary>
    public required string Url { get; init; }

    public required int Port { get; init; }
    public required bool Writable { get; init; }

    /// <summary>The password this share was started with. Every share has
    /// one, made for it; nobody browses anonymously.</summary>
    public string Password { get; init; } = "";

    /// <summary>Something the person sharing should hear once, from the
    /// platform — that the network is one Windows marks public, say. Null
    /// when there is nothing to say, which is nearly always.</summary>
    public string? Warning { get; init; }

    /// <summary>Opaque handle the provider uses to stop this share.</summary>
    public required object Handle { get; init; }

    public string Label => Vaktari.Core.FileSystem.PathRules.LeafName(Path);
}

/// <summary>
/// How a folder is to be served.
/// </summary>
/// <param name="Writable">Whether people can add and overwrite files.
/// Read-only is the default, because this opens a folder to the network.</param>
/// <param name="Announce">Whether to announce the share over mDNS and SSDP,
/// so it appears by name in file managers and in Windows Explorer across the
/// network. **Off unless asked.** It was always on, so a folder handed to
/// one person by its address was shown to everyone in the building by name.</param>
public sealed record ShareOptions(bool Writable, bool Announce = false);

/// <summary>
/// Serves a local folder over the network.
///
/// Vaktari does not implement this itself — it drives an existing server
/// (copyparty). Writing an HTTP/WebDAV server with resumable uploads, dedup and
/// a browser UI is a project in its own right, and a good one already exists
/// under a permissive licence. This interface is the seam that lets Vaktari use it
/// without depending on its internals.
///
/// Platform-specific because *locating and launching* a server differs by OS,
/// even though the concept does not.
/// </summary>
public interface IFileSharing
{
    /// <summary>False when no server is installed; the UI hides the feature.</summary>
    bool IsAvailable { get; }

    /// <summary>What the user needs to install, when unavailable.</summary>
    string? UnavailableReason { get; }

    IReadOnlyList<ShareSession> Active { get; }

    /// <summary>
    /// Installs the backend, reporting progress as it goes.
    ///
    /// Deliberately explicit rather than automatic on first use: this installs
    /// software on the user's machine, which should be something they chose,
    /// not something that happened while they were trying to do something else.
    /// </summary>
    Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct);

    /// <summary>
    /// Starts serving a folder, the way <paramref name="options"/> says. Every
    /// share gets a password of its own, and the session carries it.
    /// </summary>
    Task<ShareSession> StartAsync(string path, ShareOptions options, CancellationToken ct);

    Task StopAsync(ShareSession session);

    /// <summary>Stops everything. Called on shutdown so nothing outlives the app.</summary>
    Task StopAllAsync();

    event EventHandler? Changed;
}
