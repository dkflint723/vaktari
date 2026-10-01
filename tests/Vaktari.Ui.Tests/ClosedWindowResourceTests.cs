using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **A closed window went on paying for every change to the application's
/// resources until the collector took it** (batch-0.11.2c, the Ubuntu runner
/// cancelled at 30 minutes with a third of the Ui suite run).
///
/// A closed MainWindow kept its whole tree — about 390 controls — and every
/// write to Application.Resources reached each of them, as long as the window
/// had not been collected. ThemeApplier writes dozens of resources for every
/// window it dresses, so each window opened cost more than the one before it,
/// by every closed window still waiting for a full collection: measured, 1 ms
/// a write with none waiting and 17 ms with forty. On this machine and under
/// WSL a full collection came along often enough to hide it; on the runner
/// it stopped coming, and opening one window took thirteen seconds by the
/// end.
///
/// Measured here without the collector's help, by keeping the closed windows
/// — which is all that waiting for a collection is. A control from inside
/// each window is listened to, and a change to the application's resources
/// after they have all closed must reach none of them. Avalonia-bound, so a
/// headless fact; it runs on every platform.
/// </summary>
public sealed class ClosedWindowResourceTests : OwnedViewModels
{
    private const int Windows = 5;

    [AvaloniaFact]
    public async Task A_closed_window_hears_no_change_to_the_applications_resources()
    {
        UseSearch(PaneViewModel.Search);

        var kept = new List<MainWindow>();
        var told = new int[Windows];

        try
        {
            for (var i = 0; i < Windows; i++)
            {
                var window = new MainWindow();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                // A control from deep inside the window, which a resource
                // change reaches through the tree it hangs in.
                var inside = window.GetVisualDescendants().OfType<TextBlock>().First();
                var n = i;
                inside.ResourcesChanged += (_, _) => told[n]++;

                // While the window is open, the listener can hear at all.
                // Without this, a listener that never fired would pass below.
                Application.Current!.Resources["ClosedWindowProbe"] = new SolidColorBrush(Colors.Red);
                Dispatcher.UIThread.RunJobs();
                Assert.True(told[i] > 0, "an open window's control did not hear a resource change");

                window.Close();

                for (var k = 0; k < 200 && window.IsVisible; k++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(5);
                }

                Assert.False(window.IsVisible, "the window did not close");

                kept.Add(window);
            }

            Array.Clear(told);

            for (var w = 0; w < 5; w++)
                Application.Current!.Resources["ClosedWindowProbe"] = new SolidColorBrush(Color.FromRgb((byte)w, 0, 0));

            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new int[Windows], told);
        }
        finally
        {
            Application.Current!.Resources.Remove("ClosedWindowProbe");
            GC.KeepAlive(kept);
        }
    }
}
