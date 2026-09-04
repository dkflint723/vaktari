using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a listing row is called, for anything reading the window rather than
/// looking at it.
///
/// **Every row was read out as the record's ToString.** The window names its
/// breadcrumbs, its four sort headers, its sidebar places and their group
/// headings, and named none of the rows — which are most of what the window is.
/// A ListBoxItem with no AutomationProperties.Name whose template is anything
/// other than one piece of text falls back to the item itself, so every row
/// announced
///
///   FileEntry { Name = report.txt, FullPath = /a/report.txt, Length = 1,
///   LastWriteTime = 01/01/1970 00:00:00 +00:00, Flags = None,
///   IsDirectory = False, IsHidden = False, ... }
///
/// Measured rather than assumed: that is what ListItemAutomationPeer.GetName()
/// returned for a row shaped like ours before the setter went in. Arrowing down
/// a folder read ten fields per row, with the filename second.
/// </summary>
public sealed class ListingRowNameTests : OwnedViewModels
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    // ---- all three listings carry it ----------------------------------------

    /// <summary>
    /// Discovered from the markup rather than listed here, so a fourth layout
    /// added later is held to this without anybody remembering — the same way
    /// the ghosting and the name tooltip are.
    /// </summary>
    [AvaloniaFact]
    public void Every_listing_names_its_rows()
    {
        var listings = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "ListBox")
            .Where(l => (string?)l.Attribute("ItemsSource")
                        is "{Binding DetailsEntries}" or "{Binding CompactEntries}"
                           or "{Binding GridEntries}")
            .ToList();

        // A guard, not decoration: a renamed listing must fail here rather than
        // silently drop out of the check below.
        Assert.Equal(3, listings.Count);

        var anonymous = listings
            .Where(l => !l.Descendants(Xaml + "Setter").Any(
                       s => (string?)s.Attribute("Property") == "AutomationProperties.Name"
                            && ((string?)s.Attribute("Value"))
                               ?.Contains("FileConverters.RowName", StringComparison.Ordinal) == true))
            .Select(l => (string)l.Attribute("ItemsSource")!)
            .ToList();

        Assert.True(anonymous.Count == 0,
            "these listings hand a reader the record's ToString instead of a row name: "
            + string.Join(", ", anonymous));
    }

    // ---- and a container named that way is really what a reader gets --------

    private const string Listing = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="clr-namespace:Vaktari.Ui.ViewModels;assembly=Vaktari.Ui">
          <ListBox x:Name="Rows">
            <ListBox.Styles>
              <Style Selector="ListBoxItem">
                <Setter Property="AutomationProperties.Name"
                        Value="{Binding Converter={x:Static vm:FileConverters.RowName}}"/>
              </Style>
            </ListBox.Styles>
            <ListBox.ItemTemplate>
              <DataTemplate>
                <!-- Several cells under a non-text root, which is the shape all
                     three real templates have and the shape that loses the
                     framework's own fallback. -->
                <Panel Background="Transparent">
                  <Grid ColumnDefinitions="*,Auto,Auto">
                    <TextBlock Grid.Column="0" Text="{Binding Name}"/>
                    <TextBlock Grid.Column="1" Text="12.4 KB"/>
                    <TextBlock Grid.Column="2" Text="03 Sep 2026"/>
                  </Grid>
                </Panel>
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
        </Window>
        """;

    /// <summary>
    /// The structure is what is being tested, not the markup file: a listing, a
    /// style on its containers, and a converter behind the setter. If the name
    /// reaches the peer here it reaches it in the real listings, which are the
    /// same shape — and the test above is what says the real listings have it.
    ///
    /// A setter is the only place this can go. The name has to sit on the
    /// container, the container is generated, and nothing inside the item
    /// template is what a reader lands on when the selection moves.
    /// </summary>
    [AvaloniaFact]
    public void A_named_row_is_what_the_reader_is_handed()
    {
        var window = (Window)AvaloniaRuntimeXamlLoader.Load(Listing);
        var list = window.FindControl<ListBox>("Rows")!;

        list.ItemsSource = new[]
        {
            new FileEntry("report.txt", "/a/report.txt", 1, DateTimeOffset.UnixEpoch, EntryFlags.None),
            new FileEntry("Docs", "/a/Docs", 0, DateTimeOffset.UnixEpoch, EntryFlags.Directory),
        };

        window.Show();
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        var spoken = window.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(row => ControlAutomationPeer.CreatePeerForElement(row).GetName())
            .ToList();

        window.Close();

        Assert.Equal(["report.txt", "Docs, folder"], spoken);
    }

    // ---- what the name says -------------------------------------------------

    private static string? Spoken(string name, EntryFlags flags = EntryFlags.None)
        => (string?)FileConverters.RowName.Convert(
            new FileEntry(name, "/a/" + name, 1, DateTimeOffset.UnixEpoch, flags),
            typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);

    [AvaloniaFact]
    public void A_file_is_read_as_its_name()
        => Assert.Equal("report.txt", Spoken("report.txt"));

    /// <summary>
    /// The name the listing SHOWS, so what is read and what is drawn agree: a
    /// Windows shortcut loses its .lnk in both places or in neither. Windows
    /// only, because hiding the extension is a Windows rule — a conditional
    /// expectation would assert whatever the code currently does.
    /// </summary>
    [WindowsFact]
    public void A_shortcut_is_read_the_way_it_is_drawn()
        => Assert.Equal("Chrome", Spoken("Chrome.lnk"));

    /// <summary>
    /// Folder and link are said because a row carries neither fact in text: the
    /// folder icon and the corner emblem are artwork, the type column is
    /// optional and exists in one layout of the three, and artwork has nothing
    /// to read.
    /// </summary>
    [AvaloniaFact]
    public void A_folder_says_that_it_is_one()
        => Assert.Equal("Docs, folder", Spoken("Docs", EntryFlags.Directory));

    [AvaloniaFact]
    public void A_link_says_that_it_is_one()
        => Assert.Equal("shortcut, link", Spoken("shortcut", EntryFlags.Symlink));

    [AvaloniaFact]
    public void A_link_to_a_folder_says_both()
        => Assert.Equal("Music, folder, link",
                        Spoken("Music", EntryFlags.Directory | EntryFlags.Symlink));

    /// <summary>
    /// "" is not the absence of a name, it is silence: measured, a container
    /// whose AutomationProperties.Name is "" hands the peer back "", while a
    /// container with no name falls back to the item. FileKind.DisplayName
    /// answers "" for an entry it cannot name, so this turns that into null.
    /// </summary>
    [AvaloniaFact]
    public void An_entry_with_no_name_is_not_silenced()
        => Assert.Null(FileConverters.RowName.Convert(
               default(FileEntry), typeof(string), null,
               System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// **The name is not the tooltip.** NameTip and PathTip are both gated on
    /// the ShowTooltips setting, which is right for a hover convenience and
    /// would be wrong here: switching tooltips off is a preference about the
    /// mouse, and it must not take a row's name away with it.
    /// </summary>
    [AvaloniaFact]
    public void Switching_tooltips_off_does_not_take_the_name_away()
    {
        var before = AppSettings.Current;

        try
        {
            AppSettings.Apply(AppSettings.Current with
            {
                General = AppSettings.Current.General with { ShowTooltips = false },
            });

            Assert.Equal("report.txt", Spoken("report.txt"));
        }
        finally
        {
            AppSettings.Apply(before);
        }
    }

    // ---- and in the real window, where the bindings are compiled -----------

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// The shipped listing, because the setters above ship as COMPILED bindings
    /// and the runtime-loaded window is a reflection one. Nothing else here
    /// would notice a Style setter that stopped reaching the container.
    ///
    /// Measured on this same window with the three setters taken out, the peer
    /// returned "FileEntry { Name = report.txt, FullPath = ..., Flags = None,
    /// IsDirectory = False, ... }" for every row.
    /// </summary>
    [AvaloniaFact]
    public async Task The_real_listing_hands_a_reader_the_name()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-rowname-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Docs"));
        File.WriteAllText(Path.Combine(root, "report.txt"), "x");

        var window = new MainWindow();

        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        // Closing this window flushes a session, and the temp folder below
        // would be in it — harmlessly, since TestState points the store at a
        // directory that goes away with the run. It is put back anyway: a test
        // that leaves a pane sitting on a deleted path is one navigation away
        // from being the thing the NEXT test inherits.
        var was = shell.ActiveTab?.CurrentPath;

        try
        {
            await shell.ActiveTab!.NavigateAsync(root);
            Settle();
            window.UpdateLayout();
            Settle();

            var rows = window.GetVisualDescendants().OfType<ListBox>()
                .Where(l => l.IsVisible && ReferenceEquals(l.DataContext, shell.ActiveTab))
                .SelectMany(l => l.GetVisualDescendants().OfType<ListBoxItem>())
                .Select(row => ControlAutomationPeer.CreatePeerForElement(row).GetName())
                .ToList();

            Assert.Equal(["Docs, folder", "report.txt"], rows);
        }
        finally
        {
            if (was is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => shell.ActiveTab!.NavigateAsync(was));
                Settle();
            }

            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }
}
