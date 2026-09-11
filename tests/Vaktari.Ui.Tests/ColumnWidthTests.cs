using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dragging a details heading's edge, from the grip to the file.
///
/// **The four metadata columns were fixed widths, in a file manager.** Every
/// table a person has used lets a heading's edge be dragged; here the width
/// was a number in the metric pipeline that scaled with the text and could not
/// otherwise be moved, so a date that trimmed its year at the designed width
/// trimmed it at every size. These pin the arithmetic — a chosen width is
/// drawn through the same scale the designed one was, and a drag is measured
/// in the pixels the column was drawn at — and the two ends: every pane on
/// screen takes the width at once, and the file is written when the drag
/// ends.
///
/// Both statics the metric pipeline reads are put back afterwards, as
/// InterfaceTextSizeTests does and for its reason: this assembly runs its
/// classes in sequence, and a leak here lands on somebody else's assertion.
/// </summary>
public sealed class ColumnWidthTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly double? _systemBefore = InterfaceText.SystemScale;
    private MainWindow? _window;

    /// <summary>Stated, not inherited: the published value is this machine's
    /// own desktop text size, which is 125% on the desktop these were written
    /// on and 100% on the runner.</summary>
    public ColumnWidthTests() => InterfaceText.SystemScale = null;

    public override void Dispose()
    {
        _window?.Close();
        AppSettings.Apply(_settingsBefore);
        InterfaceText.SystemScale = _systemBefore;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Configure(DetailsViewSettings details, double interfaceScale = 0)
        => AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with
            {
                InterfaceTextScale = interfaceScale,
                Details = details,
            },
        });

    private static double Metric(string key, double fontScale = 1.0)
        => PaneScale.Compute(fontScale, 1.0).Single(m => m.Key == key).Value;

    private static DetailsViewSettings Chosen => AppSettings.Current.Views.Details;

    // ---- the width a column is drawn at -----------------------------------------

    /// <summary>
    /// **Zero is "as designed", not a width.** A settings.json written before
    /// these keys existed has none of them, deserialization does not run
    /// initializers, and so every upgrading install's columns arrive as zero
    /// — which has to draw as the width they always had rather than as the
    /// narrowest a column may be.
    /// </summary>
    [AvaloniaFact]
    public void A_column_nobody_has_dragged_is_drawn_at_its_designed_width()
    {
        Configure(new DetailsViewSettings());

        Assert.Equal(110, Metric("ColType"));
        Assert.Equal(100, Metric("ColSize"));
        Assert.Equal(150, Metric("ColModified"));
        Assert.Equal(150, Metric("ColCreated"));

        Assert.All(Enum.GetValues<DetailsColumn>(),
                   column => Assert.Equal(PaneScale.DesignedWidth(column),
                                          PaneScale.ColumnWidth(new DetailsViewSettings(), column)));
    }

    /// <summary>
    /// A chosen width rides the same scale the designed one did — the
    /// interface size and the pane's own zoom — or a column dragged to 200 at
    /// 100% would be a column dragged to 200 at every size, which is the fault
    /// the metric pipeline exists to prevent. And only the column that was
    /// dragged: its neighbour stays designed.
    /// </summary>
    [AvaloniaFact]
    public void A_chosen_width_is_drawn_through_the_same_scale_as_a_designed_one()
    {
        Configure(new DetailsViewSettings { ModifiedColumn = 200 }, interfaceScale: 1.5);

        Assert.Equal(300, Metric("ColModified"));
        Assert.Equal(600, Metric("ColModified", fontScale: 2.0));

        Assert.Equal(225, Metric("ColCreated"));
    }

    /// <summary>
    /// settings.json can be edited by hand, and a column 2 pixels wide is a
    /// column that has vanished with its heading still ticked — held to the
    /// range on the way in, the way InterfaceText holds the text size.
    /// </summary>
    [AvaloniaFact]
    public void A_width_written_by_hand_is_held_to_the_range()
    {
        Configure(new DetailsViewSettings { TypeColumn = 2, SizeColumn = 4000 });

        Assert.Equal(PaneScale.ColumnMin, Metric("ColType"));
        Assert.Equal(PaneScale.ColumnMax, Metric("ColSize"));
    }

    // ---- the drag ---------------------------------------------------------------

    /// <summary>
    /// **The grip reports pixels on screen and the width is kept at 100%.**
    /// The column under the pointer was drawn at the pane's zoom times the
    /// interface size, so the pixels are divided by that product on the way
    /// in — exactly what PaneScale multiplies by on the way out. Without the
    /// division a column at 300% moves a third as far as the pointer, and the
    /// grip slides out from under it.
    /// </summary>
    [AvaloniaFact]
    public void A_drag_moves_the_width_by_the_pixels_dragged_at_the_scale_the_column_was_drawn_at()
    {
        Configure(new DetailsViewSettings(), interfaceScale: 1.5);

        var shell = Own(new ShellViewModel(new InertFileSystem()));

        // Drawn at 1.5 × 2.0 = 3.0, so 30 pixels on screen are 10 at 100%.
        shell.ResizeColumn(DetailsColumn.Type, 30, paneFontScale: 2.0);

        Assert.Equal(120, Chosen.TypeColumn);
        Assert.Equal(0, Chosen.SizeColumn);
    }

    /// <summary>
    /// The drag stops at the ends of the range rather than storing a width
    /// the file would then be held back from. The STORED number is what is
    /// asserted: reading clamps too, so a drag that stored −900 would still
    /// draw something and the fault would be in the file alone.
    /// </summary>
    [AvaloniaFact]
    public void A_drag_stops_at_the_ends_of_the_range()
    {
        Configure(new DetailsViewSettings());

        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.ResizeColumn(DetailsColumn.Size, -1000, paneFontScale: 1.0);
        Assert.Equal(PaneScale.ColumnMin, Chosen.SizeColumn);

        shell.ResizeColumn(DetailsColumn.Size, 5000, paneFontScale: 1.0);
        Assert.Equal(PaneScale.ColumnMax, Chosen.SizeColumn);
    }

    /// <summary>
    /// **The width is one preference, not a property of the pane that was
    /// dragged**, so every pane on screen takes it at once — through the same
    /// re-apply a zoom notch runs. Read at the pane's own dictionary, which is
    /// where the cells resolve their width from.
    /// </summary>
    [AvaloniaFact]
    public void Every_pane_on_screen_takes_the_width_at_once()
    {
        Configure(new DetailsViewSettings());

        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Path.GetTempPath());

        var control = new Panel();

        PaneScale.SetPane(control, shell.ActiveTab);

        Assert.Equal(150.0, control.Resources["ColModified"]);

        shell.ResizeColumn(DetailsColumn.Modified, 50, paneFontScale: 1.0);

        Assert.Equal(200.0, control.Resources["ColModified"]);
    }

    /// <summary>
    /// The way back from a drag that went too far: all four to designed, and
    /// written out at once — a reset nobody let go of would otherwise be lost
    /// at the next start.
    /// </summary>
    [AvaloniaFact]
    public void Reset_puts_every_column_back_and_writes_it()
    {
        Configure(new DetailsViewSettings { TypeColumn = 300, CreatedColumn = 90 });

        var shell = Own(new ShellViewModel(new InertFileSystem()));
        SettingsState? written = null;

        shell.ColumnWidthsChanged += (_, settings) => written = settings;

        shell.ResetColumnWidthsCommand.Execute(null);

        Assert.Equal(0, Chosen.TypeColumn);
        Assert.Equal(0, Chosen.CreatedColumn);
        Assert.Equal(110, Metric("ColType"));

        Assert.NotNull(written);
        Assert.Equal(0, written!.Views.Details.TypeColumn);
    }

    // ---- the window -------------------------------------------------------------

    /// <summary>
    /// The real grip on the real heading, driven by the events a Thumb raises:
    /// the width moves as it is dragged, and the file is written when it is
    /// let go — not before. A drag is a hundred events, and each one written
    /// to disk is a hundred atomic rewrites of settings.json for one gesture.
    ///
    /// The desktop's text size is stated AFTER the window is built, because
    /// building one reads it from the palette; and the widths are set after
    /// it too, because a window applies whatever the settings on disk say.
    ///
    /// The pane is zoomed to 200% first, so that the forty pixels dragged are
    /// twenty at 100%: it is THIS pane's scale the column under the pointer
    /// was drawn at, and a handler that passed 1.0 — or the other side of a
    /// split's — would move the column a different distance from the pointer.
    /// </summary>
    [AvaloniaFact]
    public void The_grip_moves_the_column_and_letting_go_writes_the_file()
    {
        UseSearch(PaneViewModel.Search);

        // 1800, because the column thresholds scale with the pane: at 200%
        // the modified column needs a pane 1040 wide, and 1200 less the
        // sidebar was not that.
        var window = _window = new MainWindow { Width = 1800, Height = 800 };

        window.Show();
        Pump();

        InterfaceText.SystemScale = null;
        Configure(new DetailsViewSettings());

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        shell.ActiveTab!.FontScale = 2.0;
        shell.RefreshPaneScales();
        Pump();

        var grip = Grip(window, DetailsColumn.Modified);

        Assert.True(grip.IsVisible, "the modified column is on by default and, at 200%, needs a pane 1040 wide");

        grip.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(40, 0) });

        Assert.Equal(170, Chosen.ModifiedColumn);
        Assert.Equal(0, window.Services.SettingsStore.Load().Views.Details.ModifiedColumn);

        grip.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = new Vector(40, 0) });

        Assert.Equal(170, window.Services.SettingsStore.Load().Views.Details.ModifiedColumn);
    }

    /// <summary>
    /// **A grip hides WITH its heading, and that is not tidiness.** The cell
    /// is Auto, so a grip left visible in a column that is off keeps the cell
    /// six pixels wide with nothing in it, and the rows — whose cell really
    /// is empty — draw six pixels left of the headings from there on.
    /// Measured in this window with the created column off: 0 wide with the
    /// grip's binding, 6 without it.
    ///
    /// The column's state is SET rather than read: the window is built from
    /// the session on disk, and a session whose last tab had the column on
    /// would read the first assertion backwards.
    /// </summary>
    [AvaloniaFact]
    public void A_grip_hides_with_its_column()
    {
        UseSearch(PaneViewModel.Search);

        var window = _window = new MainWindow { Width = 1800, Height = 800 };

        window.Show();
        Pump();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var pane = shell.ActiveTab!;

        pane.ShowCreatedColumn = false;
        Pump();
        window.UpdateLayout();

        var grip = Grip(window, DetailsColumn.Created);
        var header = Assert.IsType<Grid>(grip.Parent);

        Assert.Equal(0, header.ColumnDefinitions[6].ActualWidth);

        pane.ShowCreatedColumn = true;
        Pump();
        window.UpdateLayout();

        Assert.True(header.ColumnDefinitions[6].ActualWidth >= PaneScale.ColumnMin,
                    $"with the column on its cell was {header.ColumnDefinitions[6].ActualWidth} wide");
    }

    // ---- the markup -------------------------------------------------------------

    /// <summary>
    /// The markup half of the test above, for all four: each grip sits in its
    /// heading's cell, hides on exactly the heading's condition, names a
    /// column the shell knows, says what it is to a screen reader, and is
    /// wired to the two handlers. The shape they share — width, cursor, the
    /// bare template — is one Style, so it cannot drift between them.
    /// </summary>
    [AvaloniaFact]
    public void Each_grip_sits_in_its_headings_cell_and_hides_with_it()
    {
        var doc = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var grips = doc.Descendants(Avalonia + "Thumb")
                       .Where(t => (string?)t.Attribute("Classes") == "columnGrip")
                       .ToList();

        Assert.Equal(
            Enum.GetValues<DetailsColumn>().Order(),
            grips.Select(g => Enum.Parse<DetailsColumn>((string)g.Attribute("Tag")!)).Order());

        foreach (var grip in grips)
        {
            var cell = (string?)grip.Attribute("Grid.Column");

            var heading = grip.Parent!
                .Elements(Avalonia + "Button")
                .Single(b => (string?)b.Attribute("Grid.Column") == cell);

            Assert.NotNull((string?)heading.Attribute("IsVisible"));
            Assert.Equal((string?)heading.Attribute("IsVisible"), (string?)grip.Attribute("IsVisible"));

            Assert.StartsWith("Resize the ", (string?)grip.Attribute("AutomationProperties.Name"), StringComparison.Ordinal);
            Assert.Equal("OnColumnGripDragDelta", (string?)grip.Attribute("DragDelta"));
            Assert.Equal("OnColumnGripDragCompleted", (string?)grip.Attribute("DragCompleted"));
        }

        var style = doc.Descendants(Avalonia + "Style")
                       .Single(s => (string?)s.Attribute("Selector") == "Thumb.columnGrip");

        var setters = style.Elements(Avalonia + "Setter").ToList();

        Assert.Contains(setters, s => (string?)s.Attribute("Property") == "Cursor"
                                      && (string?)s.Attribute("Value") == "SizeWestEast");
        Assert.Contains(setters, s => (string?)s.Attribute("Property") == "HorizontalAlignment"
                                      && (string?)s.Attribute("Value") == "Right");
        Assert.Contains(setters, s => (string?)s.Attribute("Property") == "Template");
    }

    /// <summary>
    /// The way back is on both menus that choose the columns — the one on
    /// the headings and the one a keyboard opens, since the grips are a
    /// pointer's — and reaches the shell through the window from either,
    /// since the widths are one preference for every pane.
    /// </summary>
    [AvaloniaFact]
    public void Both_column_menus_offer_the_way_back()
    {
        var doc = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var rows = doc.Descendants(Avalonia + "MenuItem")
                      .Where(m => (string?)m.Attribute("Header") == "Reset column _widths")
                      .ToList();

        Assert.Equal(2, rows.Count);

        Assert.All(rows, row => Assert.Equal(
            "{Binding $parent[Window].((vm:ShellViewModel)DataContext).ResetColumnWidthsCommand}",
            (string?)row.Attribute("Command")));
    }

    // ---- helpers ----------------------------------------------------------------

    private static Thumb Grip(Window window, DetailsColumn column)
        => window.GetVisualDescendants()
                 .OfType<Thumb>()
                 .Single(t => (string?)t.Tag == column.ToString());

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

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
}
