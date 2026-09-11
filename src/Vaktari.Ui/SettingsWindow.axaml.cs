using Avalonia.Controls;
using Avalonia.Interactivity;
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
        // something else on the tunnel already took.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is SettingsViewModel model && model.Keyboard.Offer(e.Key, e.KeyModifiers))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    public SettingsWindow(SettingsViewModel model) : this()
    {
        DataContext = model;
        model.CloseRequested += (_, _) => Close();
    }
}
