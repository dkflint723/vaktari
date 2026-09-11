using Vaktari.Ui.Input;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// A command as the palette lists it: its name, and the keys that also run
/// it as they stood when the palette opened.
/// </summary>
public sealed record PaletteRow(AppCommand Command, string Keys)
{
    public string Name => Command.Name;
}

/// <summary>
/// Every command the window answers, by name — for the box Ctrl+Shift+P
/// opens.
///
/// **A hundred and twenty-five menu rows and seventy-five keys, and no way
/// to run one by its name.** A command that lives three levels down a
/// context menu, or on a key nobody remembers, is a command nobody uses;
/// the palette is the same list as a flat, typed-at box, and the keys beside
/// each name are how the key gets learnt.
///
/// **The same commands the keys run, not a list of its own.** It had one,
/// and its Delete for good went straight to the pane's delete — skipping the
/// question Shift+Delete asks — while its Move to the bin skipped the one the
/// Delete key asks when the setting says to. Both now run
/// <see cref="AppCommand.Run"/>, which is what the keys run.
///
/// What is NOT here: anything that needs a row under the pointer or a
/// parameter the palette cannot supply — Open with, Copy to, the per-place
/// verbs. Those are what the right-click menu is for.
/// </summary>
public static class Palette
{
    /// <summary>Every command the palette offers, with its keys from this keymap.</summary>
    public static IReadOnlyList<PaletteRow> Rows(Keymap keymap)
        => Commands.All
            .Where(command => command.InPalette)
            .Select(command => new PaletteRow(command, keymap.Readable(command.Id)))
            .ToList();

    /// <summary>
    /// The rows a typed query leaves, in the order the box shows them.
    ///
    /// Every word of the query has to appear somewhere in the name, in any
    /// order — "tab close" finds Close tab — and a name that BEGINS with the
    /// query comes first: "up" is Up one folder before it is Duplicate tab,
    /// because somebody typing a command's name types it from the front.
    /// Within a tier the table's own order holds, which is grouped, so the
    /// empty query reads as the menus do rather than as an index.
    /// </summary>
    public static IReadOnlyList<PaletteRow> Match(string query, Keymap? keymap = null)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var whole = query.Trim();

        return Rows(keymap ?? Keymap.Current)
            .Where(row => words.All(w => row.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(row => row.Name.StartsWith(whole, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }
}
