using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Vaktari.Ui;

/// <summary>
/// Focuses a control when it becomes visible.
///
/// Needed because per-pane controls live inside a DataTemplate, so there is no
/// generated field to call Focus() on from code-behind — the same reason the
/// path box handles Enter through a command rather than a KeyDown handler.
/// </summary>
public static class FocusBehavior
{
    public static readonly AttachedProperty<bool> FocusOnVisibleProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FocusOnVisible", typeof(FocusBehavior));

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Hooked", typeof(FocusBehavior));

    public static void SetFocusOnVisible(Control target, bool value)
        => target.SetValue(FocusOnVisibleProperty, value);

    public static bool GetFocusOnVisible(Control target)
        => target.GetValue(FocusOnVisibleProperty);

    /// <summary>
    /// Focuses a control when a bound flag goes true, where
    /// <see cref="FocusOnVisibleProperty"/> waits for it to become visible.
    ///
    /// The search field needs this because it is always visible on the active
    /// side, so there is no appearance to hang the focus on. Ctrl+F set
    /// SidebarViewModel.IsSearching and nothing consumed it: the gesture
    /// revealed the sidebar and left the caret wherever it already was.
    /// </summary>
    public static readonly AttachedProperty<bool> FocusWhenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FocusWhen", typeof(FocusBehavior));

    public static void SetFocusWhen(Control target, bool value)
        => target.SetValue(FocusWhenProperty, value);

    public static bool GetFocusWhen(Control target)
        => target.GetValue(FocusWhenProperty);

    static FocusBehavior()
    {
        HookCommitOnEnter();
        HookLostFocus();

        FocusWhenProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.NewValue is not true) return;

            // Posted rather than immediate: the flag can go true before the
            // control is attached and laid out, and Focus() on a detached
            // control is a no-op that fails silently.
            Dispatcher.UIThread.Post(() =>
            {
                control.Focus();
                if (control is TextBox box) box.SelectAll();
            }, DispatcherPriority.Input);
        });

        FocusOnVisibleProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.NewValue is not true) return;
            if (control.GetValue(HookedProperty)) return;

            control.SetValue(HookedProperty, true);

            // Plain property-changed rather than an observable: Avalonia's
            // Subscribe(Action<T>) lives behind its own reactive extensions,
            // and this needs no extra dependency to say the same thing.
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property != Visual.IsVisibleProperty) return;
                if (e.NewValue is not true) return;

                // Posted: at the instant visibility flips the control is not
                // yet laid out, and focusing an unrealized control silently
                // does nothing.
                Dispatcher.UIThread.Post(() =>
                {
                    control.Focus();

                    // Select the existing text: the box opens pre-filled with
                    // the current path, and without this typing appends to it
                    // instead of replacing it.
                    if (control is TextBox box) box.SelectAll();
                });
            };
        });
    }

    /// <summary>
    /// Commits a LostFocus-triggered binding when Enter is pressed.
    ///
    /// A size box binds on LostFocus so it does not apply "1" on the way to
    /// "14" — but that means Enter would otherwise do nothing, which is the
    /// first thing anyone tries. Moving focus off the box is what commits it.
    /// </summary>
    public static readonly AttachedProperty<bool> CommitOnEnterProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("CommitOnEnter", typeof(FocusBehavior));

    public static void SetCommitOnEnter(TextBox box, bool value)
        => box.SetValue(CommitOnEnterProperty, value);

    public static bool GetCommitOnEnter(TextBox box) => box.GetValue(CommitOnEnterProperty);

    /// <summary>Called from the single static constructor above.</summary>
    private static void HookCommitOnEnter()
    {
        CommitOnEnterProperty.Changed.AddClassHandler<TextBox>((box, args) =>
        {
            if (args.NewValue is not true) return;

            box.KeyDown += (_, e) =>
            {
                if (e.Key is not Key.Enter) return;

                // Focus moves to whatever contains the box, which raises
                // LostFocus and lets the binding write through.
                (box.Parent as Control)?.Focus();
                e.Handled = true;
            };
        });
    }

    /// <summary>
    /// Runs a command when the control loses focus.
    ///
    /// Used by the path box so clicking anywhere else puts the crumbs back.
    /// A text box that stays open after you have moved on is clutter that the
    /// user has to remember to dismiss.
    /// </summary>
    public static readonly AttachedProperty<ICommand?> LostFocusCommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("LostFocusCommand", typeof(FocusBehavior));

    public static void SetLostFocusCommand(Control control, ICommand? value)
        => control.SetValue(LostFocusCommandProperty, value);

    public static ICommand? GetLostFocusCommand(Control control)
        => control.GetValue(LostFocusCommandProperty);

    private static void HookLostFocus()
    {
        LostFocusCommandProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            // Detach before attaching: templates reuse their controls, so
            // subscribing on every rebind would fire the command repeatedly.
            control.LostFocus -= OnLostFocus;

            if (args.NewValue is ICommand) control.LostFocus += OnLostFocus;
        });
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        if (GetLostFocusCommand(control) is not { } command) return;

        // **A control's own context menu is not somewhere else.**
        //
        // An open flyout takes the focus, so "focus left the box" was true of
        // the one gesture that most needs the box to stay put: right-clicking
        // the address bar to reach Cut, Copy and Paste. The path bar's
        // LostFocus command reverts the edit and hides the box, so the field
        // collapsed back to breadcrumbs out from under the menu that had just
        // opened — and whatever had been typed went with it. Right-clicking to
        // paste a path destroyed the field being pasted into.
        if (control.ContextFlyout is { IsOpen: true } flyout)
        {
            // Asked again when the menu closes. Focus normally returns to the
            // control, and then there is nothing to do; if it does not, the
            // control should still lose focus properly rather than be left
            // open and unfocused with no event coming to close it.
            void Reconsider(object? _, EventArgs __)
            {
                flyout.Closed -= Reconsider;

                // Posted: focus is restored as part of closing, and reading it
                // in the handler measures the moment before that happens.
                Dispatcher.UIThread.Post(
                    () => { if (!control.IsFocused) Run(command); },
                    DispatcherPriority.Background);
            }

            flyout.Closed += Reconsider;
            return;
        }

        if (control.ContextMenu is { IsOpen: true }) return;

        Run(command);
    }

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }
}