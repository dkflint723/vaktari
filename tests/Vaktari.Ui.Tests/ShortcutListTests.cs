using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The list F1 shows, kept honest against the application it describes.
///
/// **A printed shortcut that does nothing is worse than none**, and a list
/// written by hand drifts the first time a binding is added or renamed. So
/// every gesture in the markup has to appear here, and every gesture here that
/// looks like a KeyBinding has to exist there.
///
/// The list is written out rather than generated because several keys are
/// handled in code-behind, where one key means different things depending on
/// what has focus — Escape closes a prompt, clears a filter, or abandons a cut,
/// and no generator could describe that honestly.
/// </summary>
public sealed class ShortcutListTests
{
    private static string Markup()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Vaktari.Ui", "MainWindow.axaml");

            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("could not find MainWindow.axaml");
    }

    /// <summary>Every Gesture="..." the window binds.</summary>
    public static TheoryData<string> Bound
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var line in Markup().Split('\n'))
            {
                if (!line.Contains("KeyBinding", StringComparison.Ordinal)) continue;

                var at = line.IndexOf("Gesture=\"", StringComparison.Ordinal);
                if (at < 0) continue;

                var from = at + "Gesture=\"".Length;
                var to = line.IndexOf('"', from);

                if (to > from) data.Add(line[from..to]);
            }

            return data;
        }
    }

    /// <summary>
    /// The same gesture written the way somebody reads it. The markup spells
    /// keys the way Avalonia parses them; the list spells them the way they are
    /// printed on a keyboard.
    /// </summary>
    private static string Readable(string gesture) => gesture
        .Replace("OemPlus", "+", StringComparison.Ordinal)
        .Replace("OemMinus", "-", StringComparison.Ordinal)
        .Replace("OemComma", ",", StringComparison.Ordinal)
        .Replace("D0", "0", StringComparison.Ordinal)

        // **The pad's zero is a second key wearing the first one's keycap.**
        // Avalonia calls it NumPad0 and the top row's D0; both are printed 0,
        // so the sheet keeps one line and this folds the spelling onto it. The
        // rule above cannot do the job — its "D" is a capital and the pad's
        // spelling has a lowercase one.
        .Replace("NumPad0", "0", StringComparison.Ordinal)
        .Replace("Left", "←", StringComparison.Ordinal)
        .Replace("Right", "→", StringComparison.Ordinal)
        .Replace("Up", "↑", StringComparison.Ordinal)
        .Replace("Down", "↓", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Bound))]
    public void Every_bound_key_is_in_the_list(string gesture)
    {
        var listed = Shortcuts.All
            .SelectMany(g => g.Keys)
            .SelectMany(k => k.Keys.Split(" / ", StringSplitOptions.TrimEntries))
            .ToList();

        var wanted = Readable(gesture);

        Assert.True(
            listed.Any(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase)),
            $"{gesture} is bound in MainWindow.axaml but missing from the F1 list "
            + "— a key nobody can find is a key nobody uses.");
    }

    /// <summary>
    /// Every printed gesture that looks like a key, as opposed to "middle click"
    /// or "drag" — those are real entries and no parser can find them in a
    /// KeyBinding table.
    /// </summary>
    public static TheoryData<string> Listed
    {
        get
        {
            var data = new TheoryData<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Pointer gestures and ranges are real entries that no key table
            // can hold: "ctrl+drag" is a mouse gesture, "ctrl+1…9" is nine
            // bindings written as one line, and the side buttons are not keys.
            string[] notKeys = ["drag", "click", "wheel", "forward", "back", "…"];

            foreach (var key in Shortcuts.All
                         .SelectMany(g => g.Keys)
                         .SelectMany(k => k.Keys.Split(" / ", StringSplitOptions.TrimEntries)))
            {
                if (key.Contains(' ')) continue;
                if (notKeys.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                if (seen.Add(key)) data.Add(key);
            }

            return data;
        }
    }

    /// <summary>
    /// **F3 opens search in Explorer and splits the window in Dolphin, and
    /// Vaktari chose Dolphin.** A Windows user presses it, gets a second pane,
    /// and opens this sheet to find out why — so the split line itself has to
    /// name the key that does what they wanted, rather than leaving them to
    /// scroll to a heading they have no reason to look under.
    ///
    /// The key it must name is read from the BINDINGS, not from the sheet's own
    /// Search rows: a sheet checked against itself would go on passing after
    /// search moved to another key, which is the drift this file exists to stop.
    /// </summary>
    [Fact]
    public void The_split_key_says_where_search_is()
    {
        var split = Shortcuts.All
            .SelectMany(g => g.Keys)
            .Single(k => k.Keys
                .Split(" / ", StringSplitOptions.TrimEntries)
                .Contains("F3", StringComparer.OrdinalIgnoreCase));

        var searchKeys = KeyBindingSites.Markup()
            .Where(b => b.Value.Contains("BeginSearch", StringComparison.Ordinal))
            .Select(b => Readable(b.Key))
            .ToList();

        Assert.NotEmpty(searchKeys);

        Assert.True(
            searchKeys.Any(key => split.Does.Contains(key, StringComparison.OrdinalIgnoreCase)),
            $"the F3 line reads \"{split.Does}\" and names none of "
            + $"{string.Join(", ", searchKeys)} — somebody who pressed F3 expecting "
            + "Explorer's search and got a split learns nothing from the line they "
            + "are looking at.");
    }

    /// <summary>
    /// **The direction nothing checked.** The list was kept honest one way
    /// only: a gesture bound in the application had to be printed here, and
    /// nothing stopped a gesture being printed here that the application does
    /// not bind. That is the worse half — a key that does nothing when pressed
    /// reads as the application being broken, while a key that works but is not
    /// printed is merely undiscovered.
    ///
    /// It went unnoticed because both halves are usually written in one sitting.
    /// It was caught when an edit adding F9 landed in the list and not in the
    /// markup, and every test still passed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Listed))]
    public void Every_listed_key_is_actually_bound(string printed)
    {
        // "Handled at all", not "runs a command": Backspace, Space and Tab do
        // their work inline, and by the stricter reading they look unbound.
        var bound = KeyBindingSites.Markup().Keys
            .Concat(KeyBindingSites.CodeBehindHandled())
            .Select(Readable)
            .ToList();

        Assert.True(
            bound.Any(b => string.Equals(b, printed, StringComparison.OrdinalIgnoreCase)),
            $"{printed} is printed in the F1 list and bound nowhere — pressing it "
            + "does nothing, which reads as the application being broken.");
    }

    /// <summary>Nothing is listed twice under the same heading, which reads as
    /// a mistake even when both lines are true.</summary>
    [Fact]
    public void No_group_repeats_itself()
    {
        foreach (var group in Shortcuts.All)
        {
            var keys = group.Keys.Select(k => k.Keys).ToList();

            Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Every_entry_says_something()
    {
        Assert.NotEmpty(Shortcuts.All);

        foreach (var shortcut in Shortcuts.All.SelectMany(g => g.Keys))
        {
            Assert.NotEmpty(shortcut.Keys.Trim());
            Assert.NotEmpty(shortcut.Does.Trim());
        }
    }

    /// <summary>
    /// It opens and lays out. New markup on a path that only runs when somebody
    /// presses F1 is exactly the kind that ships broken.
    /// </summary>
    [AvaloniaFact]
    public void The_window_opens_and_lists_them()
    {
        var window = new Vaktari.Ui.ShortcutsWindow();

        window.Show();
        window.Measure(new Avalonia.Size(620, 620));
        window.Arrange(new Avalonia.Rect(0, 0, 620, 620));

        var shown = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .OfType<string>()
            .ToList();

        Assert.Contains("F2", shown);
        Assert.Contains("Rename", shown);
        Assert.Contains("Getting around", shown);

        window.Close();
    }
}
