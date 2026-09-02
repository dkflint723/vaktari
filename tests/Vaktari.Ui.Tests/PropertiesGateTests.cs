using Avalonia.Headless.XUnit;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Asking about something that is not there.
///
/// **The sheet filled itself in rather than staying empty.** Windows answers a
/// query about a file that does not exist with a size of zero, 1601-01-01 for
/// every date, and every attribute set — so the window looked authoritative and
/// was entirely invented. There were two ways to reach that state, and they had
/// to be closed separately.
///
/// The gate: the bin and Recent both hold rows naming where a file USED to be,
/// and the menu entry was correctly greyed out there while Alt+Enter went round
/// it and opened the sheet anyway.
///
/// The race: any row can go between being listed and being asked about, so the
/// gate alone was never enough.
/// </summary>
public sealed class PropertiesGateTests
{
    private static string Source()
        => File.ReadAllText(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml.cs"));

    /// <summary>
    /// **Alt+Enter went round the gate.** It called the window's own
    /// ShowProperties directly; the shell's command is the thing that refuses
    /// the bin and Recent, and the keyboard never asked it.
    /// </summary>
    [AvaloniaFact]
    public void Alt_enter_asks_the_shell_rather_than_the_window()
    {
        var handled = KeyBindingSites.CodeBehind();

        Assert.True(handled.TryGetValue("Alt+Enter", out var command),
                    "Alt+Enter is not handled where this test looks for it");

        Assert.Equal("ShowProperties", command);
    }

    /// <summary>
    /// The gate the keyboard now goes through. Without this the route above
    /// would be correct and lead somewhere that refuses nothing.
    /// </summary>
    [AvaloniaFact]
    public void The_shell_refuses_the_bin_and_recent()
    {
        var shell = File.ReadAllText(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("ActiveTab is { IsTrashListing: true } or { IsRecentListing: true }", shell);
    }

    /// <summary>
    /// And the race the gate cannot close: a row that goes between being listed
    /// and being asked about. Checked in the source because the window this
    /// lives in wants a real shell, a real properties provider and a real
    /// access editor to build, and the rule is one line inside it.
    /// </summary>
    [AvaloniaFact]
    public void A_path_that_has_gone_opens_no_sheet()
    {
        var at = Source().IndexOf(
            "private void ShowPropertiesFor(IReadOnlyList<string> paths)", StringComparison.Ordinal);

        Assert.True(at > 0, "ShowPropertiesFor is not declared the way this test looks for it");

        var end = Source().IndexOf("\n    }\n", at, StringComparison.Ordinal);
        var body = Source()[at..(end < 0 ? Source().Length : end)];

        Assert.True(body.Contains("File.Exists(p) || Directory.Exists(p)"),
            "the sheet is opened without checking the path is still there, so it "
            + "shows a size of zero and 1601 dates as though they were facts");

        // Before the window is built, not after — a sheet that appears and then
        // corrects itself is worse than one that never appears.
        Assert.True(
            body.IndexOf("File.Exists(p)", StringComparison.Ordinal)
            < body.IndexOf("new PropertiesWindow", StringComparison.Ordinal),
            "the check runs after the window is built");
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }
}
