using Avalonia;
using Avalonia.Controls;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// What has to be true before the window is allowed to close.
///
/// **A close is a question before it is an event.** Something may still be
/// running, or more tabs may be open than somebody meant to shut at once, so
/// OnClosing cancels the close, asks, and only then lets it through — which is
/// why <c>_closeApproved</c> exists and why it lives here rather than with the
/// window's other fields. Without a flag the second close would ask the same
/// question again and the window would never shut.
///
/// CountOpenTabs and ConfirmCloseAsync are private to that question and have
/// no other caller. OnClosed is the other half: what is let go of once the
/// answer was yes.
/// </summary>
public partial class MainWindow
{
    private bool _closeApproved;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;

        // **The transfer question comes first, and it is not behind a
        // preference.** Closing a window used to kill its transfer and the
        // process was ending, so nothing survived to notice. With a second
        // window the process carries on: the handle would go on writing with no
        // bar showing it, no Cancel reaching it and nobody told when it failed.
        // The tabs question below is gated on a preference that is off by
        // default, and a question about losing work must not be.
        if (_shell.RunningDescription() is { } running)
        {
            e.Cancel = true;

            if (!await ConfirmCloseAsync(running)) return;

            // Said yes. The transfer belongs to this window's bar, and leaving
            // it running is the silent loss the question exists to prevent.
            _shell.CancelAllOperations();
        }

        // **One question, not two.** `else if` rather than a second `if`: a
        // window with a transfer running and six tabs open must not ask twice
        // in a row, and the transfer question is the one that costs something.
        // RunningDescription carries the tab count when both apply, so nothing
        // is hidden by the branch that did not run.
        //
        // Asked before anything is torn down, and only when there is something
        // to lose. Off by default: the session is restored on next launch, so
        // closing a window full of tabs is not actually destructive here — which
        // is exactly why this is a preference rather than the behaviour.
        else if (AppSettings.Current.General.ConfirmClosingMultipleTabs && CountOpenTabs() > 1)
        {
            e.Cancel = true;

            var confirmed = await ConfirmCloseAsync(
                $"{CountOpenTabs()} tabs are open. Close anyway?");

            if (!confirmed) return;
        }

        // Cancel, flush, then close for real. Awaiting inside an async void
        // handler does not hold the window open — the process can otherwise
        // exit with the write still in flight.
        e.Cancel = true;

        // Asked BEFORE the release below, which takes this window out of the
        // list — after it, a second-to-last window would look like the last.
        var last = _services.IsLastWindow;

        // The session goes first. It is the one whose loss the user would
        // actually notice, and it cannot fail because of a subprocess. What is
        // written, what is flushed and what is only released on the way out of
        // the PROCESS all live on the services, because they are the
        // application's and not this window's.
        await _services.ReleaseAsync(this);

        // **Only the last window out.** IFileSharing.StopAllAsync is documented
        // "called on shutdown so nothing outlives the app", and platform.Sharing
        // is ONE CopypartyShare for every window — its running dictionary holds
        // every server in the process and StopAllAsync kills the lot. Measured:
        // two shells built over one provider, a share started through the
        // first, and the second shell's StopAllSharesAsync emptied Active. So
        // closing one of two windows was killing the other's server while its
        // Sharing section went on listing the folder as served.
        //
        // A window is not the process. Stopping only on the way out of the
        // process is what the interface's own contract asks for; per-window
        // ownership of individual shares is a second stage, and until it exists
        // a share started anywhere outlives every window but the last.
        if (last)
        {
            try
            {
                await _shell.StopAllSharesAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vaktari] stopping shares failed: {ex.Message}");
            }
        }

        _closeApproved = true;
        Close();
    }

    /// <summary>
    /// What a closed window lets go of.
    ///
    /// **`_shell.Dispose()` on its own is not teardown, and that is measured**:
    /// after Dispose, CutMarks.Mark still set the shell's CutPaths, because
    /// Dispose only stopped the rate timer and tore down the panes. Nothing in
    /// this application unsubscribed from anything, which was harmless for as
    /// long as a window lived exactly as long as the process — and stops being
    /// harmless the moment one can close while the others carry on.
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        if (_theme is not null && _onThemeChanged is not null)
            _theme.Changed -= _onThemeChanged;

        Thumbnails.IconLoader.SourceChanged -= _onIconSourceChanged;

        UnwatchKeymap();

        _shell.Dispose();
    }

    private int CountOpenTabs()
    {
        var total = _shell.Left.Tabs.Count;
        if (_shell.Right is { } right) total += right.Tabs.Count;

        return total;
    }

    /// <summary>
    /// A real dialog rather than the prompt bar: the prompt bar lives inside
    /// the window being closed, and driving a close decision from a control
    /// that is about to be destroyed is the shape of bug this project has
    /// already paid for once with Shift+Delete.
    /// </summary>
    private async Task<bool> ConfirmCloseAsync(string question)
    {
        var dialog = new Window
        {
            Title = "Close Vaktari",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        AppIcon.Apply(dialog);

        var result = false;

        // **These two said "close anyway" and "cancel" while the four
        // dialogs beside them said "Cancel".** A window built in code is
        // still a window; sentence case is the one rule, and
        // LabelCasingTests reads this file for exactly that reason.
        var close = new Button { Content = "Close anyway", Padding = new Thickness(14, 4) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4) };

        close.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = question,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, close },
                },
            },
        };

        // Focused so Enter and Space reach a real button rather than a
        // hand-rolled key path.
        cancel.Focus();

        await dialog.ShowDialog(this);

        // Deliberately does NOT close. Calling Close() here would re-enter
        // OnClosing with _closeApproved still false and confirm forever; the
        // caller falls through to the existing flush-then-close path instead.
        return result;
    }
}
