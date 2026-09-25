using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.Session;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the window calls itself.
///
/// **The title never followed navigation.** It was worked out twice — once at
/// startup and once after the settings dialog closed — so with the full-path
/// option on it named the startup folder for the whole session, and with it off
/// the title bar read "Vaktari" and nothing else, ever.
///
/// That is not really about the title bar. The taskbar button and the alt-tab
/// list carry the same string, and that is where a window's title earns its
/// keep: with four of these open there were four identical entries and no way
/// to tell them apart without looking inside each one.
/// </summary>
public sealed class WindowTitleTests : OwnedViewModels
{
    private sealed class Inert : IFileSystemProvider
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

    private static string TitleFor(PaneViewModel? pane, bool fullPath)
        => (string)typeof(MainWindow)
            .GetMethod("TitleFor", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [pane, fullPath])!;

    private PaneViewModel At(string path)
        => Own(new PaneViewModel(new Inert()) { CurrentPath = path });

    [AvaloniaFact]
    public void The_folder_on_screen_names_the_window()
    {
        var pane = At(Path.Combine(Path.GetTempPath(), "invoices"));

        Assert.Equal("invoices — Vaktari", TitleFor(pane, fullPath: false));
    }

    /// <summary>The setting still means something: the whole path when it is
    /// asked for, the leaf when it is not.</summary>
    [AvaloniaFact]
    public void The_whole_path_appears_only_when_it_was_asked_for()
    {
        var folder = Path.Combine(Path.GetTempPath(), "invoices");
        var pane = At(folder);

        Assert.Equal($"{folder} — Vaktari", TitleFor(pane, fullPath: true));
    }

    /// <summary>The bin and This PC have no path to print, and printing
    /// "vaktari:trash" in the taskbar would be worse than printing nothing.</summary>
    [AvaloniaTheory]
    [InlineData("vaktari:trash")]
    [InlineData("vaktari:computer")]
    public void A_virtual_listing_is_named_the_way_it_is_labelled(string path)
    {
        var title = TitleFor(At(path), fullPath: true);

        Assert.DoesNotContain("vaktari:", title);
        Assert.EndsWith("— Vaktari", title);
    }

    /// <summary>Before anything is open there is nothing to name it after, and
    /// a stray dash would be the first thing anybody saw.</summary>
    [AvaloniaFact]
    public void With_nothing_open_it_is_just_the_application()
    {
        Assert.Equal("Vaktari", TitleFor(null, fullPath: false));
        Assert.Equal("Vaktari", TitleFor(At(""), fullPath: true));
    }

    /// <summary>
    /// The rule is only worth having if it is asked again. Navigating and
    /// switching tabs both change the folder on screen, and the fault was that
    /// neither re-read the title.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("if (e.PropertyName == nameof(PaneViewModel.CurrentPath))")]
    [InlineData("private void OnTabStripSelectionChanged")]
    public void The_title_is_asked_again_when_the_folder_changes(string site)
    {
        Assert.Contains("RefreshTitle()",
                        RepoSource.Body(RepoSource.UiClass("", "MainWindow"), site));
    }

    // ---- and every open WINDOW ---------------------------------------------

    // A static the windows below read, so it goes back however the test ends.
    private readonly SettingsState _settingsBefore = AppSettings.Current;

    public override void Dispose()
    {
        AppSettings.Apply(_settingsBefore);

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ShowFullPath(bool on)
        => AppSettings.Apply(AppSettings.Current with
        {
            Startup = AppSettings.Current.Startup with { ShowFullPathInTitleBar = on },
        });

    /// <summary>
    /// **"Show full path in title bar" reached only the window the dialog was
    /// opened from.** Each window keeps its own copy of the flag, and the save
    /// set it on that one window alone — so a second window went on naming
    /// itself the old way, on every navigation, until a restart.
    ///
    /// Two real windows, because the fault is in which windows a save visits:
    /// a title worked out by hand for a second pane would be a peer this code
    /// path has no way of finding.
    /// </summary>
    [AvaloniaFact]
    public async Task A_save_retitles_every_open_window()
    {
        await EmptySessionAsync();
        UseSearch(null);

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Dispatcher.UIThread.RunJobs();

            founder.Shell.NewWindowCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var peer = founder.Services.Windows.First(w => !ReferenceEquals(w, founder));
            var pane = peer.Shell.ActiveTab;

            // After the windows exist, because a window's constructor applies
            // the settings file it finds and would overwrite anything set
            // before it.
            ShowFullPath(false);
            founder.SettingsChangedEverywhere();

            // The premise: the peer is somewhere whose leaf and whole path read
            // differently, and it is showing the leaf.
            Assert.NotEqual(TitleFor(pane, fullPath: false), TitleFor(pane, fullPath: true));
            Assert.Equal(TitleFor(pane, fullPath: false), peer.Title);

            ShowFullPath(true);
            founder.SettingsChangedEverywhere();

            Assert.Equal(TitleFor(pane, fullPath: true), peer.Title);
        }
        finally
        {
            foreach (var window in founder.Services.Windows.ToList().AsEnumerable().Reverse())
            {
                try { window.Close(); }
                catch (Exception ex) { Vaktari.Core.Quiet.Swallowed("test-teardown", ex); }
            }

            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// The session this test's window starts from, so it does not restore
    /// whatever an earlier test in this class left behind — the state directory
    /// is per test class and a closing window writes its own session into it.
    /// </summary>
    private static async Task EmptySessionAsync()
    {
        var directory = TestState.Current();

        Directory.CreateDirectory(directory);

        var store = new JsonSessionStore(directory);

        store.NotifyChanged(new SessionState
        {
            Version = SessionState.CurrentVersion,
            Windows = [],
        });

        await store.FlushAsync(CancellationToken.None);
        await store.DisposeAsync();
    }
}
