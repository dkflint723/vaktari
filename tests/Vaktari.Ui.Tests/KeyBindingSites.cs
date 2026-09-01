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
/// </summary>
internal static class KeyBindingSites
{
    internal static string Source(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Vaktari.Ui", name);

            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"could not find {name} above {AppContext.BaseDirectory}");
    }

    /// <summary>Gesture to the command it runs, read out of the markup.</summary>
    internal static Dictionary<string, string> Markup()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadAllLines(Source("MainWindow.axaml")))
        {
            if (!line.Contains("KeyBinding", StringComparison.Ordinal)) continue;

            var gesture = Regex.Match(line, @"Gesture=""([^""]+)""");
            var command = Regex.Match(line, @"Command=""\{Binding ([^}""]+)\}""");

            if (gesture.Success) found[gesture.Groups[1].Value] = command.Groups[1].Value;
        }

        return found;
    }

    /// <summary>
    /// Gesture to the command it runs, read out of the OnWindowKeyDown switch.
    ///
    /// Case labels stack — Ctrl+Y and Ctrl+Shift+Z share one Redo body — so
    /// labels accumulate until a body is found and every label in the group
    /// gets credited with it.
    /// </summary>
    internal static Dictionary<string, string> CodeBehind()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();

        foreach (var raw in File.ReadAllLines(Source("MainWindow.axaml.cs")))
        {
            var line = raw.Trim();

            var label = Regex.Match(
                line,
                @"^case Key\.(\w+)(?: when e\.KeyModifiers (?:==|\.HasFlag\() ?\(?([^)\r\n:]*)\)?\)?)?:");

            if (label.Success)
            {
                pending.Add(Gesture(label.Groups[1].Value, label.Groups[2].Value));
                continue;
            }

            var call = Regex.Match(line, @"\.(\w+)Command\.Execute\(");

            if (call.Success && pending.Count > 0)
            {
                foreach (var gesture in pending) found[gesture] = call.Groups[1].Value;
                pending.Clear();
                continue;
            }

            // A body that does something other than run a command still ends
            // the group: crediting the NEXT case's command to these labels
            // would invent a binding that does not exist.
            if (line.StartsWith("break;", StringComparison.Ordinal)) pending.Clear();
        }

        return found;
    }

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
