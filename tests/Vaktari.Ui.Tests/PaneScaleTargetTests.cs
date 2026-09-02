using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
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
}
