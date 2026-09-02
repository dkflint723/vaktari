using System.Reflection;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dragging a file onto another tab.
///
/// **There was no way to do it.** A tab took no drops and hovering one during a
/// drag did nothing, so moving a file into a folder open in the next tab meant
/// opening the split view, dragging across it, and closing it again — for a
/// gesture both Explorer and Dolphin answer by hovering. The two halves go
/// together: without the switch the drop would be blind, because the
/// destination is a folder you cannot see.
/// </summary>
public sealed class TabDropTests : OwnedViewModels
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

    private static object TargetAt(object? source)
        => typeof(MainWindow)
            .GetMethod("TargetAt", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source])!;

    private static T? Read<T>(object spot, string name)
        => (T?)spot.GetType().GetProperty(name)!.GetValue(spot);

    private TabStripItem Tab(string folder) => new()
    {
        DataContext = Own(new PaneViewModel(new Inert()) { CurrentPath = folder }),
    };

    /// <summary>
    /// Not the fault, and worth saying so: the routing already worked, because a
    /// tab carries its pane as its data context and the destination rules read
    /// the pane under the pointer. What was missing was any way for a drag to
    /// REACH the strip. This pins the half that was already right, so that
    /// changing the destination rules cannot quietly undo tab drops now that
    /// they are reachable.
    /// </summary>
    [AvaloniaFact]
    public void A_drop_on_a_tab_goes_to_that_tab_s_folder()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "elsewhere");

        var spot = TargetAt(Tab(elsewhere));

        Assert.Equal(elsewhere, Read<string>(spot, "Destination"));
        Assert.True(Read<bool>(spot, "Exists"), "a tab is refused, so the drop never arrives");
    }

    /// <summary>
    /// Without this the drag never reaches the strip at all, whatever the
    /// destination rules say — the toolkit only offers a drop to a control that
    /// has said it takes them.
    /// </summary>
    [AvaloniaFact]
    public void The_tab_strip_accepts_drops_in_the_markup()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        var at = markup.IndexOf("<TabStrip ItemsSource=\"{Binding Tabs}\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the tab strip is not declared the way this looks for it");

        Assert.Contains("DragDrop.AllowDrop=\"True\"", markup[at..markup.IndexOf('>', at)]);
    }

    /// <summary>
    /// Resting on a tab switches to it, so the drop can be aimed. Asked on every
    /// drag-over and BEFORE the refusal, because a tab strip is neither a pane
    /// nor a place: a hover there is refused as a drop and would otherwise never
    /// be counted as a hover.
    /// </summary>
    [AvaloniaFact]
    public void A_drag_resting_on_a_tab_is_noticed_before_it_is_refused()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private void OnDragOver(object? sender, DragEventArgs e)");

        var hover = body.IndexOf("HoverTab(TabAt(e.Source))", StringComparison.Ordinal);
        var refusal = body.IndexOf("if (!spot.Exists)", StringComparison.Ordinal);

        Assert.True(hover > 0, "a drag over a tab is never noticed, so hovering does nothing");
        Assert.True(hover < refusal,
            "the hover is asked after the refusal, so a tab — which is neither a "
            + "pane nor a place — returns first and is never counted");
    }

    /// <summary>
    /// Every way a drag can end clears it, or the strip keeps switching tabs
    /// after the pointer has gone.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("private void OnDragLeave(object? sender, DragEventArgs e)")]
    [InlineData("private void OnDrop(object? sender, DragEventArgs e)")]
    public void Ending_a_drag_stops_the_switch(string declaration)
        => Assert.Contains("HoverTab(null)",
                           RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"), declaration));
}
