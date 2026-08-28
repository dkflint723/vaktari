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
}
