using Avalonia.Controls;

namespace Vaktari.Ui;

/// <summary>
/// What a closed window lets go of: its tree.
///
/// **A closed window went on paying for every change to the application's
/// resources until the collector took it** (batch-0.11.2c): its controls stayed
/// in its tree and each write to Application.Resources reached them all. See
/// MainWindow.OnClosed, which does the same after its shell is disposed, and
/// ClosedWindowResourceTests. The fields the markup names still hold their
/// controls, so a dialog read after it has closed reads what it held.
/// </summary>
internal static class ClosedWindow
{
    /// <summary>Empties <paramref name="window"/> once it has closed.</summary>
    public static void LetGo(Window window) => window.Closed += (_, _) => window.Content = null;
}
