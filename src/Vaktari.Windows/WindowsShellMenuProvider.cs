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
    public async Task<IShellMenu?> BuildAsync(IReadOnlyList<string> paths)
        => await ShellContextMenu.ForAsync(paths).ConfigureAwait(false);
}
