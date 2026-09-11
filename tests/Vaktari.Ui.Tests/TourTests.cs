using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Session;
using Vaktari.Ui.Settings;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The tour, and the first run that points at it.
///
/// **A first run wrote a settings file and said nothing.** The sidebar, the
/// split, the search box, the filter and the sheet of keys were all there to
/// be found, and nothing pointed at any of them. These pin the four parts:
/// that a first run is told from the write nobody else can make, that the
/// founder window says so once and a later run does not, that the cards walk
/// and every key on them is a key the sheet lists, and that the view menu
/// reaches the tour for everybody.
/// </summary>
public sealed class TourTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private readonly List<Window> _windows = [];

    public override void Dispose()
    {
        foreach (var window in _windows) window.Close();

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private T Shown<T>(T window) where T : Window
    {
        _windows.Add(window);
        window.Show();
        Pump();

        return window;
    }

    // ---- the first run -----------------------------------------------------------

    /// <summary>
    /// The write is the tell: a fresh directory is a first run exactly once,
    /// and a write this build refuses — because the BACKUP beside the absent
    /// file was written by a newer one — is not a first run at all, since the
    /// answer is what is on disk afterwards.
    ///
    /// The newer file has to be the backup and not the file itself: with the
    /// file present the store answers "not a first run" before it ever tries
    /// to write, and the refused-write rule is never reached. Measured — the
    /// first version of this put the newer content in settings.json, and a
    /// store that counted a refused write as a first run passed it.
    /// </summary>
    [AvaloniaFact]
    public void A_fresh_settings_directory_is_a_first_run_exactly_once()
    {
        var root = Directory.CreateTempSubdirectory("vaktari-tour").FullName;

        try
        {
            var store = new JsonSettingsStore(root);

            Assert.True(store.EnsureFileExists(new SettingsState()));
            Assert.False(store.EnsureFileExists(new SettingsState()));

            File.Delete(Path.Combine(root, "settings.json"));
            File.WriteAllText(Path.Combine(root, "settings.json.bak"), "{ \"version\": 99 }");

            var newer = new JsonSettingsStore(root);

            newer.Load();

            Assert.NotNull(newer.ReadOnlyReason);
            Assert.False(newer.EnsureFileExists(new SettingsState()));
            Assert.False(File.Exists(Path.Combine(root, "settings.json")), "the refused write happened anyway");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The founder of a first run says where the tour is, on the line that
    /// stays until dismissed; the founder of the next run — the same
    /// directory, the file now there — does not.
    ///
    /// The file is removed first rather than assumed absent: the other test
    /// in this class that builds a window may have run before this one, and
    /// the directory is this class's own.
    /// </summary>
    [AvaloniaFact]
    public void The_founder_of_a_first_run_says_where_the_tour_is_and_the_next_does_not()
    {
        UseSearch(PaneViewModel.Search);

        var file = Path.Combine(JsonSessionStore.DefaultDirectory(), "settings.json");

        if (File.Exists(file)) File.Delete(file);

        var first = Shown(new MainWindow());

        Assert.True(first.Services.FirstRun, "the settings file was absent, so this launch wrote it");
        Assert.Contains("Take the tour", first.Shell.OperationStatus, StringComparison.Ordinal);

        first.Close();
        Pump();

        var second = Shown(new MainWindow());

        Assert.False(second.Services.FirstRun);
        Assert.DoesNotContain("Take the tour", second.Shell.OperationStatus, StringComparison.Ordinal);
    }

    // ---- the cards ---------------------------------------------------------------

    /// <summary>
    /// **Each line teaches a command, and the command's keys come from the
    /// keymap**, so the rules are about the commands: every one a line names
    /// exists, every one ships with a key — a first run reading "no key yet"
    /// would teach nothing — and a line written as a fixed gesture is not a
    /// key some command owns, which would be a promise about a key that can
    /// be moved. That the tour follows a moved key is KeymapTests' business.
    /// </summary>
    [AvaloniaFact]
    public void Every_line_teaches_a_command_that_has_a_key_or_a_fixed_gesture()
    {
        var lines = Tour.Cards.SelectMany(card => card.Lines).ToList();

        // Every line that names a command names one the keymap has.
        var strangers = lines
            .Where(line => line.Command is { } id && Commands.Find(id) is null)
            .Select(line => line.Command)
            .ToList();

        Assert.True(strangers.Count == 0,
            "the tour names commands the keymap does not have: " + string.Join(", ", strangers));

        // A first run is the shipped keys, and a line reading "no key yet" on
        // the very first screen somebody sees would be teaching nothing.
        var keyless = lines
            .Where(line => line.Command is { } id && Keymap.Default.KeysOf(id).Count == 0)
            .Select(line => line.Command)
            .ToList();

        Assert.True(keyless.Count == 0,
            "the tour teaches commands that ship with no key: " + string.Join(", ", keyless));

        // **A fixed line must not print a key a command answers to.** That key
        // can be moved, and the line would go on promising it.
        var movable = lines
            .Where(line => line.Command is null
                           && KeyChords.Parse(line.Gesture) is { } g
                           && Keymap.Default.Owner(g) is not null)
            .Select(line => line.Gesture)
            .ToList();

        Assert.True(movable.Count == 0,
            "the tour prints these as fixed when a command owns them: " + string.Join(", ", movable));

        // And the rule is looking at something: three cards, none empty.
        Assert.Equal(3, Tour.Cards.Count);
        Assert.All(Tour.Cards, card => Assert.NotEmpty(card.Lines));
    }

    /// <summary>
    /// Next walks forward, Back walks back and is dead on the first card, and
    /// from the last card Next reads "Done" and closes the window — one
    /// button that always means onward.
    /// </summary>
    [AvaloniaFact]
    public void The_three_cards_walk_forward_and_back_and_done_closes()
    {
        var tour = Shown(new TourWindow());
        var closed = false;

        tour.Closed += (_, _) => closed = true;

        Assert.Equal(0, tour.Index);
        Assert.False(tour.BackButton.IsEnabled);
        Assert.Equal("Next", tour.NextButton.Content);
        Assert.Equal(Tour.Cards[0].Title, tour.CardTitle.Text);

        tour.Next();

        Assert.Equal(1, tour.Index);
        Assert.True(tour.BackButton.IsEnabled);
        Assert.Equal(Tour.Cards[1].Title, tour.CardTitle.Text);

        tour.Back();

        Assert.Equal(0, tour.Index);

        tour.Next();
        tour.Next();

        Assert.Equal(2, tour.Index);
        Assert.Equal("Done", tour.NextButton.Content);
        Assert.Equal(Tour.Cards[2].Title, tour.CardTitle.Text);
        Assert.Contains("3 of 3", tour.Progress.Text, StringComparison.Ordinal);

        tour.Next();
        Pump();

        Assert.True(closed, "Done did not close the tour");
    }

    // ---- the route ---------------------------------------------------------------

    /// <summary>
    /// On the view menu beside the shortcut sheet, through the window, for
    /// anybody and not only on a first run — and the command really raises
    /// the request the window answers.
    /// </summary>
    [AvaloniaFact]
    public void The_view_menu_offers_the_tour_and_the_shell_asks_for_it()
    {
        var doc = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var row = doc.Descendants(Avalonia + "Button")
                     .Single(b => (string?)b.Attribute("Content") == "Take the tour…");

        Assert.Equal(
            "{Binding $parent[Window].((vm:ShellViewModel)DataContext).ShowTourCommand}",
            (string?)row.Attribute("Command"));

        var shell = Own(new ShellViewModel(new InertFileSystem()));
        var asked = 0;

        shell.TourRequested += (_, _) => asked++;

        shell.ShowTourCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    /// <summary>The window answers the request with the tour, owned by it.</summary>
    [AvaloniaFact]
    public void The_window_opens_the_tour_when_the_shell_asks()
    {
        UseSearch(PaneViewModel.Search);

        var window = Shown(new MainWindow());

        window.Shell.ShowTourCommand.Execute(null);
        Pump();

        var tour = Assert.Single(window.OwnedWindows.OfType<TourWindow>());

        _windows.Add(tour);

        Assert.Equal(0, tour.Index);
    }

    // ---- helpers -----------------------------------------------------------------

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
