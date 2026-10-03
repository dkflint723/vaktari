using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The prompt bar, as rename QA found it: a long in-use sentence and its hint
/// ran off the window's edge, Try again had the keyboard without showing it,
/// and Enter in the bar's own box — naming a pinned place, connecting to a
/// server — did nothing although the hint said "enter to confirm".
/// </summary>
public sealed class PromptBarLayoutAndKeysTests : OwnedViewModels
{
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private MainWindow Open(double width)
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();
        window.Show();
        Settle();

        window.Width = width;
        window.Height = 700;
        window.UpdateLayout();
        Settle();

        return window;
    }

    private static void Close(MainWindow window)
    {
        window.Close();

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (window.Services.Windows.Count > 0 && DateTime.UtcNow < deadline)
        {
            Settle();
            Thread.Sleep(1);
        }
    }

    private static void Offer(MainWindow window, InUseOffer offer)
        => typeof(MainWindow)
            .GetMethod("OnInUseRequested", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [window.Shell.ActiveTab, offer]);

    private const string Long =
        "could not rename “Quarterly reports for the regional offices, final” — something inside that folder is open";

    /// <summary>
    /// **Everything after "search the name) or" was lost** at 1300 px, and
    /// more at the 560 px minimum. The sentence and the hint end inside the
    /// bar at both widths.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(560)]
    [InlineData(1300)]
    public void The_in_use_bar_keeps_every_word_inside_the_window(double width)
    {
        var window = Open(width);

        try
        {
            Offer(window, new InUseOffer(Long, () => Task.FromResult(true)));
            window.UpdateLayout();
            Settle();

            var bar = window.FindControl<Border>("PromptBar")!;

            Assert.True(bar.IsVisible);
            Assert.True(bar.Bounds.Width <= window.Bounds.Width + 0.5, $"the bar is {bar.Bounds.Width} wide in a {window.Bounds.Width} window");

            foreach (var name in new[] { "PromptLabel", "PromptHint", "PromptConfirm", "PromptCancel" })
            {
                var part = window.FindControl<Control>(name)!;
                var right = part.TranslatePoint(new Point(part.Bounds.Width, part.Bounds.Height), bar)!.Value;

                Assert.True(part.Bounds.Width > 0, $"{name} has no width");
                Assert.True(right.X <= bar.Bounds.Width + 0.5,
                            $"{name} ends at {right.X:0} in a bar {bar.Bounds.Width:0} wide, at a {width} px window");
                Assert.True(right.Y <= bar.Bounds.Height + 0.5,
                            $"{name} ends below the bar ({right.Y:0} of {bar.Bounds.Height:0})");

                // **And the words fit the box they were given.** Layout clamps
                // a text block to the room it has, so a line that does not wrap
                // still reports bounds inside the bar while its last words are
                // drawn past them and cut off — the text's own width says so.
                if (part is TextBlock text)
                    Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 0.5,
                                $"{name}'s text is {text.TextLayout.WidthIncludingTrailingWhitespace:0} wide in a box {text.Bounds.Width:0} wide, at a {width} px window");
            }
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// Try again has the keyboard when the bar opens, and shows it: focus
    /// given as the keyboard would give it, which is what draws the ring.
    /// </summary>
    [AvaloniaFact]
    public void Try_again_shows_that_it_has_the_keyboard()
    {
        var window = Open(1000);

        try
        {
            Offer(window, new InUseOffer(Long, () => Task.FromResult(true)));
            Settle();

            var button = window.FindControl<Button>("PromptConfirm")!;

            Assert.True(button.IsFocused);
            Assert.True(button.Classes.Contains(":focus-visible"), "Try again has the keyboard and shows nothing");
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// "Connect to": Enter in the box confirms. An empty address connects to
    /// nothing, so the test reaches no server — the bar closing is the answer.
    /// </summary>
    [AvaloniaFact]
    public void Enter_in_the_connect_box_confirms()
    {
        var window = Open(1000);

        try
        {
            window.Shell.ConnectCommand.Execute(null);
            Settle();

            var input = window.FindControl<TextBox>("PromptInput")!;
            var bar = window.FindControl<Border>("PromptBar")!;

            Assert.True(bar.IsVisible);
            Assert.True(input.IsFocused, "the address box does not have the keyboard");

            input.Text = "   ";
            Settle();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle();

            Assert.False(bar.IsVisible, "Enter in the address box did nothing");
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// "Call it": Enter in the box confirms. A place the person did not pin
    /// is not renamed by it, so nothing is written — the bar closing is the
    /// answer.
    /// </summary>
    [AvaloniaFact]
    public void Enter_in_the_place_name_box_confirms()
    {
        var window = Open(1000);

        try
        {
            SidebarReady(window);

            var place = window.Shell.Sidebar.Groups.SelectMany(g => g.Places).First(p => !p.IsUserPinned);

            typeof(MainWindow)
                .GetMethod("OnRenamePlaceRequested", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, [window.Shell, place]);
            Settle();

            var input = window.FindControl<TextBox>("PromptInput")!;
            var bar = window.FindControl<Border>("PromptBar")!;

            Assert.True(bar.IsVisible);
            Assert.True(input.IsFocused, "the name box does not have the keyboard");

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle();

            Assert.False(bar.IsVisible, "Enter in the name box did nothing");
        }
        finally
        {
            Close(window);
        }
    }
}
