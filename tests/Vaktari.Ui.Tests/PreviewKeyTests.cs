using System.Xml.Linq;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where the Space gesture lives, and why it is not in the markup.
///
/// **A Window KeyBinding is dispatched ahead of the window's own key handler**,
/// so every guard in OnWindowKeyDown — the confirm prompt, the rename mode, the
/// "a focused text box owns the keyboard" rule — was structurally unable to
/// stop it. Typing a space while renaming a file to "My Report" toggled a
/// 360-pixel preview overlay instead of typing the space.
///
/// The gesture still works and is still advertised in the F1 list; only the
/// place it is handled changed. This is a markup test rather than a behaviour
/// one because the fault was entirely about WHERE the binding was declared.
/// </summary>
public sealed class PreviewKeyTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static XDocument Markup()
    {
        var path = Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml");
        return XDocument.Load(path);
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }

    [Fact]
    public void Space_is_not_a_window_key_binding()
    {
        var gestures = Markup()
            .Descendants(Avalonia + "KeyBinding")
            .Select(k => (string?)k.Attribute("Gesture"))
            .OfType<string>()
            .ToList();

        Assert.DoesNotContain(
            "Space", gestures, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And it is still offered to the user: moving where a gesture is handled
    /// must not quietly remove it from the list that teaches people it exists.
    /// </summary>
    [Fact]
    public void Space_is_still_listed_in_the_shortcuts_help()
    {
        var listed = Vaktari.Ui.ViewModels.Shortcuts.All
            .SelectMany(group => group.Keys)
            .Any(item => item.Keys.Equals("Space", StringComparison.OrdinalIgnoreCase));

        Assert.True(listed, "Space vanished from the shortcuts window");
    }
}
