using Avalonia.Controls;
using Avalonia.Interactivity;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);

        // The list itself rather than a view model.
        //
        // **This used to say the list was a constant, and it is not any
        // more.** Backspace is a preference now — Back by default, up one
        // folder when the Navigation page says so — so Shortcuts.All is built
        // per read. Assigning it here is still right, and for a better reason
        // than "it never changes": a window is built fresh for every F1 press,
        // so the sheet is composed against the settings in force at the moment
        // it opens, and nothing about it can change while it is on screen.
        Groups.ItemsSource = Shortcuts.All;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
