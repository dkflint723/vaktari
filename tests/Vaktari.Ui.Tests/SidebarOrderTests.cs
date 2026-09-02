using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where This PC sits in the sidebar.
///
/// It led the whole list, above the group headings, which put a machine-level
/// row over the folders somebody opens twenty times a day. It belongs under
/// them — Explorer puts its own This PC under Quick access for the same reason.
///
/// A DataTemplate cannot ask where it sits, so the first group is told, and the
/// row is drawn from inside that group's template.
/// </summary>
public sealed class SidebarOrderTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    [AvaloniaFact]
    public async Task Only_the_first_group_draws_the_computer_row()
    {
        var shell = new ShellViewModel(new Inert(), places: new ThreeGroups());
        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        Assert.True(shell.Sidebar.Groups.Count >= 2, "need more than one group to mean anything");

        Assert.True(shell.Sidebar.Groups[0].IsFirst);

        foreach (var group in shell.Sidebar.Groups.Skip(1))
            Assert.False(group.IsFirst, $"{group.Label} would draw a second This PC");
    }

    /// <summary>
    /// Reloading rebuilds every group object, so the flag has to be re-applied
    /// — miss that and the row disappears the first time a drive is plugged in.
    /// </summary>
    [AvaloniaFact]
    public async Task It_survives_a_reload()
    {
        var shell = new ShellViewModel(new Inert(), places: new ThreeGroups());
        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();
        await shell.Sidebar.ReloadAsync();

        Assert.True(shell.Sidebar.Groups[0].IsFirst);
        Assert.Single(shell.Sidebar.Groups, g => g.IsFirst);
    }

    /// <summary>
    /// And it really is drawn from inside the group template rather than above
    /// the list — the fault was entirely about where in the markup it sat.
    /// </summary>
    [Fact]
    public void The_row_is_drawn_inside_the_group_template()
    {
        var markup = XDocument.Load(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

        var row = markup
            .Descendants(Avalonia + "Button")
            .Single(b => ((string?)b.Attribute("Command") ?? "").Contains("OpenComputerCommand",
                                                                         StringComparison.Ordinal));

        Assert.Equal("{Binding IsFirst}", (string?)row.Attribute("IsVisible"));

        // Inside a DataTemplate, which is what puts it among the group's rows.
        Assert.Contains(row.Ancestors(Avalonia + "DataTemplate"),
                        t => (string?)t.Attribute(
                                 XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml")
                                 + "DataType") == "vm:PlaceGroupViewModel");
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }

    private sealed class ThreeGroups : IPlacesProvider
    {
        public event EventHandler? PlacesChanged;

        private static Place At(string label, PlaceKind kind)
            => new()
            {
                Id = label,
                Label = label,
                Path = Path.GetTempPath(),
                Kind = kind,
                Icon = "folder",
            };

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup("PLACES", [At("Home", PlaceKind.UserFolder)]),
                new PlaceGroup("DEVICES", [At("Disk", PlaceKind.Device)]),
                new PlaceGroup("NETWORK", [At("Share", PlaceKind.Network)]),
            ]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.Ejected("gone"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);

        public void Raise() => PlacesChanged?.Invoke(this, EventArgs.Empty);
    }

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
}
