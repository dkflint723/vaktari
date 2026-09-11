using System.Xml.Linq;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a screen reader is told about the dialogs.
///
/// **The main window named sixty-two of its controls and the dialogs named
/// none.** A button that says "Cancel" is announced as "Cancel" without any
/// help — its text is its name — but a box, a dropdown, a spinner or a list
/// has no text of its own, and a reader arriving at the settings page heard
/// "combo box" eleven times over. Every such control in every dialog now
/// carries a name, and this is the rule that keeps it so: not a list of the
/// ones fixed, which is a list somebody must remember to add to, but the
/// shape of the thing that needs one.
/// </summary>
public sealed class DialogAccessibilityTests
{
    /// <summary>Every window but the main one, which has rules of its own —
    /// found rather than listed, so a dialog added next year is held to this
    /// without anybody remembering to add it here.</summary>
    public static TheoryData<string> Dialogs =>
        [.. RepoSource.UiMarkup().Where(f => f is not ("MainWindow.axaml" or "App.axaml")).Order()];

    /// <summary>Controls that carry no text of their own: nothing to announce
    /// unless the markup says.</summary>
    private static readonly string[] Wordless =
        ["TextBox", "ComboBox", "NumericUpDown", "Slider", "AutoCompleteBox", "ListBox", "TreeView", "ToggleSwitch"];

    /// <summary>Controls whose text IS their name — when they have text.</summary>
    private static readonly string[] Worded = ["Button", "ToggleButton", "RepeatButton", "CheckBox", "RadioButton"];

    private static XDocument Load(string file) => XDocument.Parse(RepoSource.Ui(file));

    private static bool Named(XElement e)
        => e.Attribute("AutomationProperties.Name") is { Value.Length: > 0 }
           || e.Attribute("AutomationProperties.LabeledBy") is { Value.Length: > 0 };

    /// <summary>Text, or a binding to text — a glyph like ✕ is not a name.</summary>
    private static bool HasWords(XElement e)
        => e.Attribute("Content")?.Value is { } content
           && (content.StartsWith("{Binding", StringComparison.Ordinal) || char.IsLetter(content[0]));

    private static string Describe(XElement e)
        => $"{e.Name.LocalName} {string.Join(" ", e.Attributes().Take(2).Select(a => $"{a.Name.LocalName}=\"{a.Value}\""))}";

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Every_box_dropdown_spinner_and_list_in_a_dialog_has_a_name(string file)
    {
        var unnamed = Load(file).Descendants()
            .Where(e => Wordless.Contains(e.Name.LocalName) && !Named(e))
            .Select(Describe)
            .ToList();

        Assert.True(unnamed.Count == 0,
            $"{file}: a screen reader has nothing to say for {unnamed.Count} control(s):\n  " + string.Join("\n  ", unnamed));
    }

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Every_button_and_tick_box_in_a_dialog_has_words_or_a_name(string file)
    {
        var mute = Load(file).Descendants()
            .Where(e => Worded.Contains(e.Name.LocalName) && !HasWords(e) && !Named(e))
            .Select(Describe)
            .ToList();

        Assert.True(mute.Count == 0,
            $"{file}: {mute.Count} control(s) have neither text nor a name:\n  " + string.Join("\n  ", mute));
    }

    /// <summary>
    /// The rule has teeth only if it is looking at something: a dialog with
    /// no inputs at all would pass an empty check. The settings window is the
    /// one with the most, and this pins that the walk sees them.
    /// </summary>
    [Fact]
    public void The_rule_is_looking_at_the_settings_page()
    {
        var inputs = Load("SettingsWindow.axaml").Descendants()
            .Count(e => Wordless.Contains(e.Name.LocalName));

        Assert.True(inputs >= 13, $"only {inputs} wordless controls seen in SettingsWindow.axaml; the walk is not reaching them");
    }
}
