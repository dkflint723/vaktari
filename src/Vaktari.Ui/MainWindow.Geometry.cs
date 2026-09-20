using Avalonia;
using Avalonia.Controls;
using Vaktari.Core.Session;
using Vaktari.Ui.Session;

namespace Vaktari.Ui;

/// <summary>
/// Where the window is, how big, and where the next one comes from.
///
/// Four members, out of a banner that said "geometry" over fourteen. The
/// saved size and position going on and coming off, and the two ways a second
/// window appears — opened by hand, or restored from the session, which are
/// the same act with a different source for the numbers.
///
/// **The window family is here because it is the same vocabulary.** Opening
/// another window means deciding its geometry before it is shown, and a
/// restored one means reading geometry somebody else wrote; both go through
/// WindowSession and both hand the founder's shared half to the new window
/// rather than building a second one.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The saved size and position, put back.
    ///
    /// **This rejected anything at or below 200 and let everything else
    /// through, and 200 is not a number this window knows anything about.** The
    /// floor is MinWidth/MinHeight in the markup, and the note beside them says
    /// a session that saved something smaller is clamped up on restore — which
    /// was true only because Avalonia raises Width to MinWidth when the window
    /// is measured, not because anything here did it. Between this call and that
    /// first measure the property holds the undersized number, and
    /// CaptureGeometry reads the property.
    ///
    /// So the two guards now say the two different things they were conflating.
    /// Zero is what an absent key deserializes to — the note beside the
    /// per-layout scales in SessionModel records that these initializers do not
    /// run — and an absent size must leave the window the size the markup opens
    /// it at, NOT shrink it to the smallest one allowed. Anything else is a real
    /// saved size, and a real saved size below the floor is raised to the floor,
    /// because it is unusable whether it was chosen this session or last.
    ///
    /// Internal rather than private so WindowFloorTests can hand it a session
    /// directly. The store this is otherwise fed from is the real one on the
    /// machine running the suite.
    ///
    /// <paramref name="index"/> is which saved window this one is. It defaults
    /// to the first so the tests that hand a session straight in are unchanged,
    /// and ElementAtOrDefault answers null for the negative index a window
    /// opened from another one carries — such a window has no saved geometry
    /// and is placed beside its opener instead.
    /// </summary>
    internal void ApplyGeometry(SessionState? state, int index = 0)
    {
        if (state?.Windows.ElementAtOrDefault(index) is not { } w) return;

        if (w.Width > 0) Width = Math.Max(w.Width, MinWidth);
        if (w.Height > 0) Height = Math.Max(w.Height, MinHeight);

        if (w.X != 0 || w.Y != 0)
            Position = new PixelPoint((int)w.X, (int)w.Y);

        if (w.IsMaximized)
            WindowState = Avalonia.Controls.WindowState.Maximized;
    }

    /// <summary>
    /// A peer, on the folder it was asked for, carrying the view it was opened
    /// from.
    ///
    /// **The same principle NewTab states five lines above the command that
    /// leads here**: "A new tab that resets all five is a new tab you have to
    /// set up." A window is the heavier version of that tab, so it carries the
    /// same things — the sidebar width and rail, the folded sections, the split
    /// ratio, the zoom, and the tab's own layout, sort and hidden files through
    /// <c>like</c>. Font scale in particular is an accessibility setting rather
    /// than a preference: a window that arrives at 1.0 for somebody who works
    /// at 1.4 is a window they have to fix before they can read it.
    ///
    /// Geometry is deliberately NOT carried. The new window is offset beside
    /// the opener rather than stacked exactly on top of it, which is what makes
    /// it visible that a second one opened at all.
    /// </summary>
    private void OpenNewWindow(string? folder)
    {
        var seed = _shell.ToWindowSession() with
        {
            // The view, not the contents and not the frame. An empty pane list
            // is what leaves the tab to the folder that was asked for.
            Panes = [],
            RememberedRightPane = null,
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            IsMaximized = false,
        };

        var window = new MainWindow(
            _services,
            restoreIndex: -1,
            openAt: string.IsNullOrWhiteSpace(folder) ? null : folder,
            seed: seed,
            like: _shell.ActiveTab);

        window.Show();

        // Offset only from a normal window: a maximized or minimized opener has
        // no useful position to step away from, and Position on a maximized
        // window is the screen corner.
        if (WindowState == WindowState.Normal)
            window.Position = new PixelPoint(Position.X + 32, Position.Y + 32);

        window.Raise();
    }

    /// <summary>
    /// Window <paramref name="index"/> of the saved session, opened by the
    /// founder after its own constructor has finished. Only the founder does
    /// this, so a restored window cannot recurse into opening more.
    /// </summary>
    private void RestoreWindow(int index)
    {
        var window = new MainWindow(
            _services, restoreIndex: index, openAt: null, seed: null, like: null);

        window.Show();
    }

    private WindowSession CaptureGeometry()
    {
        var maximized = WindowState == Avalonia.Controls.WindowState.Maximized;

        return new WindowSession
        {
            // While maximized the live bounds are the screen, not the size to
            // return to, so the stored values are left alone.
            X = maximized ? 0 : Position.X,
            Y = maximized ? 0 : Position.Y,
            Width = maximized ? 1000 : Width,
            Height = maximized ? 680 : Height,
            IsMaximized = maximized,
        };
    }
}
