using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A real MainWindow showing one folder in the List layout with every column
/// ticked, for the tests of the columns scrolling and fitting: its pieces, read
/// off the laid-out controls, and the device scaling a headless window can be
/// given.
///
/// The interface text size is pinned AFTER the window is built, because
/// building one reads the desktop's, and it and the settings are put back.
/// </summary>
public abstract class ColumnWindow : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly double? _systemBefore = InterfaceText.SystemScale;
    private readonly int _syncLayoutsBefore = ColumnFitter.SyncLayouts;
    private readonly TimeSpan _syncTimeBefore = ColumnFitter.SyncTime;
    private readonly int _syncRowsBefore = ColumnFitter.SyncRows;
    private readonly Vaktari.Core.FileSystem.IFileMetadataProvider? _metadataBefore = Thumbnails.RowMetadata.Provider;

    protected MainWindow? Window { get; private set; }
    protected string Root { get; } = Directory.CreateTempSubdirectory("vaktari-columns-").FullName;

    public override void Dispose()
    {
        if (Window?.DataContext is ShellViewModel shell)
        {
            foreach (var group in new[] { shell.Right, shell.Left })
            {
                if (group is null) continue;

                shell.ActivateGroup(group);

                foreach (var tab in group.Tabs.ToList())
                {
                    tab.ResetColumnWidthsCommand.Execute(null);
                    tab.View = Vaktari.Core.Session.ViewMode.Details;
                }
            }

            if (shell.IsSplit) shell.ToggleSplit();

            Pump();
        }

        Window?.Close();
        AppSettings.Apply(_settingsBefore);
        InterfaceText.SystemScale = _systemBefore;
        ColumnFitter.SyncLayouts = _syncLayoutsBefore;
        ColumnFitter.SyncTime = _syncTimeBefore;
        ColumnFitter.SyncRows = _syncRowsBefore;
        Thumbnails.RowMetadata.Provider = _metadataBefore;

        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a temp dir is not worth failing over */ }

        base.Dispose();
    }

    protected static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    protected static void Settle(Window window, int passes = 5)
    {
        for (var i = 0; i < passes; i++)
        {
            Pump();
            window.UpdateLayout();
        }
    }

    /// <summary>
    /// Gives a headless window a device scaling, through the platform's own
    /// setter and its change notice, and **says so if it did not take**: the
    /// setter is not public, and a renamed one would otherwise leave every
    /// test here quietly running at 100%.
    /// </summary>
    protected static void SetScaling(Window window, double scale)
    {
        var impl = window.PlatformImpl!;
        var property = impl.GetType().GetProperty(
            "RenderScaling", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(property);

        property!.SetValue(impl, scale);
        (impl.GetType().GetProperty("ScalingChanged")?.GetValue(impl) as Action<double>)?.Invoke(scale);

        Assert.Equal(scale, window.RenderScaling);
    }

    /// <summary>Writes <paramref name="files"/> files with names of growing
    /// length, plus <paramref name="folders"/> folders with a file in each.</summary>
    protected void Fill(int files, int folders = 1)
    {
        for (var i = 0; i < folders; i++)
        {
            Directory.CreateDirectory(Path.Combine(Root, $"folder{i}"));
            File.WriteAllText(Path.Combine(Root, $"folder{i}", "inner.txt"), "x");
        }

        for (var i = 0; i < files; i++)
            File.WriteAllText(Path.Combine(Root, $"file-{i:000}-{new string('n', i % 40)}.txt"), new string('x', i));
    }

    protected async Task<(MainWindow Window, ShellViewModel Shell, PaneViewModel Pane)> Open(
        double zoom = 1.0, double scaling = 1.0, double width = 1600, bool split = false)
    {
        UseSearch(PaneViewModel.Search);

        var window = Window = new MainWindow { Width = width, Height = 800 };

        window.Show();
        Pump();

        if (scaling != 1.0)
        {
            SetScaling(window, scaling);
            Settle(window, 2);
        }

        InterfaceText.SystemScale = null;
        AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with
            {
                InterfaceTextScale = 0,
                Details = new DetailsViewSettings(),
            },
        });

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        if (shell.IsSplit != split) shell.ToggleSplit();

        foreach (var group in new[] { shell.Right, shell.Left })
        {
            if (group?.ActiveTab is not { } pane) continue;

            shell.ActivateGroup(group);

            pane.View = Vaktari.Core.Session.ViewMode.Details;
            pane.ShowTypeColumn = true;
            pane.ShowCreatedColumn = true;
            pane.HideSizeColumn = false;
            pane.HideModifiedColumn = false;
            pane.FontScale = zoom;
            pane.ResetColumnWidthsCommand.Execute(null);

            await pane.NavigateAsync(Root);
            await pane.RefreshAsync();
        }

        shell.RefreshPaneScales();

        foreach (var pane in new[] { shell.Left.ActiveTab, shell.Right?.ActiveTab }.OfType<PaneViewModel>())
            await RowsOnScreen(window, pane);

        return (window, shell, shell.Left.ActiveTab!);
    }

    /// <summary>Waits on the subject: a realised row in this pane's list.</summary>
    protected static async Task RowsOnScreen(Window window, PaneViewModel pane)
    {
        for (var i = 0; i < 400; i++)
        {
            Settle(window, 1);

            if (List(window, pane).GetVisualDescendants().OfType<ListBoxItem>()
                    .Any(item => item.IsVisible && item.DataContext is FileEntry))
                return;

            await Task.Delay(10);
        }

        Assert.Fail($"no row of {pane.CurrentPath} was realised within four seconds");
    }

    protected static ListBox List(Window window, PaneViewModel pane)
        => window.GetVisualDescendants().OfType<ListBox>()
                 .First(l => ReferenceEquals(l.DataContext, pane) && l.ItemsSource == pane.DetailsEntries);

    protected static ScrollViewer Rows(Window window, PaneViewModel pane)
        => List(window, pane).GetVisualDescendants().OfType<ScrollViewer>().First();

    protected static Thumb Grip(Window window, PaneViewModel pane, string column)
        => window.GetVisualDescendants().OfType<Thumb>()
                 .Single(t => (string?)t.Tag == column && ReferenceEquals(t.DataContext, pane));

    protected static Grid Heading(Window window, PaneViewModel pane)
        => Assert.IsType<Grid>(Grip(window, pane, "Name").Parent);

    protected static ScrollViewer Headings(Window window, PaneViewModel pane)
        => Heading(window, pane).FindAncestorOfType<ScrollViewer>()!;

    /// <summary>The heading band: the Border that carries the column menu,
    /// outside the heading scroller's own template.</summary>
    protected static Border Band(Window window, PaneViewModel pane)
        => Heading(window, pane).GetVisualAncestors().OfType<Border>().First(b => b.ContextMenu is not null);

    /// <summary>Every realised row's grid, with its entry.</summary>
    protected static List<(FileEntry Entry, Grid Grid)> RowGrids(Window window, PaneViewModel pane)
        => [.. List(window, pane).GetVisualDescendants().OfType<ListBoxItem>()
               .Where(item => item.IsVisible && item.DataContext is FileEntry)
               .Select(item => ((FileEntry)item.DataContext!,
                                item.GetVisualDescendants().OfType<Grid>()
                                    .FirstOrDefault(g => g.Margin == new Thickness(12, 0, 18, 0))))
               .Where(pair => pair.Item2 is not null)
               .Select(pair => (pair.Item1, pair.Item2!))];

    /// <summary>The right edge of each column, 0 to 6, in the window's pixels.</summary>
    protected static double[] Edges(Grid grid, Visual window)
    {
        var x = grid.TranslatePoint(default, window)!.Value.X;
        var edges = new double[7];

        for (var c = 0; c < 7; c++)
        {
            x += grid.ColumnDefinitions[c].ActualWidth;
            edges[c] = x;
        }

        return edges;
    }

    /// <summary>The worst distance between a heading edge and the same edge
    /// on any realised row.</summary>
    protected static double Worst(Window window, PaneViewModel pane)
    {
        var heading = Edges(Heading(window, pane), window);
        var worst = 0.0;

        foreach (var (_, grid) in RowGrids(window, pane))
        {
            var row = Edges(grid, window);

            for (var c = 0; c < 7; c++) worst = Math.Max(worst, Math.Abs(heading[c] - row[c]));
        }

        return worst;
    }

    /// <summary>The visible row: the heading scroller's viewport less the grid's margins.</summary>
    protected static double VisibleRow(Window window, PaneViewModel pane)
        => Headings(window, pane).Viewport.Width - 30;

    /// <summary>A column's cell index in both grids.</summary>
    protected static int Cell(DetailsColumn column) => ColumnFitter.Index(column);

    /// <summary>
    /// Presses on <paramref name="column"/>'s right edge as drawn, moves the
    /// pointer <paramref name="dx"/> in four steps with the window laid out
    /// between them, and lets go — unless <paramref name="release"/> is false.
    /// </summary>
    protected static Point Drag(MainWindow window, PaneViewModel pane, DetailsColumn column, double dx, bool release = true)
    {
        var heading = Heading(window, pane);
        var edge = Edges(heading, window)[Cell(column)];
        var press = new Point(edge, heading.TranslatePoint(new Point(0, heading.Bounds.Height / 2), window)!.Value.Y);

        window.MouseMove(press);
        window.MouseDown(press, Avalonia.Input.MouseButton.Left);
        Pump();

        for (var step = 1; step <= 4; step++)
        {
            window.MouseMove(press + new Point(dx * step / 4, 0));
            Pump();
            window.UpdateLayout();
        }

        if (release)
        {
            window.MouseUp(press + new Point(dx, 0), Avalonia.Input.MouseButton.Left);
            Settle(window);
        }

        return press + new Point(dx, 0);
    }
}