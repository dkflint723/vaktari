using System.Reflection;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Opening something in a tab you are not sent to.
///
/// **The middle button reached the tab strip and the folder rows, and nothing
/// else.** A sidebar place and a breadcrumb match neither, and Avalonia's Button
/// ignores a middle press entirely — so middle-clicking Documents in the
/// sidebar, or an ancestor in the path bar, did nothing whatever, while that
/// same place's right-click menu offered "Open in new tab" and the F1 sheet
/// advertised the middle button. A gesture that answers fewer rows than the
/// menu beside it reads as broken rather than as careful.
///
/// **And the tab it did open stole the view.** The folder-row branch called the
/// desktop's handover route, which reuses an existing tab and jumps to it —
/// right when somebody has just asked the desktop to show them a folder, and
/// exactly wrong for a gesture whose whole meaning is "keep this open too, I am
/// not finished here".
/// </summary>
public sealed class NewTabGestureTests
{
    private static string? NewTabTarget(object? source, PointerUpdateKind kind, KeyModifiers modifiers)
        => (string?)typeof(MainWindow)
            .GetMethod("NewTabTarget", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source, kind, modifiers]);

    private sealed class NoCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }

    private static Control PlaceRow(string path) => new ContentControl
    {
        DataContext = new PlaceItemViewModel(new Place
        {
            Id = "row", Label = "a row", Path = path,
            Kind = PlaceKind.UserFolder, Icon = "folder",
        }),
    };

    private static Control Crumb(string path) => new ContentControl
    {
        DataContext = new PathSegment(Path.GetFileName(path), path, new NoCommand(), IsLast: false),
    };

    private static Control FolderRow(string path) => new ContentControl
    {
        DataContext = new FileEntry(Path.GetFileName(path), path, 0,
                                    DateTimeOffset.UnixEpoch, EntryFlags.Directory),
    };

    private static readonly string Docs = Path.Combine(Path.GetTempPath(), "documents");

    private const PointerUpdateKind Middle = PointerUpdateKind.MiddleButtonPressed;
    private const PointerUpdateKind Left = PointerUpdateKind.LeftButtonPressed;

    // ---- the two rows that answered nothing ---------------------------------

    [AvaloniaFact]
    public void A_middle_click_on_a_sidebar_place_opens_it()
        => Assert.Equal(Docs, NewTabTarget(PlaceRow(Docs), Middle, KeyModifiers.None));

    [AvaloniaFact]
    public void A_middle_click_on_a_crumb_opens_it()
        => Assert.Equal(Docs, NewTabTarget(Crumb(Docs), Middle, KeyModifiers.None));

    [AvaloniaFact]
    public void Ctrl_clicking_a_place_opens_it_too()
        => Assert.Equal(Docs, NewTabTarget(PlaceRow(Docs), Left, KeyModifiers.Control));

    [AvaloniaFact]
    public void Ctrl_clicking_a_crumb_opens_it_too()
        => Assert.Equal(Docs, NewTabTarget(Crumb(Docs), Left, KeyModifiers.Control));

    /// <summary>
    /// The bin cannot be dropped ON, which is why the drop walker refuses it —
    /// but it is somewhere you can go, and its own menu says so.
    /// </summary>
    [AvaloniaFact]
    public void The_bin_row_opens_though_nothing_can_be_dropped_on_it()
        => Assert.Equal(VirtualPaths.Trash,
                        NewTabTarget(PlaceRow(VirtualPaths.Trash), Middle, KeyModifiers.None));

    // ---- and the gestures that must keep their own meaning ------------------

    /// <summary>
    /// **Ctrl+click in the listing extends a selection**, and has since the
    /// first week. Taking it here to add a second way of doing what the middle
    /// button already does would break the most-used modifier in the
    /// application.
    /// </summary>
    [AvaloniaFact]
    public void Ctrl_clicking_a_folder_row_still_belongs_to_the_selection()
        => Assert.Null(NewTabTarget(FolderRow(Docs), Left, KeyModifiers.Control));

    /// <summary>Ctrl+middle is the pane-scale reset.</summary>
    [AvaloniaFact]
    public void Ctrl_middle_is_still_the_size_reset()
        => Assert.Null(NewTabTarget(PlaceRow(Docs), Middle, KeyModifiers.Control));

    [AvaloniaFact]
    public void A_plain_click_opens_nothing_new()
        => Assert.Null(NewTabTarget(PlaceRow(Docs), Left, KeyModifiers.None));

    [AvaloniaFact]
    public void A_middle_click_on_nothing_in_particular_is_left_alone()
        => Assert.Null(NewTabTarget(new ContentControl(), Middle, KeyModifiers.None));

    /// <summary>The folder row keeps the gesture it already had.</summary>
    [AvaloniaFact]
    public void A_middle_click_on_a_folder_row_still_opens_it()
        => Assert.Equal(Docs, NewTabTarget(FolderRow(Docs), Middle, KeyModifiers.None));

    // ---- and the tab opens behind ------------------------------------------

    /// <summary>
    /// The route matters as much as the target. The handover overload dedupes
    /// against open tabs and activates what it finds, so middle-clicking a
    /// folder already open moved you to it and opened nothing — the opposite of
    /// what the gesture asks for.
    /// </summary>
    [AvaloniaFact]
    public void The_gesture_opens_behind_rather_than_handing_over()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        var source = File.ReadAllText(
            Path.Combine(here!, "src", "Vaktari.Ui", "MainWindow.axaml.cs"));

        Assert.Contains("_shell.OpenBehind(opening)", source);
        Assert.DoesNotContain("_shell.OpenInNewTab(folder)", source);

        var shell = File.ReadAllText(
            Path.Combine(here!, "src", "Vaktari.Ui", "ViewModels", "ShellViewModel.cs"));

        var behind = shell[shell.IndexOf("public void OpenBehind(string path)", StringComparison.Ordinal)..];

        Assert.Contains("activate: false", behind[..behind.IndexOf("\n    }\n", StringComparison.Ordinal)]);
    }
}
