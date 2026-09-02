using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dragging files onto the sidebar's bin.
///
/// **The bin refused every drop, and the code that trashes one had been there
/// all along.** OnDrop matched the bin's row and called TrashPaths; OnDragOver
/// never considered it. A drag is a conversation — the toolkit delivers a drop
/// only where the drag-over said yes — so the cursor showed no-drop, the drop
/// event never arrived, and the branch in OnDrop was unreachable code that read
/// like a working feature.
///
/// The reason the two could disagree is that they worked the destination out
/// separately. They now read one <c>TargetAt</c>, and these check both halves:
/// that the bin is somewhere a drag can land, and that neither handler has
/// gone back to deciding for itself.
/// </summary>
public sealed class BinDropTests
{
    private static object TargetAt(object? source)
        => typeof(MainWindow)
            .GetMethod("TargetAt", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source])!;

    private static T Read<T>(object spot, string name)
        => (T)spot.GetType().GetProperty(name)!.GetValue(spot)!;

    private static Control Row(string path) => new ContentControl
    {
        DataContext = new PlaceItemViewModel(new Place
        {
            Id = "row",
            Label = "a row",
            Path = path,
            Kind = PlaceKind.Virtual,
            Icon = "trash",
        }),
    };

    /// <summary>
    /// The fault itself. <c>Exists</c> is what OnDragOver refuses on, and the
    /// bin has to satisfy it — PlaceAt cannot speak for the bin, because it
    /// deliberately refuses a virtual path, and there is no pane above the
    /// sidebar to speak for it either.
    /// </summary>
    [AvaloniaFact]
    public void The_bin_row_is_somewhere_a_drag_can_land()
    {
        var spot = TargetAt(Row(VirtualPaths.Trash));

        Assert.True(Read<bool>(spot, "IsBin"), "the bin's own row was not recognised");
        Assert.True(Read<bool>(spot, "Exists"),
            "OnDragOver refuses when Exists is false, so the drop never arrives");
    }

    /// <summary>Not vacuous: a row that is neither the bin, an available place
    /// nor inside a pane is still refused.</summary>
    [AvaloniaFact]
    public void Another_virtual_row_is_still_nowhere_to_drop()
    {
        var spot = TargetAt(Row(VirtualPaths.Files));

        Assert.False(Read<bool>(spot, "IsBin"));
        Assert.False(Read<bool>(spot, "Exists"));
    }

    /// <summary>The bin is a verb, not a folder, so it names no destination to
    /// copy into — which is why OnDragOver has to answer it before the
    /// destination rules run.</summary>
    [AvaloniaFact]
    public void The_bin_names_no_folder_to_copy_into()
    {
        var spot = TargetAt(Row(VirtualPaths.Trash));

        Assert.Equal("", Read<string>(spot, "Destination"));
    }

    /// <summary>
    /// **The regression this class exists to prevent.** The bug was not a wrong
    /// answer, it was two handlers answering separately and drifting apart. So
    /// long as both read TargetAt they cannot; the moment one goes back to
    /// calling PlaceAt or TrashRowAt itself, they can.
    /// </summary>
    [AvaloniaFact]
    public void Both_handlers_read_the_same_answer()
    {
        foreach (var handler in new[]
                 {
                     "private void OnDragOver(object? sender, DragEventArgs e)",
                     "private void OnDrop(object? sender, DragEventArgs e)",
                 })
            Assert.True(Body(handler).Contains("TargetAt(e.Source)"),
                $"{handler} works the drop destination out for itself, so it can "
                + "disagree with the other handler the way the bin did");
    }

    /// <summary>
    /// The other half of the same fault: a drag that STARTS in the bin carries
    /// paths that no longer exist, so it completes and achieves nothing. The
    /// rule lives on the pane and is tested there; what this pins is that the
    /// drag asks.
    /// </summary>
    [AvaloniaFact]
    public void A_drag_asks_the_pane_before_it_picks_anything_up()
    {
        var body = Body(
            "private async Task BeginDragAsync(PaneViewModel pane, PointerPressedEventArgs trigger)");

        Assert.True(body.Contains("pane.CanDragOut()"),
            "BeginDragAsync builds a payload without asking, so a drag out of the "
            + "bin arms, completes and says nothing");
    }

    /// <summary>
    /// The text of one method of MainWindow, read from the source.
    ///
    /// Through RepoSource, which normalises line endings. Read raw, this scan
    /// found nothing on a Windows agent — the repository stores LF and git
    /// hands out CRLF — and fell back to the whole file, so the assertion
    /// quietly widened from "inside this method" to "anywhere at all".
    /// </summary>
    private static string Body(string declaration)
        => RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"), declaration);
}
