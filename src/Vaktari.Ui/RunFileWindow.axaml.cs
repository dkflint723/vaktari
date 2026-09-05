using Avalonia.Controls;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Asks before starting a program somebody double-clicked.
///
/// The same arrangement as the chooser beside it: the model decides when this
/// goes away, whichever button did it. Nothing is hooked the other way round —
/// dismissing this with the X answers "neither", which is already what Cancel
/// does, so there is nothing left waiting on an answer.
/// </summary>
public partial class RunFileWindow : Window
{
    public RunFileWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
    }

    public RunFileWindow(RunFileViewModel model) : this()
    {
        DataContext = model;

        model.Closed += (_, _) => Close();
    }
}
