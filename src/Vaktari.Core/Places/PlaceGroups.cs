namespace Vaktari.Core.Places;

/// <summary>
/// The headings the sidebar draws over each run of places.
///
/// Named once rather than typed into each provider, because they are a UI
/// decision that both platforms have to make the same way — the sidebar
/// upper-cases whatever arrives and draws it, so a provider inventing its own
/// wording invents a section.
/// </summary>
public static class PlaceGroups
{
    /// <summary>Home and the desktop's own folders, plus whatever is pinned.</summary>
    public const string Places = "places";

    /// <summary>Drives and volumes attached to this machine.</summary>
    public const string Devices = "devices";

    /// <summary>
    /// Shares already connected: mapped drives on Windows, mounted network
    /// places on Linux — things you can open right now.
    ///
    /// **Deliberately not "network".** The sidebar has a literal NETWORK
    /// section directly below this one holding servers that are announcing
    /// themselves and have NOT been connected to, so with a mapped drive
    /// present the two drew consecutive identical headings — which reads as one
    /// section drawn twice rather than as two different things.
    /// </summary>
    public const string Shares = "shares";
}
