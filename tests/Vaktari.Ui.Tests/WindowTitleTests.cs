using System.Reflection;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
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
        var source = File.ReadAllText(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml.cs"));

        var at = source.IndexOf(site, StringComparison.Ordinal);
        Assert.True(at > 0, $"{site} is not written the way this test looks for it");

        var end = source.IndexOf("\n    }\n", at, StringComparison.Ordinal);

        Assert.Contains("RefreshTitle()", source[at..(end < 0 ? source.Length : end)]);
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }
}
