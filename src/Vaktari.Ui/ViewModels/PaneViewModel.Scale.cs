using CommunityToolkit.Mvvm.ComponentModel;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// How big this pane draws itself: the font, the icons, and the step between
/// one view mode and the next.
///
/// **A pane's zoom is its own, and it is per view mode.** Zooming the icon
/// grid must not resize the details list, so the scales are kept per mode and
/// swapped when the mode changes — which is why there is a map here rather
/// than two numbers. A second pane in a split keeps its own, and AdoptViewOf
/// is the one route by which one pane deliberately takes another's.
///
/// Split out of PaneViewModel under roadmap 22, and the banner it lived under
/// said "dynamic columns" — which it had not been since the column rules moved
/// to PaneViewModel.Columns. Nothing here changed but the file it is in.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>
    /// Set from the window's UI scale. Column content grows with the type
    /// scale, so the widths at which columns stop fitting have to grow with it
    /// too — fixed thresholds meant that at 2x every column still claimed to
    /// fit while overflowing the pane.
    ///
    /// How wide this pane's text is, as a multiple of the size the columns were
    /// measured at. Column thresholds are about how much room *text* needs, so
    /// they follow the font axis — not the icon one. This was left orphaned by
    /// the font/icon split and nothing assigned it, so the thresholds silently
    /// stopped following the text size.
    ///
    /// The product of this pane's own zoom and the interface size, never either
    /// one alone, and written from <see cref="SyncTextScale"/> only — which the
    /// constructor calls, because a pane created at 200% is not a pane whose
    /// zoom has changed and nothing else would have told it.
    /// </summary>
    [ObservableProperty] private double _textScale = 1.0;

    /// <summary>
    /// Type and icon scale for THIS pane. Per tab and per split side, because a
    /// reference listing beside a working one wants different sizes — which is
    /// the whole reason for having two panes.
    /// </summary>
    [ObservableProperty] private double _fontScale = 1.0;
    [ObservableProperty] private double _iconScale = 1.0;

    /// <summary>
    /// Scale per LAYOUT, not per pane.
    ///
    /// The three modes want genuinely different proportions — a grid tile and a
    /// details row are not the same object at different zooms — so one shared
    /// pair meant enlarging the grid also enlarged the details rows, and the
    /// size readout showed a number that did not describe what was on screen.
    ///
    /// `FontScale` and `IconScale` remain the ACTIVE pair, because everything
    /// downstream reads them: the metric pipeline, the column thresholds, the
    /// typed-size flyout. This dictionary is only what the inactive modes are
    /// holding while they wait.
    /// </summary>
    private readonly Dictionary<ViewMode, (double Font, double Icon)> _scales = new()
    {
        [ViewMode.Details] = (1.0, 1.0),
        [ViewMode.Grid]    = (1.0, 1.0),
        [ViewMode.Compact] = (1.0, 1.0),
    };

    /// <summary>
    /// True only while a mode switch is loading the incoming mode's pair.
    /// Without it the assignment would immediately record itself back into the
    /// slot it just came from, and every mode would converge on one value again
    /// — the bug this exists to fix, reintroduced by its own fix.
    /// </summary>
    private bool _swappingScales;

    /// <summary>
    /// Gives every mode the same starting pair. Used when a session or a folder
    /// supplies one scale: it expressed an opinion about the pane, not about a
    /// particular layout, so no mode should be left at a stale 1.0.
    /// </summary>
    private void SeedScales(double font, double icon)
    {
        foreach (var mode in _scales.Keys.ToList()) _scales[mode] = (font, icon);
    }

    /// <summary>
    /// The bases the scales multiply. Exposed as real sizes rather than as a
    /// multiplier, because "14" is something a person can reason about and
    /// "1.15" is not.
    /// </summary>
    private const double BaseFontSize = 14;
    /// <summary>
    /// **The icon size box quoted 26, and nothing on screen was 26 pixels.**
    /// This was a private copy of what ThumbSize used to be; the
    /// design-reference pass took the details row icon to 18 and left the copy
    /// behind, so the box read 26 beside an 18px icon — and in Grid and
    /// Compact, where the icons are 72 and 36, it read 26 there as well. A
    /// number no layout had drawn since.
    ///
    /// Per LAYOUT, because the scale it multiplies is per layout: the flyout
    /// edits the ACTIVE mode's IconScale, so the active mode's icon is the only
    /// size the number beside it can honestly be.
    /// </summary>
    private double BaseIconSize => PaneScale.BaseIcon(View);

    // The FONT axis keeps one range for every layout, and that is honest: 14pt
    // text is small at 0.7 and large at 2.5 whether it sits in a row, a compact
    // cell or under a tile. The ICON axis does not — see PaneScale.IconRange.
    private const double MinScale = 0.7;
    private const double MaxScale = 2.5;

    /// <summary>
    /// How far the icon axis may travel in the layout that is on screen, as a
    /// multiple of what that layout draws at 100%.
    ///
    /// **The icon clamp used MinScale/MaxScale too, and that was the ceiling
    /// the grid hit.** 2.5 x 72 is 180, so no route existed to the 256 Explorer
    /// calls extra large, by the wheel or by typing into the size box.
    /// </summary>
    public (double Min, double Max) IconLimits => PaneScale.IconRange(View);
    /// <summary>
    /// The size this pane's body text is actually drawn at, and the size typing
    /// a number into the flyout asks for.
    ///
    /// **Times the interface size, because the box quotes what is on screen.**
    /// That is the same rule the icon box below was fixed under: it multiplied
    /// a stale private copy and read 26 beside an 18px icon. The metrics
    /// multiply the pane's zoom by <see cref="InterfaceText.Scale"/>, so at
    /// 125% a pane at 100% draws 17.5px type — and a box reading 14 there would
    /// be the same fault with a different number in it.
    ///
    /// The setter divides the same product back out and moves the PANE's zoom,
    /// which is the only thing this control owns — so a typed size lands where
    /// it was typed for as long as it is inside that zoom's own 0.7–2.5, and
    /// clamps to the nearest end when it is not. **Measured, because the range
    /// moves with the interface size and an earlier comment here claimed it did
    /// not**: at 250%, typing 21 lands on 24, since 0.7 x 14 x 2.5 is the
    /// smallest type this pane can be asked for. At the combo's own top row,
    /// 200%, the box bottoms out at 20.
    ///
    /// That is the honest behaviour rather than a bug to fix here: the pane's
    /// zoom is a multiplier ON the interface size, and a pane that could be
    /// zoomed back down to 14px at 200% would be a second control undoing the
    /// first.
    /// </summary>
    public double FontPoints
    {
        get => Math.Round(FontScale * BaseFontSize * InterfaceText.Scale);
        set => FontScale = Math.Clamp(
            value / (BaseFontSize * InterfaceText.Scale), MinScale, MaxScale);
    }

    public double IconPixels
    {
        get => Math.Round(IconScale * BaseIconSize);
        set
        {
            var (min, max) = IconLimits;

            IconScale = Math.Clamp(value / BaseIconSize, min, max);
        }
    }

    /// <summary>
    /// Whether the icon axis has already reached the end of this layout's
    /// stretch. That is where the zoom gesture steps to the next layout instead
    /// of pushing against a clamp that cannot move.
    ///
    /// **Asked of the rounded PIXEL size, not of the raw scale, because an
    /// answer given on the raw scale produced a notch that did nothing.**
    /// Measured on the build before this: from details at 100%, seven notches
    /// in reached scale 2.658, which draws 48px — the ceiling, as far as
    /// anything on screen was concerned. The eighth notch only moved the scale
    /// to 2.667, redrew the same 48px and did not step, so on the icons-only
    /// Ctrl+Shift branch it changed nothing whatsoever. The step now happens on
    /// the size the user can see.
    ///
    /// Going down the size is compared against <see cref="PaneScale.StepDownAt"/>
    /// rather than this layout's own floor, which is what makes one notch out
    /// undo one notch in across a handover.
    /// </summary>
    public bool AtIconLimit(bool larger)
        => larger
            ? IconPixels >= PaneScale.IconPixelRange(View).Max
            : PaneScale.StepDownAt(View) is { } handover && IconPixels <= handover;

    /// <summary>
    /// Moves one rung up or down the layout ladder, carrying the sizes across.
    ///
    /// **Every mode keeps its own scale pair, so a bare mode switch made the
    /// sizes jump.** Arriving in Grid at whatever it was last left at is right
    /// when the switch was asked for by name, and wrong when it is the
    /// continuation of a zoom: the whole point of the step is that the size
    /// under the pointer keeps growing. Both axes are therefore read before the
    /// switch and written after it — the icon one in PIXELS, because the base
    /// it multiplies moves with the layout and the same multiplier would mean a
    /// different size on the other side.
    ///
    /// The setter clamps into the arriving layout's own range, so a step never
    /// lands outside it.
    /// </summary>
    /// <returns>False at the ends of the ladder, where there is nothing to step
    /// to and the caller should go on scaling in place.</returns>
    public bool StepLayout(bool larger)
    {
        if (PaneScale.Neighbour(View, larger) is not { } next) return false;

        var pixels = IconPixels;
        var font = FontScale;

        View = next;

        FontScale = font;
        IconPixels = pixels;

        return true;
    }

    partial void OnFontScaleChanged(double value)
    {
        if (!_swappingScales) _scales[View] = (value, IconScale);

        OnPropertyChanged(nameof(FontPoints));
        SyncTextScale();
        ScaleChanged?.Invoke(this, EventArgs.Empty);

        // Under the same flag as the bookkeeping above, and for a sharper
        // reason than tidiness: a mode switch assigns this pair from
        // `_scales[newValue]`, so an unguarded write here would record the
        // INCOMING layout's font beside the OUTGOING layout's icon — the one
        // instant in the pane's life where the two disagree. OnViewChanged
        // records the finished pair itself, a few lines after the swap.
        //
        // Per wheel tick is the right frequency: the store is an in-memory map
        // behind a debounced flush, which is what its own header says it is for.
        if (!_swappingScales) RememberFolderView();
    }

    partial void OnIconScaleChanged(double value)
    {
        if (!_swappingScales) _scales[View] = (FontScale, value);

        OnPropertyChanged(nameof(IconPixels));

        // The nesting step a folder opened in place indents by is derived from
        // the row icon, so it moves with this. Republished from the stored
        // depths rather than re-spliced: a rebuild of the row collection is a
        // Reset over every row on screen, and a Ctrl+scroll sends one of these
        // per wheel tick.
        if (_depths.Count > 0) PublishIndents();

        ScaleChanged?.Invoke(this, EventArgs.Empty);

        if (!_swappingScales) RememberFolderView();
    }

    /// <summary>
    /// Recomputes the width every column threshold is measured against.
    ///
    /// Column thresholds are measured in text width, so they follow the font
    /// axis of the pane they belong to — TIMES the interface size, which is the
    /// other half of how wide that text is. A pane at 100% inside a window at
    /// 200% draws 28px type, and thresholds that still said 340 would claim
    /// every column fits while each one overflowed: the exact fault fixed
    /// thresholds had at 2x, reintroduced by the global factor.
    ///
    /// **One method rather than the product written at each of the three
    /// places that need it**, because the first cut wrote it at two of them and
    /// the one it missed — construction — was the one every pane goes through.
    /// </summary>
    private void SyncTextScale() => TextScale = FontScale * InterfaceText.Scale;

    /// <summary>Raised so the shell can persist the change.</summary>
    public event EventHandler? ScaleChanged;

    /// <summary>
    /// Forces every pane-level metric to be recomputed and rewritten.
    ///
    /// Needed because the metrics now mix two sources — this pane's scale and
    /// the global spacing settings — and only the first of those raises a
    /// property change. Without this a spacing change would reach only the panes
    /// that happened to rescale afterwards.
    ///
    /// **The column thresholds are a third source and they are not resources**,
    /// so re-raising the metrics alone left them behind: changing the interface
    /// text size grew the text and left every threshold at the width it needed
    /// before. Recomputed here rather than notified, because the value itself
    /// has changed — <see cref="TextScale"/> is a product of the pane's zoom and
    /// the interface size, and only the first half raises anything.
    /// </summary>
    public void RefreshScale()
    {
        SyncTextScale();

        OnPropertyChanged(nameof(FontPoints));
        OnPropertyChanged(nameof(IconScale));
    }

    /// <summary>
    /// Starts this pane looking like another one.
    ///
    /// **A new tab used to start from nothing** — hidden files off, Details,
    /// sorted by name, ungrouped, at 100% — so opening one while working with
    /// hidden files showing meant setting it all up again. Both references
    /// carry the current view across.
    ///
    /// Under the reload guard, because these are set before the pane has
    /// navigated anywhere and each setter would otherwise ask for a listing
    /// that has no path yet.
    /// </summary>
    public void AdoptViewOf(PaneViewModel other)
    {
        _suppressReload = true;

        try
        {
            ShowHidden = other.ShowHidden;
            View = other.View;
            Sort = other.Sort;
            SortDescending = other.SortDescending;
            GroupBy = other.GroupBy;
            FontScale = other.FontScale;
            IconScale = other.IconScale;

            HideSizeColumn = other.HideSizeColumn;
            HideModifiedColumn = other.HideModifiedColumn;
            ShowTypeColumn = other.ShowTypeColumn;
            ShowCreatedColumn = other.ShowCreatedColumn;
        }
        finally
        {
            _suppressReload = false;
        }
    }
}
