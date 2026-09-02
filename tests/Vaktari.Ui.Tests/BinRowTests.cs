using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Emptying the bin.
///
/// **It was reachable only from inside the bin.** EmptyTrashCommand was bound
/// in exactly one place in the whole application — the button band that appears
/// once you have already navigated into the bin — so the gesture both Explorer
/// and Dolphin offer on the icon itself required going there first, and coming
/// back afterwards. The row that represents the bin offered Copy path, Eject,
/// Remove from places and Properties, and not the one verb that row is for.
/// </summary>
public sealed class BinRowTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static PlaceItemViewModel Row(string path) => new(new Place
    {
        Id = "row", Label = "a row", Path = path,
        Kind = PlaceKind.Virtual, Icon = "trash",
    });

    [AvaloniaFact]
    public void The_bin_row_knows_it_is_the_bin()
        => Assert.True(Row(VirtualPaths.Trash).IsBin);

    /// <summary>Not vacuous: no other row claims it, or Empty would appear on
    /// every place in the sidebar.</summary>
    [AvaloniaTheory]
    [InlineData("vaktari:computer")]
    [InlineData("vaktari:recent-files")]
    public void No_other_row_claims_to_be(string path)
        => Assert.False(Row(path).IsBin);

    [AvaloniaFact]
    public void An_ordinary_folder_is_not_the_bin()
        => Assert.False(Row(Path.Combine(Path.GetTempPath(), "documents")).IsBin);

    /// <summary>
    /// And the row offers it. The command existed and worked; what was missing
    /// was any way to reach it that did not begin with navigating into the bin.
    /// </summary>
    [AvaloniaFact]
    public void The_row_offers_to_empty_it()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        var path = Path.Combine(here!, "src", "Vaktari.Ui", "MainWindow.axaml");

        // Two routes now: the band inside the bin, which is a Button, and the
        // row in the sidebar, which is a menu entry. One was the whole problem.
        Assert.Equal(2, File.ReadAllText(path).Split("EmptyTrashCommand").Length - 1);

        var onTheRow = XDocument.Load(path).Descendants(Avalonia + "MenuItem")
            .Where(m => ((string?)m.Attribute("Command"))
                        ?.Contains("EmptyTrashCommand", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Single(onTheRow);
        Assert.Equal("{Binding IsBin}", (string?)onTheRow[0].Attribute("IsVisible"));
    }

    /// <summary>The bin is named the way the platform names it, as every other
    /// mention of it here is.</summary>
    [AvaloniaFact]
    public void The_entry_calls_it_what_the_platform_calls_it()
    {
        var shell = new ShellViewModel(new Nothing());

        try
        {
            Assert.Contains(Vaktari.Core.Naming.TheBin, shell.EmptyBinLabel);
            Assert.StartsWith("Empty", shell.EmptyBinLabel);
        }
        finally
        {
            shell.Dispose();
        }
    }

    private sealed class Nothing : Vaktari.Core.FileSystem.IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<Vaktari.Core.FileSystem.FileEntry>> EnumerateAsync(
            string path, Vaktari.Core.FileSystem.ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<Vaktari.Core.FileSystem.FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<Vaktari.Core.FileSystem.FileEntry?>(null);

        public IDisposable Watch(string path, Action<Vaktari.Core.FileSystem.FileSystemChange> onChange)
            => new Nowt();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nowt : IDisposable { public void Dispose() { } }
    }
}
