namespace Vaktari.Core;

/// <summary>What another application asked the file manager to do.</summary>
public enum ShowKind
{
    /// <summary>Show each item where it lives, with it HIGHLIGHTED. This is what
    /// a browser's download panel sends, and the highlight is the whole request
    /// — opening the folder and selecting nothing answers a different
    /// question.</summary>
    Items,

    /// <summary>Open each folder. No selection is implied.</summary>
    Folders,

    /// <summary>Open the properties of each item.</summary>
    ItemProperties,
}

/// <summary>One request, already turned into paths this machine can open.</summary>
public sealed record ShowRequest(ShowKind Kind, IReadOnlyList<string> Paths);

/// <summary>
/// Where answering for the desktop ended up.
///
/// **Three of these four are ways of doing nothing, and they are separate
/// because they need separate sentences** — "another file manager is your
/// default", "another file manager got there first" and "this session has no
/// message bus" are three different things for a user to do something about,
/// and one shared "unavailable" would tell them which of the three it was:
/// none.
/// </summary>
public enum FileManagerServiceState
{
    NotDefault,
    Serving,
    Taken,
    Unavailable,
}

public static class FileManagerServiceStates
{
    /// <summary>
    /// One sentence per state, used by both the settings page and the startup
    /// log — so the window and the terminal cannot describe the same state two
    /// different ways.
    /// </summary>
    public static string Describe(FileManagerServiceState state) => state switch
    {
        FileManagerServiceState.Serving =>
            "Other applications can now ask Vaktari to show a file in its folder.",

        FileManagerServiceState.NotDefault =>
            "Another file manager opens folders, so it answers \"show in folder\" "
            + "rather than Vaktari.",

        FileManagerServiceState.Taken =>
            "Another file manager is running and already answers \"show in folder\". "
            + "Close it and restart Vaktari to take that over.",

        FileManagerServiceState.Unavailable =>
            "This session has no message bus, so \"show in folder\" cannot reach Vaktari.",

        _ => "",
    };
}

/// <summary>
/// Answering the desktop's "show me this file where it lives".
///
/// **A desktop-wide role, not a private channel**, and that is why it is not
/// folded into SingleInstance. The socket in SingleInstance is Vaktari talking
/// to Vaktari and belongs to whoever starts first. This is a role the DESKTOP
/// assigns, held by one application at a time for the whole session, and the
/// user assigns it by choosing a default file manager — so the two must not
/// share a lifetime or a rule.
///
/// Null on a platform with no such role, which is Windows: there is nothing to
/// claim, and every gesture that would use one already goes through the shell.
/// </summary>
public interface IFileManagerService : IDisposable
{
    /// <summary>
    /// Another application has asked for something.
    ///
    /// **Raised off the UI thread, and the connection reads no further messages
    /// until the handler returns.** The subscriber marshals and returns at once
    /// — the same arrangement SingleInstance describes, made from the other
    /// side, because this assembly has no toolkit to marshal with.
    /// </summary>
    event EventHandler<ShowRequest>? Requested;

    /// <summary>
    /// Brings this into line with whether Vaktari is the desktop's file
    /// manager: claims the role, or gives it up. Called once at startup and
    /// again whenever that setting changes. Never throws.
    ///
    /// One verb rather than Start/Stop, because there is only ever one right
    /// answer and it is derived from a boolean somebody else owns. A pair would
    /// let the two drift.
    /// </summary>
    Task<FileManagerServiceState> ReconcileAsync();
}
