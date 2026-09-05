using Avalonia;
using Avalonia.Controls;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// How many real pixels a control given a size in logical units actually
/// covers.
///
/// **Every picture in a listing was asked for in logical units and drawn into
/// device ones.** The row templates ask for 32, 48 and 64; those are layout
/// units, and on a display at 150% one of them is one and a half pixels. So a
/// 32 became 48 pixels of screen with a 32-pixel bitmap stretched over it, on
/// every icon and every thumbnail in every listing — which is most of what a
/// file manager draws. Nothing anywhere in this assembly read RenderScaling
/// before this; a repository-wide search for it returned nothing at all.
///
/// **Measured, twice.** The shell hands back what it is asked for: asking
/// WindowsFileIcons for 16, 32, 40, 48, 64 and 96 returned 16x16, 32x32, 48x48,
/// 48x48, 96x96 and 96x96 — it rounds UP to a size it composes at, and it never
/// consults the display. And RenderScaling is the ratio between the two units
/// by definition, so a 32-unit slot at 1.5 is 48 pixels of screen and the
/// bitmap that fills it has to be 48 too.
///
/// This does not change what anything is asked to be: the Image is still 32
/// units wide and still lays out identically. It changes only how much detail
/// is fetched to fill it.
/// </summary>
internal static class DeviceSize
{
    /// <summary>
    /// Stands in for the display in tests, the same seam the Linux mount table
    /// and the Windows Quick access walk are given and for the same reason:
    /// the headless platform the UI tests run on renders at 1.0 and offers no
    /// way to claim otherwise — RenderScaling is not virtual on Window and
    /// comes from the platform implementation — so without this nothing could
    /// tell a row that asks in device pixels from one that does not. Null in
    /// the application.
    /// </summary>
    internal static Func<Visual, double>? ScalingOverride { get; set; }

    /// <summary>
    /// The size to ask a provider for, given the size the markup asked the
    /// control to be.
    ///
    /// **1.0 when there is no window yet**, which is the honest answer rather
    /// than a defensive one: a control that has not been attached has no
    /// display to be scaled for, and the row is realized again — with a top
    /// level — before anything is drawn.
    ///
    /// **The real read is a GUARD, and this says so rather than pretending
    /// otherwise.** A test can stand in for the display through the seam above,
    /// which is what pins the two call sites; what no test here can reach is
    /// the TopLevel read itself, because the headless platform renders at 1.0
    /// and offers no way to claim a different scaling. So "reads the top
    /// level's scaling" and "returns 1.0" are indistinguishable in this suite,
    /// and only the fallback and the arithmetic are pinned.
    /// </summary>
    internal static int For(Visual visual, int logical)
        => Scale(
            ScalingOverride?.Invoke(visual) ?? TopLevel.GetTopLevel(visual)?.RenderScaling ?? 1.0,
            logical);

    /// <summary>
    /// The arithmetic on its own, so the question can be asked about a scaling
    /// this machine is not running at.
    /// </summary>
    internal static int Scale(double scaling, int logical)
    {
        if (logical <= 0) return logical;

        // Anything at or below 1 is left exactly alone, so a display at 100%
        // asks for the same number it always did and shares the same cache
        // entries. Guards a nonsense scaling too, which would otherwise ask
        // for a zero-pixel icon.
        if (scaling <= 1.0) return logical;

        // Up, not to nearest: half a pixel short is still a stretch, and the
        // providers round up to a size they have anyway.
        return (int)Math.Ceiling(logical * scaling);
    }
}
