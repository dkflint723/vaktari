using Avalonia.Input;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;

namespace Vaktari.Ui.Input;

/// <summary>
/// Which key runs which command: the shipped defaults with the keys somebody
/// chose laid over them.
///
/// **Every key the window answers was written into the window's markup and
/// its key handler, twice over, and nobody could change one.** A key that
/// collides with a habit — Ctrl+D pins here and deletes in Explorer, Ctrl+B
/// folds the sidebar here and adds a place in Dolphin, F3 splits here and
/// searches in Explorer — could be explained on the F1 sheet and nothing
/// more. Now each command's keys come from here, and the F1 sheet, the
/// palette, the tour, the menus and the tooltips all print what is in force.
///
/// **One key, one command, decided here.** A file that gives Ctrl+B to Add
/// this folder to places and never took it from the sidebar is honoured the
/// way it was meant: a key somebody chose beats a default, and between two
/// chosen keys the command listed first keeps it. What loses is recorded in
/// <see cref="Dropped"/> rather than thrown, because a settings file must
/// never stop the window opening.
/// </summary>
public sealed class Keymap
{
    private readonly Dictionary<string, IReadOnlyList<KeyGesture>> _keys;
    private readonly Dictionary<KeyGesture, AppCommand> _owners;
    private readonly List<(KeyGesture Gesture, AppCommand Command)> _ordered;

    private Keymap(
        Dictionary<string, IReadOnlyList<KeyGesture>> keys,
        Dictionary<KeyGesture, AppCommand> owners,
        List<(KeyGesture, AppCommand)> ordered,
        IReadOnlyList<string> dropped)
    {
        _keys = keys;
        _owners = owners;
        _ordered = ordered;
        Dropped = dropped;
    }

    /// <summary>What was in the file and could not be honoured, each as a
    /// sentence: a word that is not a key, a key that keeps its own job, a key
    /// already taken, a command this build does not have.</summary>
    public IReadOnlyList<string> Dropped { get; }

    /// <summary>The keys Vaktari ships with.</summary>
    public static Keymap Default { get; } = Build(null);

    /// <summary>The shipped keys with these choices laid over them.</summary>
    public static Keymap From(KeyboardSettings? settings) => Build(settings?.Bindings);

    // ---- the one in force -----------------------------------------------------

    private static KeyboardSettings? _source;
    private static string _signature = "";
    private static Keymap _current = Default;

    /// <summary>
    /// The keymap the settings in force describe.
    ///
    /// **Worked out when read rather than when the settings change.** Several
    /// things listen for a settings change and read the keymap from inside
    /// their handler, and the order handlers run in is nobody's promise — so
    /// a keymap rebuilt from a handler of its own could be read stale by a
    /// handler that happened to run first. Reading compares what it was built
    /// from and rebuilds when that moved, which makes the order irrelevant.
    /// </summary>
    public static Keymap Current
    {
        get
        {
            var settings = AppSettings.Current.Keyboard;

            if (ReferenceEquals(settings, _source)) return _current;

            _source = settings;

            var signature = Signature(settings);

            if (signature != _signature)
            {
                _signature = signature;
                _current = From(settings);
            }

            return _current;
        }
    }

    /// <summary>
    /// Raised when a settings change moved a key, so a window can put its own
    /// bindings back in line. Not raised when a save left the keys alone, which
    /// is most saves.
    /// </summary>
    public static event EventHandler? Changed;

    static Keymap()
    {
        AppSettings.Changed += (_, _) =>
        {
            var before = _current;

            if (!ReferenceEquals(Current, before)) Changed?.Invoke(null, EventArgs.Empty);
        };
    }

    /// <summary>The choices as one string, so a save that rewrote the record
    /// without moving a key is told apart from one that moved a key.</summary>
    private static string Signature(KeyboardSettings? settings)
        => settings?.Bindings is not { Count: > 0 } bindings
            ? ""
            : string.Join("\n", bindings
                .OrderBy(b => b.Key, StringComparer.Ordinal)
                .Select(b => b.Key + "=" + string.Join("|", b.Value ?? ["\0"])));

    // ---- building -------------------------------------------------------------

    private static Keymap Build(Dictionary<string, List<string>>? chosen)
    {
        var keys = new Dictionary<string, IReadOnlyList<KeyGesture>>(StringComparer.Ordinal);
        var owners = new Dictionary<KeyGesture, AppCommand>();
        var dropped = new List<string>();

        // Chosen keys first, so a key somebody chose beats a default that
        // still names it.
        foreach (var command in Commands.All)
        {
            if (chosen?.GetValueOrDefault(command.Id) is not { } texts) continue;

            var mine = new List<KeyGesture>();

            foreach (var text in texts)
            {
                if (KeyChords.Parse(text) is not { } gesture)
                {
                    dropped.Add($"{command.Name}: “{text}” is not a key.");
                    continue;
                }

                if (KeyChords.Refusal(gesture, command.Tier) is { } why)
                {
                    dropped.Add($"{command.Name}: {why}");
                    continue;
                }

                if (owners.TryGetValue(KeyChords.Folded(gesture), out var holder))
                {
                    if (!ReferenceEquals(holder, command))
                        dropped.Add($"{command.Name}: {KeyChords.Readable(gesture)} is already {holder.Name}’s.");

                    continue;
                }

                owners[KeyChords.Folded(gesture)] = command;
                mine.Add(gesture);
            }

            keys[command.Id] = mine;
        }

        foreach (var command in Commands.All)
        {
            if (keys.ContainsKey(command.Id)) continue;

            var mine = new List<KeyGesture>();

            foreach (var gesture in command.DefaultKeys)
            {
                if (owners.TryGetValue(KeyChords.Folded(gesture), out var holder))
                {
                    dropped.Add($"{command.Name}: {KeyChords.Readable(gesture)} was given to {holder.Name}.");
                    continue;
                }

                owners[KeyChords.Folded(gesture)] = command;
                mine.Add(gesture);
            }

            keys[command.Id] = mine;
        }

        if (chosen is not null)
            foreach (var id in chosen.Keys.Where(id => Commands.Find(id) is null).Order(StringComparer.Ordinal))
                dropped.Add($"No command is called {id}; its keys are kept for the Vaktari that wrote them.");

        var ordered = Commands.All
            .SelectMany(command => keys[command.Id].Select(gesture => (gesture, command)))
            .ToList();

        return new Keymap(keys, owners, ordered, dropped);
    }

    // ---- asking ---------------------------------------------------------------

    /// <summary>The keys a command answers to, in the order they were given.</summary>
    public IReadOnlyList<KeyGesture> KeysOf(string id) => _keys.GetValueOrDefault(id) ?? [];

    /// <summary>The command a key runs, or null.</summary>
    public AppCommand? Owner(KeyGesture gesture) => _owners.GetValueOrDefault(KeyChords.Folded(gesture));

    /// <summary>
    /// The command a keystroke runs at this tier, and the key it matched. By
    /// Avalonia's own matching rather than by lookup, so a keystroke matches
    /// here exactly when the same gesture as a KeyBinding would.
    /// </summary>
    public (AppCommand Command, KeyGesture Gesture)? Find(KeyEventArgs e, KeyTier tier)
    {
        foreach (var (gesture, command) in _ordered)
            if (command.Tier == tier && gesture.Matches(e))
                return (command, gesture);

        return null;
    }

    /// <summary>Every key at a tier, with the command it runs.</summary>
    public IEnumerable<(KeyGesture Gesture, AppCommand Command)> Bindings(KeyTier tier)
        => _ordered.Where(b => b.Command.Tier == tier);

    /// <summary>Whether a command answers to exactly its shipped keys.</summary>
    public bool IsDefault(string id) => KeysOf(id).SequenceEqual(Commands.Get(id).DefaultKeys);

    // ---- printing -------------------------------------------------------------

    /// <summary>The keys as the sheet prints them — "Ctrl+F / Ctrl+E" — or
    /// empty for a command with none. Two gestures that print alike are one.</summary>
    public string Readable(string id)
        => string.Join(" / ", KeysOf(id).Select(KeyChords.Readable).Distinct(StringComparer.Ordinal));

    /// <summary>The keys as a label's aside — "ctrl+b or f9".</summary>
    public string Hint(string id)
        => string.Join(" or ", KeysOf(id).Select(KeyChords.Hint).Distinct(StringComparer.Ordinal));

    /// <summary>
    /// Words with the command's keys after them — "Close tab  (ctrl+w)" — or
    /// the words alone when it has none. **A label that names a key nobody can
    /// press is a promise the window breaks**, and a label that leaves out
    /// the key somebody gave it hides the one thing it is for.
    /// </summary>
    public string Labelled(string words, string id)
        => Hint(id) is { Length: > 0 } keys ? $"{words}  ({keys})" : words;
}
