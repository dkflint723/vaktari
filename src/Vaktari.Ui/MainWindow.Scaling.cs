using Avalonia;

namespace Vaktari.Ui;

/// <summary>
/// Where the interface scale gets re-applied.
///
/// Two members and one edge between them. ApplyScales writes the
/// application-level metrics — the resource keys every template reads — and
/// RescaleForDesktop calls it and then asks the shell to redo each pane's own
/// scale as well, which is why this file is not "the application-level half"
/// of anything. It is the place the scale is put back.
///
/// **Three ways in, all of them staying behind.** The constructor calls
/// ApplyScales directly before the first paint, hands it to the shell as its
/// ScaleApplier so a session restore can re-run it, and builds the
/// desktop-scheme handler that calls RescaleForDesktop. The settings window's
/// Closed handler, now in its own file, calls ApplyScales when a save changes
/// the numbers.
///
/// The ScaleApplier hand-over is the one to notice, because it is a
/// method-group assignment rather than a call: searching for ApplyScales with
/// an open bracket does not find it, and a reader who counts call sites that
/// way concludes the settings dialog is the only live caller. It is in fact
/// the rarest of the three — the shell assigns its own scale pair in one
/// place, during a session restore — while the constructor's direct call is
/// what every window's first paint comes from.
///
/// **Nothing is stranded in either direction.** Neither member declares a
/// field. ApplyScales touches no member of this window at all; RescaleForDesktop
/// reads only the shell. Nothing left behind exists to serve them: the three
/// things that name them are two calls and that assignment, all inside the
/// constructor, which does not move.
///
/// RescaleForDesktop is a method rather than an inline lambda for a reason its
/// own comment gives, and that reason is about when it is built, not about
/// where it lives.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Application-level defaults, used by everything outside a pane — the
    /// sidebar, the status bar, the properties window. Each pane overrides
    /// these with its own dictionary via PaneScale.
    /// </summary>
    private void ApplyScales(double fontScale, double iconScale)
    {
        var target = Application.Current?.Resources ?? Resources;

        foreach (var (key, value) in PaneScale.Compute(fontScale, iconScale))
            target[key] = value;
    }

    /// <summary>
    /// Everything a desktop scheme change moves that is not a colour: the
    /// application-level metrics and each pane's own.
    ///
    /// The desktop's text size arrives on the palette, and every size in the
    /// window multiplies it, so the read that repaints has to be followed by
    /// the arithmetic that resizes.
    ///
    /// **A method rather than two lines inside the handler**, because the
    /// handler is built in the constructor — where <c>_shell</c> has not been
    /// assigned yet, so the compiler rightly refuses to let a lambda defined
    /// there dereference it.
    /// </summary>
    private void RescaleForDesktop()
    {
        ApplyScales(_shell.FontScale, _shell.IconScale);
        _shell.RefreshPaneScales();
    }
}
