namespace Vaktari.Core.FileSystem;

/// <summary>
/// One row of the desktop's own context menu, as the desktop reported it.
///
/// **The entries in a Windows right-click menu that make it feel full are not
/// Windows.** 7-Zip, VLC, Warp, WizTree, Defender, OneDrive — each is a COM
/// handler registered by an installed application, and there is no list of them
/// to hand-write. The only way to show them is to ask the shell for its menu and
/// render what comes back.
///
/// A record of what the shell said, deliberately: the shell owns a native menu
/// handle full of platform types, and letting that reach the view model would
/// put Win32 in the one assembly that has to build for Linux too.
/// </summary>
/// <param name="Label">Already stripped of the accelerator ampersands the shell
/// puts in for Alt-navigation; the menu draws the text as given.</param>
/// <param name="Id">What <see cref="IShellMenu.Invoke"/> takes. Meaningless to
/// anyone else — it is an offset into one particular menu, valid only for the
/// <see cref="IShellMenu"/> it came from and only while that is alive.</param>
public sealed record ShellMenuEntry(
    string Label,
    int Id,
    bool IsSeparator = false,
    bool IsEnabled = true,
    IReadOnlyList<ShellMenuEntry>? Children = null)
{
    public IReadOnlyList<ShellMenuEntry> Items => Children ?? [];

    public bool HasChildren => Items.Count > 0;
}

/// <summary>
/// A live shell context menu: the entries, and the ability to invoke one.
///
/// **Alive is the operative word.** The handlers behind these entries are COM
/// objects living on one apartment-bound thread, and an id is an offset into
/// the menu they built. Disposing this releases them, after which the ids mean
/// nothing — so it has to outlive the menu the user is looking at and be
/// disposed when that closes, not before.
/// </summary>
public interface IShellMenu : IDisposable
{
    /// <summary>What the shell offered. Empty when nothing did, which is a
    /// normal answer and not an error.</summary>
    IReadOnlyList<ShellMenuEntry> Entries { get; }

    /// <summary>
    /// Run one entry. Does nothing for an id this menu did not issue, because
    /// the alternative is handing an arbitrary number to a third party's
    /// handler and finding out what it does with it.
    /// </summary>
    void Invoke(int id);
}

/// <summary>
/// Builds the desktop's own context menu for a selection.
///
/// **Null from Build is the normal way to say "this desktop has no such
/// thing"**, which is every desktop but Windows today: the freedesktop world
/// has no equivalent, so the menu simply does not offer the entry rather than
/// offering an empty one.
/// </summary>
public interface IShellMenuProvider
{
    /// <summary>
    /// The menu for these paths, or null if there is none to give.
    ///
    /// **Asynchronous because building has no honest time limit.** Every shell
    /// extension on the machine gets a turn, and how long that takes is a fact
    /// about the machine rather than about this code: a first right-click after
    /// boot pages in a dozen handler DLLs. A synchronous seam would force
    /// whoever called it to either block a thread for that whole time or pick a
    /// deadline and call a slow answer no answer — which is exactly what used
    /// to happen. Returning a task means the wait costs nothing, so it need not
    /// be cut short.
    ///
    /// **The provider owns the wait, deliberately, and no caller may take it
    /// back.** A bound belongs at a call site that has somewhere to put a
    /// partial answer — a headless export or a scripted invoke could wrap this
    /// in its own WaitAsync and report "the shell did not answer" — never here,
    /// where the only thing on the other end is a menu the user is looking at
    /// and the only alternative to waiting is lying.
    ///
    /// **Never faults.** It runs a third party's code, and a context menu is
    /// not worth failing to open because one of them is unhappy.
    /// </summary>
    Task<IShellMenu?> BuildAsync(IReadOnlyList<string> paths);
}
