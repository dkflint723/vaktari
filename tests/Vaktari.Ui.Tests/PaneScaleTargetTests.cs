using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Which pane the size controls change.
///
/// **The flyout holding them lives on the rightmost pane alone**, and opening
/// it makes that side active — so "this pane" could only ever mean the right
/// one. The left half of a split had no way to be sized at all except with the
/// wheel, which is a gesture rather than a control and cannot be reached from
/// the keyboard.
/// </summary>
public sealed class PaneScaleTargetTests : OwnedViewModels
{
    private sealed class InertFileSystem : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>A shell with a tab in it. Start is what gives the window its
    /// first pane; a bare view model has none.</summary>
    private ShellViewModel Started()
    {
        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Path.GetTempPath());

        return shell;
    }

    private ShellViewModel Split()
    {
        var shell = Started();

        shell.ToggleSplitCommand.Execute(null);

        Assert.True(shell.IsSplit, "these are all about a split window");

        return shell;
    }

    private PaneViewModel LeftPane(ShellViewModel s) => s.Left.ActiveTab!;
    private PaneViewModel RightPane(ShellViewModel s) => s.Right!.ActiveTab!;

    [AvaloniaFact]
    public void The_left_pane_can_be_sized_from_the_menu_on_the_right()
    {
        var shell = Split();

        shell.ScaleTargetIndex = 0;
        shell.FontLargerCommand.Execute(null);

        Assert.True(LeftPane(shell).FontScale > 1.0, "the left pane should have grown");
        Assert.Equal(1.0, RightPane(shell).FontScale);
    }

    [AvaloniaFact]
    public void The_right_pane_alone_can_be_sized()
    {
        var shell = Split();

        shell.ScaleTargetIndex = 1;
        shell.IconsLargerCommand.Execute(null);

        Assert.True(RightPane(shell).IconScale > 1.0);
        Assert.Equal(1.0, LeftPane(shell).IconScale);
    }

    /// <summary>The case the wheel cannot do at all: both at once, in step.</summary>
    [AvaloniaFact]
    public void Both_can_be_sized_together()
    {
        var shell = Split();

        shell.ScaleTargetIndex = 2;
        shell.FontLargerCommand.Execute(null);

        Assert.Equal(LeftPane(shell).FontScale, RightPane(shell).FontScale);
        Assert.True(LeftPane(shell).FontScale > 1.0);
    }

    /// <summary>
    /// **The number in the box is the size of what it changes.** Showing the
    /// active pane's size while typing into another one's is the kind of quiet
    /// mismatch that makes a control untrustworthy.
    /// </summary>
    [AvaloniaFact]
    public void The_boxes_show_the_pane_they_would_change()
    {
        var shell = Split();

        shell.ScaleTargetIndex = 0;
        shell.TargetFontPoints = 20;

        Assert.Equal(20, LeftPane(shell).FontPoints);
        Assert.Equal(20, shell.TargetFontPoints);

        shell.ScaleTargetIndex = 1;

        Assert.Equal(RightPane(shell).FontPoints, shell.TargetFontPoints);
        Assert.NotEqual(20, shell.TargetIconPixels);
    }

    /// <summary>Typing a size with both chosen sets both, rather than one and a
    /// half.</summary>
    [AvaloniaFact]
    public void Typing_a_size_with_both_chosen_sets_both()
    {
        var shell = Split();

        shell.ScaleTargetIndex = 2;
        shell.TargetIconPixels = 24;

        Assert.Equal(24, LeftPane(shell).IconPixels);
        Assert.Equal(24, RightPane(shell).IconPixels);
    }

    /// <summary>
    /// The menu's reset follows the chooser. Ctrl+0 keeps its own meaning —
    /// the pane being worked in — because a keystroke should not depend on a
    /// menu setting somebody left on "both" an hour ago.
    /// </summary>
    [AvaloniaFact]
    public void Reset_from_the_menu_follows_the_chooser_and_ctrl_zero_does_not()
    {
        var shell = Split();

        shell.ScaleTargetIndex = 2;
        shell.FontLargerCommand.Execute(null);

        shell.ScaleTargetIndex = 0;
        shell.ResetTargetedScaleCommand.Execute(null);

        Assert.Equal(1.0, LeftPane(shell).FontScale);
        Assert.True(RightPane(shell).FontScale > 1.0, "only the chosen pane resets");

        // Ctrl+0 acts on whichever pane is being worked in, whatever the
        // chooser says.
        shell.ActiveGroup = shell.Right!;
        shell.ZoomResetCommand.Execute(null);

        Assert.Equal(1.0, RightPane(shell).FontScale);
    }

    /// <summary>
    /// With one pane there is nothing to choose between, and the controls must
    /// still work — the chooser is hidden in that case rather than disabled.
    /// </summary>
    [AvaloniaFact]
    public void Without_a_split_the_controls_act_on_the_only_pane()
    {
        var shell = Started();

        Assert.False(shell.IsSplit);

        shell.FontLargerCommand.Execute(null);

        Assert.True(shell.ActiveTab!.FontScale > 1.0);
        Assert.Equal(shell.ActiveTab.FontPoints, shell.TargetFontPoints);
    }

    // ---- the number beside the box is a size something actually is ---------

    /// <summary>
    /// **The box quoted 26, and nothing on screen was 26 pixels.** It
    /// multiplied a private copy of what the details row icon used to be; the
    /// design-reference pass took that icon to 18 and left the copy behind — so
    /// the box read 26 beside an 18px icon, and read 26 in Grid and Compact
    /// too, where the icons are 72 and 36.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Details, 18)]
    [InlineData(ViewMode.Compact, 36)]
    [InlineData(ViewMode.Grid, 72)]
    public void At_full_size_the_box_reads_the_icon_that_layout_draws(ViewMode mode, double drawn)
    {
        var pane = Own(new PaneViewModel(new InertFileSystem()) { ViewportWidth = 1400 });

        pane.View = mode;
        pane.IconScale = 1.0;

        Assert.Equal(drawn, pane.IconPixels);
    }

    /// <summary>
    /// And it is the size the layout really draws, not a number that merely
    /// agrees with another constant — asked of the metrics the markup binds.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Details, "ThumbSize")]
    [InlineData(ViewMode.Grid, "TileSize")]
    public void Which_is_the_size_that_layout_is_told_to_draw(ViewMode mode, string key)
    {
        var drawn = PaneScale.Compute(1.0, 1.0).Single(m => m.Key == key).Value;

        Assert.Equal(PaneScale.BaseIcon(mode), drawn);
    }

    /// <summary>
    /// **Two layouts sitting at the same scale switched silently.** The readout
    /// hangs off the scale, and restoring an identical one raises nothing — so
    /// a Details-to-Grid switch at 100%, which is the default, left 18 in the
    /// box beside 72px tiles.
    /// </summary>
    [AvaloniaFact]
    public void Switching_layout_at_the_same_scale_still_moves_the_number()
    {
        var pane = Own(new PaneViewModel(new InertFileSystem()) { ViewportWidth = 1400 });

        pane.View = ViewMode.Details;
        pane.IconScale = 1.0;

        var raised = 0;
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.IconPixels)) raised++;
        };

        pane.View = ViewMode.Grid;

        Assert.True(raised > 0, "nothing said the size had changed");
        Assert.Equal(72, pane.IconPixels);
    }

    /// <summary>Typing back the number shown is still a no-op, in every
    /// layout — get and set go through the same base.</summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Details)]
    [InlineData(ViewMode.Compact)]
    [InlineData(ViewMode.Grid)]
    public void Typing_back_what_it_says_changes_nothing(ViewMode mode)
    {
        var pane = Own(new PaneViewModel(new InertFileSystem()) { ViewportWidth = 1400 });

        pane.View = mode;
        pane.IconScale = 1.0;

        var shown = pane.IconPixels;
        pane.IconPixels = shown;

        Assert.Equal(shown, pane.IconPixels);
        Assert.Equal(1.0, pane.IconScale, 3);
    }
}
