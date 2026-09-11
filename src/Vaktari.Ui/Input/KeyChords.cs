using Avalonia.Input;

namespace Vaktari.Ui.Input;

/// <summary>
/// Key gestures as Vaktari reads, stores and prints them, and the rules about
/// which of them a command may be given.
///
/// **Avalonia's own parser reads a bare digit as a NUMBER.** Enum.TryParse
/// takes "1" as the key whose value is 1, so "Ctrl+Shift+1" came out as
/// Ctrl+Shift+Cancel — measured once already, on a menu row that drew
/// "Ctrl+Shift+Tab". A keymap somebody edits by hand is exactly where "1" is
/// written, so this reads digits, arrows, "Page Down" and the punctuation the
/// F1 sheet prints before handing anything else to the enum.
///
/// Three spellings, one gesture: <see cref="Store"/> is what settings.json
/// holds, Avalonia's key names with Enter, Page Up and Page Down written the
/// way a person would write them; <see cref="Readable"/> is the sheet's
/// ("Alt+←", "Ctrl+Shift+,"); <see cref="Hint"/> is the lower-case aside a
/// tooltip carries ("alt+left"). <see cref="Parse"/> takes all three.
/// </summary>
public static class KeyChords
{
    /// <summary>The four modifiers a gesture can carry. Anything else in a
    /// KeyModifiers value is not something a keymap can say.</summary>
    private const KeyModifiers Modifiers =
        KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;

    /// <summary>A gesture from what was pressed, with only the modifiers a
    /// keymap can hold.</summary>
    public static KeyGesture From(Key key, KeyModifiers modifiers) => new(key, modifiers & Modifiers);

    // ---- reading ----------------------------------------------------------------

    /// <summary>
    /// A gesture from any of the three spellings, or null when the text is not
    /// one — an unknown word, a modifier with no key, or a key on its own that
    /// is only ever a modifier.
    /// </summary>
    public static KeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = Split(text.Trim());

        if (parts.Count == 0) return null;

        var modifiers = KeyModifiers.None;

        for (var i = 0; i < parts.Count - 1; i++)
        {
            if (Modifier(parts[i]) is not { } modifier) return null;

            modifiers |= modifier;
        }

        if (KeyNamed(parts[^1]) is not { } key || key == Key.None || IsModifierKey(key)) return null;

        return new KeyGesture(key, modifiers);
    }

    /// <summary>
    /// The parts between the pluses, where a plus with nothing before it IS
    /// the part — "Ctrl++" is Ctrl and the plus key, "Ctrl+Shift++" the same
    /// with Shift, and "+" on its own the plus key.
    /// </summary>
    private static List<string> Split(string text)
    {
        var parts = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '+' || i == start) continue;

            parts.Add(text[start..i].Trim());
            start = i + 1;
        }

        if (start < text.Length) parts.Add(text[start..].Trim());

        return parts.Where(p => p.Length > 0).ToList();
    }

    private static KeyModifiers? Modifier(string word) => word.ToLowerInvariant() switch
    {
        "ctrl" or "control" => KeyModifiers.Control,
        "shift" => KeyModifiers.Shift,
        "alt" => KeyModifiers.Alt,
        "meta" or "win" or "cmd" or "super" or "⌘" => KeyModifiers.Meta,
        _ => null,
    };

    private static Key? KeyNamed(string word)
    {
        if (word.Length == 1)
        {
            var c = word[0];

            if (c is >= '0' and <= '9') return Key.D0 + (c - '0');
            if (char.IsAsciiLetter(c)) return Key.A + (char.ToUpperInvariant(c) - 'A');

            return c switch
            {
                '+' => Key.OemPlus,
                '-' => Key.OemMinus,
                ',' => Key.OemComma,
                '.' => Key.OemPeriod,
                ';' => Key.OemSemicolon,
                '/' => Key.OemQuestion,
                '\'' => Key.OemQuotes,
                '[' => Key.OemOpenBrackets,
                ']' => Key.OemCloseBrackets,
                '\\' => Key.OemPipe,
                '`' => Key.OemTilde,
                '←' => Key.Left,
                '→' => Key.Right,
                '↑' => Key.Up,
                '↓' => Key.Down,
                _ => null,
            };
        }

        var plain = word.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

        switch (plain)
        {
            case "pageup" or "pgup": return Key.PageUp;
            case "pagedown" or "pgdn": return Key.PageDown;
            case "backspace": return Key.Back;
            case "menu" or "apps": return Key.Apps;
            case "esc": return Key.Escape;
            case "del": return Key.Delete;
            case "ins": return Key.Insert;
            case "enter" or "return": return Key.Enter;
        }

        // A number longer than one digit is not a key, and Enum.TryParse would
        // take it as one — the fault the class summary is about.
        if (plain.All(char.IsAsciiDigit)) return null;

        return Enum.TryParse<Key>(plain, ignoreCase: true, out var key) ? key : null;
    }

    // ---- writing ----------------------------------------------------------------

    /// <summary>
    /// The spelling settings.json holds: Avalonia's key names, which its
    /// parser and this one both read back to the same gesture.
    ///
    /// **Not Avalonia's own ToString**, because several keys have two names
    /// on one value — Return and Enter, Prior and PageUp, Next and PageDown —
    /// and the enum prints whichever it declares first. Either reads back the
    /// same, but "Alt+Return" and "Ctrl+Next" in a file somebody opens to edit
    /// by hand are a small mystery each; the names written here are the ones a
    /// person would write.
    /// </summary>
    public static string Store(KeyGesture gesture)
        => string.Join("+", ModifierWords(gesture.KeyModifiers).Append(gesture.Key switch
        {
            Key.Enter => "Enter",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.CapsLock => "CapsLock",
            Key.PrintScreen => "PrintScreen",
            var other => other.ToString(),
        }));

    /// <summary>The spelling the F1 sheet, the palette and the tour print.</summary>
    public static string Readable(KeyGesture gesture)
        => string.Join("+", ModifierWords(gesture.KeyModifiers).Append(KeyWord(gesture.Key)));

    /// <summary>
    /// The lower-case spelling a label carries as an aside — "Close tab
    /// (ctrl+w)". Arrows are words here rather than glyphs, the way every such
    /// label in the window already spelled them.
    /// </summary>
    public static string Hint(KeyGesture gesture)
    {
        var key = gesture.Key switch
        {
            Key.Left => "left",
            Key.Right => "right",
            Key.Up => "up",
            Key.Down => "down",
            var other => KeyWord(other).ToLowerInvariant(),
        };

        return string.Join("+", ModifierWords(gesture.KeyModifiers)
            .Select(m => m.ToLowerInvariant())
            .Append(key));
    }

    private static IEnumerable<string> ModifierWords(KeyModifiers modifiers)
    {
        // Avalonia's own order, so a gesture reads the same here as in a menu.
        if (modifiers.HasFlag(KeyModifiers.Control)) yield return "Ctrl";
        if (modifiers.HasFlag(KeyModifiers.Shift)) yield return "Shift";
        if (modifiers.HasFlag(KeyModifiers.Alt)) yield return "Alt";
        if (modifiers.HasFlag(KeyModifiers.Meta)) yield return "Meta";
    }

    private static string KeyWord(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),

        // The pad's digits wear the top row's keycaps, and the sheet prints
        // one line for Ctrl+0 whichever of the two answers it.
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),

        Key.OemPlus or Key.Add => "+",
        Key.OemMinus or Key.Subtract => "-",
        Key.OemComma => ",",
        Key.OemPeriod or Key.Decimal => ".",
        Key.OemSemicolon => ";",
        Key.OemQuestion => "/",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.PageUp => "Page Up",
        Key.PageDown => "Page Down",
        Key.Back => "Backspace",

        // Avalonia calls it Apps; every keyboard that has it prints Menu.
        Key.Apps => "Menu",

        // Return and Enter are one value, and ToString picks whichever name
        // the enum declares first.
        Key.Enter => "Enter",
        _ => key.ToString(),
    };

    // ---- what a command may be given --------------------------------------------

    /// <summary>Ctrl, Shift, Alt and the Windows or Command key on their own:
    /// half a gesture, which the key editor waits past rather than takes.</summary>
    public static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin;

    /// <summary>A key that types a character when nothing but Shift is held.</summary>
    private static bool IsCharacterKey(Key key) => key
        is >= Key.A and <= Key.Z
        or >= Key.D0 and <= Key.D9
        or >= Key.NumPad0 and <= Key.NumPad9
        or Key.Space
        or Key.Add or Key.Subtract or Key.Multiply or Key.Divide or Key.Decimal
        or Key.OemSemicolon or Key.OemPlus or Key.OemComma or Key.OemMinus or Key.OemPeriod
        or Key.OemQuestion or Key.OemTilde or Key.OemOpenBrackets or Key.OemPipe
        or Key.OemCloseBrackets or Key.OemQuotes or Key.Oem8 or Key.OemBackslash;

    /// <summary>
    /// **A gesture that types something.** A character key with nothing held
    /// or with Shift is a letter somebody is typing — into a box, or into the
    /// listing's type-ahead — and Ctrl+Alt with one is AltGr on Windows, which
    /// is how a great many keyboards type € and @.
    ///
    /// The first kind no command may have (see <see cref="Refusal"/>); the
    /// second it may, and <see cref="IsTextEditing"/> counts both, so a box
    /// that has the keyboard is always handed the character rather than
    /// losing it to a command.
    /// </summary>
    public static bool ProducesText(KeyGesture gesture)
    {
        if (!IsCharacterKey(gesture.Key)) return false;

        var modifiers = gesture.KeyModifiers & Modifiers;

        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0) return true;

        return modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Alt);
    }

    /// <summary>
    /// **The keys that are the grammar of every list and box**, and so belong
    /// to no command. Enter opens, Escape backs out, Tab moves on; the arrows,
    /// Home, End and the page keys move through a list and through the text of
    /// a box; Backspace goes back or up by the Navigation page's own setting;
    /// the Menu key and Shift+F10 open the menu where the keyboard is; Ctrl+1…9
    /// jump to a tab; Alt+F4 is the desktop's. Each is answered in the window's
    /// own key handling with rules a keymap cannot express, and giving one to
    /// a command would take it from all of those.
    ///
    /// With Alt they are free — Alt+Left is Back and Alt+Enter is Properties,
    /// by default — and Ctrl+Tab and Ctrl+Page Down are the tab keys.
    /// </summary>
    public static bool IsReserved(KeyGesture gesture)
    {
        var key = gesture.Key;
        var modifiers = gesture.KeyModifiers & Modifiers;

        if (key == Key.None || IsModifierKey(key)) return true;

        var none = modifiers == KeyModifiers.None;
        var shift = modifiers == KeyModifiers.Shift;
        var ctrl = modifiers == KeyModifiers.Control;
        var ctrlShift = modifiers == (KeyModifiers.Control | KeyModifiers.Shift);

        return key switch
        {
            Key.Enter or Key.Escape or Key.Tab => none || shift,
            Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
                => none || shift || ctrl || ctrlShift,
            Key.PageUp or Key.PageDown => none || shift,
            Key.Back => none || shift || ctrl,
            Key.Apps => true,
            Key.F10 => shift,
            Key.F4 => modifiers == KeyModifiers.Alt,
            >= Key.D1 and <= Key.D9 => ctrl,
            _ => false,
        };
    }

    /// <summary>
    /// **What a focused text box does with a key**: types with it, moves its
    /// caret with it, or copies, cuts, pastes, undoes or selects with it. A
    /// command that answers anywhere in the window gives these back while a
    /// box has the keyboard — so somebody who puts New tab on Ctrl+C gets a
    /// tab from the listing and still gets a copy in the address bar.
    /// </summary>
    public static bool IsTextEditing(KeyGesture gesture)
    {
        if (ProducesText(gesture)) return true;

        var modifiers = gesture.KeyModifiers & Modifiers;
        var onlyCtrlOrShift = (modifiers & ~(KeyModifiers.Control | KeyModifiers.Shift)) == 0;

        return gesture.Key switch
        {
            Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
                or Key.Back or Key.Delete => onlyCtrlOrShift,
            Key.PageUp or Key.PageDown => modifiers is KeyModifiers.None or KeyModifiers.Shift,
            Key.Insert => modifiers is KeyModifiers.Control or KeyModifiers.Shift,
            Key.Enter or Key.Escape or Key.Tab => modifiers is KeyModifiers.None or KeyModifiers.Shift,
            Key.A or Key.C or Key.V or Key.X or Key.Y => modifiers == KeyModifiers.Control,
            Key.Z => modifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift),
            _ => false,
        };
    }

    /// <summary>
    /// Why a command cannot be given this key, in a sentence for the key
    /// editor to show — or null when it can.
    ///
    /// Space on its own is the one typing key a command may have, and only a
    /// command that acts on the listing: those are answered only while no box
    /// has the keyboard, and the listing's type-ahead keeps a space that falls
    /// inside a name being typed.
    /// </summary>
    public static string? Refusal(KeyGesture gesture, KeyTier tier)
    {
        if (IsReserved(gesture))
            return $"{Readable(gesture)} keeps its own job. Enter, Escape, Tab, the arrows, "
                   + "Backspace, the menu key and Ctrl+1…9 work the same in every list and box.";

        if (gesture.Key == Key.Space && (gesture.KeyModifiers & Modifiers) == KeyModifiers.None)
            return tier == KeyTier.Listing
                ? null
                : "Space on its own types a space in every box, so it can only be the key "
                  + "for something that acts on the files in the listing.";

        // A character with nothing held, or with only Shift, is a letter being
        // typed. With Ctrl and Alt together it may be AltGr — how a great many
        // keyboards type € and @ — and it is ALLOWED: Ctrl+Alt+T is a habit
        // worth keeping, and IsTextEditing hands the key back to any box that
        // has the keyboard, so the character still arrives there and the
        // command runs only from the listing.
        var held = gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);

        if (ProducesText(gesture) && held == KeyModifiers.None)
            return $"{Readable(gesture)} types a character. Hold Ctrl or Alt with it to make it a shortcut.";

        return null;
    }
}
