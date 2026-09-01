using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Rules about the window's markup, checked against the markup itself.
///
/// **Every rule here is a bug that shipped.** Each one looked correct while
/// reading it, which is the point: they are not style preferences, they are
/// three separate cases of Avalonia doing something reasonable that the markup
/// did not account for, and each survived review, a passing test suite, and
/// hands-on use.
///
/// These are structural assertions rather than headless interaction tests, and
/// that is a deliberate trade. Instantiating a row template needs the shell's
/// whole object graph — a platform, a session, a view model per pane — so a
/// test that clicks a real row is an end-to-end test wearing a unit test's
/// clothes. Reading the shape catches the same regressions for a fraction of
/// the machinery, and cannot pass for the wrong reason.
///
/// What it cannot do is notice a NEW way to make a row unclickable. If the row
/// templates are ever restructured, revisit these rather than trusting them.
/// </summary>
public class MarkupRulesTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Markup()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.axaml")
            ?? throw new InvalidOperationException("MainWindow.axaml is not embedded in the test assembly");

        return XDocument.Load(stream, LoadOptions.SetLineInfo);
    }

    private static string Where(XElement e)
        => $"line {((IXmlLineInfo)e).LineNumber} <{e.Name.LocalName}>";

    /// <summary>
    /// **A Panel with no Background is invisible to the pointer.** Avalonia hit
    /// tests against a brush, and null is not one — events pass straight
    /// through to whatever is behind.
    ///
    /// The row templates had none, so the only clickable things in a row were
    /// its TextBlocks. A filename occupies a small part of a row, so most of
    /// every row was dead, and double-clicking to open a folder appeared to be
    /// slow rather than aimed wrong: nothing happened, so you clicked again.
    /// </summary>
    [Fact]
    public void Every_row_template_root_is_hit_testable()
    {
        // The OUTERMOST Panel of each template, not every Panel in it. An inner
        // one — a thumbnail box, an icon cell — needs no background of its own:
        // with none, the pointer falls through to the row behind it, which is
        // the row, which is what should be hit. Requiring it everywhere flagged
        // four correct panels and taught nothing.
        var offenders = Markup()
            .Descendants(Avalonia + "DataTemplate")
            .Where(t => (string?)t.Attribute(X + "DataType") == "fs:FileEntry")
            .SelectMany(t => t.Descendants(Avalonia + "Panel")
                .Where(p => !p.Ancestors(Avalonia + "Panel").Any(a => a.Ancestors().Contains(t) || a == t)))
            .Where(p => p.Attribute("Background") is null)
            .Select(Where)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A row Panel with no Background cannot be clicked except where its text is:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// **Selection decoration must not be a hit target.**
    ///
    /// These borders are bound to IsSelected, so they APPEAR when the first
    /// click of a double-click selects the row. The second click then lands on
    /// a different element, and Avalonia's double-tap gesture requires both
    /// clicks on the same one — so no DoubleTapped was ever raised, and the
    /// fallback that counts two taps lost the second tap along with it.
    ///
    /// The row stayed unopenable no matter how the background was fixed. They
    /// are decoration; they were never meant to be hit.
    /// </summary>
    [Fact]
    public void Selection_decoration_is_not_a_hit_target()
    {
        var offenders = Markup()
            .Descendants()
            .Where(e => ((string?)e.Attribute("IsVisible"))?.Contains("IsSelected", StringComparison.Ordinal) == true)
            .Where(e => (string?)e.Attribute("IsHitTestVisible") != "False")
            .Select(Where)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Decoration that appears on selection changes what the pointer hits between "
            + "the two clicks of a double-click, which stops the gesture forming:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// **TextTrimming inside a horizontal StackPanel never fires.** A
    /// horizontal StackPanel measures its children with infinite width, so a
    /// TextBlock in one is never told it ran out of room. The setting is
    /// present, correct-looking, and inert; the text simply overflows its
    /// parent and is clipped mid-character with no ellipsis.
    ///
    /// Both sidebar rows that show a network location had this, and a network
    /// label — share, host and port — is the longest thing the sidebar shows.
    /// </summary>
    [Fact]
    public void Trimming_text_is_never_inside_a_horizontal_StackPanel()
    {
        // DIRECT children only. A horizontal StackPanel hands ITS children
        // infinite width, but anything between can hand back a real one — a
        // ScrollViewer, a fixed Width, a Grid column. Following the whole
        // subtree flagged a search result inside a scrolling popup, where
        // trimming works correctly. Static reading cannot settle those, so this
        // asserts only the case that is unambiguously inert, which is also both
        // of the ones that shipped.
        var offenders = Markup()
            .Descendants(Avalonia + "StackPanel")
            .Where(s => (string?)s.Attribute("Orientation") == "Horizontal")
            .SelectMany(s => s.Elements(Avalonia + "TextBlock"))
            .Where(t => t.Attribute("TextTrimming") is not null)
            .Select(Where)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A horizontal StackPanel measures children with infinite width, so TextTrimming "
            + "on these can never engage — use a DockPanel and let the text take what is left:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// **A style applies to the control it is declared on, not only to that
    /// control's descendants.**
    ///
    /// So `Selector="MenuItem &gt; MenuItem"`, written inside a MenuItem's own
    /// Styles to reach its generated item containers, also matches THAT MenuITEM
    /// whenever it happens to sit inside another one. It then takes a Command
    /// expecting one of its own items, with a CommandParameter bound to
    /// whatever its DataContext is — and Avalonia calls CanExecute on every
    /// child the moment a submenu opens.
    ///
    /// **This crashed the application outright.** Consolidating the right-click
    /// menu moved New file, New from template and Scripts underneath New and
    /// More, and from then on clicking "New" terminated the process:
    ///
    ///   System.ArgumentException: Parameter "parameter" (object) cannot be of
    ///   type PaneGroupViewModel, as the command type requires an argument of
    ///   type NewFileKind
    ///
    /// It shipped in 0.7.0. Nothing caught it: the markup compiles, every
    /// binding resolves, the suite passed, and the three menus that were NOT
    /// moved go on working, so the menu looks fine until you open the one that
    /// was. The rule is therefore about the selector rather than about nesting
    /// — the nesting is what changed, and it can change again.
    ///
    /// Anchoring on the host's name is what fixes it: the host's parent is a
    /// different element, so it cannot match its own rule, while the containers
    /// it generates still do.
    /// </summary>
    [Fact]
    public void No_menu_style_can_match_the_menu_item_it_is_declared_on()
    {
        var offenders = Markup()
            .Descendants(Avalonia + "Style")
            .Where(style => (string?)style.Attribute("Selector") is { } selector
                            && selector.Replace(" ", "").Contains("MenuItem>MenuItem")
                            && !selector.Contains('#'))
            .Select(Where)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A style reaches the control it is declared on, so an unanchored "
            + "`MenuItem > MenuItem` selector gives its own host the item command "
            + "and crashes when a parent submenu opens. Anchor it on the host's "
            + "x:Name, as `MenuItem#TheHost > MenuItem`: "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// The other half: anchoring on a name that does not exist would match
    /// nothing, which loses every command in the submenu silently — the menu
    /// still opens, the entries still draw, and clicking one does nothing.
    /// </summary>
    [Fact]
    public void Every_anchored_menu_style_names_a_menu_item_that_exists()
    {
        var doc = Markup();

        var names = doc.Descendants(Avalonia + "MenuItem")
            .Select(m => (string?)m.Attribute(X + "Name"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var dangling = doc.Descendants(Avalonia + "Style")
            .Select(style => (Style: style, Selector: (string?)style.Attribute("Selector")))
            .Where(x => x.Selector is not null && x.Selector.Contains("MenuItem#"))
            .Select(x => (x.Style, Name: x.Selector!
                .Split("MenuItem#")[1]
                .TakeWhile(c => char.IsLetterOrDigit(c) || c == '_')
                .Aggregate("", (a, c) => a + c)))
            .Where(x => !names.Contains(x.Name))
            .Select(x => $"{Where(x.Style)} names #{x.Name}")
            .ToList();

        Assert.True(dangling.Count == 0,
            "These styles anchor on a MenuItem name that no MenuItem carries, so "
            + "they match nothing and their entries lose their commands: "
            + string.Join("; ", dangling));
    }

    /// <summary>
    /// **A shortcut printed beside a menu row must actually do something.**
    ///
    /// The menu shows gestures now, and a gesture is a claim: press this and
    /// the row happens. Nothing enforces it — InputGesture is display-only, so
    /// a row can advertise Ctrl+Shift+C while no key binding exists, and the
    /// only way anyone finds out is by pressing it and watching nothing occur.
    /// Settings used to carry a sentence with the same problem, promising that
    /// hidden entries kept working from the keyboard when most had no key.
    ///
    /// These are handled in MainWindow.axaml.cs rather than declared in markup,
    /// because they depend on what has focus: Enter and Alt+Enter act on the
    /// row, Delete and Shift+Delete have to leave a rename box alone, and Space
    /// must not fire while somebody is typing a filename. Named here so the rule
    /// can tell "deliberately elsewhere" from "not bound at all".
    /// </summary>
    [Fact]
    public void Every_shortcut_shown_in_the_menu_is_really_bound()
    {
        var doc = Markup();

        // Ctrl+A and Ctrl+Shift+A are handled in OnWindowKeyDown too: both go
        // through the ListBox's own bulk selection path, because filling the
        // bound collection row by row fires a change per file and each one
        // refreshes the details panel.
        // These run something other than a pane command — the listing's own
        // bulk selection path, a confirm prompt, a preview toggle — so they
        // have no `pane.XxxCommand.Execute` line for the reader below to find.
        var shapedDifferently = new[]
        {
            "Enter", "Delete", "Alt+Enter", "Shift+Delete", "Space", "Ctrl+A", "Ctrl+Shift+A",
        };

        // **The clipboard rows advertise Ctrl+X, Ctrl+C and Ctrl+V and no
        // KeyBinding implements them any more.** That is deliberate: as markup
        // bindings they were claimed before the focused text box saw them, so
        // the address bar could not copy or paste. Read out of the switch that
        // does implement them rather than added to the list above, so deleting
        // the case still fails this test.
        var bound = doc.Descendants(Avalonia + "KeyBinding")
            .Select(k => (string?)k.Attribute("Gesture"))
            .OfType<string>()
            .Concat(shapedDifferently)
            .Concat(KeyBindingSites.CodeBehind().Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lying = doc.Descendants(Avalonia + "MenuItem")
            .Select(m => (Item: m, Gesture: (string?)m.Attribute("InputGesture")))
            .Where(x => x.Gesture is not null && !bound.Contains(x.Gesture))
            .Select(x => $"{Where(x.Item)} shows {x.Gesture}")
            .ToList();

        Assert.True(lying.Count == 0,
            "These menu rows advertise a keyboard shortcut that no KeyBinding "
            + "implements, so pressing it does nothing: "
            + string.Join("; ", lying));
    }

    /// <summary>A guard on the guards: if the resource stops being embedded, or
    /// the file moves, every rule above would pass against nothing.</summary>
    [Fact]
    public void The_markup_under_test_is_actually_present()
    {
        var doc = Markup();

        Assert.Equal("Window", doc.Root?.Name.LocalName);
        Assert.True(doc.Descendants(Avalonia + "DataTemplate")
            .Count(t => (string?)t.Attribute(X + "DataType") == "fs:FileEntry") >= 4,
            "expected the four FileEntry row templates — details, compact, grid and the simple list");
    }
}
