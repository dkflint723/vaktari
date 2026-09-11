using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.Settings;
using Vaktari.Ui.Input;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One key on a command's row: what it prints, and how to take it off.
/// </summary>
public sealed record KeyChip(KeyGesture Gesture, string Label, string RemoveName, ICommand Remove);

/// <summary>A heading on the keyboard page, and the commands under it.</summary>
public sealed partial class KeyGroup(string name) : ObservableObject
{
    public string Name { get; } = name;

    public ObservableCollection<KeyRow> Rows { get; } = [];

    /// <summary>False while the page's filter leaves none of its rows showing.</summary>
    [ObservableProperty] private bool _isShown = true;
}

/// <summary>
/// One command on the keyboard page: its name, its keys as chips, and the
/// three things that can be done to them — add one, take one off, put them
/// back.
/// </summary>
public sealed partial class KeyRow : ObservableObject
{
    private readonly KeyboardPage _page;

    public KeyRow(KeyboardPage page, AppCommand command, IEnumerable<KeyGesture> keys)
    {
        _page = page;
        Command = command;

        foreach (var key in keys) Keys.Add(Chip(key));

        Keys.CollectionChanged += (_, _) => Changed();
    }

    public AppCommand Command { get; }

    public string Name => Command.Name;

    public ObservableCollection<KeyChip> Keys { get; } = [];

    public IEnumerable<KeyGesture> Gestures => Keys.Select(k => k.Gesture);

    /// <summary>Whether this row answers to anything but its shipped keys —
    /// which is both what the Reset button waits for and what gets written.</summary>
    public bool IsChanged => !Gestures.SequenceEqual(Command.DefaultKeys);

    public bool HasKeys => Keys.Count > 0;

    /// <summary>
    /// Whether a keystroke of this gesture would run this row's command. By
    /// the keymap's own sameness rather than equality: the pad's plus is the
    /// top row's plus to a keystroke, so a page that told them apart would
    /// hand Ctrl and the pad's plus to one command while Zoom in went on
    /// showing Ctrl++ — and the keymap would then give the key to only one of
    /// them.
    /// </summary>
    public bool Holds(KeyGesture gesture) => Gestures.Any(k => KeyChords.Same(k, gesture));

    /// <summary>False while the page's filter hides it.</summary>
    [ObservableProperty] private bool _isShown = true;

    /// <summary>True while the page is waiting for the key this row will get.</summary>
    [ObservableProperty] private bool _isListening;

    /// <summary>What the add button says: its job, or that it is waiting.</summary>
    public string AddLabel => IsListening ? "Press a key…" : "Add key";

    public string AddName => IsListening ? $"Stop listening for a key for {Name}" : $"Add a key to {Name}";

    public string ResetName => $"Put {Name} back to the keys it came with";

    partial void OnIsListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(AddLabel));
        OnPropertyChanged(nameof(AddName));
    }

    /// <summary>Listens for this row's key — or, clicked again while it
    /// listens, stops: the button then says "Press a key…", and clicking it
    /// instead of pressing one is the plainest way to say "never mind".</summary>
    [RelayCommand]
    private void Add()
    {
        if (IsListening) _page.StopListening();
        else _page.Listen(this);
    }

    [RelayCommand]
    private void Reset() => _page.ResetRow(this);

    internal void Put(KeyGesture gesture) => Keys.Add(Chip(gesture));

    /// <summary>Takes off the chip a keystroke of this gesture would run,
    /// whichever of the pad's or the top row's spellings it was given as.</summary>
    internal void Take(KeyGesture gesture)
    {
        if (Keys.FirstOrDefault(k => KeyChords.Same(k.Gesture, gesture)) is { } chip) Keys.Remove(chip);
    }

    internal void Replace(IEnumerable<KeyGesture> keys)
    {
        Keys.Clear();

        foreach (var key in keys) Keys.Add(Chip(key));
    }

    private KeyChip Chip(KeyGesture gesture)
    {
        var printed = KeyChords.Readable(gesture);

        return new KeyChip(
            gesture,
            printed + "  ✕",
            $"Take {printed} off {Name}",
            new RelayCommand(() => _page.RemoveKey(this, gesture)));
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(HasKeys));
    }
}

/// <summary>
/// The Keyboard page of the settings dialog: every command, and the keys that
/// run it, to change.
///
/// **Keys were fixed, and the collisions with other programs' habits could
/// only be explained.** Ctrl+D pins here and deletes in Explorer, Ctrl+B folds
/// the sidebar here and adds a place in Dolphin, and the F1 sheet said so on
/// the line for each — to somebody who had already pressed the key and got
/// the wrong thing. Now the key can be moved.
///
/// **Listening, not typing.** Add key puts the row in a listening state and
/// the next key pressed in the dialog is the key — the settings window hands
/// every keystroke here while a row listens, so Enter and Escape do not save
/// or close the dialog under it. Escape stops listening; a key no command may
/// have is refused with the reason, and listening goes on so another can be
/// tried; a key another command has is offered — Take it, or leave it where
/// it is — because moving it silently would take a key from a command
/// somebody may not be looking at.
///
/// **Only what differs is written.** A row that answers to exactly its
/// shipped keys writes nothing, so a key a later release adds to it reaches
/// this person too. Entries naming a command this build does not have came
/// from a newer Vaktari and are carried through a save untouched.
/// </summary>
public sealed partial class KeyboardPage : ObservableObject
{
    private Dictionary<string, List<string>> _foreign = new(StringComparer.Ordinal);

    public ObservableCollection<KeyGroup> Groups { get; } = [];

    public IEnumerable<KeyRow> Rows => Groups.SelectMany(g => g.Rows);

    /// <summary>
    /// What settings.json asked for that could not be given, one sentence
    /// each, as the keymap worded it when the dialog opened: a word that is
    /// not a key, a key that keeps its own job, a key two commands were given.
    /// **The keymap records these rather than throwing, so a hand-edited file
    /// never stops the window opening — and this is where they are said,
    /// because a key that silently does nothing is the other half of that
    /// bargain.** Empty for a file nobody edited by hand.
    /// </summary>
    public string Unused { get; private set; } = "";

    public bool HasUnused => Unused.Length > 0;

    /// <summary>What the search box holds: part of a command's name, or a key
    /// as the sheet or a label spells it.</summary>
    [ObservableProperty] private string _filter = "";

    /// <summary>The row waiting for its key, if one is.</summary>
    [ObservableProperty] private KeyRow? _listening;

    /// <summary>The line under the search box: what to press, why a key was
    /// refused, or what just happened.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>A key pressed for the listening row that another row holds,
    /// waiting on Take it or Keep it there.</summary>
    [ObservableProperty] private KeyGesture? _offered;

    [ObservableProperty] private KeyRow? _offeredFrom;

    /// <summary>
    /// Whether this page is the one on screen — bound to its tab. **Leaving
    /// the page stops a row listening**, because the window hands every key to
    /// a listening row: one left listening would take the keys typed into the
    /// next page's boxes, on a page nobody could see it from.
    /// </summary>
    [ObservableProperty] private bool _isOpen;

    public bool HasStatus => Status.Length > 0;

    public bool IsOffering => Offered is not null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnOfferedChanged(KeyGesture? value) => OnPropertyChanged(nameof(IsOffering));

    partial void OnListeningChanged(KeyRow? oldValue, KeyRow? newValue)
    {
        if (oldValue is not null) oldValue.IsListening = false;
        if (newValue is not null) newValue.IsListening = true;
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value) StopListening();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    // ---- in and out -----------------------------------------------------------------

    /// <summary>Every command, with the keys these settings give it.</summary>
    public void Load(KeyboardSettings? settings)
    {
        var keymap = Keymap.From(settings);

        _foreign = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (settings?.Bindings is { } bindings)
            foreach (var (id, keys) in bindings)
                if (Commands.Find(id) is null)
                    _foreign[id] = keys?.ToList() ?? [];

        Unused = string.Join("\n", keymap.Dropped);
        OnPropertyChanged(nameof(Unused));
        OnPropertyChanged(nameof(HasUnused));

        Groups.Clear();

        foreach (var command in Commands.All)
        {
            var group = Groups.FirstOrDefault(g => g.Name == command.Group);

            if (group is null)
            {
                group = new KeyGroup(command.Group);
                Groups.Add(group);
            }

            group.Rows.Add(new KeyRow(this, command, keymap.KeysOf(command.Id)));
        }

        Listening = null;
        Offered = null;
        OfferedFrom = null;
        Status = "";

        ApplyFilter();
    }

    /// <summary>
    /// What gets written: the rows that differ from their shipped keys, in the
    /// spelling settings.json holds, and whatever a newer Vaktari left.
    /// </summary>
    public Dictionary<string, List<string>> Collect()
    {
        var written = new Dictionary<string, List<string>>(_foreign, StringComparer.Ordinal);

        foreach (var row in Rows.Where(r => r.IsChanged))
            written[row.Command.Id] = row.Gestures.Select(KeyChords.Store).ToList();

        return written;
    }

    // ---- listening -------------------------------------------------------------------

    public void Listen(KeyRow row)
    {
        Offered = null;
        OfferedFrom = null;
        Listening = row;
        Status = $"Press the key for “{row.Name}”. Escape stops listening.";
    }

    /// <summary>No row listening and nothing on offer, as though Escape had
    /// been pressed.</summary>
    public void StopListening()
    {
        if (Listening is null && Offered is null) return;

        Listening = null;
        Offered = null;
        OfferedFrom = null;
        Status = "";
    }

    /// <summary>
    /// A key pressed while a row listens. True when the page took it, which
    /// while a row listens is every key — the dialog must not act on Enter or
    /// Escape meant for the row.
    /// </summary>
    public bool Offer(Key key, KeyModifiers modifiers)
    {
        if (Listening is not { } row) return false;

        // Half a key: wait for the rest of it.
        if (KeyChords.IsModifierKey(key) || key == Key.None) return true;

        // A key pressed instead of answering an offer withdraws it, whatever
        // that key turns out to be: the two buttons must never answer for a
        // key the line above them no longer names.
        Offered = null;
        OfferedFrom = null;

        if (key == Key.Escape && modifiers == KeyModifiers.None)
        {
            StopListening();
            return true;
        }

        var gesture = KeyChords.From(key, modifiers);

        if (KeyChords.Refusal(gesture, row.Command.Tier) is { } why)
        {
            Status = why;
            return true;
        }

        if (row.Holds(gesture))
        {
            Listening = null;
            Status = $"“{row.Name}” already answers to {KeyChords.Readable(gesture)}.";
            return true;
        }

        if (Rows.FirstOrDefault(r => !ReferenceEquals(r, row) && r.Holds(gesture)) is { } holder)
        {
            Offered = gesture;
            OfferedFrom = holder;
            Status = $"{KeyChords.Readable(gesture)} runs “{holder.Name}”. Take it for “{row.Name}”?";
            return true;
        }

        row.Put(gesture);
        Listening = null;
        Status = $"“{row.Name}” now answers to {KeyChords.Readable(gesture)}.";
        return true;
    }

    /// <summary>The offered key moves to the listening row.</summary>
    [RelayCommand]
    private void TakeIt()
    {
        if (Listening is not { } row || Offered is not { } gesture || OfferedFrom is not { } holder) return;

        holder.Take(gesture);
        row.Put(gesture);

        Status = $"{KeyChords.Readable(gesture)} moved from “{holder.Name}” to “{row.Name}”.";
        Listening = null;
        Offered = null;
        OfferedFrom = null;
    }

    /// <summary>The offered key stays where it was, and listening stops.</summary>
    [RelayCommand]
    private void KeepIt()
    {
        if (Offered is { } gesture && OfferedFrom is { } holder)
            Status = $"{KeyChords.Readable(gesture)} stays with “{holder.Name}”.";

        Listening = null;
        Offered = null;
        OfferedFrom = null;
    }

    // ---- taking keys off, and putting them back ---------------------------------------

    internal void RemoveKey(KeyRow row, KeyGesture gesture)
    {
        row.Take(gesture);

        Status = row.HasKeys
            ? $"{KeyChords.Readable(gesture)} no longer runs “{row.Name}”."
            : $"“{row.Name}” has no key now. It is still in the command palette.";
    }

    /// <summary>
    /// A row back to its shipped keys — except any another row has been given
    /// since, which stay with that row and are named: taking a key back from
    /// a command somebody is not looking at is not what Reset on this one
    /// says.
    /// </summary>
    internal void ResetRow(KeyRow row)
    {
        var elsewhere = row.Command.DefaultKeys
            .Where(g => Rows.Any(r => !ReferenceEquals(r, row) && r.Holds(g)))
            .ToList();

        row.Replace(row.Command.DefaultKeys.Except(elsewhere));

        Status = elsewhere.Count == 0
            ? $"“{row.Name}” is back to the keys it came with."
            : $"“{row.Name}” is back to its keys, except {string.Join(" and ", elsewhere.Select(KeyChords.Readable))}, "
              + "which another command has now.";
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var row in Rows) row.Replace(row.Command.DefaultKeys);

        Listening = null;
        Offered = null;
        OfferedFrom = null;
        Status = "Every key is back to what Vaktari ships with. Nothing is saved until you press Save.";
    }

    // ---- finding ---------------------------------------------------------------------

    /// <summary>
    /// A row shows when every word of the filter is in its name, or when the
    /// filter reads as one of its keys — "ctrl+b" finds whatever Ctrl+B runs,
    /// which is the question somebody arriving from another program asks.
    /// </summary>
    private void ApplyFilter()
    {
        var query = Filter.Trim();
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var asKey = KeyChords.Parse(query);

        foreach (var group in Groups)
        {
            foreach (var row in group.Rows)
                row.IsShown = query.Length == 0
                              || words.All(w => row.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
                              || (asKey is not null && row.Holds(asKey));

            group.IsShown = group.Rows.Any(r => r.IsShown);
        }
    }
}
