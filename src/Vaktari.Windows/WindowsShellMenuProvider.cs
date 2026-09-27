using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The platform's answer to "what does this desktop want to put on the menu".
///
/// A thin seam over <see cref="ShellContextMenu"/> so the view model can ask
/// for a menu without the UI assembly knowing that COM, apartments or menu
/// handles exist — the same shape every other provider here takes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellMenuProvider : IShellMenuProvider
{
    /// <summary>
    /// **No menu for a name the shell would read as another.** Both menus
    /// are bound through SHParseDisplayName, which takes "…\report " for
    /// "…\report" — measured, the seventh round's hunt (H6) — so the hosted
    /// menu's own Delete, Rename and Properties acted on the neighbour. The
    /// whole menu is withheld rather than built for the rest of a selection:
    /// a menu for fewer items than were selected acts on fewer than were asked.
    /// </summary>
    public async Task<IShellMenu?> BuildAsync(IReadOnlyList<string> paths)
        => paths.Any(path => WindowsLauncher.HandOff(path) is not null)
            ? null
            : await ShellContextMenu.ForAsync(paths).ConfigureAwait(false);

    public async Task<IShellMenu?> BuildBackgroundAsync(string folder)
        => WindowsLauncher.HandOff(folder) is not null
            ? null
            : await ShellContextMenu.ForBackgroundAsync(folder).ConfigureAwait(false);
}
