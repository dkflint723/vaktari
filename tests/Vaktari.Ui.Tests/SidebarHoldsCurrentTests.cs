using System.Globalization;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the sidebar shows while you are one folder down.
///
/// **It showed nothing.** The row mark was an exact path match and nothing
/// else, so Documents lit while you were in Documents and went dark the instant
/// you opened a folder inside it — which is where the time is actually spent.
/// The whole list would go blank and give no clue where in it you were.
///
/// The nearest place that HOLDS the folder now takes a third, fainter wash of
/// the same accent. Nearest rather than every ancestor, and only when no row is
/// exactly here, so one location never puts two marks on screen.
/// </summary>
public sealed class SidebarHoldsCurrentTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static readonly string Home = Path.Combine(Path.GetTempPath(), "flint");
    private static readonly string Documents = Path.Combine(Home, "Documents");
    private static readonly string Work = Path.Combine(Documents, "Work");
    private static readonly string Music = Path.Combine(Home, "Music");

    /// <summary>Shares a prefix with Documents without containing anything of
    /// it, which is the trap a bare StartsWith falls into.</summary>
    private static readonly string Doc = Path.Combine(Home, "Doc");

    private static readonly string Elsewhere =
        Path.Combine(Path.GetTempPath(), "somewhere-else", "deep");

    private static async Task<SidebarViewModel> LoadedAsync()
    {
        var sidebar = new SidebarViewModel(new Nested());

        await sidebar.ReloadAsync();

        return sidebar;
    }

    private static IEnumerable<PlaceItemViewModel> Rows(SidebarViewModel sidebar)
        => sidebar.Groups.SelectMany(group => group.Places);

    private static PlaceItemViewModel Row(SidebarViewModel sidebar, string label)
        => Rows(sidebar).Single(row => row.Label == label);

    [AvaloniaFact]
    public async Task A_folder_inside_a_place_marks_that_place()
    {
        var sidebar = await LoadedAsync();

        sidebar.SetCurrentPath(Work);

        var documents = Row(sidebar, "Documents");

        Assert.True(documents.HoldsCurrent,
                    "browsing a subfolder of Documents lights no sidebar entry");
        Assert.False(documents.IsCurrent, "the row is not the place being shown");
    }

    /// <summary>
    /// Home holds Work every bit as much as Documents does. Marking both would
    /// be two answers to one question, and the useful one is the nearer.
    /// </summary>
    [AvaloniaFact]
    public async Task The_nearest_holder_is_the_only_one_marked()
    {
        var sidebar = await LoadedAsync();

        sidebar.SetCurrentPath(Work);

        var marked = Rows(sidebar).Where(row => row.HoldsCurrent).ToList();

        Assert.Equal("Documents", Assert.Single(marked).Label);
    }

    /// <summary>
    /// The order the rows happen to be built in must not decide it. Home is
    /// listed FIRST here, so a search that keeps its first hit picks the wrong
    /// one.
    /// </summary>
    [AvaloniaFact]
    public async Task Home_is_listed_first_and_still_loses_to_documents()
    {
        var sidebar = await LoadedAsync();

        Assert.Equal("Home", Rows(sidebar).First().Label);

        sidebar.SetCurrentPath(Work);

        Assert.False(Row(sidebar, "Home").HoldsCurrent);
    }

    [AvaloniaFact]
    public async Task Standing_in_the_place_itself_marks_nothing_above_it()
    {
        var sidebar = await LoadedAsync();

        sidebar.SetCurrentPath(Documents);

        Assert.True(Row(sidebar, "Documents").IsCurrent);
        Assert.False(Row(sidebar, "Documents").HoldsCurrent);
        Assert.False(Row(sidebar, "Home").HoldsCurrent);
        Assert.DoesNotContain(Rows(sidebar), row => row.HoldsCurrent);
    }

    /// <summary>
    /// The mark is state on a row that outlives the navigation that set it, so
    /// walking out of the branch has to take it off again.
    /// </summary>
    [AvaloniaFact]
    public async Task Leaving_the_branch_clears_the_mark()
    {
        var sidebar = await LoadedAsync();

        sidebar.SetCurrentPath(Work);
        Assert.Contains(Rows(sidebar), row => row.HoldsCurrent);

        sidebar.SetCurrentPath(Elsewhere);

        Assert.DoesNotContain(Rows(sidebar), row => row.HoldsCurrent);
    }

    /// <summary>
    /// A sibling that merely shares the start of the name is not a holder.
    /// GUARD: PathRules.Contains already ends its prefix at a separator and has
    /// its own tests — this pins that the sidebar asks IT rather than growing a
    /// second, sloppier comparison here.
    /// </summary>
    [AvaloniaFact]
    public async Task A_name_that_merely_shares_a_prefix_is_not_a_holder()
    {
        var sidebar = await LoadedAsync();

        sidebar.SetCurrentPath(Work);

        Assert.False(Row(sidebar, "Doc").HoldsCurrent);
    }

    /// <summary>
    /// GUARD: a reload throws every row object away and builds new ones, and
    /// SetCurrentPath is re-run afterwards for exactly this reason. It carries
    /// the new state as it always carried the old.
    /// </summary>
    [AvaloniaFact]
    public async Task The_mark_survives_a_reload()
    {
        var sidebar = await LoadedAsync();

        sidebar.SetCurrentPath(Work);

        await sidebar.ReloadAsync();

        Assert.True(Row(sidebar, "Documents").HoldsCurrent);
    }

    /// <summary>
    /// The row objects outlive the navigation, so the row has to ANNOUNCE the
    /// change — a silent flag would be correct in the view model and invisible
    /// on screen, the row still painted for wherever you were when it was
    /// built.
    /// </summary>
    [AvaloniaFact]
    public async Task The_mark_announces_itself_so_a_standing_row_repaints()
    {
        var sidebar = await LoadedAsync();

        var documents = Row(sidebar, "Documents");
        var announced = new List<string?>();

        documents.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        sidebar.SetCurrentPath(Work);

        Assert.Contains(nameof(PlaceItemViewModel.HoldsCurrent), announced);
    }

    // ---- what it looks like ---------------------------------------------------

    private static object? Convert(IMultiValueConverter converter, params object?[] values)
        => converter.Convert(values, typeof(object), null, CultureInfo.InvariantCulture);

    private static Color ColourOf(object? brush)
        => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Window Themed()
    {
        var window = new Window();

        ThemeApplier.Apply(window, new Vaktari.Core.ThemePalette
        {
            IsDark = true,
            Colours = new Dictionary<string, string>(),
        });

        return window;
    }

    /// <summary>
    /// Visible, and quieter than the row that IS the place — a holder as loud
    /// as the current row would be a second claim to be where you are.
    /// </summary>
    [AvaloniaFact]
    public void The_holding_wash_sits_below_the_you_are_here_wash()
    {
        var window = Themed();

        try
        {
            var holds = ColourOf(Convert(FileConverters.PlaceRowFill, false, false, true)).A;
            var here = ColourOf(Convert(FileConverters.PlaceRowFill, true, false, false)).A;
            var drop = ColourOf(Convert(FileConverters.PlaceRowFill, false, true, false)).A;

            Assert.True(holds > 0, "the holding row is not washed at all");
            Assert.True(holds < here, $"holding {holds} is not quieter than current {here}");
            Assert.True(here < drop, $"current {here} is not quieter than drop {drop}");
            Assert.Equal(0, ColourOf(Convert(FileConverters.PlaceRowFill, false, false, false)).A);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The converter reads its values BY POSITION, so the markup has to hand
    /// them over in the order it expects — three bindings, here then drop then
    /// holder. A converter nobody hands the third value to marks nothing.
    /// </summary>
    [AvaloniaFact]
    public void The_place_row_hands_the_converter_all_three_states_in_order()
    {
        var markup = XDocument.Load(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

        var binding = markup
            .Descendants(Avalonia + "MultiBinding")
            .Single(m => ((string?)m.Attribute("Converter") ?? "")
                         .Contains("PlaceRowFill", StringComparison.Ordinal));

        Assert.Equal(
            ["IsCurrent", "IsDropTarget", "HoldsCurrent"],
            binding.Elements(Avalonia + "Binding").Select(b => (string?)b.Attribute("Path")));
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }

    /// <summary>Places that nest, listed shallowest first.</summary>
    private sealed class Nested : IPlacesProvider
    {
        public event EventHandler? PlacesChanged;

        private static Place At(string label, string path)
            => new()
            {
                Id = label,
                Label = label,
                Path = path,
                Kind = PlaceKind.UserFolder,
                Icon = "folder",
            };

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup("PLACES",
                [
                    At("Home", Home),
                    At("Doc", Doc),
                    At("Documents", Documents),
                    At("Music", Music),
                ]),
            ]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.Ejected("gone"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RenameAsync(string id, string label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);

        public void Raise() => PlacesChanged?.Invoke(this, EventArgs.Empty);
    }
}
