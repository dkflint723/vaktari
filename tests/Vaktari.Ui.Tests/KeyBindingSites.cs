using System.Text.RegularExpressions;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The two places a gesture can be implemented, so a test can tell "bound
/// somewhere else" from "not bound at all".
///
/// **There are two on purpose.** A markup KeyBinding is claimed before the
/// focused control sees the key, which is right for F5 and catastrophic for
/// Ctrl+V — so every gesture a text cursor owns is handled in OnWindowKeyDown
/// instead, behind the guard that lets a focused text box keep its own keys.
/// A test that only reads the markup would therefore call the correct
/// arrangement a missing binding.
///
/// Both sides are read from the real source rather than listed by hand: a hand
/// list is exactly the thing that drifts, and it would let a gesture be deleted
/// from the application while the test that guards it went on passing.
///
/// **The code-behind side is read as a CLASS, not as a file**, and it had its
/// own file-finder that could not be. MainWindow is spread across partials now,
/// and the first of them to leave took the prompt bar's Tab and Shift+Tab with
/// it — so Shift+Tab, still printed on the F1 sheet and still working, read as
/// bound nowhere. A reader that names one file goes quietly weak the moment
/// that file is split, which is the whole reason <see cref="RepoSource.UiClass"/>
/// exists; this one was missed when the other forty-eight were converted
/// because it reached for the source its own way.
/// </summary>
internal static class KeyBindingSites
{
    private static string[] CodeBehind()
        => RepoSource.UiClass("", "MainWindow").Split('\n');

    /// <summary>Gesture to the command it runs, read out of the markup.</summary>
    internal static Dictionary<string, string> Markup()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in RepoSource.Ui("MainWindow.axaml").Split('\n'))
        {
            if (!line.Contains("KeyBinding", StringComparison.Ordinal)) continue;

            var gesture = Regex.Match(line, @"Gesture=""([^""]+)""");
            var command = Regex.Match(line, @"Command=""\{Binding ([^}""]+)\}""");

            if (gesture.Success) found[gesture.Groups[1].Value] = command.Groups[1].Value;
        }

        return found;
    }

    /// <summary>
    /// Every gesture the code-behind has a case for, whatever the body does.
    ///
    /// **"Handled at all" rather than "runs a command".** There used to be a
    /// second reader here which credited only case labels followed by a command
    /// call, to answer which command a key runs. 177d65e answered that from the
    /// keymap instead and left the reader behind, uncalled, where it could not
    /// pass or fail and so could not say anything; it went out with this.
    ///
    /// The looser question is the one the F1 list has to be checked against:
    /// Backspace, Space and Tab all do their work inline rather than through a
    /// command, and by the stricter reading they look unbound.
    /// </summary>
    internal static HashSet<string> CodeBehindHandled()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in CodeBehind())
        {
            var line = raw.Trim();

            // **A comment is not a binding, and one of them said it was.**
            // MEASURED: the sidebar's step chain carries a comment reading
            // "it reads those claims from `e.Key == Key.X`", and the guard
            // pattern below matched it -- so a bare X came back as a handled
            // gesture, alongside the real Ctrl+X. Nothing noticed while this
            // set was only used to check that PRINTED keys are bound; it
            // surfaced the moment the set was also used the other way round.
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;

            var label = Regex.Match(line, CaseLabel);

            if (label.Success)
            {
                found.Add(Gesture(label.Groups[1].Value, label.Groups[2].Value));
                continue;
            }

            // **Not every key is answered by the switch.** Quick preview and the
            // split's Tab are plain guards in their own handlers — `if (e.Key ==
            // Key.Space && ...)` — and reading only case labels called both of
            // them unbound while both worked.
            var guard = Regex.Match(line, @"e\.Key (?:==|!=) Key\.(\w+)");

            if (!guard.Success) continue;

            var modifiers = Regex.Match(line, @"e\.KeyModifiers ?(?:==|!=|\.HasFlag\() ?\(?([^)&|;]*)");

            found.Add(Gesture(guard.Groups[1].Value,
                              modifiers.Success ? modifiers.Groups[1].Value : ""));
        }

        return found;
    }

    /// <summary>
    /// One case label of a key handler's switch — the window's, and now the
    /// prompt bar's as well, since the class is read whole.
    ///
    /// **The space mattered.** The earlier spelling required one between
    /// <c>e.KeyModifiers</c> and what follows it — which the <c>==</c> form has
    /// and <c>.HasFlag(</c> does not. So every HasFlag label failed to match,
    /// and Ctrl+A, Shift+Delete and Alt+Enter read as handled nowhere.
    /// </summary>
    private const string CaseLabel =
        @"^case Key\.(\w+)(?: when e\.KeyModifiers ?(?:==|\.HasFlag\() ?\(?([^)\r\n:]*)\)?\)?)?:";

    private static string Gesture(string key, string modifiers)
    {
        var parts = new List<string>();

        if (modifiers.Contains("Control", StringComparison.Ordinal)) parts.Add("Ctrl");
        if (modifiers.Contains("Shift", StringComparison.Ordinal)) parts.Add("Shift");
        if (modifiers.Contains("Alt", StringComparison.Ordinal)) parts.Add("Alt");

        parts.Add(key);

        return string.Join('+', parts);
    }
}
