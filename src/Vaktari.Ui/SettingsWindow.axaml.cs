using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);

        // **While a row on the Keyboard page listens, every key is the key.**
        // Taken, so Save and Cancel — which answer an Enter and an Escape
        // nobody else took — never see one meant for the row. And taken on
        // the tunnel, because the Add key button that started the listening
        // usually still has the keyboard, and a focused button answers Enter
        // itself before a key bubbles up to the window: moved to the bubble,
        // the second Enter pressed Add key again and the row stopped
        // listening (KeyboardPageTests). Here, reading the model at the
        // keystroke, rather than in the constructor that is handed one,
        // because the dialog is also built bare and given its model
        // afterwards. KeyboardPage.Offer decides, and takes nothing while
        // no row listens; this only ever marks a key handled, never clears one
        // something else on the tunnel already took. The one exception is the
        // few keys that reach Take it and Keep it there while a key is on
        // offer — see AnswersTheOffer.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not SettingsViewModel model) return;

            if (model.Keyboard.IsOffering && AnswersTheOffer(e)) return;

            if (model.Keyboard.Offer(e.Key, e.KeyModifiers))
                e.Handled = true;

            // A key the listening row took is handled by now, so this one
            // test keeps a page turn from stealing it.
            if (!e.Handled && PageStep(e) is var step and not 0)
            {
                e.Handled = true;
                TurnPage(model, step);
            }
        }, RoutingStrategies.Tunnel);

        // **The help was the mouse's only.** Each setting's explanation is its
        // tooltip, and a tooltip opens on hover — so somebody working this
        // dialog from the keyboard, who can see it, could not reach any of
        // it. A screen reader has it as HelpText; a sighted keyboard user now
        // has it too, opened when Tab or an arrow brings a control that has
        // help the keyboard, and closed when the keyboard moves on. Not on a
        // click: the pointer already has hover for that.
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is Control control
                && e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional
                && Avalonia.Automation.AutomationProperties.GetHelpText(control) is { Length: > 0 })
            {
                // **Under the control, not at the pointer.** A tooltip's
                // default placement is wherever the mouse happens to be, so
                // help opened from the keyboard was drawn by a pointer parked
                // in a corner, far from the box it explains.
                ToolTip.SetPlacement(control, PlacementMode.Bottom);
                ToolTip.SetIsOpen(control, true);
            }
        });

        AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is not Control control) return;

            if (ToolTip.GetIsOpen(control)) ToolTip.SetIsOpen(control, false);

            // Hover goes back to the pointer, as everywhere else.
            control.ClearValue(ToolTip.PlacementProperty);
        });

        // **The offer's answers take the keyboard when they appear**, rather
        // than leaving it on Add key where the keystroke that made the offer
        // started. Posted, because the line the buttons sit on is shown by
        // the status the offer writes AFTER it makes the offer, and a control
        // inside a hidden one refuses focus. Tab, as the method, so the
        // focus ring is drawn: this is the keyboard's route in.
        OfferAnswers.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;

            if (OfferAnswers.IsVisible)
            {
                // Where it is taken from, to be given back — see
                // GiveBackAfterOffer.
                _beforeOffer = (
                    FocusManager?.GetFocusedElement() as Control,
                    (DataContext as SettingsViewModel)?.Keyboard.Listening);

                Dispatcher.UIThread.Post(() => TakeItButton.Focus(NavigationMethod.Tab));
            }
            else
            {
                GiveBackAfterOffer();
            }
        };
    }

    /// <summary>
    /// What had the keyboard when an offer appeared and took it, and which row
    /// was listening — normally that row's own Add key. Cleared once given back.
    /// </summary>
    private (Control? Focused, KeyRow? Row)? _beforeOffer;

    /// <summary>
    /// **Answering the offer from the keyboard left the keyboard nowhere.**
    /// The answers took focus when the offer appeared, and every way the offer
    /// ends — Take it, Keep it there, Escape, or any other key pressed, which
    /// withdraws it — hides them with focus still on one. Nothing put it back,
    /// so it fell out of the page and the next Tab started again from the top
    /// of the dialog rather than from the row being edited; before the answers
    /// took the keyboard it had simply stayed on that row's Add key.
    ///
    /// Given back where it was taken from, when that is still there to take
    /// it, and otherwise to the Add key of the row that was listening. Only
    /// when the answers still had it: somebody who clicked elsewhere has put
    /// it where they wanted it. Posted, because hiding the buttons is what
    /// moves focus off them, and that has to have happened first.
    /// </summary>
    private void GiveBackAfterOffer()
    {
        if (_beforeOffer is not var (focused, row)) return;

        _beforeOffer = null;

        Dispatcher.UIThread.Post(() =>
        {
            var now = FocusManager?.GetFocusedElement();

            var stranded = now is null
                           || ReferenceEquals(now, this)
                           || ReferenceEquals(now, TakeItButton)
                           || ReferenceEquals(now, KeepItButton)
                           || now is Visual { IsEffectivelyVisible: false };

            if (!stranded) return;

            var back = focused is { IsEffectivelyVisible: true } && focused.IsAttachedToVisualTree()
                       && !ReferenceEquals(focused, TakeItButton) && !ReferenceEquals(focused, KeepItButton)
                ? focused
                : AddKeyOf(row);

            back?.Focus(NavigationMethod.Tab);
        });
    }

    /// <summary>The Add key button on <paramref name="row"/>'s line of the
    /// Keyboard page, if it is on screen.</summary>
    private Button? AddKeyOf(KeyRow? row)
        => row is null
            ? null
            : this.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => ReferenceEquals(b.DataContext, row)
                                     && ReferenceEquals(b.Command, row.AddCommand)
                                     && b.IsEffectivelyVisible);

    /// <summary>
    /// **Take it and Keep it there could not be reached from the keyboard.**
    /// Every key goes to the listening row, and an offer leaves the row
    /// listening — so Tab toward the buttons was a key pressed instead of an
    /// answer, which withdrew the offer and hid them before focus could land;
    /// and Enter or Space on one was the same. Escape did Keep it there's job;
    /// Take it had no keyboard route at all.
    ///
    /// So while a key is on offer, these few go where they would on any other
    /// page: Tab and Shift+Tab move focus, and Enter and Space press the
    /// answer that has it. Only on the answers — from anywhere else Enter and
    /// Space are still keys pressed for the row, refused with the reason, and
    /// any other key still withdraws the offer, as before.
    /// </summary>
    private bool AnswersTheOffer(KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
            return e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift;

        return e.Key is Key.Enter or Key.Space
               && e.KeyModifiers == KeyModifiers.None
               && FocusManager?.GetFocusedElement() is { } focused
               && (ReferenceEquals(focused, TakeItButton) || ReferenceEquals(focused, KeepItButton));
    }

    /// <summary>
    /// +1 for the next page, -1 for the previous, 0 for a key that is not a
    /// page turn.
    ///
    /// **Ctrl+Tab moved focus like Tab, and there was no key for the next
    /// page at all.** A tabbed dialog answers Ctrl+Tab and Ctrl+PageDown with
    /// the next page and their Shift and PageUp partners with the previous one
    /// — in Windows' property sheets, in KDE's, in every browser — and here
    /// the only way between pages from the keyboard was to Tab back up to the
    /// strip and arrow along it.
    ///
    /// On the tunnel, before the focused control: a text box or a list would
    /// otherwise take PageDown for itself, and Avalonia's own Tab handling
    /// ignores Ctrl and would move focus instead. After the Keyboard page's
    /// listening row, which is given every key first — Ctrl+Tab is a key
    /// somebody might be trying to assign.
    /// </summary>
    internal static int PageStep(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;

        return e.Key switch
        {
            Key.Tab when mods == KeyModifiers.Control => 1,
            Key.Tab when mods == (KeyModifiers.Control | KeyModifiers.Shift) => -1,
            Key.PageDown when mods == KeyModifiers.Control => 1,
            Key.PageUp when mods == KeyModifiers.Control => -1,
            _ => 0,
        };
    }

    /// <summary>
    /// Moves to the next or previous page, wrapping round, and gives that
    /// page's name in the strip the keyboard — the page that was showing is
    /// gone, and focus left inside it would fall out of the window.
    /// </summary>
    private void TurnPage(SettingsViewModel model, int step)
    {
        var count = Pages.ItemCount;

        if (count == 0) return;

        model.PageIndex = ((model.PageIndex + step) % count + count) % count;

        Dispatcher.UIThread.Post(() =>
            (Pages.ContainerFromIndex(model.PageIndex) as Control)?.Focus(NavigationMethod.Directional));
    }

    public SettingsWindow(SettingsViewModel model) : this()
    {
        DataContext = model;
        model.CloseRequested += (_, _) => Close();
    }
}
