using Avalonia.Headless.XUnit;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Getting out of a dialog.
///
/// **Escape closed nothing but the shortcut sheet.** Settings, Properties,
/// Batch rename, Share, Connection and the conflict prompt all had to be
/// dismissed with the mouse — and the conflict prompt is the one that appears
/// in the middle of a copy, when both hands are on the keyboard. Every desktop
/// convention says Escape leaves a dialog, so the first thing anybody tries
/// does nothing, twice, before they reach for the pointer.
///
/// Checked in the markup rather than by pressing the key, because five of the
/// six windows want their own Cancel pressed rather than a blanket close:
/// abandoning a conflict prompt cancels the operation behind it, which is not
/// the same as dismissing the window, and a test that only proved "the window
/// went away" would pass on the wrong behaviour.
///
/// The list is discovered rather than written out, so a dialog added later is
/// held to this without anybody remembering to add it here.
/// </summary>
public sealed class EscapeClosesDialogsTests
{
    /// <summary>Every window in the application except the main one, which is
    /// not a dialog and must not vanish on Escape.</summary>
    public static TheoryData<string> Dialogs
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var file in Directory.EnumerateFiles(
                         Path.Combine(Repo(), "src", "Vaktari.Ui"), "*Window.axaml"))
            {
                if (Path.GetFileName(file) == "MainWindow.axaml") continue;

                data.Add(Path.GetFileName(file));
            }

            return data;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Dialogs))]
    public void Escape_leaves_every_dialog(string window)
    {
        var directory = Path.Combine(Repo(), "src", "Vaktari.Ui");

        var markup = File.ReadAllText(Path.Combine(directory, window));

        var codeBehind = Path.Combine(directory, window + ".cs");

        var code = File.Exists(codeBehind) ? File.ReadAllText(codeBehind) : "";

        var answered =
            markup.Contains("IsCancel=\"True\"", StringComparison.Ordinal)
            || code.Contains("Key.Escape", StringComparison.Ordinal);

        Assert.True(answered,
            $"{window} does not answer Escape — it can only be dismissed with the "
            + "mouse. Give its cancel button IsCancel=\"True\", or handle the key "
            + "when there is no such button.");
    }

    /// <summary>
    /// Not vacuous: the main window must NOT close on Escape, where the key
    /// clears a filter, abandons a cut or closes a prompt instead. A rule that
    /// swept it up with the dialogs would be a much worse bug than the one this
    /// class is about.
    /// </summary>
    [AvaloniaFact]
    public void The_main_window_does_not_close_on_escape()
    {
        var markup = File.ReadAllText(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

        Assert.DoesNotContain("IsCancel=\"True\"", markup);
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }
}
