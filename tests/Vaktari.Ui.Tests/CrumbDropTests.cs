using System.Reflection;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dropping a file onto a breadcrumb.
///
/// **The crumbs refused drops, and worse than refusing, they lied.** They sit
/// above the listing, so a drag over "Documents" on the way to the current
/// folder found the pane underneath and offered the pane's own folder as the
/// destination. The cursor said the drop was fine, the drop happened, and the
/// file went where it already was — a no-op that reads as the application
/// having quietly failed. Dragging up two levels is the one thing crumbs are
/// better at than anything else in the window, and both Explorer and Dolphin
/// take it.
/// </summary>
public sealed class CrumbDropTests
{
    private static object TargetAt(object? source)
        => typeof(MainWindow)
            .GetMethod("TargetAt", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source])!;

    private static T? Read<T>(object spot, string name)
        => (T?)spot.GetType().GetProperty(name)!.GetValue(spot);

    private sealed class NoCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }

    private static Control Crumb(string path) => new ContentControl
    {
        DataContext = new PathSegment(
            Path.GetFileName(path), path, new NoCommand(), IsLast: false),
    };

    /// <summary>
    /// The fault. Before this the answer was the pane's folder, so the file was
    /// copied on top of itself or the move was discarded as a no-op.
    /// </summary>
    [AvaloniaFact]
    public void A_crumb_names_the_ancestor_it_stands_for()
    {
        var ancestor = Path.Combine(Path.GetTempPath(), "projects");

        var spot = TargetAt(Crumb(ancestor));

        Assert.Equal(ancestor, Read<string>(spot, "Destination"));
    }

    /// <summary>
    /// A crumb is a destination in its OWN right, not a fallback — which is the
    /// difference the right-button drop menu reads to decide whether it is
    /// offering to put things into what you pointed at.
    /// </summary>
    [AvaloniaFact]
    public void A_crumb_is_something_the_pointer_is_over_rather_than_a_fallback()
    {
        var ancestor = Path.Combine(Path.GetTempPath(), "projects");

        var spot = TargetAt(Crumb(ancestor));

        Assert.Equal(ancestor, Read<string>(spot, "Explicit"));
        Assert.True(Read<bool>(spot, "Exists"));
    }

    /// <summary>
    /// The bin's and This PC's crumbs are not folders, so they cannot take a
    /// drop — and answering with the literal string "vaktari:computer" would
    /// create a directory of that name on Linux, where a colon is legal.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("vaktari:trash")]
    [InlineData("vaktari:computer")]
    [InlineData("vaktari:recent-files")]
    public void A_virtual_crumb_is_not_a_destination(string path)
    {
        var spot = TargetAt(Crumb(path));

        Assert.Null(Read<string>(spot, "Crumb"));
        Assert.False(Read<bool>(spot, "Exists"));
    }

    /// <summary>
    /// The crumbs must be able to be a drop target at all. Without AllowDrop the
    /// drag never reaches them, whatever the destination rules say — which is
    /// the half of this that lives in the markup.
    /// </summary>
    [AvaloniaFact]
    public void The_crumb_strip_accepts_drops_in_the_markup()
    {
        var markup = File.ReadAllText(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

        var at = markup.IndexOf("ItemsSource=\"{Binding ActiveTab.Breadcrumbs}\"",
                                StringComparison.Ordinal);

        Assert.True(at > 0, "the breadcrumb strip is not declared the way this looks for it");

        // Within the element's own attributes, not somewhere later in the file.
        var element = markup[at..markup.IndexOf('>', at)];

        Assert.Contains("DragDrop.AllowDrop=\"True\"", element);
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }
}
