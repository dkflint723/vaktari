using Avalonia.Headless.XUnit;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Three things the interface knew and did not say.
///
/// Each is one attribute, and each was the difference between an answer being
/// available and being reachable. None of them is a bug in the sense of
/// something computing the wrong result — the value was worked out correctly in
/// every case, and then not shown to anybody.
/// </summary>
public sealed class DiscoverabilityTests
{
    private static string Markup(string file)
        => File.ReadAllText(Path.Combine(Repo(), "src", "Vaktari.Ui", file));

    /// <summary>
    /// **The properties window was called "properties".** The view model has
    /// worked out the name of what it is describing since it was written, and
    /// the window never asked. Open properties for three files to compare them
    /// — which is the reason to open three — and the taskbar showed three
    /// identical buttons.
    /// </summary>
    [AvaloniaFact]
    public void The_properties_window_names_what_it_is_describing()
    {
        var markup = Markup("PropertiesWindow.axaml");

        Assert.DoesNotContain("Title=\"properties\"", markup);
        Assert.Contains("Title=\"{Binding Title", markup);
    }

    /// <summary>
    /// **A sidebar place showed a label and never a path.** Two places can
    /// easily be called Documents — one local, one on a share — and there was
    /// nothing anywhere in the interface that said which was which short of
    /// clicking one and reading the address bar.
    /// </summary>
    [AvaloniaFact]
    public void A_sidebar_place_says_where_it_goes()
    {
        var markup = Markup("MainWindow.axaml");

        var at = markup.IndexOf("AutomationProperties.Name=\"{Binding Label}\"",
                                StringComparison.Ordinal);

        Assert.True(at > 0, "the place row is not written the way this test looks for it");

        // Within the same element, not somewhere else in the file.
        var element = markup[at..markup.IndexOf('>', at)];

        Assert.Contains("ToolTip.Tip=\"{Binding Path}\"", element);
    }

    /// <summary>
    /// **F1 was the only way to the list of shortcuts**, which is circular: the
    /// list of keys could only be reached by already knowing one of them.
    /// </summary>
    [AvaloniaFact]
    public void The_shortcut_sheet_can_be_reached_without_a_shortcut()
    {
        var markup = Markup("MainWindow.axaml");

        var routes = markup.Split("ShowShortcutsCommand").Length - 1;

        Assert.True(routes >= 2,
            "the shortcut sheet is reachable only by pressing F1, so the list of "
            + "keys is behind one of the keys it lists");
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }
}
