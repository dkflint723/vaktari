using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **The drag gesture itself asks DragPayload, and says why it stops.**
/// TrailingNameDragTests calls <see cref="DragPayload.BuildAsync"/> directly,
/// so nothing there reached the one line in <c>BeginDragAsync</c> that hands
/// the payload to the platform: put the old inline builder back — the storage
/// provider, a FileInfo underneath, which folds "…\report " to "…\report" — and
/// every one of those tests stayed green while a real drag carried the
/// neighbour (fix-7 round-3 verification).
///
/// Driven the way DragKeepsTheSelectionTests drives a drag: a real press on the
/// "report " row of a plainly opened pane and a move past the threshold, so
/// BeginDragAsync runs from the pointer. It must refuse before any drag is in
/// flight, put the refusal naming "report " on the status bar, and leave both
/// files as they were. If a drag does start (the fault), it is cancelled with
/// Escape so nothing is dropped anywhere.
/// </summary>
public sealed class TrailingNameDragGestureTests : OwnedViewModels
{
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static bool Dragging(MainWindow window)
        => (bool)typeof(MainWindow)
            .GetField("_dragging", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("report ")]
    [InlineData("report.")]
    public async Task A_real_drag_of_a_trailing_named_row_is_refused_before_it_starts(string name)
    {
        UseSearch(PaneViewModel.Search);

        // Beside the test binaries, not under %TEMP%: nothing here is volatile.
        var root = Path.Combine(AppContext.BaseDirectory, "vaktari-r3drag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var trailing = Path.Combine(root, name);
        var neighbour = Path.Combine(root, "report");
        File.WriteAllText(@"\\?\" + trailing, "the dragged file");
        File.WriteAllText(neighbour, "the neighbour nobody dragged");

        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = Own(shell).ActiveTab!;

            await pane.NavigateAsync(root);
            await pane.RefreshAsync();
            Settle();

            pane.View = ViewMode.Details;
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Settle();

            var list = window.GetVisualDescendants().OfType<ListBox>()
                .First(l => l.IsVisible && ReferenceEquals(l.DataContext, pane)
                            && l.SelectionMode.HasFlag(SelectionMode.Multiple));

            // The premise: the pane lists the row by its true name and path.
            var rows = pane.DetailsEntries.ToList();
            var index = rows.FindIndex(r => r.FullPath == trailing);
            Assert.True(index >= 0, $"the pane does not list \"{name}\": {string.Join(" | ", rows.Select(r => "[" + r.FullPath + "]"))}");

            list.SelectedItems!.Add(rows[index]);
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Settle();

            var container = (Control)list.ContainerFromIndex(index)!;
            var at = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;

            pane.Status = "";

            window.MouseDown(at, MouseButton.Left);
            Settle();
            window.MouseMove(new Point(at.X + 40, at.Y + 40), RawInputModifiers.LeftMouseButton);

            // Waits for either ending: the refusal on the status bar, or a drag in flight.
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < until && !Dragging(window) && !(pane.Status ?? "").Contains(name, StringComparison.Ordinal))
            {
                Settle();
                await Task.Delay(10);
            }

            var started = Dragging(window);

            if (started)
            {
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Settle();
            }

            window.MouseUp(new Point(at.X + 40, at.Y + 40), MouseButton.Left);
            Settle();

            Assert.False(started, "a drag of \"" + name + "\" went into flight; the platform was handed a payload");
            Assert.Contains("\"" + name + "\"", pane.Status, StringComparison.Ordinal);
            Assert.Contains("cannot be handed to another program", pane.Status, StringComparison.Ordinal);
            Assert.Equal("the neighbour nobody dragged", File.ReadAllText(neighbour));
            Assert.Equal("the dragged file", File.ReadAllText(@"\\?\" + trailing));
        }
        finally
        {
            window.Close();

            foreach (var f in Directory.GetFiles(@"\\?\" + root, "*", SearchOption.AllDirectories)) File.Delete(f);
            Directory.Delete(@"\\?\" + root, recursive: true);
        }
    }
}
