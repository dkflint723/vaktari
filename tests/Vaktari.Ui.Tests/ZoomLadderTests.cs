using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What Ctrl+wheel can reach.
///
/// **It scaled inside one layout, between 0.7 and 2.5, and stopped there.** One
/// multiplier over three different bases is three different ranges: on the
/// grid's 72px tile it ran 50 to 180, so there was no route by any gesture to
/// the 256 Explorer calls extra large, and on the details row's 18px icon the
/// same two numbers ran 13 to 45. And at either end the wheel simply stopped:
/// reaching large thumbnails from a list of names meant leaving the wheel,
/// opening a menu and picking a layout by name, which is the work the gesture
/// exists to save.
///
/// Three things are pinned here. Each layout owns its own slice of an icon-size
/// ladder that runs 12 to 256; a notch past the end of one slice steps to the
/// neighbouring LAYOUT carrying the size across, so the ladder is walked as one
/// continuous gesture; and the two halves of every handover are the same size,
/// so one notch out undoes one notch in there as it does anywhere else.
/// </summary>
public sealed class ZoomLadderTests : OwnedViewModels
{
    private sealed class Inert : IFileSystemProvider
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

    /// <summary>Counts the saves the shell asks for, which is the only outward
    /// sign that something was judged worth persisting.</summary>
    private sealed class Recording : ISessionStore
    {
        public int Changes;

        public SessionState? Load() => null;
        public void NotifyChanged(SessionState state) => Changes++;
        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private (ShellViewModel Shell, PaneViewModel Pane) Pair(ViewMode mode)
    {
        var shell = Own(new ShellViewModel(new Inert()));
        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        pane.View = mode;

        return (shell, pane);
    }

    // ---- the range each layout is allowed ---------------------------------

    /// <summary>
    /// **The grid stopped at 180 pixels.** 2.5 was the ceiling for every axis
    /// in every layout, and 2.5 x 72 is 180 — so the size a folder of
    /// photographs is browsed at was unreachable by the wheel, by the buttons
    /// and by typing into the size box. The floor was the same accident in the
    /// other direction: 0.7 x 72 is 50, so the tiles could not be packed
    /// tighter than that either.
    ///
    /// The ceilings are now Explorer's own icon rungs — medium 48, large 96,
    /// extra large 256 — and the slices overlap, which is what lets a size
    /// cross a handover unchanged.
    ///
    /// Asked through the size box's own setter, because that is the one place a
    /// number is turned into a scale.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Details, 12, 48)]
    [InlineData(ViewMode.Compact, 20, 96)]
    [InlineData(ViewMode.Grid, 32, 256)]
    public void Each_layout_zooms_between_the_sizes_that_layout_makes_sense_at(
        ViewMode mode, double smallest, double largest)
    {
        var (_, pane) = Pair(mode);

        pane.IconPixels = 4000;

        Assert.Equal(largest, pane.IconPixels);

        pane.IconPixels = 1;

        Assert.Equal(smallest, pane.IconPixels);
    }

    /// <summary>
    /// And the wheel really gets there, rather than the range merely being
    /// declared wider than the thing that steps through it. Forty notches is
    /// well past the ten it takes, so this measures the ceiling and not the
    /// count.
    ///
    /// The scale is asserted EXACTLY, not just the pixels it draws. A notch is
    /// rounded to three places before it is clamped, and the other order hands
    /// back a scale a hair past the limit it was just held to — 3.556 against a
    /// ceiling of 256/72, which is 3.5555…. Both draw 256px, so the pixel
    /// assertion above cannot see it.
    /// </summary>
    [AvaloniaFact]
    public void The_wheel_carries_the_grid_all_the_way_to_extra_large()
    {
        var (shell, pane) = Pair(ViewMode.Grid);

        for (var i = 0; i < 40; i++) shell.ZoomPane(pane, 0, 0.15);

        Assert.Equal(256, pane.IconPixels);
        Assert.Equal(ViewMode.Grid, pane.View);
        Assert.Equal(PaneScale.IconRange(ViewMode.Grid).Max, pane.IconScale);
    }

    // ---- the ladder --------------------------------------------------------

    /// <summary>
    /// A notch past the top of a layout is a step to the next layout up, and
    /// the icon size does not jump across the handover — the slices overlap so
    /// that the arriving layout can hold the size the leaving one ended at.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Details, 48, ViewMode.Compact)]
    [InlineData(ViewMode.Compact, 96, ViewMode.Grid)]
    public void A_notch_past_the_top_of_a_layout_steps_to_the_next_one(
        ViewMode from, double top, ViewMode to)
    {
        var (shell, pane) = Pair(from);

        pane.IconPixels = 4000;

        Assert.Equal(top, pane.IconPixels);

        shell.ZoomPane(pane, 0, 0.15);

        Assert.Equal(to, pane.View);
        Assert.Equal(top, pane.IconPixels);
    }

    /// <summary>The same going down, which is how a wall of thumbnails is wound
    /// back to a list of names without touching a menu.</summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Grid, 32, ViewMode.Compact)]
    [InlineData(ViewMode.Compact, 20, ViewMode.Details)]
    public void A_notch_past_the_bottom_of_a_layout_steps_back_down(
        ViewMode from, double bottom, ViewMode to)
    {
        var (shell, pane) = Pair(from);

        pane.IconPixels = 1;

        Assert.Equal(bottom, pane.IconPixels);

        shell.ZoomPane(pane, 0, -0.15);

        Assert.Equal(to, pane.View);
        Assert.Equal(bottom, pane.IconPixels);
    }

    /// <summary>
    /// And one notch out undoes one notch in ACROSS a handover, which is the
    /// invariant the ladder is only worth having if it keeps.
    ///
    /// **The two halves of a handover were two different sizes.** Stepping up
    /// happened at the leaving layout's maximum and stepping back down at the
    /// arriving layout's minimum — 48 and 20 across the details/compact join, a
    /// factor of 2.4 apart. Measured on the build before this fix: one notch in
    /// from details at its ceiling gave compact at 48px, one notch out gave
    /// compact at 42px, and getting back to details took eight notches in all
    /// and landed on 20px rather than the 48 it left. A single overshoot could
    /// not be undone.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Details, ViewMode.Compact)]
    [InlineData(ViewMode.Compact, ViewMode.Grid)]
    public void A_notch_out_undoes_a_notch_in_across_a_handover(ViewMode from, ViewMode to)
    {
        var (shell, pane) = Pair(from);

        pane.IconPixels = 4000;

        var top = pane.IconPixels;

        shell.ZoomPane(pane, 0, 0.15);

        Assert.Equal(to, pane.View);

        shell.ZoomPane(pane, 0, -0.15);

        Assert.Equal(from, pane.View);
        Assert.Equal(top, pane.IconPixels);
    }

    /// <summary>
    /// The step happens on the size the user can SEE.
    ///
    /// **Comparing the raw scale against the limit made one notch inert.** From
    /// details at 100%, seven notches in reached scale 2.658, which draws 48px
    /// — the ceiling, as far as anything on screen was concerned. The eighth
    /// only moved the scale to 2.667, redrew the same 48px and did not step; on
    /// the icons-only Ctrl+Shift branch it changed nothing whatsoever. Measured
    /// on the build before this fix, where the run read 48, 48, then compact.
    /// </summary>
    [AvaloniaFact]
    public void The_step_happens_on_the_size_the_user_can_see()
    {
        var (shell, pane) = Pair(ViewMode.Details);

        // Short of the 2.667 ceiling, but 2.659 x 18 rounds to the same 48px
        // the ceiling draws.
        pane.IconScale = 2.659;

        Assert.Equal(48, pane.IconPixels);

        shell.ZoomPane(pane, 0, 0.15);

        Assert.Equal(ViewMode.Compact, pane.View);
    }

    /// <summary>
    /// The text size crosses with it.
    ///
    /// **Every layout keeps its own scale pair**, so arriving in Compact
    /// restores whatever Compact was last left at — right when the layout was
    /// asked for by name, wrong when it is the continuation of a zoom, where a
    /// step that put the text back to 100% would read as the gesture undoing
    /// itself.
    /// </summary>
    [AvaloniaFact]
    public void The_text_size_crosses_the_step_with_the_icons()
    {
        var (shell, pane) = Pair(ViewMode.Details);

        pane.FontScale = 1.5;
        pane.IconPixels = 4000;

        shell.ZoomPane(pane, 0, 0.15);

        Assert.Equal(ViewMode.Compact, pane.View);
        Assert.Equal(1.5, pane.FontScale);
    }

    /// <summary>
    /// And it does not merely cross — it takes its own notch while it does.
    ///
    /// **The step returned before the font was touched, so the notch that
    /// crossed a boundary moved neither axis' number.** Plain Ctrl+wheel is
    /// documented as moving both, and one click in every seven or eight of a
    /// run is the one that crosses; measured on the build before this fix, a
    /// notch at the details ceiling took the icons to compact and left the text
    /// at 1.0. The test above cannot see it: that one passes a font delta of
    /// zero, where carrying the value across and stepping it are the same
    /// answer.
    /// </summary>
    [AvaloniaFact]
    public void The_notch_that_crosses_a_handover_still_moves_the_text()
    {
        var (shell, pane) = Pair(ViewMode.Details);

        pane.IconPixels = 4000;

        shell.ZoomPane(pane, 0.1, 0.15);

        Assert.Equal(ViewMode.Compact, pane.View);
        Assert.Equal(1.1, pane.FontScale);
    }

    /// <summary>
    /// A font-only notch is not a direction on the icon ladder.
    ///
    /// <c>AtIconLimit</c> takes "larger or smaller" as a bool, and a delta of
    /// zero is neither — so without the guard on the icon delta a caller
    /// growing only the text would be read as asking to step DOWN, and would
    /// change the layout out from under itself. No caller passes zero today;
    /// this is what stops the next one from finding out the hard way.
    /// </summary>
    [AvaloniaFact]
    public void A_font_only_notch_leaves_the_layout_alone()
    {
        var (shell, pane) = Pair(ViewMode.Compact);

        // Compact's floor, which is below the 48px it hands down at — so the
        // ladder would step here if it were consulted at all.
        pane.IconPixels = 1;

        Assert.Equal(20, pane.IconPixels);

        shell.ZoomPane(pane, 0.1, 0);

        Assert.Equal(ViewMode.Compact, pane.View);
        Assert.Equal(20, pane.IconPixels);
        Assert.Equal(1.1, pane.FontScale);
    }

    /// <summary>
    /// And the font axis keeps ONE range for all three layouts, which is the
    /// honest half of the split the icon axis needed: 14px text is small at 0.7
    /// and large at 2.5 whether it sits in a row, a compact cell or under a
    /// tile. Forty notches each way is well past the ten it takes, so this
    /// measures the ends and not the count.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ViewMode.Details)]
    [InlineData(ViewMode.Compact)]
    [InlineData(ViewMode.Grid)]
    public void The_text_has_the_same_ceiling_and_floor_in_every_layout(ViewMode mode)
    {
        var (shell, pane) = Pair(mode);

        for (var i = 0; i < 40; i++) shell.ZoomPane(pane, 0.1, 0);

        Assert.Equal(2.5, pane.FontScale);

        for (var i = 0; i < 40; i++) shell.ZoomPane(pane, -0.1, 0);

        Assert.Equal(0.7, pane.FontScale);
        Assert.Equal(mode, pane.View);
    }

    /// <summary>
    /// The ends of the ladder hold. The outermost notch has to do nothing
    /// rather than wrap round to the other end, which is what a bare "next
    /// mode" would have done.
    /// </summary>
    [AvaloniaFact]
    public void The_ends_of_the_ladder_hold()
    {
        var (shell, grid) = Pair(ViewMode.Grid);

        grid.IconPixels = 4000;
        shell.ZoomPane(grid, 0, 0.15);

        Assert.Equal(ViewMode.Grid, grid.View);
        Assert.Equal(256, grid.IconPixels);

        var (_, details) = Pair(ViewMode.Details);

        details.IconPixels = 1;
        shell.ZoomPane(details, 0, -0.15);

        Assert.Equal(ViewMode.Details, details.View);
        Assert.Equal(12, details.IconPixels);
    }

    /// <summary>
    /// A notch out undoes a notch in.
    ///
    /// **A notch is a proportion of the current scale, not a fixed addition.**
    /// Adding 0.15 at 0.7 is a 21% jump and adding it at 3.5 is 4%, so once the
    /// grid reached 256 the same wheel click crawled at the top of the range
    /// and lurched at the bottom. Multiplying instead makes every notch the
    /// same perceived step — and the way down has to be the reciprocal of the
    /// way up, because x1.15 followed by x0.85 lands at 0.98 and a zoom that
    /// cannot return to where it started is a zoom nobody trusts.
    /// </summary>
    [AvaloniaFact]
    public void A_notch_out_undoes_a_notch_in()
    {
        var (shell, pane) = Pair(ViewMode.Details);

        shell.ZoomPane(pane, 0.1, 0.15);

        Assert.True(pane.IconScale > 1.0, "a notch in should have grown something");

        shell.ZoomPane(pane, -0.1, -0.15);

        Assert.Equal(1.0, pane.IconScale);
        Assert.Equal(1.0, pane.FontScale);
    }

    /// <summary>
    /// And a notch is the SAME notch wherever the scale already is.
    ///
    /// **A fixed additive delta was a 21% jump at 0.7 and a 4% nudge at 3.5.**
    /// That was invisible while every layout was clamped to 0.7–2.5; once the
    /// grid ran to 256px it was the difference between a wheel that lurched at
    /// the small end and crawled at the large one. The reversibility test above
    /// cannot see this — adding 0.15 and subtracting it is perfectly reversible
    /// — so the ratio is measured directly, at two starting scales far apart in
    /// the same layout.
    /// </summary>
    [AvaloniaFact]
    public void A_notch_is_the_same_proportion_wherever_the_scale_already_is()
    {
        var (shell, small) = Pair(ViewMode.Grid);
        var (_, large) = Pair(ViewMode.Grid);

        // Both well inside Grid's 0.44–3.56, so neither notch meets a clamp or
        // the ladder — this measures the arithmetic and nothing else.
        small.IconScale = 1.0;
        large.IconScale = 3.0;

        shell.ZoomPane(small, 0, 0.15);
        shell.ZoomPane(large, 0, 0.15);

        Assert.Equal(small.IconScale / 1.0, large.IconScale / 3.0, 2);
    }

    /// <summary>
    /// A step survives a restart, and the route it takes there is narrow.
    ///
    /// **The shell does not watch the pane's View.** OnPaneChanged lists
    /// CurrentPath, IconPixels, Sort, ShowHidden and the column flags, and View
    /// is not among them — so nothing about the layout itself marks the
    /// session. What does is the pane's ScaleChanged, wired to MarkDirty where
    /// the shell builds the pane: a step always moves the icon SCALE, because
    /// the arriving layout draws a different base and the same pixel size
    /// cannot be the same multiplier.
    ///
    /// Measured, not assumed — with ZoomPane returning before it reached the
    /// scaling path, this test still passed. That is why the branch carries no
    /// MarkDirty of its own, and why the dependency is pinned here rather than
    /// left to be rediscovered.
    /// </summary>
    [AvaloniaFact]
    public void A_ladder_step_is_worth_saving()
    {
        var store = new Recording();
        var shell = Own(new ShellViewModel(new Inert(), store: store));

        shell.Start(null, Path.GetTempPath());

        var pane = shell.ActiveTab!;

        pane.View = ViewMode.Details;
        pane.IconPixels = 4000;

        store.Changes = 0;

        shell.ZoomPane(pane, 0, 0.15);

        Assert.Equal(ViewMode.Compact, pane.View);
        Assert.True(store.Changes > 0, "a layout step should have marked the session");
    }

    // ---- what must NOT walk the ladder -------------------------------------

    /// <summary>
    /// The flyout's own icon buttons sit beside a layout chooser, and must not
    /// move it. They share the arithmetic with the wheel and not the ladder,
    /// which is why <c>ScalePane</c> and <c>ZoomPane</c> are separate methods
    /// rather than one with a flag.
    /// </summary>
    [AvaloniaFact]
    public void The_flyouts_icon_buttons_stay_in_the_layout_they_are_shown_beside()
    {
        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(null, Path.GetTempPath());

        var pane = shell.ActiveTab!;

        pane.View = ViewMode.Details;
        pane.IconPixels = 4000;

        shell.IconsLargerCommand.Execute(null);

        Assert.Equal(ViewMode.Details, pane.View);
        Assert.Equal(48, pane.IconPixels);
    }

    // ---- and what must -----------------------------------------------------

    /// <summary>
    /// Ctrl+plus is the same gesture without a wheel, so it walks the same
    /// ladder. Two controls for one gesture that disagreed about where the
    /// gesture ends would be worse than either of them alone.
    /// </summary>
    [AvaloniaFact]
    public void Ctrl_plus_walks_the_same_ladder_the_wheel_does()
    {
        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(null, Path.GetTempPath());

        var pane = shell.ActiveTab!;

        pane.View = ViewMode.Details;
        pane.IconPixels = 4000;

        shell.ZoomInCommand.Execute(null);

        Assert.Equal(ViewMode.Compact, pane.View);
        Assert.Equal(48, pane.IconPixels);

        pane.IconPixels = 1;

        shell.ZoomOutCommand.Execute(null);

        Assert.Equal(ViewMode.Details, pane.View);
        Assert.Equal(20, pane.IconPixels);
    }

    // ---- and the gesture itself -------------------------------------------

    /// <summary>
    /// The real wheel, on the real window, through the real handler.
    ///
    /// The view models above pass perfectly well with nothing calling them:
    /// the wheel handler used to call <c>ScalePane</c>, and pointing it at
    /// <c>ZoomPane</c> is the whole of what makes the ladder reachable by the
    /// gesture the finding is about.
    ///
    /// Both of its branches, because the handler has two — plain Ctrl moves
    /// both axes and Ctrl+Shift narrows it to the icons — and only one of them
    /// having a ladder would be the same fault in half the gesture.
    /// </summary>
    [AvaloniaFact]
    public void Ctrl_wheel_at_the_top_of_a_layout_steps_to_the_next_one()
    {
        // The constructor assigns the platform's real search backend to
        // PaneViewModel's static; this borrows it so Dispose gives it back.
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            pane.View = ViewMode.Details;
            pane.IconPixels = 4000;

            Assert.Equal(48, pane.IconPixels);

            window.MouseWheel(new Point(20, 20), new Vector(0, 1), RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ViewMode.Compact, pane.View);
            Assert.Equal(48, pane.IconPixels);

            // Plain Ctrl is the both-axes branch, and the notch that crossed
            // still took its own font step — 1.0 x 1.1.
            Assert.Equal(1.1, pane.FontScale);

            // The Shift branch, from the top of Compact. Shift means icons
            // only, so this step must carry the font across untouched — and the
            // number it has to leave alone is the 1.1 the branch above put
            // there, not the 1.0 the window opened with.
            pane.IconPixels = 4000;

            Assert.Equal(96, pane.IconPixels);

            window.MouseWheel(
                new Point(20, 20), new Vector(0, 1),
                RawInputModifiers.Control | RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ViewMode.Grid, pane.View);
            Assert.Equal(96, pane.IconPixels);
            Assert.Equal(1.1, pane.FontScale);
        }
        finally
        {
            // **Closing a headless window flushes the run's shared session.**
            // Every store in this assembly is pointed at one temp directory, so
            // a window that closes holding a 256px grid writes that over
            // whatever the next test restores from.
            //
            // ALL THREE modes, not just the active one. A pane keeps a scale
            // pair per layout and ToTabState writes all three into the state it
            // saves, so putting only Details back left grid at 1.333 and
            // compact at 2.667 in the flushed tab — 96px icons waiting for
            // whichever later test restores it and switches layout. Measured:
            // the assertion below read exactly that before this loop replaced a
            // three-line restore of Details alone.
            if (window.DataContext is ShellViewModel shell && shell.ActiveTab is { } tab)
            {
                foreach (var mode in new[] { ViewMode.Grid, ViewMode.Compact, ViewMode.Details })
                {
                    tab.View = mode;
                    tab.FontScale = 1.0;
                    tab.IconScale = 1.0;
                }

                var restored = tab.ToTabState();

                Assert.Equal(ViewMode.Details, restored.View);
                Assert.Equal(1.0, restored.IconScale);
                Assert.Equal(1.0, restored.GridIconScale);
                Assert.Equal(1.0, restored.CompactIconScale);
                Assert.Equal(1.0, restored.FontScale);
                Assert.Equal(1.0, restored.GridFontScale);
                Assert.Equal(1.0, restored.CompactFontScale);
            }

            window.Close();
        }
    }
}
