using Avalonia;
using Avalonia.Headless.XUnit;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The width a details panel borrows, and who it belongs to when the session is
/// written.
///
/// **The window came back permanently wider.** Under
/// <see cref="NarrowPanelBehaviour.GrowWindow"/> a panel that does not fit
/// widens the window and the window remembers what it was before, so closing
/// the panel hands the width back. That memory is a field, and the session is
/// written from the LIVE width — so a window closed with the panel still open
/// saved the grown size, next launch had nothing recorded to give back, and the
/// loan could never be repaid. Every such session added the panel's width to the
/// window for good.
///
/// The rule these pin: the session stores the width the window OWNS. A loan is
/// a thing that happens between an open and a close, and it does not outlive
/// either.
/// </summary>
public sealed class PanelWidthLoanTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;
    private MainWindow? _window;

    public override void Dispose()
    {
        // A window left open is torn down later on whatever thread xunit is on,
        // and surfaces as a threading failure in some unrelated test that merely
        // ran afterwards — see OwnedViewModels.
        _window?.Close();

        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The width the user chose, and the width their side of it has
    /// once the sidebar has taken its share. Too narrow for a 280px panel
    /// beside the 420px minimum listing, which is what makes the window grow.
    /// </summary>
    private const double Chosen = 700;
    private const double Side = 490;

    /// <summary>
    /// A window whose panel has just borrowed width.
    ///
    /// Not shown: this is about what the geometry is, and a shown headless
    /// window feeds <c>GroupWidth</c> from its own layout, which would fight the
    /// width being set here. The view is the only thing that assigns that
    /// property in the application, so assigning it is what the view does.
    ///
    /// **Every input is pinned, and that is not belt and braces.** The state
    /// directory is per test CLASS, so each window here closes onto a session
    /// the next one restores — including the panel's wish and its width. Left to
    /// itself the second test in this file found <c>IsInfoVisible</c> already
    /// true, set it to true again, changed nothing, grew nothing, and passed on
    /// a window that had never borrowed a pixel.
    /// </summary>
    private MainWindow Borrowing(out PaneGroupViewModel group)
    {
        // The constructor assigns the platform's real search backend to
        // PaneViewModel's static; this borrows it so Dispose gives it back.
        UseSearch(PaneViewModel.Search);

        // AFTER the window, never before: its constructor applies the settings
        // it loads from disk and would overwrite this.
        var window = new MainWindow();
        _window = window;

        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore with
        {
            Views = _settingsBefore.Views with
            {
                NarrowDetailsPanel = NarrowPanelBehaviour.GrowWindow,
                KeepWidthAfterPanelClose = false,
            },
        });

        window.Width = Chosen;

        group = window.Shell.Left;
        group.IsInfoVisible = false;
        group.InfoWidth = 280;
        group.GroupWidth = Side;
        group.IsInfoVisible = true;

        // The precondition, asserted rather than assumed, for the same reason.
        Assert.True(group.GrewForPanel,
                    "nothing was borrowed, so there is no loan to be wrong about");

        Assert.True(window.Width > Chosen,
                    $"the group asked but the window stayed at {window.Width}");

        return window;
    }

    /// <summary>
    /// The mechanism itself, so nothing below can pass because the window never
    /// grew in the first place.
    /// </summary>
    [AvaloniaFact]
    public void A_panel_that_does_not_fit_widens_the_window()
    {
        var window = Borrowing(out var group);

        Assert.True(group.GrewForPanel, "the group did not record that it took width");
        Assert.True(window.Width > Chosen,
                    $"the window stayed at {window.Width} rather than growing");
    }

    /// <summary>
    /// **The bug.** The session written while the panel is holding the width has
    /// to name the width the window had before it lent any — the grown one is
    /// not a size anybody chose.
    /// </summary>
    [AvaloniaFact]
    public void A_session_written_while_the_panel_borrows_holds_the_width_before_the_loan()
    {
        var window = Borrowing(out _);

        Assert.Equal(Chosen, window.Shell.ToWindowSession().Width);
    }

    /// <summary>
    /// And the whole round trip, which is how this was found: grow, save,
    /// relaunch. The restored window has no loan outstanding and no way to learn
    /// of one, so arriving at the grown width means arriving permanently wider.
    /// </summary>
    [AvaloniaFact]
    public void A_window_restored_from_that_session_is_the_width_that_was_chosen()
    {
        var saved = Borrowing(out _).Shell.ToWindowSession();

        var next = new MainWindow();

        try
        {
            next.ApplyGeometry(new SessionState { Windows = [saved] });

            Assert.Equal(Chosen, next.Width);
        }
        finally
        {
            next.Close();
        }
    }

    /// <summary>
    /// **And where the window was goes with the width it was.** Growing pushes
    /// the right edge outward, and past the edge of the screen the window
    /// manager shoves the whole window left to keep it visible — which is why
    /// the grow records a position as well as a width. Writing the chosen width
    /// down beside the shoved position would not leave the window wider: it
    /// would walk it a panel's width to the left on every launch.
    /// </summary>
    [AvaloniaFact]
    public void The_place_it_was_shoved_from_is_saved_with_the_width_it_was()
    {
        var window = Borrowing(out _);

        // Nothing moves a headless window, so where it sits after the grow is
        // where it sat before one — which is the position the grow recorded.
        var home = window.Position;

        // What a window manager does to a window whose right edge has just run
        // off the screen.
        window.Position = new PixelPoint(home.X - 210, home.Y);

        var saved = window.Shell.ToWindowSession();

        Assert.Equal(Chosen, saved.Width);
        Assert.Equal((double)home.X, saved.X);
    }

    /// <summary>
    /// The half that already worked, kept honest: within one session closing the
    /// panel still hands the width back.
    /// </summary>
    [AvaloniaFact]
    public void Closing_the_panel_gives_the_borrowed_width_back()
    {
        var window = Borrowing(out var group);

        group.IsInfoVisible = false;

        Assert.Equal(Chosen, window.Width);
        Assert.Equal(Chosen, window.Shell.ToWindowSession().Width);
    }

    /// <summary>
    /// **A width dragged since the grow belongs to whoever dragged it.**
    /// <c>ReleaseGrownWidth</c> already refuses to snap back over a manual
    /// resize, because the recorded width no longer describes anything the user
    /// would recognise — and a session that wrote the recorded one anyway would
    /// undo their drag at the next launch instead, which is the same fault a
    /// day later.
    /// </summary>
    [AvaloniaFact]
    public void A_window_resized_by_hand_since_the_grow_saves_what_it_was_dragged_to()
    {
        var window = Borrowing(out _);

        window.Width = 1200;

        Assert.Equal(1200d, window.Shell.ToWindowSession().Width);
    }

    /// <summary>
    /// And with nothing outstanding the live width is the answer, which is every
    /// window that never opened a panel it had no room for.
    /// </summary>
    [AvaloniaFact]
    public void A_window_that_never_borrowed_saves_the_width_it_has()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();
        _window = window;

        window.Width = 820;

        Assert.Equal(820d, window.Shell.ToWindowSession().Width);
    }
}
