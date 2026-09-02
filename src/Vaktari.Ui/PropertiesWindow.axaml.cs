using Avalonia.Controls;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
    }

    public PropertiesWindow(PropertiesViewModel model) : this()
    {
        DataContext = model;
        _ = model.LoadAsync();

        // The measurement and the checksum stop with the window. They kept
        // running after it closed — harmless in outcome, but a tree walk or a
        // gigabyte checksum for a window nobody can see is disk and battery
        // spent on nothing.
        Closed += (_, _) => model.CancelBackgroundWork();
    }

    /// <summary>
    /// **Escape closed nothing but the shortcut sheet.** Every other dialog
    /// here had to be dismissed with the mouse, which is the wrong way round:
    /// Properties is opened from the keyboard with Alt+Enter, and the key that
    /// gets you out of it is the first one anybody tries.
    ///
    /// A handler rather than a button with IsCancel, because this window has no
    /// close button to hang one on — the other five each press their own
    /// Cancel, which is not the same thing as closing and must not become it.
    /// </summary>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || e.Key != Avalonia.Input.Key.Escape) return;

        e.Handled = true;
        Close();
    }
}
