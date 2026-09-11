using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The window's side of the keymap: the keys that answer anywhere, the two
/// places the key handler asks the keymap, and the few commands that are the
/// window's own rather than a view model's.
/// </summary>
public partial class MainWindow : ICommandHost
{
    /// <summary>The KeyBindings this window made from the keymap, so a
    /// reinstall takes out exactly those and nothing a control added.</summary>
    private readonly List<KeyBinding> _keymapBindings = [];

    private EventHandler? _onKeymapChanged;

    // ---- the command host -------------------------------------------------------

    ShellViewModel ICommandHost.Shell => _shell;

    bool ICommandHost.TypingInABox => FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>Through the ListBox rather than the view model: filling the
    /// bound collection row by row fires a change per file, and each one
    /// refreshes the details panel and recomputes the summary.</summary>
    void ICommandHost.SelectAll()
    {
        if (ActiveListing() is { } list && _shell.ActiveTab is { } pane) SelectWholeFolder(list, pane);
    }

    void ICommandHost.SelectNone() => SelectNone();

    void ICommandHost.InvertSelection() => InvertSelection();

    void ICommandHost.DeletePermanently()
    {
        if (_shell.ActiveTab is { } pane) PermanentlyDelete(pane);
    }

    /// <summary>
    /// **F6 only ever went one place.** Explorer cycles three regions and
    /// Dolphin's F6 is Replace Location; here it put the keyboard in the
    /// listing and did nothing else — so pressed from the listing, which is
    /// where it had just put you, it did nothing at all.
    /// </summary>
    void ICommandHost.NextRegion() => GoToRegion(FocusCycle.Next(CurrentRegion(), SidebarShowing));

    // ---- the keys that answer anywhere ------------------------------------------

    /// <summary>
    /// Puts the keymap's anywhere-keys on the window as KeyBindings.
    ///
    /// **KeyBindings rather than a handler of the window's own**, because
    /// that is what these always were and the timing is the point: Avalonia
    /// checks every KeyBindings list from the focused control up to the window
    /// before the key is routed at all, so F5, Ctrl+T and the rest reach the
    /// window from inside the address bar and through an open rename box. A
    /// tunnel handler runs later than that and would have changed which keys
    /// a focused control gets to see first.
    /// </summary>
    private void InstallKeymap()
    {
        foreach (var binding in _keymapBindings) KeyBindings.Remove(binding);

        _keymapBindings.Clear();

        foreach (var (gesture, command) in Keymap.Current.Bindings(KeyTier.Anywhere))
        {
            var binding = new KeyBinding
            {
                Gesture = gesture,
                Command = new Bound(this, command, gesture),
            };

            _keymapBindings.Add(binding);
            KeyBindings.Add(binding);
        }
    }

    private void WatchKeymap()
    {
        _onKeymapChanged = (_, _) => InstallKeymap();
        Keymap.Changed += _onKeymapChanged;
    }

    /// <summary>The keymap is one per application and outlives every window,
    /// so a closed window has to let go of it or it goes on being rebuilt —
    /// and kept alive — by every save.</summary>
    private void UnwatchKeymap()
    {
        if (_onKeymapChanged is not null) Keymap.Changed -= _onKeymapChanged;
    }

    /// <summary>
    /// One anywhere-key, as the command a KeyBinding runs.
    ///
    /// **A focused box keeps the keys it edits with.** Avalonia's KeyBinding
    /// takes the keystroke only when the command says it can run, so saying
    /// no to an editing key while a box has the keyboard hands the key on to
    /// the box. None of the shipped anywhere-keys is one; this is for the key
    /// somebody chooses — New tab on Ctrl+C opens a tab from the listing and
    /// still copies in the address bar.
    /// </summary>
    private sealed class Bound(ICommandHost host, AppCommand command, KeyGesture gesture) : ICommand
    {
        // Never raised: a KeyBinding asks at the keystroke and caches nothing.
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter)
            => !(host.TypingInABox && KeyChords.IsTextEditing(gesture)) && command.CanRun(host);

        public void Execute(object? parameter) => command.Run(host);
    }

    // ---- the keys the handler asks about ------------------------------------------

    /// <summary>
    /// Runs the command a keystroke is bound to at this tier, if any, and says
    /// whether it did. Called from the two places in <c>OnWindowKeyDown</c>
    /// where those commands used to be case labels — see <see cref="KeyTier"/>
    /// for what each place guards.
    ///
    /// The keystroke is claimed even when the command has nothing to do, as
    /// the case labels claimed it: a key that means Rename must not fall
    /// through to something else because nothing was selected.
    /// </summary>
    private bool DispatchKeymap(KeyEventArgs e, KeyTier tier)
    {
        if (Keymap.Current.Find(e, tier) is not var (command, gesture)) return false;

        // A box keeps its editing keys. The guarded commands are answered
        // above the text-box guard on purpose, and a key somebody chose for
        // one of them must not become a key the address bar cannot type with.
        if (tier == KeyTier.Guarded
            && FocusManager?.GetFocusedElement() is TextBox
            && KeyChords.IsTextEditing(gesture))
            return false;

        // **Not while a name is being typed.** Space is part of a filename far
        // more often than it is a shortcut: "new folder" toggled the preview on
        // the fourth keystroke and threw the prefix away. Left unhandled, so
        // the type-ahead handler downstream gets it.
        if (tier == KeyTier.Listing
            && KeyChords.ProducesText(gesture)
            && _shell.ActiveTab?.IsTypeAheadActive == true)
            return false;

        e.Handled = true;
        command.Run(this);
        return true;
    }
}
