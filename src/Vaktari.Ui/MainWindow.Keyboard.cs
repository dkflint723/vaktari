using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Vaktari.Ui.Input;
using Vaktari.Core.Session;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Every key this window answers: the handler itself, the keys that answer
/// anywhere as KeyBindings, the two tiers the handler asks the keymap about,
/// and the few commands that are the window's own rather than a view model's.
///
/// **The handler and the keymap it asks were five thousand lines apart.**
/// <see cref="DispatchKeymap"/> lived here and said so in its own comment —
/// "called from the two places in OnWindowKeyDown" — while OnWindowKeyDown sat
/// at the far end of MainWindow.axaml.cs. A gate in one file and the thing it
/// gates in another is the arrangement roadmap 22 keeps finding, and the two
/// halves of a single keystroke are the plainest case of it.
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

    // ---- the handler itself -----------------------------------------------------

    /// <summary>
    /// The narrow set of keys that must be claimed before anything else sees
    /// them. Deliberately tiny: a tunnel handler runs ahead of every control in
    /// the window, so anything added here is taken away from all of them.
    /// </summary>
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        // The box on the row answers Enter, Escape and Tab. On the TUNNEL for
        // the reason the path box below is: keyboard navigation claims Tab
        // before any bubble handler runs, so by then focus has already left the
        // box. The other two come with it rather than being hung on the
        // control, because the control is built by a DataTemplate and there is
        // no field here to subscribe to.
        if (_prompt is PromptMode.Rename && RenameHasTheKeyboard())
        {
            OnPromptKeyDown(sender, e);

            if (e.Handled) return;
        }

        if (PageCompactListing(e)) return;

        if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None) return;

        // Only while the path box is open and focused. Tab keeps its ordinary
        // meaning everywhere else, including the other text boxes.
        if (_shell.ActiveTab is not { IsPathEditing: true } pane) return;
        if (FocusManager?.GetFocusedElement() is not TextBox box) return;

        pane.CompletePathCommand.Execute(null);

        // Caret to the end, and the selection collapsed there, so the next
        // keystroke continues the path instead of landing wherever the caret
        // happened to sit — or worse, replacing a selection the text
        // replacement left behind.
        //
        // POSTED rather than set inline: the command assigns PathText, and the
        // binding has to propagate to this TextBox before its Text is the new
        // value. Setting CaretIndex now would measure against the OLD text and
        // get clamped to the wrong place.
        Dispatcher.UIThread.Post(() =>
        {
            var end = box.Text?.Length ?? 0;

            box.CaretIndex = end;
            box.SelectionStart = end;
            box.SelectionEnd = end;
        }, DispatcherPriority.Background);

        e.Handled = true;
    }

    /// <summary>
    /// Jump-to-letter in the listing. Bubble, so any control that wants the
    /// character has already had it.
    /// </summary>
    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;

        // Never while typing somewhere real — the filter bar, the path box and
        // the prompt bar are all TextBoxes, and stealing their characters would
        // be a far worse bug than not having type-ahead.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        // Control characters are not a search: Escape, Backspace and friends
        // arrive here too and have their own meanings.
        if (char.IsControl(e.Text[0])) return;

        if (_shell.ActiveTab is not { } pane) return;

        pane.TypeAhead(e.Text);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell is null) return;

        // The prompt owns the keyboard while it is open.
        if (IsConfirming)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ConfirmPrompt();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClosePrompt();
            }
            return;
        }

        if (_prompt is PromptMode.Rename)
        {
            // **An inline editor can lose the row it is drawn on.** The box is
            // built by the listing's item template, so scrolling its row out of
            // view unrealizes it — and nothing is then holding the keyboard,
            // while this guard would go on refusing every shortcut in the
            // window with nothing on screen to say why. A key pressed with the
            // keyboard out of the box ends the rename and is then handled as it
            // always was.
            if (RenameHasTheKeyboard()) return;

            ClosePrompt();
        }

        // The guarded commands: F6 between the regions, and the filter,
        // new-folder, hidden-files, pin and search keys. See KeyTier.Guarded.
        //
        // **Above the text-box guard, deliberately.** Leaving a text box is
        // most of what F6 is FOR: behind that guard the second step of the
        // cycle could never be taken, because the address bar is a text box
        // and F6 pressed in it would be swallowed. Three consequences follow,
        // each an accepted cost rather than an oversight — F6 from the path bar
        // discards a half-typed path, which is what clicking away already does;
        // F6 from the search field closes it when the draft is empty, which is
        // that field's own lost-focus rule; and F6 from the filter opens the
        // path bar with the filter text intact. Ctrl+F from the address bar
        // moving to the search field, and Ctrl+I from inside the filter putting
        // it away, are answered from inside a box for the same reason.
        //
        // **And above the sidebar's own keys**, or the panel F6 delivers you to
        // would be one F6 could not take you out of. None of these commands can
        // be given a key the sidebar claims: those are the arrows, Home, End
        // and the menu key, which KeyChords.IsReserved keeps from every command.
        //
        // The rename box is a text box too, and none of these may pull the
        // keyboard out from under a name being typed — but that is already
        // answered by the rename guard higher up, which returns before this is
        // reached.
        if (DispatchKeymap(e, Input.KeyTier.Guarded)) return;

        // The sidebar answers its own keys while it has the keyboard.
        //
        // **F6 delivered you to a panel you could not move in.** Up, Down, Home
        // and End were unbound anywhere in the application, so from a place row
        // the only way on was Tab through every button in the panel — and the
        // keys that WERE bound went on acting on the listing: Delete trashed
        // whatever was selected in a folder that no longer had the keyboard.
        //
        // **After F6 and above the text-box guard.** After F6, or the panel
        // this delivers you to would be one F6 could not take you out of; above
        // the guard is free, because there is no TextBox anywhere in the
        // sidebar, and putting it there keeps the two region branches together.
        if (CurrentRegion() == Input.KeyboardRegion.Sidebar)
        {
            // Written out here rather than mapped in SidebarWalk, one key to a
            // line: the shortcuts sheet is cross-checked against the keys this
            // handler claims, and it reads those claims from `e.Key == Key.X`.
            // A map behind a helper is a key nothing can see is bound.
            //
            // Modifiers must be absent rather than ignored — Shift+Down extends
            // a selection in a listing and means nothing here, and swallowing it
            // would take it from anything that later wants it.
            Input.SidebarStep? step =
                e.KeyModifiers != KeyModifiers.None ? null
                : e.Key == Key.Down ? Input.SidebarStep.Next
                : e.Key == Key.Up ? Input.SidebarStep.Previous
                : e.Key == Key.Home ? Input.SidebarStep.First
                : e.Key == Key.End ? Input.SidebarStep.Last
                : null;

            if (step is { } move)
            {
                e.Handled = true;
                MoveInSidebar(move);
                return;
            }

            // The Menu key opens the menu for the ROW, not for the listing.
            // Avalonia raises ContextRequested for a right-click and for
            // nothing else, so this is the only keyboard route into a place's
            // own menu — and the listing's menu, which is what these two keys
            // opened from here, acts on files that are not on screen.
            if (e.Key == Key.Apps
                || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
            {
                e.Handled = true;

                if (FocusManager?.GetFocusedElement() is Interactive row)
                    row.RaiseEvent(new ContextRequestedEventArgs());

                return;
            }

            // Refused rather than passed on. See SidebarWalk.ActsOnTheListing:
            // the selection these act on is not what the keyboard is pointing
            // at, and Delete is the one that costs files.
            if (Input.SidebarWalk.ActsOnTheListing(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
                return;
            }
        }

        // Any focused text box owns the keyboard. Checking the type rather
        // than named controls, because the path and filter boxes now live
        // inside a per-pane template and have no generated fields — and it
        // is the more honest rule anyway. Escape and Enter inside those
        // boxes are handled by their own KeyBindings in the markup.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        // The listing's own commands: preview, rename, delete, the clipboard,
        // undo, select and properties. Behind the text-box guard above, so a
        // focused box keeps every key a text cursor owns — see KeyTier.Listing
        // for what each of them did as a window KeyBinding.
        if (DispatchKeymap(e, Input.KeyTier.Listing)) return;

        // **The Menu key and Shift+F10 open the context menu**, which nothing
        // did: there was no Key.Apps handler, no F10, and no ContextRequested
        // raise anywhere — the only handler for that event SUPPRESSES the menu
        // after a right-drag. Avalonia does not provide this for free, so the
        // whole menu was mouse-only, and every keyboard route into it that the
        // shortcuts sheet implies simply did not exist.
        if (e.Key == Key.Apps
            || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        {
            e.Handled = true;

            // **The keyboard on the address bar got the LISTING's menu.** A
            // crumb is an ordinary focusable Button, so Tab reaches it and both
            // these keys fell straight through to the listing — offering Cut,
            // Copy and Delete for files that are not what the keyboard is
            // pointing at, and leaving the bar's own two rows reachable by
            // mouse alone. The same treatment the sidebar already gets one
            // screen up, and for the same reason: Avalonia raises
            // ContextRequested for a right-click and for nothing else, so
            // raising it here is the only keyboard route into a flyout that
            // hangs off a control inside a template.
            if (FocusManager?.GetFocusedElement() is Interactive onTheBar
                && InAddressBar(onTheBar))
            {
                onTheBar.RaiseEvent(new ContextRequestedEventArgs());
                return;
            }

            OpenListingMenu();
            return;
        }

        // **Escape did not close the preview**, which is the one thing on
        // screen that is drawn OVER the listing rather than beside it — and the
        // key everybody tries on a thing that covers something else. Space
        // reopens it, but Space is also how you got here, and a key that only
        // toggles is no help to someone who does not know that.
        //
        // First, and handled: the topmost dismissible thing goes first, and the
        // clear below is not something to do on the way past. Escape pressed to
        // put a preview away should not also throw away a filter the person is
        // still using.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
            && _shell.ActiveTab is { IsPreviewVisible: true } previewing)
        {
            e.Handled = true;
            previewing.TogglePreview();
            return;
        }

        // Escape abandons a pending cut, which the F1 sheet has promised all
        // along: "Escape — Clear the filter, and any pending cut".
        //
        // **It was reachable only from inside the filter box.** ClearFilter is
        // bound in exactly one place, that TextBox's own KeyBindings, and the
        // box is hidden unless the filter is open — so with a cut pending and
        // no filter showing, the key did nothing and the sheet was lying.
        // Not marked handled: Escape has other meanings further down, and this
        // one is harmless to whichever of them the user meant.
        //
        // DismissInListing rather than ClearFilter: the latter also closes the
        // bar once the text is empty, which is right from inside the box and
        // wrong from out here — it took away a bar the startup setting had
        // deliberately opened.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            _shell.ActiveTab?.DismissInListing();

        // Ctrl+1..9 jumps to a tab, browser-style.
        if (e.KeyModifiers == KeyModifiers.Control &&
            e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            e.Handled = true;
            _shell.SelectTabByIndex(e.Key - Key.D1);
            return;
        }

        // No Ctrl+arrow zoom. It was tried and removed: this handler is on the
        // bubble phase, so a focused ListBox — which is the normal state —
        // takes arrow keys first and moves the selection instead. Winning the
        // keystroke would mean tunnelling and stealing a key the listing has a
        // legitimate claim to. Ctrl+wheel and Ctrl +/- cover it.

        // Tab moves between sides rather than traversing focus, matching
        // Dolphin. Only when split, so it keeps its normal meaning otherwise —
        // and never while typing, or it would jump panes mid-edit.
        if (e.Key == Key.Tab && _shell.IsSplit && e.KeyModifiers == KeyModifiers.None
            && AppSettings.Current.General.TabSwitchesSplitPanes
            && FocusManager?.GetFocusedElement() is not TextBox)
        {
            e.Handled = true;
            _shell.FocusOtherPaneCommand.Execute(null);

            // **And move the keyboard with it.** The command only reassigns
            // which group is active, so focus stayed in the old ListBox: arrows
            // went on moving the OLD pane's selection while Enter, Delete and
            // Ctrl+C/X/V resolve through ActiveTab and acted on the NEW one.
            // Arrow to a file, press Delete, and the wrong file went to the bin.
            //
            // Posted rather than called: the listing for the other side is
            // chosen by ActiveTab, and that binding has not been applied yet at
            // this point in the keystroke.
            Dispatcher.UIThread.Post(() => ActiveListing()?.Focus());
            return;
        }

        if (_shell.ActiveTab is not { } pane) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = pane.OpenSelectedAsync();
                break;

            // Right opens the folder the keyboard is on without leaving this
            // one; Left shuts it again. Dolphin's keys for the same feature,
            // and the only route to it that is not a 16px triangle.
            //
            // **Handled only when it actually does something.** These two keys
            // belong to the grid and compact layouts, where the wrap panel
            // moves the selection sideways with them, and to any text box that
            // has the keyboard — the guard above has already returned for that
            // one. Left on a row that is not open, or either key in a layout
            // that has no triangles, falls straight through.
            //
            // Modifiers spelled out rather than ignored: Alt+Left is Back.
            case Key.Right when e.KeyModifiers == KeyModifiers.None:
                e.Handled = TurnExpansion(pane, open: true);
                break;

            case Key.Left when e.KeyModifiers == KeyModifiers.None:
                e.Handled = TurnExpansion(pane, open: false);
                break;

            // **Backspace answered Explorer's habit and nobody else's.** It
            // went Back through history, full stop — so somebody who learned
            // the key in Dolphin, where it goes to the parent folder, pressed
            // it at the bottom of a deep tree and was thrown to wherever they
            // had been ten minutes ago. A key that does nothing is a puzzle; a
            // key that confidently does the other thing is a wrong turn you
            // then have to undo.
            //
            // A preference rather than a choice made for everybody, because
            // both habits are real and neither is wrong. Back is the default:
            // it is what shipped, and it is what the larger audience expects.
            //
            // Read at the keystroke rather than captured at startup, so the
            // setting takes effect the moment Save is pressed. Alt+Left and
            // Alt+Up are untouched by this and still do their own jobs either
            // way, so flipping it costs no route.
            case Key.Back:
                e.Handled = true;

                _ = AppSettings.Current.Navigation.BackspaceGoesUp
                    ? pane.GoUpAsync()
                    : pane.GoBackAsync();

                break;
        }
    }
    /// <summary>
    /// Opens the folder the keyboard is on without leaving this one, or shuts
    /// it again — and says whether it did anything.
    ///
    /// **False is the answer that gives the key back.** Left and Right already
    /// mean something in the grid and compact layouts, where the wrap panel
    /// moves the selection sideways with them, so a key claimed here whenever
    /// it was pressed would take that away. It is claimed only for the press
    /// that actually turns a triangle: the right key, on a folder, in the one
    /// layout that draws them, in a state the press would change.
    /// </summary>
    internal static bool TurnExpansion(ViewModels.PaneViewModel pane, bool open)
    {
        if (!pane.IsDetailsView || !pane.CanExpandRows) return false;

        if (pane.SelectedEntry is not { IsDirectory: true } row) return false;

        // Already the way it was asked to be. Right on an open folder is a
        // keystroke Dolphin spends moving into the first child; here it is left
        // alone rather than given a second meaning nothing announces.
        if (pane.IsExpanded(row.FullPath) == open) return false;

        _ = pane.ToggleExpandAsync(row);

        return true;
    }

    /// <summary>
    /// Pages the compact listing sideways.
    ///
    /// **On the TUNNEL phase, because something else was claiming these keys and
    /// doing nothing with them.** Compact disables vertical scrolling, so the
    /// ScrollViewer cannot act on PageUp/PageDown — yet mapping them inside the
    /// panel changed nothing, so the key was never reaching it. Tunnelling
    /// settles it without needing to know who was eating them: nothing
    /// downstream gets the chance.
    ///
    /// **Moves the VIEW, not the selection**, which is what Page already does in
    /// the grid — there the ScrollViewer pages the viewport and leaves the cursor
    /// where it was, and the user has said that feels right. Compact behaving
    /// differently would be the odd one out.
    /// </summary>
    private bool PageCompactListing(KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown)) return false;
        if (e.KeyModifiers != KeyModifiers.None) return false;

        // Never while typing — a path box or the rename prompt owns its own keys.
        if (FocusManager?.GetFocusedElement() is TextBox) return false;

        if (_shell.ActiveTab is not { View: ViewMode.Compact }) return false;
        if (ActiveListing() is not { } list || Scroller(list) is not { } scroller)
            return false;

        // A viewport less a sliver, so the column you were reading stays on
        // screen as an anchor rather than vanishing off the edge.
        var page = Math.Max(1, scroller.Viewport.Width - 48);
        var step = e.Key == Key.PageDown ? page : -page;

        var limit = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);

        scroller.Offset = scroller.Offset.WithX(
            Math.Clamp(scroller.Offset.X + step, 0, limit));

        // Claimed either way. At the end of the extent the key has still been
        // dealt with, and letting it fall through hands it back to whatever was
        // silently swallowing it before.
        e.Handled = true;
        return true;
    }
}
