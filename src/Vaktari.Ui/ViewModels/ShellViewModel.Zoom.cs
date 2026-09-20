using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Which pane the menu's size controls act on.
///
/// **The menu is one and the panes are two.** A zoom row in a shared menu has
/// to mean a particular pane, and "the active one" is wrong the moment
/// somebody opens the menu over the other side — so the target is explicit,
/// and the sizes reported back are the target's rather than the active pane's.
/// </summary>
public sealed partial class ShellViewModel
{
    // ---- which pane the menu's size controls act on ------------------------

    /// <summary>
    /// **The menu lives on the rightmost pane, and the sizes it changed were
    /// always that pane's.** Opening the flyout makes its own side active, so
    /// "this pane" could only ever mean the right one — leaving the left half
    /// of a split with no way to be sized at all, by any route but the wheel.
    ///
    /// 0 left, 1 right, 2 both. Only meaningful while split; with one pane
    /// there is nothing to choose between and the chooser is hidden.
    /// </summary>
    [ObservableProperty] private int _scaleTargetIndex = 1;

    partial void OnScaleTargetIndexChanged(int value) => NotifyTargetSizes();

    /// <summary>The panes the menu's controls act on.</summary>
    private IEnumerable<PaneViewModel> ScaleTargets
    {
        get
        {
            if (!IsSplit)
            {
                // Nothing to choose between: whatever is showing.
                if (ActiveTab is { } only) yield return only;

                yield break;
            }

            if (ScaleTargetIndex is 0 or 2 && Left.ActiveTab is { } left) yield return left;
            if (ScaleTargetIndex is 1 or 2 && Right?.ActiveTab is { } right) yield return right;
        }
    }

    /// <summary>What the boxes show: the first target's size, since with both
    /// selected there is no single answer and the left is the one read first.</summary>
    private PaneViewModel? PrimaryTarget => ScaleTargets.FirstOrDefault();

    public double TargetFontPoints
    {
        get => PrimaryTarget?.FontPoints ?? 14;
        set
        {
            foreach (var pane in ScaleTargets.ToList()) pane.FontPoints = value;

            NotifyTargetSizes();
        }
    }

    public double TargetIconPixels
    {
        get => PrimaryTarget?.IconPixels ?? 16;
        set
        {
            foreach (var pane in ScaleTargets.ToList()) pane.IconPixels = value;

            NotifyTargetSizes();
        }
    }

    /// <summary>The boxes follow the wheel and the buttons as well as their own
    /// typing, or they would sit showing a size that is no longer true.</summary>
    public void NotifyTargetSizes()
    {
        OnPropertyChanged(nameof(TargetFontPoints));
        OnPropertyChanged(nameof(TargetIconPixels));
    }

    private void ScaleTargeted(double fontDelta, double iconDelta)
    {
        foreach (var pane in ScaleTargets.ToList()) ScalePane(pane, fontDelta, iconDelta);

        NotifyTargetSizes();
    }

    [RelayCommand] private void FontLarger()  => ScaleTargeted(0.1, 0);
    [RelayCommand] private void FontSmaller() => ScaleTargeted(-0.1, 0);
    [RelayCommand] private void IconsLarger()  => ScaleTargeted(0, 0.15);
    [RelayCommand] private void IconsSmaller() => ScaleTargeted(0, -0.15);

    /// <summary>
    /// The menu's reset, which follows the chooser. Ctrl+0 keeps its own
    /// meaning — the pane being worked in — because a keystroke should not
    /// depend on a menu setting somebody left on "both" an hour ago.
    /// </summary>
    [RelayCommand]
    private void ResetTargetedScale()
    {
        foreach (var pane in ScaleTargets.ToList()) ResetPaneScale(pane);

        NotifyTargetSizes();
    }

    /// <summary>Ctrl+0 puts both back, since one control resetting only half of
    /// the sizing would be a puzzle rather than a reset.</summary>
    [RelayCommand]
    private void ZoomReset() => ResetPaneScale(ActiveTab);

    /// <summary>
    /// Back to default for one pane. Separate from the command so the wheel
    /// click can reset whichever pane the pointer is over, matching how
    /// Ctrl+wheel already targets by position rather than by focus.
    /// </summary>
    public void ResetPaneScale(PaneViewModel? pane)
    {
        if (pane is null) return;

        pane.FontScale = 1.0;
        pane.IconScale = 1.0;
        MarkDirty();
    }

    /// <summary>
    /// Combined zoom moves BOTH axes — it was stepping only the font, which
    /// made it identical to FontLarger and meant icons never grew with it.
    /// Icons step further per notch because their range is wider.
    ///
    /// Through <see cref="ZoomPane"/>, so the keyboard walks the same layout
    /// ladder the wheel does. Two controls for one gesture that disagreed about
    /// where the gesture ends would be worse than either.
    /// </summary>
    [RelayCommand] private void ZoomIn()  => ZoomPane(ActiveTab, 0.1, 0.15);
    [RelayCommand] private void ZoomOut() => ZoomPane(ActiveTab, -0.1, -0.15);
}
